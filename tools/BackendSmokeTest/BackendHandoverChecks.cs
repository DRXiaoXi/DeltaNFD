using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

/// <summary>
/// T5 统一排空/交接门禁检查：使用临时测试后端（本测试 EXE 复制为官方后端名后运行夹具宿主），
/// 在隔离数据根内证明：
///   (0) 只是「有活动后端」不是阻塞理由——空闲后端可正常排空后交接；
///   (a) 有在途、不可中断操作的活后端不能被排空，交接被阻止且保留未知证据；
///   (b) 连接中断（结果未知）时交接被阻止并保留证据；
///   (c) 已确认原后端退出 + 完成证据核对后，交接才放行；
///   (d) 主后端 PID 已退出但仍有存活的子进程/安装器时，交接仍被阻止。
/// 全部为内存夹具与隔离数据，不做任何真实系统操作、不杀真实游戏或系统进程。
/// </summary>
internal static class BackendHandoverChecks
{
    private static int _passed;

    public static async Task RunAsync(bool deploymentOnly = false)
    {
        _passed = 0;
        var testId = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-handover", testId.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), testId.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, testId);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", testId.ToString("D"));

        var backendsRoot = Path.Combine(root, "backends");
        Environment.SetEnvironmentVariable(BackendPaths.BackendRootEnvironmentVariable, backendsRoot);
        try
        {
            var fixture = new FixtureBackendRoot(backendsRoot);
            fixture.Install(BackendModules.Ace);
            fixture.Install(BackendModules.Runtime);
            fixture.Install(BackendModules.Gpu);
            fixture.Install(BackendModules.CpuFrame);
            fixture.Install(BackendModules.System);

            if (!deploymentOnly)
            {
            await PositiveControlAsync(fixture);
            await RetainedStaticBackupAsync(root);
            await SendCancellationBlocksAsync();
            await InFlightBlocksAsync(fixture);
            await DisconnectRetainsEvidenceAsync(fixture);
            await LiveChildBlocksAsync(fixture);
            await CpuRestoreFailureBlocksAsync();
            await CpuRestoreTimeoutBlocksAsync();
            await IdleCpuDisconnectBlocksAsync();
            await ReadOnlyFailureDoesNotBlockAsync();
            await PartialStopsBeforeReviewAsync();
            await ChildrenRecheckAsync();
            KnownChildrenMustRefresh();
            await GateBlocksCallsAsync();
            await AdmittedSendDrainsAsync();
            await DefenderBatchDoesNotSelfLockAsync();
            ConfirmedRestoreMustMatch();
            await MemoryOnlyUnknownStillBlocksAsync();
            await DefenderSessionChecks.RunAsync(Check);
            await FrameEntryRecoveryChecks.RunAsync(Check);
            await OfflineModeTransitionChecks.RunAsync(Check);
            await AceCleanupFlowChecks.RunAsync(Check);
            }
            await DeploymentHandoverChecks.RunAsync(Check);

            var final = await BackendLifecycle.PrepareHandoverAsync("最终交接检查", TimeSpan.FromSeconds(5));
            Check(final.Count == 0, "全部确认退出并核对后，统一交接放行：" + string.Join("；", final)
                + string.Join("；", BackendUnconfirmedStore.ReviewAll().Select(r => r.Description)));

            Console.WriteLine($"Backend handover checks passed ({_passed} assertions): idle backend drains; " +
                "in-flight non-cancellable operation blocks handover and retains evidence; disconnect blocks and retains evidence; " +
                "handover proceeds only after confirmed exit + evidence reconciliation; live child blocks even with a dead backend PID. " +
                "Temporary test backends, isolated data only; no real operation, no real process killed.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackendPaths.BackendRootEnvironmentVariable, null);
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", null);
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", null);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task RetainedStaticBackupAsync(string root)
    {
        var backup = Path.Combine(root, "optimize_backup.json");
        File.WriteAllText(backup, "{\"fixture\":\"original\"}");
        var originalBackup = File.ReadAllBytes(backup);
        var client = await BackendClient.StartAsync(BackendModules.System, default);
        var first = await client.InvokeAsync(BackendCommands.SystemBxApply, new { testRetainedBackup = 1 }, null, default);
        Check(first.Status == BackendResultStatuses.Ok && first.PendingRestore, "兼容旧后端成功并保留静态备份的结果");
        Check(!BackendUnconfirmedStore.PendingModules().Contains(BackendModules.System), "成功静态备份不进入结果未知门禁");
        var second = await client.InvokeAsync(BackendCommands.SystemBxGearApply, new { testRetainedBackup = 1 }, null, default);
        Check(second.Status == BackendResultStatuses.Ok, "保留备份后下一项优化仍可执行");

        var source = BackendUnconfirmedStore.RecordPathForTests(BackendModules.System);
        using var process = (Process)typeof(BackendClient).GetField("_process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
        var legacy = JsonSerializer.Serialize(new { Module = BackendModules.System, Phase = "unconfirmed", Reason = "ok",
            Command = BackendCommands.SystemBxApply, RequestId = Guid.NewGuid().ToString("D"), SessionId = Guid.NewGuid().ToString("D"),
            Pid = process.Id, StartTicks = process.StartTime.ToUniversalTime().Ticks, Path = BackendPaths.Resolve(BackendModules.System) });
        File.WriteAllText(source, legacy);
        var exited = await BackendLifecycle.PrepareHandoverAsync("旧版静态优化退出", TimeSpan.FromSeconds(10));
        Check(exited.Count == 0 && client.ExitConfirmed, "旧版明确成功的静态记录归档后实际排空仍执行，后端退出");
        Check(File.ReadAllBytes(backup).SequenceEqual(originalBackup), "归档不还原系统、不删除或覆盖原值备份");
        var archive = Directory.GetFiles(Path.Combine(root, "official-backend-reviewed"), "system-completed-static-backup-*.json")
            .Single(p => !p.EndsWith(".outcome.json", StringComparison.Ordinal));
        Check(File.ReadAllText(archive) == legacy, "旧误判记录逐字保留并归档");
    }

    /// <summary>(0) 正向对照：单纯有活动后端不是阻塞理由，可排空后交接。</summary>
    private static async Task PositiveControlAsync(FixtureBackendRoot fixture)
    {
        var client = await BackendClient.StartAsync(BackendModules.Gpu, CancellationToken.None);
        var quick = await client.InvokeAsync(BackendCommands.GpuSpoofStatus, new { }, null, CancellationToken.None);
        Check(quick.Status == BackendResultStatuses.Ok, "临时测试后端可完成只读命令");
        Check(BackendSessions.Snapshot().Any(c => ReferenceEquals(c, client)), "活动会话已登记");

        var failed = await BackendLifecycle.PrepareHandoverAsync("正向对照", TimeSpan.FromSeconds(10));
        Check(failed.Count == 0, "空闲活动后端可正常排空，交接放行");
        Check(client.ExitConfirmed, "排空后测试后端进程已退出");
        Check(BackendUnconfirmedStore.PendingModules().Count == 0, "正常排空不留下未知证据");

        var idle = await BackendClient.StartAsync(BackendModules.Gpu, CancellationToken.None);
        var second = await BackendLifecycle.PrepareHandoverAsync("正向对照二", TimeSpan.FromSeconds(10));
        Check(second.Count == 0, "第二次空闲活动后端同样可排空");
        Check(idle.ExitConfirmed, "第二次排空后进程退出");
    }

    private static async Task CpuRestoreFailureBlocksAsync()
    {
        Environment.SetEnvironmentVariable("DELTANFD_TEST_RESTORE_FAIL_ONCE", "1");
        BackendClient client;
        try { client = await BackendClient.StartAsync(BackendModules.CpuFrame, default); }
        finally { Environment.SetEnvironmentVariable("DELTANFD_TEST_RESTORE_FAIL_ONCE", null); }
        var failed = await BackendLifecycle.PrepareHandoverAsync("模拟还原失败", TimeSpan.FromSeconds(5));
        Check(failed.Contains(BackendModules.CpuFrame) && client.ExitConfirmed, "还原失败保留证据，同时允许后端停止供恢复核对");
        await AcknowledgeAsync(BackendModules.CpuFrame);
        client = await BackendClient.StartAsync(BackendModules.CpuFrame, default);
        var retried = await BackendLifecycle.PrepareHandoverAsync("重试还原", TimeSpan.FromSeconds(5));
        Check(retried.Count == 0 && client.ExitConfirmed, "还原重试成功才允许退出");
    }

    private static async Task CpuRestoreTimeoutBlocksAsync()
    {
        Environment.SetEnvironmentVariable("DELTANFD_TEST_RESTORE_DELAY_MS", "3500");
        Environment.SetEnvironmentVariable("DELTANFD_TEST_RESTORE_CHILD_MS", "6000");
        BackendClient client;
        try { client = await BackendClient.StartAsync(BackendModules.CpuFrame, default); }
        finally
        {
            Environment.SetEnvironmentVariable("DELTANFD_TEST_RESTORE_DELAY_MS", null);
            Environment.SetEnvironmentVariable("DELTANFD_TEST_RESTORE_CHILD_MS", null);
        }
        var failed = await BackendLifecycle.PrepareHandoverAsync("模拟还原超时", TimeSpan.FromMilliseconds(300));
        Check(failed.Contains(BackendModules.CpuFrame) && client.RequiresAttention
            || client.ExitConfirmed && client.CpuRestoreConfirmed && !BackendUnconfirmedStore.PendingModules().Contains(BackendModules.CpuFrame),
            "还原超时保留未知；若迟到结果已复核且进程已退出，可收敛但不得重建伪未知记录：failed="
            + string.Join(",", failed) + "; attention=" + client.RequiresAttention + "; exited=" + client.ExitConfirmed
            + "; restoreConfirmed=" + client.CpuRestoreConfirmed + "; records=" + string.Join(",", BackendUnconfirmedStore.PendingModules()));
        await WaitUntilAsync(() => client.ExitConfirmed, TimeSpan.FromSeconds(15), "超时夹具完成后退出");
        await client.QuiesceAsync(TimeSpan.FromSeconds(5));
        Check(!BackendUnconfirmedStore.PendingModules().Contains(BackendModules.CpuFrame), "迟到的完整还原成功自动归档本请求证据："
            + string.Join("；", BackendUnconfirmedStore.ReviewAll().Select(r => r.Description)));
        Check(!client.RequiresAttention, "迟到还原解除对应会话的恢复义务");
        await client.QuiesceAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task IdleCpuDisconnectBlocksAsync()
    {
        var client = await BackendClient.StartAsync(BackendModules.CpuFrame, default);
        await client.InvokeAsync(BackendCommands.CpuFrameMonitorStart, new { }, null, default);
        var pipe = (IDisposable)typeof(BackendClient).GetField("_pipe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
        pipe.Dispose();
        await WaitUntilAsync(() => client.RequiresAttention && client.ExitConfirmed
            && BackendUnconfirmedStore.PendingModules().Contains(BackendModules.CpuFrame),
            TimeSpan.FromSeconds(15), "空闲监控断线保留证据");
        Check(BackendUnconfirmedStore.PendingModules().Contains(BackendModules.CpuFrame), "无 UI 请求时监控断线仍阻止静默重启");
        await AcknowledgeAsync(BackendModules.CpuFrame);
    }

    private static async Task SendCancellationBlocksAsync()
    {
        var client = await BackendClient.StartAsync(BackendModules.Gpu, default);
        var gate = (SemaphoreSlim)typeof(BackendClient).GetField("_writeGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
        await gate.WaitAsync();
        using var cancel = new CancellationTokenSource();
        try
        {
            var request = client.InvokeAsync(BackendCommands.GpuSpoofApply, new { }, null, cancel.Token);
            await WaitUntilAsync(() => BackendUnconfirmedStore.PendingModules().Contains(BackendModules.Gpu), TimeSpan.FromSeconds(5), "发送前意图已落盘");
            cancel.Cancel();
            var result = await request.WaitAsync(TimeSpan.FromSeconds(5));
            Check(result.Status == BackendResultStatuses.Failed, "发送阶段取消如实失败");
            Check(client.RequiresAttention, "发送阶段取消标记会话不可信");
            Check(BackendUnconfirmedStore.PendingModules().Contains(BackendModules.Gpu), "发送阶段取消保留证据");
            var blocked = false;
            try { await client.DisposeAsync(); } catch (BackendContractException) { blocked = true; }
            Check(blocked, "不得通过 Dispose 强杀未知后端");
        }
        finally { gate.Release(); }
        var pipe = (IDisposable)typeof(BackendClient).GetField("_pipe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
        pipe.Dispose(); // Test-owned fixture exits on disconnect; no real operation was dispatched.
        await WaitUntilAsync(() => client.ExitConfirmed, TimeSpan.FromSeconds(10), "测试后端断线退出");
        await AcknowledgeAsync(BackendModules.Gpu);
    }

    private static void KnownChildrenMustRefresh()
    {
        var path = BackendUnconfirmedStore.RecordPathForTests(BackendModules.Gpu);
        using var self = Process.GetCurrentProcess();
        File.WriteAllText(path, JsonSerializer.Serialize(new { Module = BackendModules.Gpu, Pid = int.MaxValue,
            StartTicks = 1L, Path = Environment.ProcessPath, ChildrenUnknown = false,
            Children = new[] { new { Name = "old-parent", Pid = int.MaxValue - 1, StartTicks = 1L } } }));
        BackendUnconfirmedStore.DescendantsForTests = pid => pid == int.MaxValue - 1
            ? ([new BackendUnconfirmedStore.ChildProcess("new-orphan", self.Id, self.StartTime.ToUniversalTime().Ticks)], true) : ([], true);
        try
        {
            Check(BackendUnconfirmedStore.RefreshChildren(BackendModules.Gpu)
                && BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.Gpu).ChildrenAlive,
                "已知子进程后来启动的孤立安装器也纳入重新核对");
            BackendUnconfirmedStore.DescendantsForTests = _ => ([], false);
            Check(!BackendUnconfirmedStore.RefreshChildren(BackendModules.Gpu)
                && BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.Gpu).ChildrenUnknown,
                "曾经成功的查询再次失败时重新标为未知，不依赖过时快照放行");
        }
        finally { BackendUnconfirmedStore.DescendantsForTests = null; File.Delete(path); }
    }

    /// <summary>(a) 在途、不可中断操作：活后端无法排空，交接被阻止并保留证据。</summary>
    private static async Task InFlightBlocksAsync(FixtureBackendRoot fixture)
    {
        var client = await BackendClient.StartAsync(BackendModules.Ace, CancellationToken.None);
        var inFlight = Task.Run(() => client.InvokeAsync(
            BackendCommands.AceClean, new { testDelayMs = 4000 }, null, CancellationToken.None));
        await Task.Delay(400);

        var failed = await BackendLifecycle.PrepareHandoverAsync("在途操作", TimeSpan.FromSeconds(1));
        Check(failed.Contains(BackendModules.Ace), "有在途操作的活后端不能排空，交接被阻止");
        Check(BackendUnconfirmedStore.PendingModules().Contains(BackendModules.Ace), "排空失败保留未知证据");

        try { await inFlight.WaitAsync(TimeSpan.FromSeconds(20)); } catch { /* 结果不是本断言重点 */ }
        await WaitUntilAsync(() => client.ExitConfirmed, TimeSpan.FromSeconds(15), "测试后端的在途操作结束后退出");
        var archive = await AcknowledgeAsync(BackendModules.Ace);
        Check(File.Exists(archive), "核对后归档未知证据");
    }

    /// <summary>(b) 连接中断：结果未知，交接被阻止并保留证据；(c) 核对后才放行。</summary>
    private static async Task DisconnectRetainsEvidenceAsync(FixtureBackendRoot fixture)
    {
        var client = await BackendClient.StartAsync(BackendModules.Runtime, CancellationToken.None);
        var inFlight = Task.Run(() => client.InvokeAsync(
            BackendCommands.RuntimeGuardDisable, new { testDelayMs = 8000 }, null, CancellationToken.None));
        await Task.Delay(500);

        Check(!client.RequiresAttention, "在途请求尚未失败时不误报未知结果");
        Check(KillFixture(BackendModules.Runtime), "断开临时测试后端（模拟结果未知的断线）");

        await WaitUntilAsync(
            () => client.RequiresAttention && BackendUnconfirmedStore.PendingModules().Contains(BackendModules.Runtime),
            TimeSpan.FromSeconds(15), "断线被登记为未知证据");
        Check(client.ExitConfirmed, "断线后原后端已退出");
        await inFlight.WaitAsync(TimeSpan.FromSeconds(20));

        var blocked = await BackendLifecycle.PrepareHandoverAsync("断线未知", TimeSpan.FromSeconds(2));
        Check(blocked.Contains(BackendModules.Runtime), "结果未知时交接被阻止");

        // 未完成核对前不能解除阻塞。
        var review = BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.Runtime);
        Check(review.Description.Contains("连接中断", StringComparison.Ordinal), "未知原因如实展示为连接中断：" + review.Description);

        try { await inFlight.WaitAsync(TimeSpan.FromSeconds(20)); } catch { /* 断线后请求以失败落定 */ }

        var archive = await AcknowledgeAsync(BackendModules.Runtime);
        Check(File.Exists(archive), "确认退出并核对后归档证据");
        var after = await BackendLifecycle.PrepareHandoverAsync("核对后", TimeSpan.FromSeconds(5));
        Check(after.Count == 0, "完成核对后交接放行");
    }

    /// <summary>(d) 主后端 PID 已退出但仍有存活子进程/安装器：交接仍被阻止。</summary>
    private static async Task LiveChildBlocksAsync(FixtureBackendRoot fixture)
    {
        using var self = Process.GetCurrentProcess();
        var recordPath = BackendUnconfirmedStore.RecordPathForTests(BackendModules.Gpu);
        var record = JsonSerializer.Serialize(new
        {
            Module = BackendModules.Gpu,
            Pid = int.MaxValue,
            StartTicks = 1L,
            Path = Environment.ProcessPath!,
            CreatedUtc = DateTimeOffset.UtcNow,
            Command = BackendCommands.GpuSpoofApply,
            Children = new[]
            {
                new { Name = "vc_redist.x64.exe", Pid = self.Id, StartTicks = self.StartTime.ToUniversalTime().Ticks },
            },
        });
        File.WriteAllText(recordPath, record);

        var blocked = await BackendLifecycle.PrepareHandoverAsync("存活子进程", TimeSpan.FromSeconds(2));
        Check(blocked.Contains(BackendModules.Gpu), "主后端 PID 已退出但子进程存活时交接仍被阻止");
        var review = BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.Gpu);
        Check(!review.CanAcknowledge && review.ChildrenAlive, "存活子进程被识别且不允许解除阻塞");

        File.Delete(recordPath);
        Check(BackendUnconfirmedStore.PendingModules().Count == 0, "清理夹具证据");
    }

    private static async Task<string> AcknowledgeAsync(string module)
    {
        await WaitUntilAsync(() => BackendUnconfirmedStore.ReviewAll().Any(r => r.Module == module && r.CanAcknowledge),
            TimeSpan.FromSeconds(15), "等待测试后端及其子进程均退出：" + module);
        var review = BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == module);
        Check(review.CanAcknowledge, $"{module} 记录在退出后可供人工核对：{review.Description}");
        return await BackendLifecycle.AcknowledgeCurrentStateAsync(review);
    }

    private static async Task ReadOnlyFailureDoesNotBlockAsync()
    {
        foreach (var (module, command) in new[] {
            (BackendModules.System, BackendCommands.SystemShaderDiagnose),
            (BackendModules.Runtime, BackendCommands.RuntimeStatus),
            (BackendModules.CpuFrame, BackendCommands.CpuFrameTopology) })
        {
            var client = await BackendClient.StartAsync(module, default);
            using var cancel = new CancellationTokenSource(150);
            var result = await client.InvokeAsync(command, new { testDelayMs = 600 }, null, cancel.Token);
            Check(result.Status == BackendResultStatuses.Failed && result.Message.Contains("只读"), "只读失败如实报告检测失败：" + command);
            Check(!client.RequiresAttention && !BackendUnconfirmedStore.PendingModules().Contains(module), "只读失败不形成恢复义务：" + command);
            Check(!BackendAttention.Snapshot().Any(p => p.Module == module), "只读失败不误弹系统修改恢复引导：" + command);
            await client.QuiesceAsync(TimeSpan.FromSeconds(5));
            Check(client.ExitConfirmed, "只读失败后可以安全停止：" + command);
        }
        var cpu = await BackendClient.StartAsync(BackendModules.CpuFrame, default);
        await cpu.InvokeAsync(BackendCommands.CpuFrameMonitorStart, new { }, null, default);
        using var monitorCancel = new CancellationTokenSource(150);
        await cpu.InvokeAsync(BackendCommands.CpuFrameStatus, new { testDelayMs = 500 }, null, monitorCancel.Token);
        Check(cpu.RequiresAttention, "CPU 监控运行时只读断线仍保留后台修改证据");
        Check(BackendAttention.Snapshot().Any(p => p.Module == BackendModules.CpuFrame), "后台 CPU 监控异常主动进入核对提示队列");
        await cpu.QuiesceAsync(TimeSpan.FromSeconds(5));
        await AcknowledgeAsync(BackendModules.CpuFrame);
    }

    private static async Task PartialStopsBeforeReviewAsync()
    {
        var client = await BackendClient.StartAsync(BackendModules.System, default);
        var result = await client.InvokeAsync(BackendCommands.SystemBxApply, new { testPartial = 1 }, null, default);
        Check(result.Status == BackendResultStatuses.Partial && client.RequiresAttention && !client.ExitConfirmed, "部分失败的活动会话保留证据");
        Check(BackendAttention.Snapshot().Any(p => p.Module == BackendModules.System), "部分失败在结果返回时已提供直接恢复提示");
        var reviews = await BackendLifecycle.PrepareReviewAsync();
        Check(client.ExitConfirmed && reviews.Single(r => r.Module == BackendModules.System).CanRestore, "直接核对入口停止原后端并开放恢复，不依赖先尝试退出");
        await AcknowledgeAsync(BackendModules.System);
    }

    private static async Task ChildrenRecheckAsync()
    {
        var path = BackendUnconfirmedStore.RecordPathForTests(BackendModules.Gpu);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Module = BackendModules.Gpu, Pid = int.MaxValue,
            StartTicks = 1L, Path = Environment.ProcessPath, ChildrenUnknown = true, Command = BackendCommands.GpuSpoofApply }));
        var original = BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.Gpu);
        Check(!original.CanAcknowledge && original.ChildrenUnknown, "失败的子进程查询暂时阻止确认");
        BackendUnconfirmedStore.DescendantsForTests = _ => ([], false);
        try
        {
            Check(!BackendUnconfirmedStore.RefreshChildren(BackendModules.Gpu), "重新查询仍失败时如实返回失败");
            Check(BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.Gpu).Fingerprint == original.Fingerprint,
                "查询失败不修改原证据或解除阻塞");
        }
        finally { BackendUnconfirmedStore.DescendantsForTests = null; }
        Check(BackendUnconfirmedStore.RefreshChildren(BackendModules.Gpu), "可以重新执行子进程查询");
        var refreshed = BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.Gpu);
        Check(refreshed.CanAcknowledge && !refreshed.ChildrenUnknown && refreshed.Fingerprint != original.Fingerprint, "重新核对后更新状态和证据指纹");
        var rejected = false;
        try { BackendUnconfirmedStore.AcknowledgeCurrentState(original); } catch (IOException) { rejected = true; }
        Check(rejected, "旧核对指纹不能清除刷新后的记录");
        await AcknowledgeAsync(BackendModules.Gpu);
        using var self = Process.GetCurrentProcess();
        File.WriteAllText(path, JsonSerializer.Serialize(new { Module = BackendModules.Gpu, Pid = int.MaxValue,
            StartTicks = 1L, Path = Environment.ProcessPath, ChildrenUnknown = true,
            Children = new[] { new { Name = "known-child", Pid = self.Id, StartTicks = self.StartTime.ToUniversalTime().Ticks } } }));
        Check(BackendUnconfirmedStore.RefreshChildren(BackendModules.Gpu), "存活子进程也允许重新查询");
        Check(BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.Gpu) is { ChildrenAlive: true, CanRestore: false }, "刷新不会丢弃已知存活的子进程");
        File.Delete(path); // Test-owned synthetic evidence only.
        File.WriteAllText(path, JsonSerializer.Serialize(new { Module = BackendModules.Gpu, Pid = int.MaxValue,
            StartTicks = 1L, Path = Environment.ProcessPath, ChildrenUnknown = true,
            Children = new[] { new { Name = "incomplete-child", Pid = int.MaxValue, StartTicks = 0L } } }));
        Check(BackendUnconfirmedStore.RefreshChildren(BackendModules.Gpu)
            && BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.Gpu).CanAcknowledge,
            "已消失的子进程即使旧启动时间缺失也能重新核对收敛");
        await AcknowledgeAsync(BackendModules.Gpu);
    }

    private static async Task GateBlocksCallsAsync()
    {
        var client = await BackendClient.StartAsync(BackendModules.Gpu, default);
        var inFlight = client.InvokeAsync(BackendCommands.GpuSpoofStatus, new { testDelayMs = 600 }, null, default);
        await Task.Delay(100);
        var queued = client.InvokeAsync(BackendCommands.GpuSpoofApply, new { }, null, default);
        using (BackendOperationGate.BlockNewOperations())
        {
            foreach (var attempt in new Func<Task>[] {
                async () => { await BackendClient.StartAsync(BackendModules.System, default); },
                async () => { await new BackendCaller(BackendModules.Gpu).InvokeAsync(BackendCommands.GpuSpoofStatus, new { }, null, default); },
                async () => { await client.InvokeAsync(BackendCommands.GpuSpoofStatus, new { }, null, default); } })
            {
                var rejected = false;
                try { await attempt(); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "交接门禁统一拒绝新后端、封装调用和直接调用");
            }
            await inFlight;
            var queuedRejected = false;
            try { await queued; } catch (InvalidOperationException) { queuedRejected = true; }
            Check(queuedRejected && BackendUnconfirmedStore.PendingModules().Count == 0, "排队请求在发送前重新检查门禁，不留下虚假意图");
            Check((await BackendLifecycle.PrepareHandoverAsync("门禁控制排空", TimeSpan.FromSeconds(5))).Count == 0, "内部停止控制可以穿过外层门禁");
        }
        var next = await BackendClient.StartAsync(BackendModules.Gpu, default);
        Check(next.IsUsable, "交接取消或结束后门禁释放");
        await next.QuiesceAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task AdmittedSendDrainsAsync()
    {
        var client = await BackendClient.StartAsync(BackendModules.Gpu, default);
        var writeGate = (SemaphoreSlim)typeof(BackendClient).GetField("_writeGate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(client)!;
        await writeGate.WaitAsync();
        var admitted = client.InvokeAsync(BackendCommands.GpuSpoofStatus, new { testDelayMs = 200 }, null, default);
        var handover = BackendLifecycle.PrepareHandoverAsync("发送中交接", TimeSpan.FromSeconds(5));
        try
        {
            await Task.Delay(100);
            Check(!handover.IsCompleted && !client.ExitConfirmed, "交接先等待已获准但尚未发送完的请求");
        }
        finally { writeGate.Release(); }
        await admitted;
        Check((await handover).Count == 0 && client.ExitConfirmed, "已获准请求发送完成后仍纳入排空");
    }

    private static async Task DefenderBatchDoesNotSelfLockAsync()
    {
        var client = await BackendClient.StartAsync(BackendModules.System, default);
        var added = await client.InvokeAsync(BackendCommands.SystemDefenderExclusionsAdd, new { }, null, default);
        Check(added.Status == BackendResultStatuses.Ok && !BackendUnconfirmedStore.PendingModules().Contains(BackendModules.System), "已知成功的排除项添加不锁住优化批次");
        Check((await client.InvokeAsync(BackendCommands.SystemBxApply, new { }, null, default)).Status == BackendResultStatuses.Ok, "排除项之后优化正常执行");
        Check((await client.InvokeAsync(BackendCommands.SystemDefenderPostApply, new { }, null, default)).Status == BackendResultStatuses.Ok, "优化之后 PostApply 正常执行");
        await client.InvokeAsync(BackendCommands.SystemDefenderExclusionsAdd, new { testPartial = 1 }, null, default);
        Check(client.RequiresAttention, "排除项部分失败保留证据");
        var cleanup = await client.InvokeAsync(BackendCommands.SystemDefenderPostApply, new { }, null, default);
        Check(cleanup.Status == BackendResultStatuses.Ok && !client.RequiresAttention && !BackendUnconfirmedStore.PendingModules().Contains(BackendModules.System),
            "专用清理可穿过待核对记录，复核成功后归档对应 Defender 证据");
        await client.QuiesceAsync(TimeSpan.FromSeconds(5));
    }

    private static void ConfirmedRestoreMustMatch()
    {
        var request = Guid.NewGuid(); var session = Guid.NewGuid();
        BackendUnconfirmedStore.BeginIntent(new(BackendModules.CpuFrame, BackendCommands.CpuFrameRestore, request, session,
            "fixture", int.MaxValue, 1, Environment.ProcessPath!));
        BackendUnconfirmedStore.PromoteIntent(BackendModules.CpuFrame, request, "timeout", int.MaxValue, 1,
            Environment.ProcessPath!, BackendCommands.CpuFrameRestore, [], false);
        var result = BackendResult.Ok("restore", "已还原", OperationResult.Ok("已还原"));
        Check(!BackendUnconfirmedStore.ArchiveConfirmedRestore(BackendModules.CpuFrame, Guid.NewGuid(), session, result), "其他请求的迟到成功不能清除本请求证据");
        Check(!BackendUnconfirmedStore.ArchiveConfirmedRestore(BackendModules.CpuFrame, request, Guid.NewGuid(), result), "其他会话的成功不能清除本请求证据");
        Check(!BackendUnconfirmedStore.ArchiveConfirmedRestore(BackendModules.CpuFrame, request, session,
            BackendResult.Ok("restore", "还原失败", OperationResult.Fail("failed"))), "外层 ok 但内部失败仍保留证据");
        Check(BackendUnconfirmedStore.ArchiveConfirmedRestore(BackendModules.CpuFrame, request, session, result), "仅归档匹配请求和会话的完整还原成功");
    }

    private static async Task MemoryOnlyUnknownStillBlocksAsync()
    {
        var client = await BackendClient.StartAsync(BackendModules.Gpu, default);
        typeof(BackendClient).GetField("_unconfirmedOperation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(client, true);
        var result = await BackendLifecycle.PrepareHandoverAsync("模拟证据落盘失败", TimeSpan.FromSeconds(5));
        Check(result.Contains(BackendModules.Gpu) && client.ExitConfirmed, "证据落盘失败时内存未知状态仍阻止交接，但不阻止停止后端");
        await client.ReleaseAcknowledgedSessionAsync(); // Synthetic memory-only flag; no real operation was executed.
    }

    private static bool KillFixture(string moduleId)
    {
        var spec = BackendModules.Require(moduleId);
        var name = Path.GetFileNameWithoutExtension(spec.ExecutableName);
        var dir = Path.Combine(Environment.GetEnvironmentVariable(BackendPaths.BackendRootEnvironmentVariable)!, moduleId);
        var killed = false;
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                if ((process.MainModule?.FileName ?? "").StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: false);
                    killed = true;
                }
            }
            catch { /* 进程可能恰好退出或无权读取，忽略 */ }
            finally { process.Dispose(); }
        }
        return killed;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new Exception("等待超时：" + what);
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("交接检查失败：" + name);
        _passed++;
    }

    /// <summary>把本测试 EXE 复制成官方后端名，作为临时测试后端宿主。</summary>
    private sealed class FixtureBackendRoot(string backendsRoot)
    {
        public void Install(string moduleId)
        {
            var spec = BackendModules.Require(moduleId);
            var directory = Path.Combine(backendsRoot, moduleId);
            Directory.CreateDirectory(directory);
            foreach (var source in Directory.GetFiles(AppContext.BaseDirectory))
            {
                var name = Path.GetFileName(source);
                if (name.Equals(Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase))
                    name = spec.ExecutableName; // 复制为官方后端 EXE 名，触发夹具宿主分支。
                else if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                         name.StartsWith("DeltaNFD.Backend.", StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Copy(source, Path.Combine(directory, name), overwrite: true);
            }
        }
    }
}
