using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>
/// 未知操作恢复闭环的前端协调器：只读核对、按可信备份恢复、恢复后复核并归档证据。
/// 恢复动作由后端按自身已知的范围 id 解析执行；前端只做选择与展示，不传任意命令或路径。
/// 恢复未通过复核时不归档、不解除阻塞；失败可重试（原始记录保留）。
/// 核对/恢复使用的临时后端会话在结束后立即释放，避免干扰「保留当前状态」的人工核对。
/// </summary>
internal static class BackendRecovery
{
    internal static bool CanKeepCurrentState(BackendUnconfirmedStore.Review review, ReconcileSnapshot? snapshot) =>
        review.CanAcknowledge && (review.Module != BackendModules.Ace
            || snapshot is { Complete: true } && snapshot.Module == BackendModules.Ace);
    private static bool CanRestoreCpuRecord(string command) => BackendModules.IsReadOnly(BackendModules.CpuFrame, command)
        || command is BackendCommands.CpuFrameMonitorStart or BackendCommands.CpuFrameMonitorStop
        or BackendCommands.CpuFramePriorityApply or BackendCommands.CpuFrameAffinityApply
        or BackendCommands.CpuFrameCcdApply or BackendCommands.CpuFrameCcdRevert
        or BackendCommands.CpuFrameSingleCcdApply or BackendCommands.CpuFrameSingleCcdRevert
        or BackendCommands.CpuFrameRestore or BackendCommands.CpuFrameRecover
        or BackendCommands.CpuFramePowerLock or BackendCommands.CpuFramePowerUnlock
        or BackendCommands.CpuFrameTimerApply or BackendCommands.CpuFrameTimerRestore
        or BackendCommands.CpuFrameTweaksApply or BackendCommands.CpuFrameTweaksRevert or BackendCommands.CpuFrameBoostApply;

    internal static IReadOnlyList<RecoveryScope> RecoveryChoices(BackendUnconfirmedStore.Review review, ReconcileSnapshot? snapshot)
    {
        var module = review.Module;
        if (snapshot is null || !snapshot.Complete || snapshot.Module != module) return [];
        if (module == BackendModules.CpuFrame)
            return CanRestoreCpuRecord(review.Command) && snapshot.RecoveryScopes.Any(s => s.Available)
                ? [new RecoveryScope { Id = "all", Description = "完整恢复 CPU/帧格状态并复核", Available = true }] : [];
        return snapshot.RecoveryScopes.Where(s => s.Available).ToArray();
    }
    /// <summary>只读核对：返回模块当前实际状态与可用恢复范围；模块未提供核对命令时返回 null。</summary>
    public static async Task<ReconcileSnapshot?> ReconcileAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        var command = BackendModules.ReconcileCommand(moduleId);
        if (command is null) return null;
        var caller = new BackendCaller(moduleId);
        try
        {
            var result = await caller.InvokeReconcileAsync(command, new { }, null, cancellationToken);
            return BackendResults.RequireData<ReconcileSnapshot>(result, "只读状态核对");
        }
        finally
        {
            if (!await caller.ResetAsync()) throw new InvalidOperationException("核对后端未确认安全停止，恢复证据保留。");
        }
    }

    /// <summary>
    /// 按可信备份恢复指定范围并复核。仅在复核通过时归档原始记录并释放会话；
    /// 复核失败或执行失败一律保留阻塞，玩家可重试。
    /// </summary>
    public static async Task<RecoverOutcome> RestoreAsync(BackendUnconfirmedStore.Review review, string scope,
        CancellationToken cancellationToken = default)
    {
        var current = BackendUnconfirmedStore.ReviewAll().SingleOrDefault(r => r.Module == review.Module);
        if (current is null || current.Fingerprint != review.Fingerprint || !current.CanRestore)
            throw new InvalidOperationException("记录已变化或原后端、子进程尚未确认退出，请重新核对。");
        if (review.Module == BackendModules.CpuFrame && (scope != "all" || !CanRestoreCpuRecord(review.Command)))
            throw new InvalidOperationException("该 CPU/帧格记录需要对应的可信完整恢复路径。单个子范围、不可回滚操作或缺少命令的旧记录不能自动解除阻塞，请人工核对。");
        var command = BackendModules.RecoverCommand(review.Module)
                      ?? throw new InvalidOperationException($"模块 {review.Module} 没有可信备份恢复路径。");
        var caller = new BackendCaller(review.Module);
        RecoverOutcome outcome;
        try
        {
            var result = await caller.InvokeReconcileAsync(command, new RecoverArgs { Scope = scope }, null, cancellationToken);
            outcome = BackendResults.RequireData<RecoverOutcome>(result, "按可信备份恢复");
        }
        finally
        {
            if (!await caller.ResetAsync()) throw new InvalidOperationException("恢复后端未确认安全停止或收尾失败，原始证据保留。");
        }

        var verified = outcome.Success && outcome.Verified && outcome.Scope == scope
            && outcome.VerifiedSnapshot is { Complete: true } snapshot && snapshot.Module == review.Module
            && (review.Module != BackendModules.CpuFrame || !snapshot.RecoveryScopes.Any(s => s.Available));
        if (verified)
        {
            BackendUnconfirmedStore.ArchiveAfterRecovery(review, scope, verified: true, outcome.Message, outcome.VerifiedSnapshot);
            await BackendLifecycle.ReleaseRecoveredSessionsAsync(review.Module);
            Log.Warn($"恢复闭环：模块 {review.Module} 范围 {scope} 复核通过并已归档；原值备份未删除。");
        }
        else
        {
            Log.Warn($"恢复闭环：模块 {review.Module} 范围 {scope} 未通过复核，保留阻塞与原始证据。");
        }
        return new RecoverOutcome
        {
            Scope = outcome.Scope, Success = outcome.Success, Verified = verified,
            Message = outcome.Message + (verified ? "" : "\n恢复结果或完整状态未通过复核，旧记录仍保留。"),
            VerifiedSnapshot = outcome.VerifiedSnapshot,
        };
    }
}
