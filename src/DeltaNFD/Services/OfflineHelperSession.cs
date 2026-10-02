namespace DeltaNFD.Services;

/// <summary>一次性助手交接的代次与身份；旧回调不能覆盖取消/恢复或新一轮状态。</summary>
public static class OfflineHelperSession
{
    public static bool CanExecute(OfflineModeState state, Guid runId) => runId != Guid.Empty &&
        state.HelperRunId == runId && state.OfflineModeEnabled && !state.CancelRequested && state.SavedPreferences is not null &&
        state.Status is OfflineModeStatus.WaitingForGame or OfflineModeStatus.Applying;

    public static bool TryRegister(OfflineModeStateStore store, Guid runId, int pid, long startTicks, out string error) =>
        store.TryUpdate(state =>
        {
            if (!CanExecute(state, runId) || (state.HelperProcessId.HasValue &&
                (state.HelperProcessId != pid || state.HelperStartTimeUtcTicks != startTicks)))
                throw new InvalidOperationException("助手交接已取消或代次/身份不匹配。");
            state.HelperProcessId = pid;
            state.HelperStartTimeUtcTicks = startTicks;
            state.Status = OfflineModeStatus.WaitingForGame;
            state.HelperOperationCompleted = false;
            state.Message = "脱机助手已登记，等待游戏启动。";
        }, out error);

    public static bool TryProgress(OfflineModeStateStore store, Guid runId, int pid, long startTicks,
        OfflineModeStatus status, string message, bool completed, OfflineModeStatus completionStatus, out string error) =>
        store.TryUpdate(state =>
        {
            if (!CanExecute(state, runId) || state.HelperProcessId != pid || state.HelperStartTimeUtcTicks != startTicks)
                throw new InvalidOperationException("旧助手或已取消的操作不能覆盖当前状态。");
            state.Status = status;
            state.Message = message;
            state.HelperOperationCompleted = completed;
            state.HelperCompletionStatus = completionStatus;
        }, out error);
}
