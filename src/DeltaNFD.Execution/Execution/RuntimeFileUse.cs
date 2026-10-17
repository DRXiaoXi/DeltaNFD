using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Services;

internal interface IRuntimeFileUseProcess : IDisposable
{
    RuntimeFileUseProcess Identity { get; }
    bool RequestClose();
    void ForceEnd();
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken token);
}
internal interface IRuntimeFileUseEnvironment
{
    Task<RuntimeFileUseScan> InspectAsync(CancellationToken token, RuntimeFileUseScope scope = RuntimeFileUseScope.Vc14);
    IRuntimeFileUseProcess Open(RuntimeFileUseProcess expected);
    string? Blocker();
}

/// <summary>One-use approvals bind selected processes to a recent server-side Restart Manager snapshot.</summary>
internal sealed class RuntimeFileUseEngine(IRuntimeFileUseEnvironment environment, Func<DateTimeOffset>? clock = null)
{
    private readonly Dictionary<string, (DateTimeOffset Created, RuntimeFileUseScan Scan)> _snapshots = [];
    private readonly object _snapshotsGate = new();
    private readonly SemaphoreSlim _closing = new(1, 1);
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    internal async Task<RuntimeFileUseScan> ScanAsync(CancellationToken token, RuntimeFileUseScope scope = RuntimeFileUseScope.Vc14)
    {
        if (!Enum.IsDefined(scope)) return new(false, "占用检测范围无效，未检测或结束进程。", []);
        var report = (await environment.InspectAsync(token, scope).ConfigureAwait(false)) with { Scope = scope };
        Log.Info($"运行库占用检测：范围={scope}，完成={report.Success}，进程数={report.Processes.Count}；{report.Message}");
        if (!report.Success) return report with { SnapshotId = "" };
        if (report.Processes.Count > 256 || report.Processes.Any(p => p.Pid <= 0)
            || report.Processes.Select(p => p.Pid).Distinct().Count() != report.Processes.Count)
            return new(false, "占用进程列表不完整或身份冲突，请重新检测。", []);
        var snapshot = report with { SnapshotId = Guid.NewGuid().ToString("N") };
        lock (_snapshotsGate)
        {
            foreach (var expired in _snapshots.Where(p => Now - p.Value.Created > TimeSpan.FromMinutes(5)).Select(p => p.Key).ToArray())
                _snapshots.Remove(expired);
            while (_snapshots.Count >= 4) _snapshots.Remove(_snapshots.OrderBy(p => p.Value.Created).First().Key);
            _snapshots[snapshot.SnapshotId] = (Now, snapshot);
        }
        return snapshot;
    }

    internal static bool SameIdentity(RuntimeFileUseProcess a, RuntimeFileUseProcess b) => a.Pid == b.Pid
        && a.StartFileTime > 0 && a.StartFileTime == b.StartFileTime && a.ImagePath.Length > 0
        && a.ImagePath.Equals(b.ImagePath, StringComparison.OrdinalIgnoreCase);

    internal async Task<RuntimeFileUseCloseResult> CloseAsync(RuntimeFileUseCloseArgs args, CancellationToken token)
    {
        RuntimeFileUseCloseResult Refuse(string text) => new(false, text, []);
        if (!args.Confirmed || args.Force && !args.ForceConfirmed)
            return Refuse("尚未确认结束这些进程，未操作。强制结束需要单独确认。");
        if (args.ProcessIds is not { Count: > 0 and <= 16 } || args.ProcessIds.Any(p => p <= 0)
            || args.ProcessIds.Distinct().Count() != args.ProcessIds.Count)
            return Refuse("每次只能选择 1–16 个不重复的已核验进程。");
        await _closing.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RuntimeFileUseScan approved;
            lock (_snapshotsGate)
            {
                if (!_snapshots.Remove(args.SnapshotId ?? "", out var saved) || Now - saved.Created > TimeSpan.FromMinutes(5))
                    return Refuse("占用列表已过期或已使用，请重新检测并确认。");
                approved = saved.Scan;
            }
            var selected = args.ProcessIds.Select(pid => approved.Processes.FirstOrDefault(p => p.Pid == pid)).ToArray();
            if (selected.Any(p => p is null || !p.CanClose)) return Refuse("选择包含未确认或受保护进程，未操作。");
            if (environment.Blocker() is { } blocker) return Refuse(blocker);
            var current = await environment.InspectAsync(token, approved.Scope).ConfigureAwait(false);
            if (!current.Success) return Refuse("无法重新确认文件占用，未结束进程：" + current.Message);
            var actions = new List<RuntimeFileUseAction>();
            void Add(RuntimeFileUseAction action)
            {
                actions.Add(action);
                Log.Info($"运行库占用处理：PID={action.Pid}，强制={args.Force}，状态={action.Status}，{action.Message}");
            }
            foreach (var expected in selected.Cast<RuntimeFileUseProcess>())
            {
                if (token.IsCancellationRequested) { Add(new(expected.Pid, "cancelled", "已停止后续进程操作；已完成的退出无法撤销。")); break; }
                if (environment.Blocker() is { } changed) { Add(new(expected.Pid, "blocked", changed)); break; }
                var live = current.Processes.FirstOrDefault(p => p.Pid == expected.Pid);
                if (live is null || !SameIdentity(expected, live))
                { Add(new(expected.Pid, "skipped", "原进程已退出、不再占用或身份已变化，未操作。")); continue; }
                if (!live.CanClose) { Add(new(expected.Pid, "blocked", live.ProtectionReason)); continue; }
                try
                {
                    using var process = environment.Open(live); // Retains a handle; never terminate by image name or PID alone.
                    if (!SameIdentity(expected, process.Identity) || !process.Identity.CanClose)
                    { Add(new(expected.Pid, "blocked", "进程身份或保护状态已变化，未操作。")); continue; }
                    token.ThrowIfCancellationRequested();
                    Log.Info($"运行库占用处理开始：PID={expected.Pid}，启动时间={expected.StartFileTime}，强制={args.Force}，路径={expected.ImagePath}");
                    if (args.Force) process.ForceEnd();
                    else if (!process.RequestClose())
                    { Add(new(expected.Pid, "still-running", "无法请求正常退出，可手动退出或另行确认强制结束。")); continue; }
                    var exited = await process.WaitAsync(TimeSpan.FromSeconds(args.Force ? 2 : 4), token).ConfigureAwait(false);
                    Add(new(expected.Pid, exited ? "exited" : "still-running", exited ? "进程已退出。" : "进程尚未退出，未自动升级为强制结束。"));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { Add(new(expected.Pid, "unconfirmed", "已停止等待，退出结果尚未确认，请重新检测。")); break; }
                catch (Exception ex) { Add(new(expected.Pid, "failed", "未能结束进程：" + ex.Message)); }
            }
            var success = actions.Count == selected.Length && actions.All(a => a.Status is "exited" or "skipped");
            return new(success, string.Join("\n", actions.Select(a => "PID " + a.Pid + "：" + a.Message)), actions);
        }
        finally { _closing.Release(); }
    }
}

public static class RuntimeFileUseMaintenance
{
    private static readonly RuntimeFileUseEngine Engine = new(new RuntimeFileUseNative());
    public static Task<RuntimeFileUseScan> ScanAsync(CancellationToken token, RuntimeFileUseScope scope = RuntimeFileUseScope.Vc14) => Task.Run(() => Engine.ScanAsync(token, scope), token);
    public static Task<RuntimeFileUseCloseResult> CloseAsync(RuntimeFileUseCloseArgs args, CancellationToken token)
        => Task.Run(() =>
        {
            RuntimeFileUseCloseResult? result = null;
            var operation = RuntimeGuardProtection.Exclusive(() =>
            {
                result = Engine.CloseAsync(args, token).GetAwaiter().GetResult();
                return result.Success ? OperationResult.Ok(result.Message) : OperationResult.Fail(result.Message);
            });
            return result ?? new RuntimeFileUseCloseResult(false, operation.Message, []);
        }, token);
}

internal static class RuntimeFileUseSafety
{
    internal static string Reason(int pid, string image, int session, int currentSession, bool sameUser, bool criticalKnown,
        bool critical, int applicationType, string windowsRoot, string appRoot)
    {
        if (pid <= 4 || pid == Environment.ProcessId) return "系统或当前工具进程，不能在此结束。";
        if (!criticalKnown || critical) return "关键进程或无法确认关键状态，不能在此结束。";
        if (session == 0 || session != currentSession || !sameUser) return "服务、其他会话或其他用户进程，不能在此结束。";
        if (applicationType is not (1 or 2 or 5)) return "系统、服务或应用类型未确认，不能在此结束。";
        if (!Path.IsPathFullyQualified(image)) return "无法确认进程路径，不能在此结束。";
        var path = Path.GetFullPath(image);
        bool Under(string root) => root.Length > 0 && path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (Under(windowsRoot)) return "Windows 组件，不能在此结束。";
        var name = Path.GetFileNameWithoutExtension(path);
        if (Under(appRoot) || name.StartsWith("DeltaNFD", StringComparison.OrdinalIgnoreCase) || name.Equals("DeltaOptimizer", StringComparison.OrdinalIgnoreCase))
            return "本工具或官方后端，不能在此结束。";
        if (name.StartsWith("setup", StringComparison.OrdinalIgnoreCase) || name.StartsWith("install", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("vc_redist", StringComparison.OrdinalIgnoreCase) || name.StartsWith("vcredist", StringComparison.OrdinalIgnoreCase)
            || new[] { "msiexec", "dism", "sfc", "TiWorker", "TrustedInstaller" }.Contains(name, StringComparer.OrdinalIgnoreCase))
            return "安装器或系统维护进程，不能在此结束。";
        if (path.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\PackageCache\", StringComparison.OrdinalIgnoreCase))
            return "安装包缓存中的执行进程，不能在此结束。";
        return "";
    }
    internal static bool InstallerMetadata(string text) => System.Text.RegularExpressions.Regex.IsMatch(text,
        @"installer|\bsetup\b|redistributable|安装程序|安装器|安装包", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

internal sealed class RuntimeFileUseNative : IRuntimeFileUseEnvironment
{
    private static readonly string[] V14Names = ["msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "msvcp140_atomic_wait.dll", "msvcp140_codecvt_ids.dll",
        "vcruntime140.dll", "vcruntime140_1.dll", "vcruntime140_threads.dll", "concrt140.dll", "vcomp140.dll"];
    private static readonly string[] LegacyNames = new[] { "80", "90", "100", "110", "120" }
        .SelectMany(version => new[] { "msvcr", "msvcp", "mfc", "mfcm", "vcomp", "atl" }.Select(prefix => prefix + version + ".dll")
            .Concat(new[] { "mfc" + version + "u.dll", "mfcm" + version + "u.dll", "mfc" + version + "enu.dll" })).ToArray();

    // Fixed OS shared-runtime locations only; never scan application folders or
    // accept caller-supplied resource paths. The optional roots are test fixtures.
    internal static string[] Files(RuntimeFileUseScope scope = RuntimeFileUseScope.Vc14, string[]? systemDirectories = null, string? windowsRoot = null)
    {
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        systemDirectories ??= new[] { Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86 }.Select(Environment.GetFolderPath).ToArray();
        windowsRoot ??= Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var names = scope == RuntimeFileUseScope.FullRepair ? V14Names.Concat(LegacyNames).ToArray() : V14Names;
        var files = systemDirectories.Where(d => d.Length > 0).SelectMany(d => names.Select(n => Path.Combine(d, n))).Where(File.Exists).ToList();
        if (scope == RuntimeFileUseScope.FullRepair)
        {
            var sxs = Path.Combine(windowsRoot, "WinSxS");
            if (Directory.Exists(sxs))
            {
                if ((File.GetAttributes(sxs) & FileAttributes.ReparsePoint) != 0) throw new IOException("WinSxS 路径异常，未返回不完整的占用名单。");
                foreach (var directory in Directory.EnumerateDirectories(sxs))
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(directory),
                        @"^(?:amd64|x86)_microsoft\.vc(?:80|90)\.(?:crt|mfc|mfcloc|atl|openmp)_", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("C++ 并行程序集路径异常，未继续检测。");
                    files.AddRange(LegacyNames.Select(n => Path.Combine(directory, n)).Where(File.Exists));
                }
            }
        }
        var result = files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Length > 1024) throw new IOException("共享 C++ 文件过多，未返回不完整的占用名单。");
        if (result.Any(f => (File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0)) throw new IOException("共享 C++ 文件路径异常，未继续检测。");
        return result;
    }
    public string? Blocker() => RuntimeInstallationReview.InstallerBusy() || File.Exists(Path.Combine(AppDataPaths.Root, "runtime-repair-active.json"))
        ? "安装事务正在运行或待核对，请先等待并核对；不会在安装过程中结束进程。" : null;
    public IRuntimeFileUseProcess Open(RuntimeFileUseProcess expected) => new HeldProcess(expected.Pid, expected.Name, 1);

    public Task<RuntimeFileUseScan> InspectAsync(CancellationToken token, RuntimeFileUseScope scope = RuntimeFileUseScope.Vc14)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var files = Files(scope);
            var range = scope == RuntimeFileUseScope.FullRepair ? "C++ 2005–2013 / 14.x 共享 DLL（System32、SysWOW64 及固定的 VC80/VC90 WinSxS 程序集）" : "System32 / SysWOW64 中固定的 C++ 14.x DLL";
            if (files.Length == 0) return Task.FromResult(new RuntimeFileUseScan(true, "本次检测范围：" + range + "。未发现可检测的共享 DLL；不代表其他位置没有占用。", []));
            var owners = Owners(files);
            var result = new List<RuntimeFileUseProcess>();
            foreach (var info in owners.GroupBy(p => p.Process.Pid).Select(g => g.First()))
            {
                token.ThrowIfCancellationRequested();
                var start = ((long)info.Process.Start.dwHighDateTime << 32) | (uint)info.Process.Start.dwLowDateTime;
                try
                {
                    using var process = new HeldProcess((int)info.Process.Pid, info.Name, info.Type);
                    var identity = process.Identity;
                    if (identity.StartFileTime != start) identity = identity with { CanClose = false, ProtectionReason = "检测期间进程身份变化，请重新检测。" };
                    result.Add(identity);
                }
                catch { result.Add(new((int)info.Process.Pid, start, info.Name, "", false, "无法读取完整进程身份，只能手动处理。")); }
            }
            return Task.FromResult(new RuntimeFileUseScan(true,
                "本次检测范围：" + range + "，共 " + files.Length + " 个文件。占用不一定阻止安装；关闭进程也不会移除安装器登记的依赖引用。", result));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Task.FromResult(new RuntimeFileUseScan(false, "Windows 文件占用检测未完成：" + ex.Message, [])); }
    }

    private static ProcessInfo[] Owners(string[] files)
    {
        var error = RmStartSession(out var session, 0, new StringBuilder(33));
        if (error != 0) throw new System.ComponentModel.Win32Exception(error);
        try
        {
            error = RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null);
            if (error != 0) throw new System.ComponentModel.Win32Exception(error);
            uint count = 0;
            error = RmGetList(session, out var needed, ref count, null, out _);
            for (var attempt = 0; error == 234 && attempt < 4; attempt++)
            {
                if (needed > 512) throw new IOException("文件占用进程过多，未返回不完整名单。");
                var rows = new ProcessInfo[needed]; count = needed;
                error = RmGetList(session, out needed, ref count, rows, out _);
                if (error == 0) return rows.Take((int)count).ToArray();
            }
            if (error != 0) throw new System.ComponentModel.Win32Exception(error);
            return [];
        }
        finally { RmEndSession(session); }
    }

    private sealed class HeldProcess : IRuntimeFileUseProcess
    {
        private readonly Process _process;
        public RuntimeFileUseProcess Identity { get; }
        public HeldProcess(int pid, string friendlyName, int type)
        {
            _process = Process.GetProcessById(pid);
            try
            {
                var handle = _process.SafeHandle; // Retained through termination/wait, protects against PID reuse.
                var path = new StringBuilder(32768); var length = path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref length)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var criticalKnown = IsProcessCritical(handle, out var critical);
                var sameUser = false;
                if (OpenProcessToken(handle, 8, out var token))
                {
                    using (token)
                    using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
                    using (var current = WindowsIdentity.GetCurrent()) sameUser = owner.User is { } sid && current.User is { } currentSid && sid.Equals(currentSid);
                }
                using var self = Process.GetCurrentProcess();
                var reason = RuntimeFileUseSafety.Reason(pid, path.ToString(), _process.SessionId, self.SessionId, sameUser,
                    criticalKnown, critical, type, Environment.GetFolderPath(Environment.SpecialFolder.Windows), BackendEnvironment.AppRoot);
                if (reason.Length == 0)
                {
                    var version = FileVersionInfo.GetVersionInfo(path.ToString());
                    if (RuntimeFileUseSafety.InstallerMetadata((version.FileDescription ?? "") + " " + (version.OriginalFilename ?? "") + " " + (version.ProductName ?? "")))
                        reason = "文件身份属于安装器，不能在此结束。";
                }
                if (reason.Length == 0 && new GameTargetService(AppSettingsStore.DefaultPath).ProtectFromCleanup(_process))
                    reason = "当前目标游戏，请先在游戏内安全退出。";
                Identity = new(pid, _process.StartTime.ToFileTimeUtc(), string.IsNullOrWhiteSpace(friendlyName) ? _process.ProcessName : friendlyName,
                    path.ToString(), reason.Length == 0, reason);
            }
            catch { _process.Dispose(); throw; }
        }
        public bool RequestClose()
        {
            CheckCriticalState();
            return _process.CloseMainWindow();
        }
        public void ForceEnd()
        {
            CheckCriticalState();
            if (!TerminateProcess(_process.SafeHandle, 1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        private void CheckCriticalState()
        {
            if (!IsProcessCritical(_process.SafeHandle, out var critical) || critical)
                throw new IOException("关键进程状态已变化或无法确认，未结束进程。");
        }
        public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken token)
        {
            try { await _process.WaitForExitAsync(token).WaitAsync(timeout, token).ConfigureAwait(false); return true; }
            catch (TimeoutException) { return false; }
        }
        public void Dispose() => _process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct UniqueProcess { public uint Pid; public FILETIME Start; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
        public int Type; public uint Status, Session;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint session, uint count,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] files, uint appCount, UniqueProcess[]? apps, uint svcCount, string[]? services);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? rows, out uint reasons);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessCritical(SafeProcessHandle process, out bool critical);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
}
