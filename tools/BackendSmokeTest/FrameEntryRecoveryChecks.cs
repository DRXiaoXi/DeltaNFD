using System.Reflection;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class FrameEntryRecoveryChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        BackendUnconfirmedStore.DescendantsForTests = _ => ([], true);
        try { await RunCoreAsync(check); }
        finally { BackendUnconfirmedStore.DescendantsForTests = null; }
    }

    private static async Task RunCoreAsync(Action<bool, string> check)
    {
        var called = false;
        var clean = await FrameBackendReadiness.RunAsync(() =>
        {
            called = true;
            return Task.FromResult(OperationResult.Ok("已开启"));
        }, "开启", false);
        check(clean.Success && called, "无旧记录时帧格开启正常执行");

        var client = await BackendClient.StartAsync(BackendModules.CpuFrame, default);
        typeof(BackendCpuFrameService).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(BackendCpuFrameService.Instance, client);
        await client.InvokeAsync(BackendCommands.CpuFramePriorityApply, new { testPartial = 1 }, null, default);
        var path = BackendUnconfirmedStore.RecordPathForTests(BackendModules.CpuFrame);
        var original = File.ReadAllBytes(path);
        called = false;
        var blocked = await FrameBackendReadiness.RunAsync(() =>
        {
            called = true;
            return Task.FromResult(OperationResult.Ok("不应执行"));
        }, "开启", false);
        check(!blocked.Success && !called && blocked.Message.Contains("核对"), "旧 CPU 记录阻止普通开启，并给出恢复入口");
        check(File.ReadAllBytes(path).SequenceEqual(original), "开启失败保留原记录，不重放系统操作");
        var declined = await BackendCpuFrameService.Instance.UnlockPowerAsync();
        check(!declined.Success && declined.Message.Contains("核对"), "普通解锁电源返回明确失败，不向 UI 抛出未处理异常");

        var unrelated = await BackendClient.StartAsync(BackendModules.System, default);
        var reviews = await BackendLifecycle.PrepareReviewAsync([BackendModules.CpuFrame]);
        var review = reviews.Single();
        check(client.ExitConfirmed && review.CanRestore && unrelated.IsUsable, "帧格核对停止自己的旧执行者，保留无关系统会话");
        Environment.SetEnvironmentVariable("DELTANFD_TEST_RECOVERY_SCENARIO", "success");
        try
        {
            var snapshot = await BackendRecovery.ReconcileAsync(BackendModules.CpuFrame);
            check(BackendRecovery.RecoveryChoices(review, snapshot) is [{ Id: "all" }], "CPU 待核对记录只提供完整恢复选择");
            var rejected = false;
            try { await BackendRecovery.RestoreAsync(review, "power"); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && File.ReadAllBytes(path).SequenceEqual(original), "不能靠恢复单个子范围归档整条 CPU 证据");
            var outcome = await BackendRecovery.RestoreAsync(review, "all");
            check(outcome.Success && outcome.Verified && !File.Exists(path), "完整恢复复核通过后归档旧记录");
            check(!client.RequiresAttention && client.ExitConfirmed, "恢复通过后释放专用 CPU 服务持有的旧未知会话");
            check(Directory.GetFiles(Path.Combine(AppDataPaths.Root, "official-backend-reviewed"), "cpuframe-restored-*.json")
                .Any(p => !p.EndsWith(".outcome.json") && File.ReadAllBytes(p).SequenceEqual(original)), "恢复归档保留原始证据字节");
        }
        finally { Environment.SetEnvironmentVariable("DELTANFD_TEST_RECOVERY_SCENARIO", null); }

        var resumed = await FrameBackendReadiness.RunAsync(async () =>
        {
            var power = await BackendCpuFrameService.Instance.UnlockPowerAsync();
            var tweaks = await BackendCpuFrameService.Instance.RevertTweaksAsync();
            return power.Success && tweaks.Success ? OperationResult.Ok("已恢复开启前状态") : OperationResult.Fail(power.Message + tweaks.Message);
        }, "开启", false);
        check(resumed.Success, "恢复后普通帧格开启前的解锁电源和临时优化恢复能够继续执行");
        check((await BackendLifecycle.PrepareHandoverAsync("恢复入口测试收尾", TimeSpan.FromSeconds(5))).Count == 0, "恢复后的会话正常排空，无新自锁");

        foreach (var scenario in new[] { "failed", "incomplete", "remaining", "wrong-module" })
        {
            SeedCpuEvidence(path);
            var before = File.ReadAllBytes(path);
            var pending = BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.CpuFrame);
            Environment.SetEnvironmentVariable("DELTANFD_TEST_RECOVERY_SCENARIO", scenario);
            try
            {
                var verified = false;
                try { verified = (await BackendRecovery.RestoreAsync(pending, "all")).Verified; }
                catch (InvalidOperationException) { }
                check(!verified && File.Exists(path), "恢复失败或错误快照继续阻止放行：" + scenario);
                check(File.ReadAllBytes(path).SequenceEqual(before), "失败恢复保留原始证据：" + scenario);
            }
            finally { Environment.SetEnvironmentVariable("DELTANFD_TEST_RECOVERY_SCENARIO", null); }
            var current = BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.CpuFrame);
            await BackendLifecycle.AcknowledgeCurrentStateAsync(current); // Explicit test-only consent for the synthetic state.
        }

        var failedCall = await FrameBackendReadiness.RunAsync(() => throw new IOException("模拟后端故障"), "开启", false);
        check(!failedCall.Success && failedCall.Message.Contains("模拟后端故障"), "传输或恢复异常转成可展示失败结果");
        SeedCpuEvidence(path, BackendCommands.CpuFrameDwmRestart);
        var irreversible = BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.CpuFrame);
        var unrelatedBackup = new ReconcileSnapshot
        {
            Module = BackendModules.CpuFrame, Summary = "存在其他帧格备份", Complete = true,
            RecoveryScopes = [new RecoveryScope { Id = "power", Description = "电源备份", Available = true }],
        };
        check(BackendRecovery.RecoveryChoices(irreversible, unrelatedBackup).Count == 0,
            "无关电源备份不能被当作 DWM 重启未知操作的恢复依据");
        var refused = false;
        try { await BackendRecovery.RestoreAsync(irreversible, "all"); } catch (InvalidOperationException) { refused = true; }
        check(refused && File.Exists(path), "不可回滚的旧操作需人工核对，完整帧格恢复不能误归档它");
        await BackendLifecycle.AcknowledgeCurrentStateAsync(irreversible);
        using (BackendOperationGate.BlockNewOperations())
        {
            called = false;
            var gated = await FrameBackendReadiness.RunAsync(() => { called = true; return Task.FromResult(OperationResult.Ok("不应调用")); }, "开启", false);
            check(!gated.Success && !called, "退出交接期间开启不会越过统一门禁");
        }
        var system = BackendUnconfirmedStore.RecordPathForTests(BackendModules.System);
        File.WriteAllText(system, JsonSerializer.Serialize(new { Module = BackendModules.System, Pid = int.MaxValue, StartTicks = 1L,
            Path = Environment.ProcessPath, Command = BackendCommands.SystemBxApply }));
        check(FrameBackendReadiness.DescribeBlocker(false).Length == 0, "无关系统维护记录不锁住不依赖它的帧格开启");
        File.Delete(system);
        var gpu = BackendUnconfirmedStore.RecordPathForTests(BackendModules.Gpu);
        File.WriteAllText(gpu, JsonSerializer.Serialize(new { Module = BackendModules.Gpu, Pid = int.MaxValue, StartTicks = 1L,
            Path = Environment.ProcessPath, Command = BackendCommands.GpuSpoofApply }));
        check(FrameBackendReadiness.DescribeBlocker(false).Length == 0 && FrameBackendReadiness.DescribeBlocker(true).Length > 0,
            "仅配置显卡伪装的帧格流程需要处理旧 GPU 记录");
        File.Delete(gpu);
    }

    private static void SeedCpuEvidence(string path, string command = BackendCommands.CpuFramePowerUnlock) => File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        Module = BackendModules.CpuFrame, Phase = "unconfirmed", Reason = "timeout", Pid = int.MaxValue, StartTicks = 1L,
        Path = Environment.ProcessPath, Command = command,
        RequestId = Guid.NewGuid().ToString("D"), SessionId = Guid.NewGuid().ToString("D"), Children = Array.Empty<object>(),
    }));
}
