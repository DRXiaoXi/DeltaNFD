using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using DeltaNFD.Native;
using DeltaNFD.Services;

/// <summary>
/// 真实脱机助手端到端检查：启动编译产物 OfflineHelper.exe，走完整管道握手，
/// 用临时测试子进程充当自定义目标，验证一次性 CPU Sets 应用/还原、取消、
/// 单实例租约与 180 秒等待超时。只对测试子进程写入 CPU Sets；
/// 设置、状态、日志和锁均使用带标记的独立测试目录，不读取或改写真实用户配置。
/// </summary>
internal static class OfflineHelperE2EChecks
{
    private const int HelperExitOk = 0;
    private const int HelperExitFail = 1;
    private const int HelperExitHandshakeError = 6;
    private const int HelperExitDeadlineKilled = 124;
    private static string AppRoot => AppDataPaths.Root;
    private static string OfflineStatePath => Path.Combine(AppRoot, "offline-mode.json");
    private static string SettingsPath => AppSettingsStore.DefaultPath;
    private static Guid _testId;

    public static async Task RunAsync(string[] args)
    {
        var helperExe = ResolveHelperExe(args);
        var fixtureParent = Path.Combine(FindRepositoryRoot(), "_buildcheck", "offline-helper-e2e");
        _testId = Guid.NewGuid();
        var fixtureRoot = Path.Combine(fixtureParent, _testId.ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        File.WriteAllText(Path.Combine(fixtureRoot, "isolated-test.marker"), _testId.ToString("D"));
        try
        {
            AppDataPaths.ConfigureIsolatedTestRoot(fixtureRoot, _testId);
            if (AppRoot != Path.GetFullPath(fixtureRoot) || SettingsPath != Path.Combine(AppRoot, "settings.json") ||
                OfflineModeStateStore.Default.PathName != OfflineStatePath)
                throw new InvalidOperationException("默认服务未使用隔离数据目录，拒绝测试。");
            Console.WriteLine("隔离测试数据：" + AppRoot);
            await RunScenarioApplyAndRestoreAsync(helperExe, fixtureRoot);
            await RunScenarioCancelDuringWaitAsync(helperExe, fixtureRoot);
            await RunScenarioSecondInstanceRejectedAsync(helperExe, fixtureRoot);
            await RunScenarioWaitTimeoutAsync(helperExe, fixtureRoot);
        }
        finally
        {
            await CleanupAsync(fixtureParent, fixtureRoot);
        }
        Console.WriteLine("真实脱机助手四场景及完整目录清理通过，未使用真实用户配置。");
    }

    // ---------- 场景 1：发现目标 → 应用 CPU Sets → 助手退出 → 完成 → 还原 ----------

    private static async Task RunScenarioApplyAndRestoreAsync(string helperExe, string fixtureRoot)
    {
        var topology = await new CpuTopologyService().GetTopologyAsync();
        var affinityMask = topology.AllMask & ~1UL; // 保存的亲和性规则偏好：排除 CPU0
        if (affinityMask == 0 || (affinityMask & ~topology.AllMask) != 0)
            throw new InvalidOperationException("本机拓扑无法构造亲和性规则掩码。");

        var targetExe = CreateFixtureExe(fixtureRoot, "target");
        PrepareState(affinityMask, targetExe);
        var store = OfflineModeStateStore.Default;

        using var target = StartFixtureChild(targetExe);
        try
        {
            var runId = ReadRunId();
            using var handshake = await StartHelperHandshakeAsync(helperExe, runId);
            await handshake.SendStartAsync();
            await WaitForStatusAsync(OfflineModeStatus.Applying, "CPU Sets 应用阶段未被助手进入");
            var exit = await handshake.WaitForExitAsync(TimeSpan.FromSeconds(60));
            if (exit != HelperExitOk)
                throw new InvalidOperationException($"应用场景助手退出码异常：{exit}；{handshake.ReadOutput()}");

            // 助手正常退出后，由主程序同款完成确认语义收敛状态。
            if (!OfflineHelperCompletion.TryConfirmExited(store))
                throw new InvalidOperationException("完成确认未执行（助手可能未退出或仍持租约）。");
            if (!store.TryRead(out var confirmed, out var error)) throw new InvalidOperationException(error);
            if (confirmed.HelperProcessId is not null)
                throw new InvalidOperationException("完成确认后 PID 记录未清空。");
            if (confirmed.Status is not (OfflineModeStatus.Kept or OfflineModeStatus.Ready))
                throw new InvalidOperationException($"应用成功后状态异常：{confirmed.Status}；{confirmed.Message}");
            if (confirmed.HelperOperationCompleted)
                throw new InvalidOperationException("完成标记在确认后应被复位。");

            if (confirmed.CpuSetChanges.Count == 0)
                throw new InvalidOperationException("应用成功但没有产生 CPU Sets 恢复记录。");
            var targetRecord = confirmed.CpuSetChanges.SingleOrDefault(r => r.Purpose == "target-game");
            if (targetRecord is null || targetRecord.Status != OfflineCpuSetRecordStatus.Applied)
                throw new InvalidOperationException("目标进程缺少 Applied 状态的 CPU Sets 记录。");
            if (targetRecord.ProcessId != target.Id ||
                !targetRecord.ExecutablePath.EndsWith("target.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("目标记录身份不匹配。");

            // 双 CCD 偏好关闭 → 不分流其他进程。
            if (confirmed.CpuSetChanges.Any(r => r.Purpose == "other-process"))
                throw new InvalidOperationException("非双 CCD 计划不应分流其他进程。");

            AssertDefaultCpuSets(target, expectedSet: true, mask: affinityMask, "应用后目标进程");

            // 还原：主程序退出脱机路径使用的同一入口。
            var restore = new OfflineCpuSetScheduler().RestoreRecordedAssignments();
            if (!restore.Success) throw new InvalidOperationException("还原失败：" + restore.Message);
            if (!store.TryRead(out var restored, out error)) throw new InvalidOperationException(error);
            if (restored.CpuSetChanges.Count != 0)
                throw new InvalidOperationException("还原后仍残留 CPU Sets 记录。");
            AssertDefaultCpuSets(target, expectedSet: false, mask: 0, "还原后目标进程");

            Console.WriteLine("场景 1 通过：真实助手发现目标、写入并复核 CPU Sets、退出并完成确认，还原入口全部确认。");
        }
        finally
        {
            TryKillProcess(target);
        }
    }

    // ---------- 场景 2：等待期间取消 → 助手静默退出，不写完成状态 ----------

    private static async Task RunScenarioCancelDuringWaitAsync(string helperExe, string fixtureRoot)
    {
        var targetExe = CreateFixtureExe(fixtureRoot, "target");
        PrepareState(0, targetExe);
        var store = OfflineModeStateStore.Default;

        var runId = ReadRunId();
        using var handshake = await StartHelperHandshakeAsync(helperExe, runId);
        try
        {
            await handshake.SendStartAsync();
            // 等 helper 进入等待循环的消息（START 后写入），避开握手期间的取消检查点。
            await WaitForStatusMessageAsync("脱机助手运行中", "等待取消场景未进入等待循环");
            if (!store.TryUpdate(s => { s.CancelRequested = true; }, out var error))
                throw new InvalidOperationException("写入取消请求失败：" + error);

            var exit = await handshake.WaitForExitAsync(TimeSpan.FromSeconds(30));
            if (exit is not (HelperExitOk or HelperExitHandshakeError))
                throw new InvalidOperationException($"取消等待时助手退出码异常：{exit}；{handshake.ReadOutput()}");

            if (!store.TryRead(out var cancelled, out var error2)) throw new InvalidOperationException(error2);
            if (cancelled.HelperOperationCompleted)
                throw new InvalidOperationException("取消场景不应写入完成标记。");
            if (cancelled.CpuSetChanges.Count != 0)
                throw new InvalidOperationException("取消场景不应残留 CPU Sets 记录。");
            Console.WriteLine("场景 2 通过：等待期间取消，助手 30 秒内静默退出且未写完成状态。");
        }
        finally
        {
            await handshake.TryKillAsync();
        }
    }

    // ---------- 场景 3：已有实例持租约 → 第二个助手被拒绝 ----------

    private static async Task RunScenarioSecondInstanceRejectedAsync(string helperExe, string fixtureRoot)
    {
        var targetExe = CreateFixtureExe(fixtureRoot, "target");
        PrepareState(0, targetExe);
        var store = OfflineModeStateStore.Default;

        using var firstLease = OfflineModeHelperInstanceLease.TryAcquire(store, out var leaseError);
        if (firstLease is null) throw new InvalidOperationException("无法预持单实例租约：" + leaseError);
        using (firstLease)
        {
            var runId = ReadRunId();
            var pipeName = "DeltaNFD_Offline_" + runId.ToString("N");
            var start = BuildHelperStart(helperExe, pipeName, runId);
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动第二个助手。");
            try
            {
                await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
                using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                var response = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var exited = await WaitForExitCodeAsync(process, TimeSpan.FromSeconds(20));
                if (response is null || !response.StartsWith("ERROR|", StringComparison.Ordinal))
                    throw new InvalidOperationException($"单实例场景应返回 ERROR，实际：{response}");
                if (exited.code != 3)
                    throw new InvalidOperationException($"单实例场景助手退出码应为 3，实际 {exited.code}：{exited.output}");
                if (!response.Contains("实例", StringComparison.Ordinal))
                    throw new InvalidOperationException($"单实例错误文案异常：{response}");
            }
            finally
            {
                TryKillProcess(process);
            }
        }
        Console.WriteLine("场景 3 通过：已有实例时第二个助手通过握手返回错误并退出。");
    }

    // ---------- 场景 4：不启动目标 → 助手真实等满 180 秒后失败退出 ----------

    private static async Task RunScenarioWaitTimeoutAsync(string helperExe, string fixtureRoot)
    {
        var targetExe = CreateFixtureExe(fixtureRoot, "target");
        PrepareState(0, targetExe);
        var store = OfflineModeStateStore.Default;

        var runId = ReadRunId();
        using var handshake = await StartHelperHandshakeAsync(helperExe, runId);
        try
        {
            await handshake.SendStartAsync();
            var sw = Stopwatch.StartNew();
            var (code, output) = await handshake.WaitForExitWithOutputAsync(TimeSpan.FromSeconds(200));
            var elapsed = sw.Elapsed;
            if (code is not (HelperExitFail or HelperExitDeadlineKilled))
                throw new InvalidOperationException($"等待超时场景助手退出码应为 1/124，实际 {code}：{output}");
            if (elapsed < TimeSpan.FromSeconds(175))
                throw new InvalidOperationException($"助手在 {elapsed.TotalSeconds:F0} 秒即退出，未真实等待 180 秒。");
            if (elapsed > TimeSpan.FromSeconds(195))
                throw new InvalidOperationException($"助手等待 {elapsed.TotalSeconds:F0} 秒才退出，超过硬截止预期。");

            if (!OfflineHelperCompletion.TryConfirmExited(store))
                throw new InvalidOperationException("超时场景完成确认未执行。");
            if (!store.TryRead(out var timedOut, out var error)) throw new InvalidOperationException(error);
            if (timedOut.Status != OfflineModeStatus.Failed)
                throw new InvalidOperationException($"超时后状态应为 Failed，实际 {timedOut.Status}：{timedOut.Message}");
            if (timedOut.CpuSetChanges.Count != 0)
                throw new InvalidOperationException("超时场景不应残留 CPU Sets 记录。");
            Console.WriteLine($"场景 4 通过：助手真实等待 {elapsed.TotalSeconds:F0} 秒后按 180 秒超时失败退出。");
        }
        finally
        {
            await handshake.TryKillAsync();
        }
    }

    // ---------- 共用基础设施 ----------

    /// <summary>模拟主程序启动助手前的登记：启用脱机、写入偏好与运行代次。</summary>
    private static void PrepareState(ulong affinityMask, string targetExe)
    {
        if (File.Exists(OfflineStatePath)) File.Delete(OfflineStatePath);
        AppSettingsStore.Update(SettingsPath, s =>
        {
            s.CustomGameModeEnabled = true;
            s.CustomGameExecutablePath = targetExe;
            s.FrameModeActive = false;
            s.DwmRestartOnGameStart = false;
            s.GamePriorityEnabled = false;
            s.GameAffinityRuleEnabled = false;
            s.SingleCcdExcludeCpu0Enabled = false;
            s.DualCcdImmediateEnabled = false;
            s.DualCcdArmed = false;
            s.CloseToTrayEnabled = false;
        });
        var store = OfflineModeStateStore.Default;
        if (!store.TryUpdate(s =>
        {
            s.OfflineModeEnabled = true;
            s.Status = OfflineModeStatus.WaitingForGame;
            s.CancelRequested = false;
            s.SavedPreferences = new OfflineModePreferences
            {
                DwmRestartOnGameStart = false,
                FramePowerLockEnabled = false,
                GamePriorityEnabled = false,
                GameAffinityRuleEnabled = affinityMask != 0,
                GameAffinityRuleMask = affinityMask,
                SingleCcdExcludeCpu0Enabled = false,
                DualCcdImmediateEnabled = false,
                DualCcdArmed = false,
                DualCcdFrameEnabled = false,
                CloseToTrayEnabled = false,
            };
            s.CpuSetChanges.Clear();
            s.HelperProcessId = null;
            s.HelperStartTimeUtcTicks = null;
            s.HelperOperationCompleted = false;
            s.HelperRunId = Guid.NewGuid();
            s.HelperCompletionStatus = OfflineModeStatus.Ready;
        }, out var error)) throw new InvalidOperationException("准备脱机状态失败：" + error);
    }

    private static Guid ReadRunId()
    {
        if (!OfflineModeStateStore.Default.TryRead(out var state, out var error) || state.HelperRunId == Guid.Empty)
            throw new InvalidOperationException("读取运行代次失败：" + error);
        return state.HelperRunId;
    }

    private static string CreateFixtureExe(string fixtureRoot, string name)
    {
        var dir = Path.Combine(fixtureRoot, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var self = Path.Combine(AppContext.BaseDirectory, "BackendSmokeTest.exe");
        if (!File.Exists(self)) throw new InvalidOperationException("缺少测试 apphost。");
        var dest = Path.Combine(dir, name + ".exe");
        File.Copy(self, dest, overwrite: true);
        // apphost 需要同目录的 dll/deps/runtimeconfig 才能启动托管代码。
        var selfDir = Path.GetDirectoryName(self)!;
        foreach (var file in Directory.EnumerateFiles(selfDir))
        {
            var ext = Path.GetExtension(file);
            if (ext is ".dll" or ".json" or ".pdb")
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
        }
        return dest;
    }

    private static Process StartFixtureChild(string exe)
    {
        var start = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--offline-helper-e2e-child");
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动测试子进程。");
        try
        {
            var line = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            if (line != "E2E-CHILD-READY") throw new InvalidOperationException($"测试子进程未就绪：{line}");
            return process;
        }
        catch { TryKillProcess(process); process.Dispose(); throw; }
    }

    private static ProcessStartInfo BuildHelperStart(string helperExe, string pipeName, Guid runId)
    {
        var start = new ProcessStartInfo
        {
            FileName = helperExe,
            WorkingDirectory = Path.GetDirectoryName(helperExe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--offline-helper");
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add("--operation-id");
        start.ArgumentList.Add(runId.ToString("N"));
        start.ArgumentList.Add("--isolated-test-root");
        start.ArgumentList.Add(AppRoot);
        start.ArgumentList.Add("--isolated-test-id");
        start.ArgumentList.Add(_testId.ToString("D"));
        return start;
    }

    /// <summary>按主程序相同协议启动助手并完成 READY 握手与 PID/登记校验。</summary>
    private static async Task<HelperHandle> StartHelperHandshakeAsync(string helperExe, Guid runId)
    {
        var pipeName = "DeltaNFD_Offline_" + Guid.NewGuid().ToString("N");
        var start = BuildHelperStart(helperExe, pipeName, runId);
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("无法启动脱机助手。"); }
        catch { server.Dispose(); throw; }
        var handle = new HelperHandle(process, server);
        try
        {
            await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var clientPid) || clientPid != process.Id)
                throw new InvalidOperationException("管道客户端不是本次启动的助手。");
            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            var response = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (response != "READY")
                throw new InvalidOperationException($"助手未返回 READY：{response}");
            if (!OfflineModeStateStore.Default.TryRead(out var state, out var error) ||
                state.HelperRunId != runId || state.HelperProcessId != process.Id)
                throw new InvalidOperationException("助手登记未持久化：" + error);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private sealed class HelperHandle : IDisposable
    {
        private readonly Process _process;
        private readonly NamedPipeServerStream _server;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;
        private bool _started;
        private bool _disposed;

        public HelperHandle(Process process, NamedPipeServerStream server)
        {
            _process = process;
            _server = server;
            _stdout = process.StandardOutput.ReadToEndAsync();
            _stderr = process.StandardError.ReadToEndAsync();
        }

        public async Task SendStartAsync()
        {
            if (_started) return;
            _started = true;
            using var writer = new StreamWriter(_server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("START");
        }

        public async Task<int> WaitForExitAsync(TimeSpan timeout)
        {
            await _process.WaitForExitAsync().WaitAsync(timeout);
            return _process.ExitCode;
        }

        public async Task<(int code, string output)> WaitForExitWithOutputAsync(TimeSpan timeout)
        {
            await _process.WaitForExitAsync().WaitAsync(timeout);
            return (_process.ExitCode, await _stdout + await _stderr);
        }

        public string ReadOutput()
        {
            return (_stdout.IsCompletedSuccessfully ? _stdout.Result : "输出尚未结束") +
                (_stderr.IsCompletedSuccessfully ? _stderr.Result : "");
        }

        public async Task<string> ReadOutputAsync()
        {
            return await _stdout.WaitAsync(TimeSpan.FromSeconds(5)) + await _stderr.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task TryKillAsync()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: false);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _server.Dispose();
            try { TryKillProcess(_process); }
            finally { _process.Dispose(); }
        }
    }

    private static async Task WaitForStatusAsync(OfflineModeStatus expected, string message)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (OfflineModeStateStore.Default.TryRead(out var state, out _) && state.Status == expected) return;
            await Task.Delay(200);
        }
        throw new InvalidOperationException("超时：" + message);
    }

    private static async Task WaitForStatusMessageAsync(string fragment, string message)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (OfflineModeStateStore.Default.TryRead(out var state, out _) && state.Message.Contains(fragment, StringComparison.Ordinal))
                return;
            await Task.Delay(200);
        }
        throw new InvalidOperationException("超时：" + message);
    }

    private static async Task<(int code, string output)> WaitForExitCodeAsync(Process process, TimeSpan timeout)
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(timeout);
        return (process.ExitCode, await stdout + await stderr);
    }

    private static void AssertDefaultCpuSets(Process process, bool expectedSet, ulong mask, string context)
    {
        var api = Environment.OSVersion.Version.Build >= 22000
            ? OfflineCpuSetApi.Windows11Masks
            : OfflineCpuSetApi.Windows10Ids;
        var handle = CpuSets.OpenProcessForDefaultCpuSets(process.Id);
        if (handle == IntPtr.Zero) throw new InvalidOperationException($"{context}：无法打开进程读取 CPU Sets。");
        try
        {
            if (api == OfflineCpuSetApi.Windows11Masks)
            {
                if (!CpuSets.TryGetDefaultCpuSetMasks(handle, out var masks))
                    throw new InvalidOperationException($"{context}：读取默认 CPU Set 掩码失败。");
                if (!expectedSet)
                {
                    if (masks.Length != 0) throw new InvalidOperationException($"{context}：应无默认集合。");
                    return;
                }
                var actual = masks.Aggregate(0UL, (a, m) => a | m.Mask);
                if (actual != mask)
                    throw new InvalidOperationException($"{context}：掩码 {actual:X} != 预期 {mask:X}。");
            }
            else
            {
                if (!CpuSets.TryGetDefaultCpuSetIds(handle, out var ids))
                    throw new InvalidOperationException($"{context}：读取默认 CPU Set ID 失败。");
                if (!expectedSet)
                {
                    if (ids.Length != 0) throw new InvalidOperationException($"{context}：应无默认集合。");
                    return;
                }
                if (!OfflineCpuSetCatalog.TryRead(out var catalog, out var catalogError))
                    throw new InvalidOperationException($"{context}：读取 CPU Set 清单失败：" + catalogError);
                var expectedMask = ids.Aggregate(0UL, (a, id) => a | (catalog.FirstOrDefault(d => d.Id == id) is { } d ? 1UL << d.LogicalProcessorIndex : 0));
                if (expectedMask != mask)
                    throw new InvalidOperationException($"{context}：ID 换算掩码 {expectedMask:X} != 预期 {mask:X}。");
            }
        }
        finally { CpuSets.CloseHandleSafe(handle); }
    }

    private static void TryKillProcess(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: false);
        if (!process.WaitForExit(5000)) throw new TimeoutException("测试进程未确认退出：" + process.Id);
    }

    private static async Task CleanupAsync(string fixtureParent, string fixtureRoot)
    {
        var root = Path.GetFullPath(fixtureRoot);
        var parent = Path.GetFullPath(fixtureParent);
        if (!string.Equals(Path.GetDirectoryName(root), parent, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(root) != _testId.ToString("N"))
            throw new InvalidOperationException("拒绝越界清理测试目录。");
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("清理路径存在重解析目录，保留现场。");
        CheckCleanupTree(new DirectoryInfo(root));
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(root, recursive: true); break; }
            catch (IOException) when (attempt < 15) { await Task.Delay(200); }
            catch (UnauthorizedAccessException) when (attempt < 15) { await Task.Delay(200); }
        }
        if (Directory.Exists(root)) throw new IOException("测试目录清理未完成。");
    }

    private static void CheckCleanupTree(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("测试目录存在重解析条目，保留现场：" + entry.FullName);
            if (entry is DirectoryInfo child) CheckCleanupTree(child);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DeltaNFD.sln"))) return directory.FullName;
        throw new InvalidOperationException("无法定位仓库根。");
    }

    private static string ResolveHelperExe(string[] args)
    {
        var index = Array.IndexOf(args, "--offline-helper-exe");
        if (index < 0 || index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1]))
            throw new InvalidOperationException("请用 --offline-helper-exe 指定本次构建的绝对助手路径；禁止自动选取旧产物。");
        var path = Path.GetFullPath(args[index + 1]);
        if (!File.Exists(path) || Path.GetFileName(path) != "DeltaNFD.OfflineHelper.exe")
            throw new InvalidOperationException("指定助手产物不存在或名称不匹配。");
        var dll = Path.Combine(Path.GetDirectoryName(path)!, "DeltaNFD.OfflineHelper.dll");
        var probeStart = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        probeStart.ArgumentList.Add("--describe-isolated-test-protocol");
        using (var probe = Process.Start(probeStart) ?? throw new InvalidOperationException("无法探测助手隔离协议。"))
        {
            try
            {
                var output = probe.StandardOutput.ReadToEndAsync();
                var error = probe.StandardError.ReadToEndAsync();
                probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                if (probe.ExitCode != 0 || output.GetAwaiter().GetResult().Trim() != "DeltaNFD.OfflineHelper isolated-test-root v1")
                    throw new InvalidOperationException("助手不支持隔离测试协议，拒绝测试旧产物：" + error.GetAwaiter().GetResult());
            }
            finally { TryKillProcess(probe); }
        }
        using var exeStream = File.OpenRead(path);
        Console.WriteLine("指定助手：" + path + "\nEXE SHA256=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(exeStream)));
        if (File.Exists(dll))
        {
            using var dllStream = File.OpenRead(dll);
            Console.WriteLine("DLL SHA256=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(dllStream)));
        }
        else Console.WriteLine("使用通过隔离协议探针的单文件助手，无外置 DLL。");
        return path;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

    // ---------- 子进程入口：完整硬亲和性 + 常驻待命 ----------

    public static void RunChild()
    {
        using var self = Process.GetCurrentProcess();
        try
        {
            var count = Environment.ProcessorCount;
            var mask = count >= 64 ? ~0UL : (1UL << count) - 1UL;
            self.ProcessorAffinity = new IntPtr(unchecked((long)mask));
        }
        catch { }
        Console.WriteLine("E2E-CHILD-READY");
        Console.Out.Flush();
        Thread.Sleep(TimeSpan.FromMinutes(10));
    }
}
