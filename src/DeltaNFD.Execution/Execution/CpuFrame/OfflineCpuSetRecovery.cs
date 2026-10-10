namespace DeltaNFD.Services;

/// <summary>The UI holds the control lease, not the helper or execution lease, during IPC.</summary>
public static class OfflineCpuSetRecovery
{
    public static Task<OperationResult> RestoreAsync(CancellationToken token = default) =>
        RestoreAsync(OfflineModeStateStore.Default, TimeSpan.FromSeconds(5), token);

    internal static async Task<OperationResult> RestoreAsync(OfflineModeStateStore store, TimeSpan timeout,
        CancellationToken token, Func<bool>? elevated = null, Func<OperationResult>? restore = null)
    {
        if (!(elevated ?? (() => ElevationHelper.IsElevated))())
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        using var helper = OfflineModeHelperInstanceLease.TryAcquire(store, out var helperError);
        if (helper is null) return OperationResult.Fail("脱机助手未排空：" + helperError);
        using var operation = await OfflineModeExecutionLock.WaitAcquireAsync(store, timeout, token);
        if (operation is null) return OperationResult.Fail("脱机操作锁仍被占用，未执行恢复。");
        token.ThrowIfCancellationRequested();
        if (!store.TryRead(out var state, out var error)) return OperationResult.Fail(error);
        if (!state.CancelRequested || state.Status != OfflineModeStatus.RestorePending)
            return OperationResult.Fail("未进入取消并待恢复状态，拒绝脱机 CPU Sets 恢复。");
        if (state.HelperProcessId is int pid && state.HelperStartTimeUtcTicks is long started)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == started)
                    return OperationResult.Fail("登记的脱机助手仍在运行，未执行恢复。");
            }
            catch (ArgumentException) { /* Recorded process has exited. */ }
            catch (Exception ex) { return OperationResult.Fail("无法确认登记助手已退出：" + ex.Message); }
        }
        var settings = AppSettingsStore.Read();
        if (settings.FrameModeActive || settings.GamePriorityEnabled || settings.GameAffinityRuleEnabled
            || settings.SingleCcdExcludeCpu0Enabled || settings.DualCcdImmediateEnabled)
            return OperationResult.Fail("普通帧格或 CPU 自动调度未停用，拒绝并发恢复。");
        var result = (restore ?? (() => new OfflineCpuSetScheduler().RestoreRecordedAssignments(token)))();
        Log.Info("后端脱机 CPU Sets 恢复：success=" + result.Success + ", message=" + result.Message);
        return result;
    }
}
