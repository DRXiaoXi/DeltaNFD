using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

/// <summary>实际脱机协调器 + 实际 IPC/文件租约；只有 UI、计划任务和 CPU 写入边界用夹具。</summary>
internal static class OfflineModeTransitionChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var store = OfflineModeStateStore.Default;
        var coordinator = new OfflineModeCoordinator();
        var scenario = "success";
        OfflineTaskOwnership.ProcessRunnerForTests = (args, _) =>
        {
            if (args[0] != "/Query") throw new Exception("夹具只允许查询不存在的计划任务，禁止真实删除/创建。");
            return Task.FromResult((1, "", "ERROR: The system cannot find the file specified."));
        };
        BackendUnconfirmedStore.DescendantsForTests = _ => ([], true);
        Environment.SetEnvironmentVariable("DELTANFD_TEST_OFFLINE_RESTORE", scenario);
        try
        {
            AppSettingsStore.Update(s =>
            {
                s.FrameModeActive = false; s.DwmRestartOnGameStart = false; s.GamePriorityEnabled = false;
                s.GameAffinityRuleEnabled = false; s.SingleCcdExcludeCpu0Enabled = false; s.DualCcdImmediateEnabled = false;
                s.CloseToTrayEnabled = false; s.FramePowerRestorePending = false;
            });
            using (BackendOperationGate.BlockNewOperations())
            {
                var denied = false;
                try { await BackendCpuFrameService.Instance.HasAffinityRestorePendingAsync(); }
                catch (InvalidOperationException) { denied = true; }
                check(denied, "复现旧门禁：切换外层封锁时普通亲和性查询被拒绝");
                var enabled = await coordinator.EnableAsync();
                check(!enabled.Success && enabled.Message.Contains("已移除"), "已移除的启用入口拒绝执行，不穿透外层门禁");
                Seed(OfflineModeStatus.Ready, enabled: true); // Existing-version fixture, not a new activation.
                check(IsBlocked(), "内部控制调用返回后不把权限泄漏给页面");
            }
            check(!IsBlocked(), "启用结束释放门禁");

            // Precisely reproduce the log's old failure: enabling stopped midway, leaving a saved snapshot.
            Seed(OfflineModeStatus.Preparing, enabled: false);
            Environment.SetEnvironmentVariable("DELTANFD_TEST_OFFLINE_DELAY_MS", "700");
            await DrainAsync(); // Start the next fixture with the configured delay.
            using (BackendOperationGate.BlockNewOperations())
            {
                var closing = coordinator.DisableAsync();
                await Task.Delay(100);
                check(!closing.IsCompleted && IsBlocked(), "关闭恢复进行中，普通新请求持续被阻止");
                var normalDenied = false;
                try { await BackendClient.StartAsync(BackendModules.System, default); } catch (InvalidOperationException) { normalDenied = true; }
                check(normalDenied, "恢复期间不能新启动无关后端");
                var closed = await closing;
                var state = store.Read();
                check(closed.Success && state is { Status: OfflineModeStatus.Off, OfflineModeEnabled: false, SavedPreferences: null, CancelRequested: false }
                    && state.CpuSetChanges.Count == 0 && !state.BlocksNormalAutomation, "半途启用遗留状态可以完整恢复并关闭");
                check(state.PreviousNormalPreferences is { GameAffinityRuleMask: 3, FrameModeWasActive: true }, "原参数和原启用偏好另存，没有丢失");
                var settings = AppSettingsStore.Read();
                check(!settings.FrameModeActive && !settings.GamePriorityEnabled && !settings.GameAffinityRuleEnabled && !settings.CloseToTrayEnabled,
                    "关闭后不会自动重新开启帧格、锁核、优先级或托盘");
                check(IsBlocked(), "关闭返回后外层门禁仍保持有效");
            }
            Environment.SetEnvironmentVariable("DELTANFD_TEST_OFFLINE_DELAY_MS", null);
            check(!IsBlocked(), "关闭完成释放所有内部权限和门禁");
            check((await coordinator.DisableAsync()).Success, "重复关闭幂等成功");

            await DrainAsync();
            Seed(OfflineModeStatus.Kept, enabled: true);
            Environment.SetEnvironmentVariable("DELTANFD_TEST_OFFLINE_RESTORE", "failed");
            var failed = await coordinator.DisableAsync();
            var retained = store.Read();
            check(!failed.Success && retained.Status == OfflineModeStatus.RestorePending && retained.SavedPreferences is not null
                && retained.CpuSetChanges.Count == 1 && retained.CpuSetChanges[0].OriginalCpuSetIds.SequenceEqual([2u]),
                "CPU 恢复失败保留快照和原始 CPU Sets，不能假装已关闭");
            check(!IsBlocked(), "失败返回后仍释放交接门禁，可以重试");
            await DrainAsync();
            Environment.SetEnvironmentVariable("DELTANFD_TEST_OFFLINE_RESTORE", "success");
            check((await coordinator.DisableAsync()).Success && !store.Read().BlocksNormalAutomation, "失败后的再次关闭能够完成恢复");

            Seed(OfflineModeStatus.RestorePending, enabled: false);
            var beforeCancel = File.ReadAllBytes(store.PathName);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var observed = false;
                try { await coordinator.DisableAsync(cancelled.Token); } catch (OperationCanceledException) { observed = true; }
                check(observed && File.ReadAllBytes(store.PathName).SequenceEqual(beforeCancel), "提前取消不修改或清空恢复记录");
            }
            check(!IsBlocked(), "取消不泄漏门禁");
            check((await coordinator.DisableAsync()).Success, "取消后可重试关闭");

            await DrainAsync();
            Seed(OfflineModeStatus.Kept, enabled: true);
            using (BackendOperationGate.BlockNewOperations())
                check((await coordinator.PrepareForRemovalAsync(restoreCpuSets: true)).Success
                    && store.Read() is { Status: OfflineModeStatus.Off, SavedPreferences: null }, "卸载恢复内部调用同样能穿过门禁");
            Seed(OfflineModeStatus.Kept, enabled: true);
            var preparedUpdate = await coordinator.PrepareForRemovalAsync(restoreCpuSets: false);
            check(preparedUpdate.Success && store.Read() is { OfflineModeEnabled: true, Status: OfflineModeStatus.Kept, SavedPreferences: not null }
                && store.Read().CpuSetChanges.Count == 1, "仅准备更新仍保留脱机模式与 CPU 恢复记录");
            check((await coordinator.DisableAsync()).Success, "更新准备后仍可关闭脱机模式");

            var savedRunner = OfflineTaskOwnership.ProcessRunnerForTests;
            OfflineTaskOwnership.ProcessRunnerForTests = (_, _) => Task.FromException<(int, string, string)>(new IOException("模拟计划任务查询故障"));
            var removed = await coordinator.EnableAsync();
            check(!removed.Success && removed.Message.Contains("已移除") && !IsBlocked() && !store.Read().BlocksNormalAutomation,
                "移除后的启用入口不会查询计划任务或伪造状态，也不泄漏门禁");
            OfflineTaskOwnership.ProcessRunnerForTests = savedRunner;
            var intact = File.ReadAllBytes(store.PathName);
            File.WriteAllText(store.PathName, "{broken");
            var corrupt = await coordinator.DisableAsync();
            check(!corrupt.Success && File.ReadAllText(store.PathName) == "{broken" && !IsBlocked(), "损坏状态不被覆盖，失败不锁死页面门禁");
            File.WriteAllBytes(store.PathName, intact);

            store.TryUpdate(s => { s.CancelRequested = true; s.Status = OfflineModeStatus.RestorePending; }, out _);
            var missingSnapshot = await coordinator.DisableAsync();
            check(!missingSnapshot.Success && store.Read().BlocksNormalAutomation && !IsBlocked(),
                "缺少快照的待恢复状态不能误报本就未启用，也不泄漏交接门禁");
            File.WriteAllBytes(store.PathName, intact); // Restore the test-owned valid state, not user recovery evidence.

            using (BackendOperationGate.BlockNewOperations())
            {
                var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task delayed;
                using (BackendOperationGate.AllowControlOperations())
                    delayed = Task.Run(async () => { await resume.Task; BackendOperationGate.Check(); });
                resume.SetResult();
                var revoked = false;
                try { await delayed; } catch (InvalidOperationException) { revoked = true; }
                check(revoked, "捕获旧上下文的后台回调在控制租约结束后不能绕过新门禁");
            }
        }
        finally
        {
            OfflineTaskOwnership.ProcessRunnerForTests = null;
            BackendUnconfirmedStore.DescendantsForTests = null;
            Environment.SetEnvironmentVariable("DELTANFD_TEST_OFFLINE_RESTORE", null);
            Environment.SetEnvironmentVariable("DELTANFD_TEST_OFFLINE_DELAY_MS", null);
        }

        void Seed(OfflineModeStatus status, bool enabled)
        {
            if (!store.TryUpdate(s =>
            {
                s.Status = status; s.OfflineModeEnabled = enabled; s.CancelRequested = false;
                s.HelperProcessId = null; s.HelperStartTimeUtcTicks = null; s.HelperRunId = Guid.Empty;
                s.SavedPreferences = new OfflineModePreferences { GameAffinityRuleEnabled = true, GameAffinityRuleMask = 3,
                    FrameModeWasActive = true, GamePriorityEnabled = true, DwmRestartOnGameStart = true, CloseToTrayEnabled = true };
                s.CpuSetChanges = [new OfflineCpuSetChange { ProcessId = int.MaxValue, StartTimeUtcTicks = 1,
                    ExecutablePath = "C:/isolated-target.exe", Status = OfflineCpuSetRecordStatus.Applied,
                    Api = OfflineCpuSetApi.Windows10Ids, OriginalAssignmentWasSet = true, OriginalCpuSetIds = [2u],
                    AppliedAssignmentWasSet = true, AppliedCpuSetIds = [3u] }];
            }, out var error)) throw new Exception(error);
        }
        static bool IsBlocked()
        {
            try { BackendOperationGate.Check(); return false; } catch (InvalidOperationException) { return true; }
        }
        static async Task DrainAsync()
        {
            var failed = await BackendLifecycle.PrepareHandoverAsync("脱机回归切换夹具", TimeSpan.FromSeconds(5));
            if (failed.Count != 0) throw new Exception("模拟后端未排空：" + string.Join(",", failed));
        }
    }
}
