using System.Collections.Concurrent;
using System.Diagnostics;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

/// <summary>
/// CPU/帧格后端状态通道检查（T4）：
/// 1) 后端空闲时主动推送 status 帧，前端订阅可在无 invoke 的情况下收到真实状态快照；
/// 2) 目标代次/PID/启动时间/可执行路径在操作前由后端复核（自定义目标实机验证）；
/// 3) 监控可重复启停；取消/断线不会把失败折算成成功、不破坏后续调用；
/// 4) reconcile/recover 分类正确；未知范围被拒绝；
/// 5) 前端监控已改为消费后端状态（静态守卫：源文件不再自行枚举目标进程）。
/// 全部使用隔离数据根与自建子进程，不运行真实游戏优化、不改真实电源/DWM/显卡。
/// </summary>
internal static class CpuFrameStatusChecks
{
    private static int _passed;

    public static async Task RunAsync()
    {
        _passed = 0;
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-cpuframe", id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", id.ToString("D"));

        Process? child = null;
        var selfExe = Environment.ProcessPath ?? throw new Exception("无法确定测试自身路径。");
        void Check(bool value, string name) { if (!value) throw new Exception("CPU/帧格状态检查失败：" + name); _passed++; }

        try
        {
            // 自定义目标 = 测试自身可执行文件；随后启动一个同路径子进程作为可控“目标”。
            AppSettingsStore.Update(s =>
            {
                s.CustomGameModeEnabled = true;
                s.CustomGameExecutablePath = selfExe;
            });
            child = Process.Start(new ProcessStartInfo(selfExe, "--game-target-test-child")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new Exception("无法启动目标测试子进程。");

            await using var client = await BackendClient.StartAsync(BackendModules.CpuFrame, CancellationToken.None);

            var queue = new ConcurrentQueue<CpuFrameStatusSnapshot>();
            using var pumpCts = new CancellationTokenSource();
            var pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var status in client.SubscribeStatusAsync(pumpCts.Token))
                    {
                        if (status.Data is { } data)
                        {
                            try { queue.Enqueue(BackendJson.Deserialize<CpuFrameStatusSnapshot>(data)); }
                            catch { /* 忽略无法解析的快照，不据此判成功 */ }
                        }
                    }
                }
                catch { /* 泵随连接结束 */ }
            });

            // 1) 空闲推送：不发送任何 invoke 也能收到 status 帧。
            var idle = await WaitAsync(queue, _ => true, TimeSpan.FromSeconds(8));
            Check(idle is not null, "后端空闲时主动推送状态快照（订阅生效）");
            Check(idle!.Module == BackendModules.CpuFrame, "快照模块标识为 cpuframe");
            Check(!idle.MonitorRunning, "未显式启动时监控为停止（不自动跑真实优化）");

            // 2) 启动监控并复核目标身份。
            Check((await client.InvokeAsync(BackendCommands.CpuFrameMonitorStart, new { }, null, CancellationToken.None)).Status
                == BackendResultStatuses.Ok, "cpuframe.monitor.start 成功");
            var running = await WaitAsync(queue, s => s.MonitorRunning, TimeSpan.FromSeconds(8));
            Check(running is not null, "启动后快照报告监控运行中");
            var identified = await WaitAsync(queue, s => s.GameRunning && s.GamePid is > 0, TimeSpan.FromSeconds(15));
            Check(identified is not null, "监控识别到自定义目标进程");
            Check(SamePath(identified!.TargetExecutablePath, selfExe), "快照目标路径与设置一致");
            Check(identified.TargetGeneration >= 1, "快照携带目标代次");
            Check(SamePath(identified.GameExecutablePath, selfExe), "快照游戏映像路径经后端实时读取");
            using (var live = Process.GetProcessById(identified.GamePid!.Value))
            {
                Check(live.StartTime.ToUniversalTime().Ticks == identified.GameStartTimeUtcTicks,
                    "快照 PID 与启动时间指向同一真实进程（操作前复核依据）");
            }

            // 3) 重复启停。
            for (var i = 0; i < 2; i++)
            {
                Check((await client.InvokeAsync(BackendCommands.CpuFrameMonitorStop, new { }, null, CancellationToken.None)).Status == BackendResultStatuses.Ok,
                    $"第 {i + 1} 次停止监控成功");
                Check((await client.InvokeAsync(BackendCommands.CpuFrameMonitorStart, new { }, null, CancellationToken.None)).Status == BackendResultStatuses.Ok,
                    $"第 {i + 1} 次启动监控成功");
            }

            // 4) 取消不破坏后续调用：已取消的令牌在进入前拒绝，之后仍能正常调用。
            try
            {
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await client.InvokeAsync(BackendCommands.CpuFrameStatus, new { }, null, cancelled.Token);
                throw new Exception("已取消的令牌仍执行了命令");
            }
            catch (OperationCanceledException) { Check(true, "已取消令牌在发送前被拒绝"); }
            Check((await client.InvokeAsync(BackendCommands.CpuFrameStatus, new { }, null, CancellationToken.None)).Status == BackendResultStatuses.Ok,
                "取消之后后续调用仍正常");

            // 5) reconcile / recover 分类。
            var reconcile = await client.InvokeAsync(BackendCommands.CpuFrameReconcile, new { }, null, CancellationToken.None);
            Check(reconcile.Status == BackendResultStatuses.Ok && reconcile.Data is not null, "cpuframe.reconcile 返回 ok");
            var snapshot = BackendJson.Deserialize<ReconcileSnapshot>(reconcile.Data!.Value);
            Check(snapshot.Module == BackendModules.CpuFrame, "核对快照模块为 cpuframe");
            Check(snapshot.RecoveryScopes.Any(s => s.Id == "priority") && snapshot.RecoveryScopes.Any(s => s.Id == "affinity")
                && snapshot.RecoveryScopes.Any(s => s.Id == "ccd") && snapshot.RecoveryScopes.Any(s => s.Id == "power"),
                "核对返回优先级/亲和性/CCD/电源恢复范围");
            var bad = await client.InvokeAsync(BackendCommands.CpuFrameRecover, new RecoverArgs { Scope = "definitely-not-a-scope" }, null, CancellationToken.None);
            Check(bad.Status == BackendResultStatuses.Failed, "未知恢复范围被拒绝");
            var timerRecover = await client.InvokeAsync(BackendCommands.CpuFrameRecover, new RecoverArgs { Scope = "timer" }, null, CancellationToken.None);
            Check(timerRecover.Status == BackendResultStatuses.Ok, "无待恢复项的合法范围返回成功（如实不误报阻塞）");

            // 5b) T4 扩展迁移命令的只读/无副作用路径（不触发任何真实系统写入）。
            var topo = await client.InvokeAsync(BackendCommands.CpuFrameTopology, new { }, null, CancellationToken.None);
            Check(topo.Status == BackendResultStatuses.Ok && topo.Data is not null, "cpuframe.topology 返回 ok");
            var topology = BackendJson.Deserialize<CpuTopology>(topo.Data!.Value);
            Check(topology.LogicalProcessors > 0, "拓扑对象可反序列化且逻辑处理器数有效");

            var affCheck = await client.InvokeAsync(BackendCommands.CpuFrameAffinityCheck,
                new CpuFrameAffinityCheckArgs { Mask = 0 }, null, CancellationToken.None);
            Check(affCheck.Status == BackendResultStatuses.Ok
                && BackendJson.Deserialize<CpuFrameAffinityCheckResult>(affCheck.Data!.Value).Ok, "亲和性归属复核（掩码 0）通过");
            var affPending = await client.InvokeAsync(BackendCommands.CpuFrameAffinityPending, new { }, null, CancellationToken.None);
            Check(affPending.Status == BackendResultStatuses.Ok
                && !BackendJson.Deserialize<CpuFramePendingResult>(affPending.Data!.Value).Pending, "无待还原的亲和性记录");

            var memLimit = await client.InvokeAsync(BackendCommands.CpuFrameMemoryLimitDetect, new { }, null, CancellationToken.None);
            Check(memLimit.Status == BackendResultStatuses.Ok && memLimit.Data is not null, "cpuframe.memorylimit.detect 返回 ok（只读）");

            var prioOff = await client.InvokeAsync(BackendCommands.CpuFramePriorityApply,
                new CpuFrameToggleArgs { Enabled = false }, null, CancellationToken.None);
            Check(prioOff.Status == BackendResultStatuses.Ok, "priority.apply(false) 无目标时安全返回 ok");

            foreach (var retired in new[] {
                new CpuFrameBoostArgs { CoreParking = true, Timer = true },
                new CpuFrameBoostArgs { CoreParking = true, EcoQos = true, Timer = true } })
            {
                var refused = await client.InvokeAsync(BackendCommands.CpuFrameBoostApply, retired, null, CancellationToken.None);
                Check(refused.Status == BackendResultStatuses.Failed && refused.Message.Contains("已移除"),
                    "旧协议开启退休功能时明确拒绝整个请求");
                var after = await client.InvokeAsync(BackendCommands.CpuFrameStatus, new { }, null, CancellationToken.None);
                Check(after.Status == BackendResultStatuses.Ok && after.Data is not null &&
                    !BackendJson.Deserialize<CpuFrameStatusSnapshot>(after.Data.Value).TimerActive,
                    "退休请求被拒绝后不会顺带启动计时器");
            }
            var boostOff = await client.InvokeAsync(BackendCommands.CpuFrameBoostApply,
                new CpuFrameBoostArgs { CoreParking = false, EcoQos = false, Timer = false }, null, CancellationToken.None);
            Check(boostOff.Status == BackendResultStatuses.Ok, "旧协议的关闭请求继续接受");

            // 6) 目标切换：后端在下一次 tick 同步新目标并自增代次（重新复核路径）。
            var notRunning = Path.Combine(root, "not-running-target.exe");
            File.WriteAllText(notRunning, "not a real executable");
            AppSettingsStore.Update(s => s.CustomGameExecutablePath = notRunning);
            var switched = await WaitAsync(queue,
                s => s.TargetGeneration > identified.TargetGeneration && SamePath(s.TargetExecutablePath, notRunning),
                TimeSpan.FromSeconds(15));
            if (switched is null)
            {
                var diag = await client.InvokeAsync(BackendCommands.CpuFrameStatus, new { }, null, CancellationToken.None);
                var s = diag.Data is { } d ? BackendJson.Deserialize<CpuFrameStatusSnapshot>(d) : null;
                Console.WriteLine($"  [诊断] 切换超时：gen={s?.TargetGeneration} display={s?.TargetDisplayName} path={s?.TargetExecutablePath} err={s?.LastError}");
            }
            Check(switched is not null, "目标切换后后端代次自增并改为新路径");
            Check(!switched!.GameRunning, "新目标无匹配进程时报告未运行（不把偏好当生效）");

            var timerWaiting = await client.InvokeAsync(BackendCommands.CpuFrameBoostApply,
                new CpuFrameBoostArgs { Timer = true }, null, CancellationToken.None);
            Check(timerWaiting.Status == BackendResultStatuses.Ok, "计时器可配置为随游戏启动，不把等待状态当失败");
            var waitingStatus = await client.InvokeAsync(BackendCommands.CpuFrameStatus, new { }, null, CancellationToken.None);
            Check(waitingStatus.Status == BackendResultStatuses.Ok && waitingStatus.Data is not null &&
                !BackendJson.Deserialize<CpuFrameStatusSnapshot>(waitingStatus.Data.Value).TimerActive,
                "无目标游戏时仍不请求定时器");
            Check((await client.InvokeAsync(BackendCommands.CpuFrameBoostApply,
                new CpuFrameBoostArgs { Timer = false }, null, CancellationToken.None)).Status == BackendResultStatuses.Ok,
                "仅计时器的新请求可正常关闭");

            // 7) 断线：杀掉后端进程后调用必须如实失败，不能折算成功。
            // Only disconnect the process launched by this isolated test. Never enumerate and
            // terminate other application instances just because their executable name matches.
            var ownedBackend = (Process)typeof(BackendClient).GetField("_process",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(client)!;
            if (!ownedBackend.HasExited) ownedBackend.Kill();
            await Task.Delay(500);
            BackendResult afterKill;
            try { afterKill = await client.InvokeAsync(BackendCommands.CpuFrameStatus, new { }, null, CancellationToken.None); }
            catch (BackendContractException ex) { afterKill = BackendResult.Failed("thrown", ex.Message); }
            Check(afterKill.Status != BackendResultStatuses.Ok, "后端断线后调用不被折算为成功");
            try { await client.ReleaseExitedSessionAsync(); }
            catch (BackendContractException) { /* 进程身份无法确认时保留会话，由人工核对处理 */ }

            pumpCts.Cancel();
            try { await pump.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }

            // 8) 静态守卫：前端监控不再自行枚举目标进程。
            GuardFrontendMonitorIsStatusDriven();
        }
        finally
        {
            if (child is not null)
            {
                try { if (!child.HasExited) child.Kill(); } catch { }
                try { await child.WaitForExitAsync(); } catch { }
                child.Dispose();
            }
            try
            {
                var full = Path.GetFullPath(root);
                var temp = Path.GetFullPath(Path.GetTempPath());
                if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) Directory.Delete(full, recursive: true);
            }
            catch { /* 清理失败不影响结果 */ }
        }

        Console.WriteLine($"CPU/帧格状态通道检查通过（{_passed} 项断言）：空闲状态推送/订阅、目标代次/PID/启动时间/路径复核、"
            + "重复启停、取消与断线如实失败、reconcile/recover 分类、前端监控状态化。隔离数据与自建子进程，未运行真实优化。");
    }

    private static async Task<CpuFrameStatusSnapshot?> WaitAsync(
        ConcurrentQueue<CpuFrameStatusSnapshot> queue, Func<CpuFrameStatusSnapshot, bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            while (queue.TryDequeue(out var candidate))
            {
                if (predicate(candidate)) return candidate;
            }
            await Task.Delay(100);
        }
        return null;
    }

    private static bool SamePath(string left, string right)
    {
        if (string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right)) return true;
        try
        {
            return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// 前端监控必须消费后端状态，而不是自行枚举目标进程（否则就是重复监控）。
    /// 源文件不可定位时如实记为 SKIP，不折算通过。
    /// </summary>
    private static void GuardFrontendMonitorIsStatusDriven()
    {
        var file = FindRepositoryFile(Path.Combine("src", "DeltaNFD", "Services", "GameProcessMonitor.cs"));
        if (file is null)
        {
            Console.WriteLine("  [SKIP] 未定位 GameProcessMonitor.cs（工作目录不在仓库内），前端监控状态化未做静态校验。");
            return;
        }
        var text = File.ReadAllText(file);
        if (text.Contains("GameTargetService.Default.GetProcesses", StringComparison.Ordinal))
            throw new Exception("CPU/帧格状态检查失败：前端监控仍在自行枚举目标进程（重复监控）");
        if (!text.Contains("BackendCpuFrameService", StringComparison.Ordinal))
            throw new Exception("CPU/帧格状态检查失败：前端监控未订阅后端状态通道");
        _passed++;

        // 前端不再执行被迁移的写入：FrameService 不直接请求计时器/热点环/内存清理；GameProcessService 不直接设优先级。
        var frame = FindRepositoryFile(Path.Combine("src", "DeltaNFD", "Services", "FrameService.cs"));
        var gameProcess = FindRepositoryFile(Path.Combine("src", "DeltaNFD", "Services", "GameProcessService.cs"));
        var gpuReload = FindRepositoryFile(Path.Combine("src", "DeltaNFD", "Services", "Backends", "BackendGpuReload.cs"));
        if (frame is null || gameProcess is null || gpuReload is null)
        {
            Console.WriteLine("  [SKIP] 未定位 FrameService/GameProcessService/BackendGpuReload，前端写入迁移未做静态校验。");
            return;
        }

        var frameText = File.ReadAllText(frame);
        if (frameText.Contains("ProcessPerf.", StringComparison.Ordinal) || frameText.Contains("CoreParkingLoop", StringComparison.Ordinal)
            || frameText.Contains("MemoryPurge.", StringComparison.Ordinal))
            throw new Exception("CPU/帧格状态检查失败：FrameService 仍在本地执行计时器/核心常驻/内存清理");
        if (!frameText.Contains("BackendCpuFrameService", StringComparison.Ordinal))
            throw new Exception("CPU/帧格状态检查失败：FrameService 未调用 CPU/帧格后端");
        _passed++;

        var gameText = File.ReadAllText(gameProcess);
        if (gameText.Contains("PriorityClass", StringComparison.Ordinal))
            throw new Exception("CPU/帧格状态检查失败：GameProcessService 仍在本地设置进程优先级");
        if (!gameText.Contains("BackendCpuFrameService", StringComparison.Ordinal))
            throw new Exception("CPU/帧格状态检查失败：GameProcessService 未调用 CPU/帧格后端");
        _passed++;

        // 显卡重载执行已迁入显卡后端（前端仅保留黑屏兜底与亮屏确认）。
        if (!File.ReadAllText(gpuReload).Contains("GpuSpoofReload", StringComparison.Ordinal))
            throw new Exception("CPU/帧格状态检查失败：显卡重载未走显卡后端");
        _passed++;
    }

    private static string? FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        for (var i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
