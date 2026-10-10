namespace DeltaNFD.Services;

internal static class OfflineRestoreHandover
{
    internal sealed record Leases(OfflineModeHelperInstanceLease Helper, OfflineModeExecutionLock Operation) : IDisposable
    {
        public void Dispose() { Operation.Dispose(); Helper.Dispose(); }
    }

    internal static async Task<(OperationResult Result, Leases? Locks)> RunAsync(
        OfflineModeStateStore store, OfflineModeExecutionLock operation, OfflineModeHelperInstanceLease helper,
        Func<CancellationToken, Task<OperationResult>> invoke, TimeSpan timeout, CancellationToken token)
    {
        operation.Dispose(); helper.Dispose();
        OfflineModeHelperInstanceLease? helperLease = null;
        OfflineModeExecutionLock? operationLease = null;
        try
        {
            var result = await invoke(token);
            if (!result.Success) return (result, null);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < timeout)
            {
                token.ThrowIfCancellationRequested();
                helperLease = OfflineModeHelperInstanceLease.TryAcquire(store, out _);
                if (helperLease is not null) break;
                await Task.Delay(TimeSpan.FromMilliseconds(50), token);
            }
            if (helperLease is null) return (OperationResult.Fail("恢复后助手锁未排空，未更新模式状态。"), null);
            operationLease = await OfflineModeExecutionLock.WaitAcquireAsync(store, timeout, token);
            if (operationLease is null) return (OperationResult.Fail("恢复后操作锁未排空，未更新模式状态。"), null);
            token.ThrowIfCancellationRequested();
            if (!store.TryRead(out var state, out var error) || state.CpuSetChanges.Count != 0
                || !state.CancelRequested || state.Status != OfflineModeStatus.RestorePending)
                return (OperationResult.Fail("恢复记录或模式状态复核未通过：" + error), null);
            var leases = new Leases(helperLease, operationLease);
            helperLease = null; operationLease = null;
            return (result, leases);
        }
        catch (Exception ex)
        {
            return (OperationResult.Fail("后端恢复交接未确认：" + ex.Message + "；原值记录保留。"), null);
        }
        finally { operationLease?.Dispose(); helperLease?.Dispose(); }
    }
}
