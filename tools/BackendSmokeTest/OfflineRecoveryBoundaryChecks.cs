using DeltaNFD.Services;

internal static class OfflineRecoveryBoundaryChecks
{
    public static async Task RunAsync()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_RecoveryBoundary_" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var store = OfflineModeStateStore.Default;
        AppSettingsStore.Update(s => {
            s.FrameModeActive = false; s.GamePriorityEnabled = false; s.GameAffinityRuleEnabled = false;
            s.SingleCcdExcludeCpu0Enabled = false; s.DualCcdImmediateEnabled = false;
        });
        var passed = 0;
        var calls = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
        OperationResult Restore() { calls++; return OperationResult.Ok("模拟恢复"); }
        var timeout = TimeSpan.FromMilliseconds(150);
        Task<OperationResult> Run(Func<OperationResult>? restore = null, CancellationToken token = default, bool admin = true)
            => OfflineCpuSetRecovery.RestoreAsync(store, timeout, token, () => admin, restore ?? Restore);

        Check(!(await Run()).Success && calls == 0, "非待恢复状态不得执行");
        Check(store.TryUpdate(s => { s.CancelRequested = true; s.Status = OfflineModeStatus.RestorePending; s.SavedPreferences = new(); }, out _), "准备恢复状态");
        Check(!(await Run(admin: false)).Success && calls == 0, "非管理员拒绝");
        using (var self = System.Diagnostics.Process.GetCurrentProcess())
        {
            Check(store.TryUpdate(s => { s.HelperProcessId = self.Id; s.HelperStartTimeUtcTicks = self.StartTime.ToUniversalTime().Ticks; s.HelperRunId = Guid.NewGuid(); }, out _), "登记仍存活助手身份");
            Check(!(await Run()).Success && calls == 0, "已释放租约但登记进程仍存活时拒绝");
            Check(store.TryUpdate(s => { s.HelperProcessId = null; s.HelperStartTimeUtcTicks = null; s.HelperRunId = Guid.Empty; }, out _), "清理模拟助手身份");
        }
        using (var helper = OfflineModeHelperInstanceLease.TryAcquire(store, out _))
        {
            Check(helper is not null, "模拟助手取锁");
            Check(!(await Run()).Success && calls == 0, "助手未排空不得恢复");
        }
        using (var operation = await OfflineModeExecutionLock.WaitAcquireAsync(store, timeout))
        {
            Check(operation is not null, "模拟执行取锁");
            Check(!(await Run()).Success && calls == 0, "执行锁被占用超时不执行");
        }
        using (var control = await OfflineModeExecutionLock.WaitControlAsync(store, timeout))
        {
            Check(control is not null, "前端持有控制锁");
            Check((await Run()).Success && calls == 1, "控制锁留前端不导致后端恢复死锁");
            using var helper = OfflineModeHelperInstanceLease.TryAcquire(store, out _);
            using var operation = await OfflineModeExecutionLock.WaitAcquireAsync(store, timeout);
            Check(helper is not null && operation is not null, "恢复结束两个锁均释放，前端可重取");
        }
        var failed = await Run(() => OperationResult.Fail("模拟还原失败"));
        Check(!failed.Success && store.Read().SavedPreferences is not null && store.Read().Status == OfflineModeStatus.RestorePending,
            "失败不清快照、不退出待恢复状态");
        AppSettingsStore.Update(s => s.GameAffinityRuleEnabled = true);
        Check(!(await Run()).Success && calls == 1, "普通调度未停用不得恢复");
        AppSettingsStore.Update(s => s.GameAffinityRuleEnabled = false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var stopped = false;
        try { await Run(token: cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        Check(stopped && calls == 1, "取消不执行");
        async Task<(OperationResult Result, OfflineRestoreHandover.Leases? Locks)> Handover(
            Func<CancellationToken, Task<OperationResult>> callback, CancellationToken token = default)
        {
            using var helper = OfflineModeHelperInstanceLease.TryAcquire(store, out _);
            using var operation = await OfflineModeExecutionLock.WaitAcquireAsync(store, timeout);
            Check(helper is not null && operation is not null, "交接前锁可获取");
            return await OfflineRestoreHandover.RunAsync(store, operation!, helper!, callback, timeout, token);
        }
        void NoLeakedLocks()
        {
            using var helper = OfflineModeHelperInstanceLease.TryAcquire(store, out _);
            var acquired = OfflineModeExecutionLock.TryAcquire(store, out var operation, out _);
            Check(helper is not null && acquired, "故障交接不泄漏租约");
            operation?.Dispose();
        }
        var disconnected = await Handover(_ => Task.FromException<OperationResult>(new IOException("模拟断线")));
        Check(!disconnected.Result.Success && disconnected.Locks is null && store.Read().SavedPreferences is not null, "断线保留快照");
        NoLeakedLocks();
        using var afterResultCancel = new CancellationTokenSource();
        var cancelledHandover = await Handover(_ => { afterResultCancel.Cancel(); return Task.FromResult(OperationResult.Ok("模拟完成")); }, afterResultCancel.Token);
        Check(!cancelledHandover.Result.Success && cancelledHandover.Locks is null, "后端返回后取消不宣称交接完成");
        NoLeakedLocks();
        OfflineModeExecutionLock? competing = null;
        var busyHandover = await Handover(ct => {
            OfflineModeExecutionLock.TryAcquire(store, out competing, out _);
            return Task.FromResult(OperationResult.Ok("模拟完成"));
        });
        Check(!busyHandover.Result.Success && busyHandover.Locks is null, "后端返回后操作锁占用拒绝交接");
        competing?.Dispose();
        NoLeakedLocks();
        var changedState = await Handover(ct => {
            store.TryUpdate(s => s.CancelRequested = false, out _);
            return Task.FromResult(OperationResult.Ok("模拟完成"));
        });
        Check(!changedState.Result.Success && changedState.Locks is null, "模式状态变化拒绝交接");
        NoLeakedLocks();
        store.TryUpdate(s => s.CancelRequested = true, out _);
        var successful = await Handover(_ => Task.FromResult(OperationResult.Ok("模拟完成")));
        Check(successful.Result.Success && successful.Locks is not null, "成功交接持有复核后的租约");
        successful.Locks?.Dispose();
        NoLeakedLocks();
        File.WriteAllText(store.PathName, "{broken");
        Check(!(await Run()).Success && calls == 1, "损坏状态拒绝且不覆盖");
        Console.WriteLine($"脱机恢复边界通过：{passed} 项；模拟恢复，无真实 CPU 写入。");
    }
}
