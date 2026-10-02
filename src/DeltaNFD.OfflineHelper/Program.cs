using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DeltaNFD.Services;

namespace DeltaNFD.OfflineHelper;

internal static class Program
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(30);
    private static Guid _runId;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--describe-isolated-test-protocol")
        {
            Console.WriteLine("DeltaNFD.OfflineHelper isolated-test-root v1");
            return 0;
        }
        if (!Guid.TryParse(GetArgument(args, "--operation-id"), out _runId) || _runId == Guid.Empty) return 2;
        var testRoot = GetArgument(args, "--isolated-test-root");
        var testIdText = GetArgument(args, "--isolated-test-id");
        if (args.Contains("--isolated-test-root") || args.Contains("--isolated-test-id"))
        {
            try
            {
                if (!Guid.TryParse(testIdText, out var testId)) return 2;
                AppDataPaths.ConfigureIsolatedTestRoot(testRoot ?? "", testId);
            }
            catch (Exception ex) { Console.Error.WriteLine("隔离测试路径拒绝：" + ex.Message); return 2; }
        }
        try { return await RunAsync(args); }
        catch (Exception ex)
        {
            Log.Error("脱机助手：单次流程异常退出", ex);
            using var process = Process.GetCurrentProcess();
            return Fail(OfflineModeStateStore.Default, "单次流程异常，保留恢复记录：" + ex.Message,
                process.Id, process.StartTime.ToUniversalTime().Ticks);
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var pipeName = GetArgument(args, "--pipe");
        if (!args.Contains("--offline-helper", StringComparer.Ordinal) || string.IsNullOrWhiteSpace(pipeName))
            return 2;

        Log.Startup();
        var store = OfflineModeStateStore.Default;
        using var singleton = OfflineModeHelperInstanceLease.TryAcquire(store, out var singletonError);
        if (singleton is null)
        {
            await SendHandshakeAsync(pipeName, "ERROR|" + singletonError);
            return 3;
        }

        if (!store.TryRead(out var initial, out var readError))
        {
            await SendHandshakeAsync(pipeName, "ERROR|" + readError);
            Log.Error("脱机助手：脱机状态读取失败", new InvalidDataException(readError));
            return 4;
        }
        if (!OfflineHelperSession.CanExecute(initial, _runId))
        {
            await SendHandshakeAsync(pipeName, "ERROR|脱机模式未启用或原配置快照缺失。");
            return 5;
        }
        using var helper = Process.GetCurrentProcess();
        var helperPid = helper.Id;
        var helperStartTicks = helper.StartTime.ToUniversalTime().Ticks;
        if (!OfflineHelperSession.TryRegister(store, _runId, helperPid, helperStartTicks, out var registerError))
        {
            await SendHandshakeAsync(pipeName, "ERROR|" + registerError);
            return 6;
        }
        if (!await SendHandshakeAsync(pipeName, "READY", waitForStart: true)) return 6;
        Log.Info($"脱机助手启动：PID={helperPid}，只执行一次本轮调度，不创建后续监控。");
        if (!SetState(store, OfflineModeStatus.WaitingForGame,
            "脱机助手运行中：等待用户通过原游戏平台启动目标游戏（最多 180 秒）。",
            helperPid, helperStartTicks)) return 6;

        var scheduler = new OfflineCpuSetScheduler();
        var waitClock = Stopwatch.StartNew();
        using var waitDeadline = new OfflineHelperDeadline(WaitTimeout);
        OfflineTargetDiscovery? discovery = null;
        while (waitClock.Elapsed < WaitTimeout)
        {
            if (!store.TryRead(out var current, out readError))
                return Fail(store, "脱机等待期间状态文件读取失败：" + readError, helperPid, helperStartTicks);
            if (!OfflineHelperSession.CanExecute(current, _runId))
            {
                Log.Info("脱机助手：脱机模式已关闭，等待任务取消。");
                return 0;
            }

            discovery = scheduler.DiscoverCurrentTargets();
            var inSession = discovery.VerifiedProcesses
                .Where(p => p.SessionId == Process.GetCurrentProcess().SessionId)
                .ToArray();
            if (inSession.Length > 0)
            {
                discovery = new OfflineTargetDiscovery(discovery.ObservedProcessCount, inSession);
                break;
            }
            if (discovery.VerifiedProcesses.Count > 0)
                return Fail(store, "目标游戏进程位于其他 Windows 会话；为避免重启错误会话的 DWM，助手已退出。", helperPid, helperStartTicks);
            if (discovery.ObservedProcessCount > 0 && discovery.VerifiedProcesses.Count == 0 &&
                !GameTargetService.Default.IsCustom)
            {
                return Fail(store, "发现同名目标进程，但 EXE 完整路径无法确认；已拒绝应用。", helperPid, helperStartTicks);
            }

            var remaining = WaitTimeout - waitClock.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < PollInterval ? remaining : PollInterval);
        }

        if (discovery is null || discovery.VerifiedProcesses.Count == 0)
            return Fail(store, "180 秒内未发现可验证的目标游戏进程；助手已退出，未报告脱机就绪。", helperPid, helperStartTicks);
        waitDeadline.Dispose();

        var actionClock = Stopwatch.StartNew();
        using var actionTimeout = new CancellationTokenSource(ApplyTimeout);
        using var hardDeadline = new OfflineHelperDeadline(ApplyTimeout);
        using var operationLease = await OfflineModeExecutionLock.WaitAcquireAsync(store, TimeSpan.FromSeconds(5), actionTimeout.Token);
        if (operationLease is null)
            return Fail(store, "无法取得脱机操作锁，助手已退出。", helperPid, helperStartTicks);

        if (!store.TryRead(out var state, out readError) || !OfflineHelperSession.CanExecute(state, _runId))
            return Fail(store, "准备阶段状态已变化，未应用脱机调度。", helperPid, helperStartTicks);

        using var cancellationWatchStop = new CancellationTokenSource();
        var cancellationWatch = WatchModeCancellationAsync(store, actionTimeout, cancellationWatchStop.Token);
        try
        {
        if (!SetState(store, OfflineModeStatus.Applying, "目标进程已识别，正在执行一次性 DWM/CPU Sets 操作。",
            helperPid, helperStartTicks)) return 6;

        var preferences = state.SavedPreferences!;
        OperationResult? dwmResult = null;
        if (preferences.DwmRestartOnGameStart)
        {
            var first = discovery.VerifiedProcesses.OrderBy(p => p.StartTimeUtcTicks).First();
            var started = new DateTime(first.StartTimeUtcTicks, DateTimeKind.Utc);
            var remaining = started.AddSeconds(5) - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                try { await Task.Delay(remaining, actionTimeout.Token); }
                catch (OperationCanceledException)
                {
                    if (IsModeCancellationRequested(store)) return 0;
                    return Fail(store, "目标游戏启动后 5 秒等待超时，未重启 DWM 或设置 CPU Sets。", helperPid, helperStartTicks);
                }
            }

            if (actionClock.Elapsed > ApplyTimeout || actionTimeout.IsCancellationRequested)
                return Fail(store, "等待 DWM 安全延迟时超过 30 秒，助手已退出。", helperPid, helperStartTicks);
            var live = scheduler.DiscoverCurrentTargets().VerifiedProcesses
                .FirstOrDefault(p => SameIdentity(p, first));
            dwmResult = live is null
                ? OperationResult.Fail("DWM 执行前目标进程已退出或身份变化；本轮不重试。")
                : RestartDwmForSession(live.SessionId, live, actionTimeout.Token);
            Log.Info("脱机 DWM 单次结果：" + dwmResult.Message);
        }

        var cpuResult = await scheduler.ApplyOnceAsync(preferences, actionTimeout.Token);
        if (IsModeCancellationRequested(store))
        {
            Log.Info("脱机助手：收到退出脱机模式的取消请求，未写入完成状态。");
            return 0;
        }
        var success = cpuResult.Success && (dwmResult?.Success ?? true) && !actionTimeout.IsCancellationRequested;
        var keptAnyState = store.TryRead(out var afterRun, out _) && afterRun.CpuSetChanges.Count != 0;
        var completionStatus = success
            ? (keptAnyState || dwmResult?.Success == true ? OfflineModeStatus.Kept : OfflineModeStatus.Ready)
            : OfflineModeStatus.Failed;
        var summary = "脱机本轮已结束；助手随后退出。" + cpuResult.Message +
            (dwmResult is null ? "" : " DWM：" + dwmResult.Message) +
            (actionTimeout.IsCancellationRequested ? " 处理超过 30 秒，后续步骤已停止。" : "");
        SetState(store, OfflineModeStatus.Applying, summary + " 助手退出后才会确认本轮状态。",
            helperPid, helperStartTicks, operationCompleted: true, completionStatus);
        Log.Info("脱机助手结束：" + summary);
        return success ? 0 : 7;
        }
        finally
        {
            cancellationWatchStop.Cancel();
            try { await cancellationWatch; } catch (Exception ex) { Log.Warn("助手取消监控收尾：" + ex.Message); }
        }
    }

    private static OperationResult RestartDwmForSession(int targetSessionId, OfflineTargetProcess target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrentTarget(target))
            return OperationResult.Fail("DWM 操作前目标身份复核失败。");

        string systemDwm;
        try
        {
            systemDwm = GameTargetService.CanonicalPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "dwm.exe"));
        }
        catch (Exception ex) { return OperationResult.Fail("无法确认系统 DWM 路径：" + ex.Message); }

        var candidates = new List<Process>();
        foreach (var process in Process.GetProcessesByName("dwm"))
        {
            try
            {
                if (process.SessionId != targetSessionId) { process.Dispose(); continue; }
                var imagePath = GameTargetService.CanonicalPath(GameTargetService.ReadProcessPath(process.Id));
                if (!imagePath.Equals(systemDwm, StringComparison.OrdinalIgnoreCase)) { process.Dispose(); continue; }
                candidates.Add(process);
            }
            catch { process.Dispose(); }
        }

        if (candidates.Count == 0)
            return OperationResult.Fail("目标交互会话中没有可验证路径的系统 DWM；本轮不重试。");
        if (candidates.Count != 1)
        {
            foreach (var candidate in candidates) candidate.Dispose();
            return OperationResult.Fail("目标交互会话出现多个系统 DWM 实例，无法唯一确认重启对象；本轮不重试。");
        }

        var killed = 0;
        var failures = new List<string>();
        foreach (var process in candidates)
        {
            using (process)
            {
                if (!IsCurrentTarget(target))
                {
                    failures.Add("目标游戏已退出或身份变化");
                    break;
                }
                try
                {
                    token.ThrowIfCancellationRequested();
                    using var handle = OpenProcess(0x00100000 | 0x1000 | 0x0001, false, process.Id);
                    if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    var image = new StringBuilder(32768);
                    var size = image.Capacity;
                    if (!QueryFullProcessImageNameW(handle, 0, image, ref size) ||
                        !GameTargetService.CanonicalPath(image.ToString()).Equals(systemDwm, StringComparison.OrdinalIgnoreCase) ||
                        !ProcessIdToSessionId(process.Id, out var session) || session != targetSessionId ||
                        WaitForSingleObject(handle, 0) != 0x102)
                        throw new InvalidOperationException("DWM 句柄对应的路径/会话/存活状态无法复核。");
                    token.ThrowIfCancellationRequested();
                    if (!IsCurrentTarget(target)) throw new InvalidOperationException("重启前目标游戏已变化。");
                    if (!TerminateProcess(handle, 1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    if (WaitForSingleObject(handle, 10_000) != 0) failures.Add($"DWM PID {process.Id} 退出超时");
                    else killed++;
                }
                catch (Exception ex) { failures.Add($"DWM PID {process.Id}：{ex.Message}"); }
            }
        }
        return failures.Count == 0 && killed > 0
            ? OperationResult.Ok($"已尝试重启目标交互会话的系统 DWM（{killed} 个实例）。")
            : OperationResult.Fail("DWM 单次重启未完整确认：" + string.Join("；", failures));
    }

    private static bool IsCurrentTarget(OfflineTargetProcess identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.StartTimeUtcTicks ||
                process.SessionId != identity.SessionId)
                return false;
            var path = GameTargetService.CanonicalPath(GameTargetService.ReadProcessPath(process.Id));
            return path.Equals(identity.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                   GameTargetService.Default.Matches(process, GameTargetService.Default.Current);
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(int pid, out int session);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    private static bool SameIdentity(OfflineTargetProcess left, OfflineTargetProcess right)
        => left.ProcessId == right.ProcessId && left.StartTimeUtcTicks == right.StartTimeUtcTicks &&
           left.SessionId == right.SessionId && left.ExecutablePath.Equals(right.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> SendHandshakeAsync(string pipeName, string message, bool waitForStart = false)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(8_000);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(message);
            if (waitForStart)
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                if (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)) != "START") return false;
                if (IsModeCancellationRequested(OfflineModeStateStore.Default)) return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("脱机助手启动握手失败", ex);
            return false;
        }
    }

    private static string? GetArgument(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.Ordinal));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int Fail(OfflineModeStateStore store, string message, int? helperPid, long? helperStartTicks)
    {
        SetState(store, OfflineModeStatus.Failed, message, helperPid, helperStartTicks);
        Log.Warn("脱机助手：" + message);
        return 1;
    }

    private static bool IsModeCancellationRequested(OfflineModeStateStore store)
        => !store.TryRead(out var state, out _) || !OfflineHelperSession.CanExecute(state, _runId);

    private static async Task WatchModeCancellationAsync(OfflineModeStateStore store,
        CancellationTokenSource operationTimeout, CancellationToken stopToken)
    {
        try
        {
            while (!stopToken.IsCancellationRequested && !operationTimeout.IsCancellationRequested)
            {
                await Task.Delay(250, stopToken);
                if (IsModeCancellationRequested(store))
                {
                    operationTimeout.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static bool SetState(OfflineModeStateStore store, OfflineModeStatus status, string message,
        int? helperPid, long? helperStartTicks, bool operationCompleted = false,
        OfflineModeStatus completionStatus = OfflineModeStatus.Ready)
    {
        if (helperPid is null || helperStartTicks is null) return false;
        if (!OfflineHelperSession.TryProgress(store, _runId, helperPid.Value, helperStartTicks.Value, status, message,
                operationCompleted, completionStatus, out var error))
        {
            Log.Error("脱机助手无法保存状态：" + error, new IOException(error));
            return false;
        }
        return true;
    }
}
