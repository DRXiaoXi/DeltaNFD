using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class AceCleanupFlowChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var items = new[] { new BackendResultItem("fixture", "ok", "已完成项") };
        var mapped = AceCleanupResults.ToOperationResult(BackendResult.PartialResult("ace.clean-partial", "仅部分完成", items,
            OperationResult.Ok("旧版 data 错写成功", requiresReboot: true)));
        check(!mapped.Success && mapped.IsPartial && mapped.RequiresReboot && mapped.Message.Contains("已完成项"), "旧版 partial+ok 数据不再映射为成功，保留明细与重启要求");
        check(!AceCleanupResults.ToOperationResult(new(BackendResultStatuses.Cancelled, "ace.cancelled", "已取消", [], false,
            JsonSerializer.SerializeToElement(OperationResult.Ok("不可采信"), BackendJson.SerializerOptions))).Success, "取消不能被成功数据覆盖");
        check(!AceCleanupResults.ToOperationResult(BackendResult.Ok("ace.clean", "失败数据", OperationResult.Fail("未完成"))).Success, "外层 ok 但数据失败不能显示成功");
        check(AceCleanupResults.ToOperationResult(BackendResult.Ok("ace.clean", "全部完成")).Success, "无 data 的完整成功兼容映射");
        check(!AceCleanupResults.ToOperationResult(BackendResult.Ok("ace.clean", "收尾待确认", OperationResult.Ok("完成"), pendingRestore: true)).Success,
            "待确认收尾不能显示完整成功");

        var path = BackendUnconfirmedStore.RecordPathForTests(BackendModules.Ace);
        var trace = Path.Combine(AppDataPaths.Root, "ace-fixture-clean.log");
        Environment.SetEnvironmentVariable("DELTANFD_TEST_ACE_RESULT", "partial");
        BackendUnconfirmedStore.DescendantsForTests = _ => ([], true);
        try
        {
            var service = new BackendAceService();
            check((await service.ScanAsync()).AnythingFound, "ACE 服务正常扫描隔离组件");
            var result = await service.CleanAsync();
            check(!result.Success && result.IsPartial && result.RequiresReboot, "真实 IPC 前端将部分清理如实返回");
            check(AceCleanupReadiness.RequiresReview && File.Exists(path), "部分结果保留 ACE 原记录并显示核对入口");
            var original = File.ReadAllBytes(path);
            var calls = File.ReadAllLines(trace).Length;
            var repeated = await service.CleanAsync();
            check(!repeated.Success && repeated.Message.Contains("核对") && File.ReadAllLines(trace).Length == calls,
                "旧记录下重试返回明确说明，不重放清理");
            check(File.ReadAllBytes(path).SequenceEqual(original), "普通重试不覆盖旧证据");
            check((await service.ScanAsync()).AnythingFound && File.Exists(path), "旧执行者退出后可只读扫描，仍保留清理阻塞");
            check(File.ReadAllLines(trace).Length == calls, "清理后重扫不会再执行清理");

            var reviews = await BackendLifecycle.PrepareReviewAsync([BackendModules.Ace]);
            var review = reviews.Single();
            var snapshot = await BackendRecovery.ReconcileAsync(BackendModules.Ace);
            check(BackendRecovery.CanKeepCurrentState(review, snapshot), "退出并完成 ACE 状态核对后允许明确保留");
            check(!BackendRecovery.CanKeepCurrentState(review, null), "核对失败时不允许归档 ACE 记录");
            check(!BackendRecovery.CanKeepCurrentState(review, new ReconcileSnapshot { Module = BackendModules.Ace, Summary = "游戏运行中", Complete = false }),
                "游戏运行导致核对不完整时不允许归档 ACE 记录");
            check(BackendRecovery.RecoveryChoices(review, snapshot).Count == 0 && BackendModules.RecoverCommand(BackendModules.Ace) is null,
                "不可逆 ACE 清理不提供伪造的自动还原");
            await BackendLifecycle.AcknowledgeCurrentStateAsync(review); // Explicit consent for synthetic state only.
            check(!AceCleanupReadiness.RequiresReview && !File.Exists(path) && File.ReadAllLines(trace).Length == calls,
                "明确保留只归档证据，不自动清理");
            check(Directory.GetFiles(Path.Combine(AppDataPaths.Root, "official-backend-reviewed"), "ace-kept-current-state-*.json")
                .Any(p => File.ReadAllBytes(p).SequenceEqual(original)), "旧 ACE 证据逐字保留");

            Environment.SetEnvironmentVariable("DELTANFD_TEST_ACE_RESULT", null);
            check((await service.CleanAsync()).Success && File.ReadAllLines(trace).Length == calls + 1, "明确重新调用清理后可以完成新的清理");
            check(!AceCleanupReadiness.RequiresReview && (await service.ScanAsync()).AnythingFound, "完整清理正常退出且后续扫描可重启");

            using (BackendOperationGate.BlockNewOperations())
                check(!(await service.CleanAsync()).Success && File.ReadAllLines(trace).Length == calls + 1, "交接门禁期间清理正常返回失败，不抛 UI 异常");

            using var self = Process.GetCurrentProcess();
            File.WriteAllText(path, JsonSerializer.Serialize(new { Module = BackendModules.Ace, Pid = int.MaxValue, StartTicks = 1L,
                Path = Environment.ProcessPath, Command = BackendCommands.AceClean,
                Children = new[] { new { Name = "test-child", Pid = self.Id, StartTicks = self.StartTime.ToUniversalTime().Ticks } } }));
            var blockedChild = BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.Ace);
            check(!BackendRecovery.CanKeepCurrentState(blockedChild, snapshot), "已知子进程仍存活时不能用扫描快照解除阻塞");
            check(!(await service.CleanAsync()).Success && File.ReadAllLines(trace).Length == calls + 1, "存活子进程记录继续阻止清理");
            File.Delete(path); // Test-owned synthetic child evidence.
            File.WriteAllText(path, "{broken");
            check(!(await service.CleanAsync()).Success && File.ReadAllText(path) == "{broken", "损坏记录返回待核对失败，不覆盖记录");
            File.Delete(path);

            var beforeCoreCalls = File.ReadAllLines(trace).Length;
            await service.StopBackendIfIdleAsync();
            Environment.SetEnvironmentVariable("DELTANFD_TEST_ACE_RESULT", "partial");
            var corePartial = await service.CleanCoreFilesAsync();
            check(!corePartial.Success && corePartial.IsPartial && File.Exists(path),
                "CORE 文件清理的部分结果保留写前证据：" + corePartial.Message);
            check(File.ReadAllLines(trace).Last() == BackendCommands.AceCoreClean, "前端 IPC 发送专用 CORE 命令，不转成全量清理");
            var coreEvidence = File.ReadAllBytes(path);
            check(!(await service.CleanCoreFilesAsync()).Success && !(await service.CleanAsync()).Success
                && File.ReadAllLines(trace).Length == beforeCoreCalls + 1, "CORE 未确认记录同时阻止两种清理重放");
            check(File.ReadAllBytes(path).SequenceEqual(coreEvidence), "再次点击不会覆盖 CORE 清理的原始证据");
            await service.ScanAsync();
            var coreReview = (await BackendLifecycle.PrepareReviewAsync([BackendModules.Ace])).Single();
            var coreSnapshot = await BackendRecovery.ReconcileAsync(BackendModules.Ace);
            check(BackendRecovery.CanKeepCurrentState(coreReview, coreSnapshot), "CORE 清理后退出并只读核对后可明确保留状态");
            await BackendLifecycle.AcknowledgeCurrentStateAsync(coreReview);
            Environment.SetEnvironmentVariable("DELTANFD_TEST_ACE_RESULT", null);
            check((await service.CleanCoreFilesAsync()).Success && !AceCleanupReadiness.RequiresReview
                && File.ReadAllLines(trace).Length == beforeCoreCalls + 2, "明确重试专用清理可成功退出，不留下未确认门禁");
            check((await service.ScanAsync()).AnythingFound && File.ReadAllLines(trace).Length == beforeCoreCalls + 2,
                "CORE 清理后重扫只读，不再次执行删除");
            var failed = await BackendLifecycle.PrepareHandoverAsync("ACE 入口回归收尾", TimeSpan.FromSeconds(5));
            check(failed.Count == 0, "ACE 入口回归无未知会话泄漏");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DELTANFD_TEST_ACE_RESULT", null);
            BackendUnconfirmedStore.DescendantsForTests = null;
        }
    }
}
