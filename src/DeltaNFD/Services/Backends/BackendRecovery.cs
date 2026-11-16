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
    internal static bool IsStartupOperation(BackendUnconfirmedStore.Review review) => review.Module == BackendModules.System
        && review.Command is BackendCommands.SystemStartupChange or BackendCommands.SystemStartupRestoreAll;
    internal static bool IsDriverPriorityOperation(BackendUnconfirmedStore.Review review) => review.Module == BackendModules.System
        && review.Command is BackendCommands.SystemDriverPriorityChange or BackendCommands.SystemDriverPriorityRestoreAll;
    internal static bool IsClockEnvironmentOperation(BackendUnconfirmedStore.Review review) => review.Module == BackendModules.System
        && review.Command == BackendCommands.SystemClockEnvironmentChange;
    internal static bool IsRuntimeFileReplacement(BackendUnconfirmedStore.Review review) =>
        review.Module == BackendModules.Runtime && review.Command == BackendCommands.Vc14ForceReplace;
    internal static bool CanKeepCurrentState(BackendUnconfirmedStore.Review review, ReconcileSnapshot? snapshot) =>
        review.CanAcknowledge && !IsRuntimeInstallation(review) && (review.Module != BackendModules.Ace
            || snapshot is { Complete: true } && snapshot.Module == BackendModules.Ace);
    internal static bool IsRuntimeInstallation(BackendUnconfirmedStore.Review review) => review.Module == BackendModules.Runtime
        && review.Command is BackendCommands.RuntimeRepair or BackendCommands.RuntimeCompatibilityRepair
            or BackendCommands.RuntimeRedistUninstall or BackendCommands.RuntimeRedistUninstallAll or BackendCommands.Vc14Repair or BackendCommands.Vc14LegacyClean
            or BackendCommands.RuntimeHighFpsRepair or BackendCommands.RuntimeSafeUninstall;
    internal static bool IsRuntimeInstallationScope(string scope) => scope is "repair-review" or "repair-restart-checkpoint";
    private static bool CanRestoreCpuRecord(string command) => BackendModules.IsReadOnly(BackendModules.CpuFrame, command)
        || command is BackendCommands.CpuFrameMonitorStart or BackendCommands.CpuFrameMonitorStop
        or BackendCommands.CpuFramePriorityApply or BackendCommands.CpuFrameAffinityApply
        or BackendCommands.CpuFrameCcdApply or BackendCommands.CpuFrameCcdRevert
        or BackendCommands.CpuFrameSingleCcdApply or BackendCommands.CpuFrameSingleCcdRevert
        or BackendCommands.CpuFrameRestore or BackendCommands.CpuFrameRecover
        or BackendCommands.CpuFramePowerLock or BackendCommands.CpuFramePowerUnlock
        or BackendCommands.CpuFrameTweaksApply or BackendCommands.CpuFrameTweaksRevert or BackendCommands.CpuFrameGamePowerApply
        // Historical records remain recoverable; these retired commands are not execution entries.
        or "cpuframe.boost.apply" or "cpuframe.timer.apply" or "cpuframe.timer.restore";

    internal static IReadOnlyList<RecoveryScope> RecoveryChoices(BackendUnconfirmedStore.Review review, ReconcileSnapshot? snapshot)
    {
        var module = review.Module;
        if (IsRuntimeFileReplacement(review)) return []; // Manual DLL restoration is not IFEO/UE4 guard recovery.
        if (module == BackendModules.Runtime && review.Command == BackendCommands.RuntimeFileUseClose) return [];
        if (module == BackendModules.Procman && review.Command == BackendCommands.ProcmanMemoryTrim) return [];
        // A driver profile reset has no relation to the display-name spoof backup.
        if (module == BackendModules.Gpu && review.Command == BackendCommands.GpuDriverProfileReset) return [];
        if (snapshot is null || !snapshot.Complete || snapshot.Module != module) return [];
        if (IsStartupOperation(review)) return snapshot.RecoveryScopes.Where(s => s.Available && s.Id == "startup").ToArray();
        if (IsDriverPriorityOperation(review)) return snapshot.RecoveryScopes.Where(s => s.Available && s.Id == "driver-priority").ToArray();
        if (IsClockEnvironmentOperation(review)) return snapshot.RecoveryScopes.Where(s => s.Available && s.Id == "clock-environment").ToArray();
        // Boot/trust preparation is not restored by unrelated tweak/startup scopes.
        if (review.Module == BackendModules.System && review.Command == BackendCommands.SystemPendulumDriverPrepare) return [];
        if (module == BackendModules.Runtime)
            return snapshot.RecoveryScopes.Where(s => s.Available && (IsRuntimeInstallation(review)
                ? IsRuntimeInstallationScope(s.Id) : s.Id == "guard")).ToArray();
        if (module == BackendModules.CpuFrame)
            return CanRestoreCpuRecord(review.Command) && snapshot.RecoveryScopes.Any(s => s.Available)
                ? [new RecoveryScope { Id = "all", Description = "完整恢复 CPU/帧格状态并复核", Available = true }] : [];
        return snapshot.RecoveryScopes.Where(s => s.Available).ToArray();
    }
    /// <summary>只读核对：返回模块当前实际状态与可用恢复范围；模块未提供核对命令时返回 null。</summary>
    public static async Task<ReconcileSnapshot?> ReconcileAsync(string moduleId, CancellationToken cancellationToken = default, string? scope = null)
    {
        var command = BackendModules.ReconcileCommand(moduleId);
        if (command is null) return null;
        var caller = new BackendCaller(moduleId);
        try
        {
            var result = await caller.InvokeReconcileAsync(command, new RecoverArgs { Scope = scope ?? "" }, null, cancellationToken);
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
        CancellationToken cancellationToken = default, IReadOnlyList<FrameIncompletePowerBackup>? confirmedPower = null)
    {
        if (IsRuntimeFileReplacement(review))
            throw new InvalidOperationException("系统 DLL 替换不能使用运行库防护恢复。请按强制替换备份目录中的 restore.ps1/README 手动恢复并核对文件，再确认保留当前状态；旧记录保留。");
        if (review.Module == BackendModules.System && review.Command == BackendCommands.SystemPendulumDriverPrepare)
            throw new InvalidOperationException("驱动准备涉及证书与启动配置，不能通过无关系统优化范围解除记录。请核对准备子进程、专用记录与BCD备份；确认保留当前状态后可按原授权继续准备。");
        if (IsStartupOperation(review) && scope != "startup")
            throw new InvalidOperationException("自启动操作只能按自启动原始备份核对恢复，不能通过其他系统优化范围解除阻塞。");
        if (IsDriverPriorityOperation(review) && scope != "driver-priority")
            throw new InvalidOperationException("驱动中断操作只能使用设备中断原始备份恢复，不能通过其他优化范围解除记录。");
        if (IsClockEnvironmentOperation(review) && scope != "clock-environment")
            throw new InvalidOperationException("计时源操作只能按计时源与 HPET 原始备份核对恢复。");
        var current = BackendUnconfirmedStore.ReviewAll().SingleOrDefault(r => r.Module == review.Module);
        if (current is null || current.Fingerprint != review.Fingerprint || !current.CanRestore)
            throw new InvalidOperationException("记录已变化或原后端、子进程尚未确认退出，请重新核对。");
        if (review.Module == BackendModules.CpuFrame && (scope != "all" || !CanRestoreCpuRecord(review.Command)))
            throw new InvalidOperationException("该 CPU/帧格记录需要对应的可信完整恢复路径。单个子范围、不可回滚操作或缺少命令的旧记录不能自动解除阻塞，请人工核对。");
        if (review.Module == BackendModules.Runtime && !(IsRuntimeInstallation(review) ? IsRuntimeInstallationScope(scope) : scope == "guard"))
            throw new InvalidOperationException("运行库安装记录必须核对事务结束；防护记录必须按备份恢复，不能混用解除路径。");
        var command = BackendModules.RecoverCommand(review.Module)
                      ?? throw new InvalidOperationException($"模块 {review.Module} 没有可信备份恢复路径。");
        if (confirmedPower is not null && (review.Module != BackendModules.CpuFrame || scope != "all" || confirmedPower.Count is < 1 or > 3))
            throw new InvalidOperationException("电源记录确认范围无效，未处理。");
        var caller = new BackendCaller(review.Module);
        RecoverOutcome outcome;
        try
        {
            object args = confirmedPower is null ? new RecoverArgs { Scope = scope }
                : new { Scope = "power-preserve", Confirmed = confirmedPower };
            var result = await caller.InvokeReconcileAsync(command, args, null, cancellationToken);
            outcome = BackendResults.RequireData<RecoverOutcome>(result, "按可信备份恢复");
        }
        finally
        {
            if (!await caller.ResetAsync()) throw new InvalidOperationException("恢复后端未确认安全停止或收尾失败，原始证据保留。");
        }

        var checkpoint = review.Module == BackendModules.Runtime && scope == "repair-restart-checkpoint";
        var verified = !checkpoint && outcome.Success && outcome.Verified && outcome.Scope == scope
            && outcome.VerifiedSnapshot is { Complete: true } snapshot && snapshot.Module == review.Module
            && (!IsStartupOperation(review) || snapshot.RecoveryScopes.Any(s => s.Id == "startup" && !s.Available && s.AffectedCount == 0))
            && (!IsDriverPriorityOperation(review) || snapshot.RecoveryScopes.Any(s => s.Id == "driver-priority" && !s.Available && s.AffectedCount == 0))
            && (!IsClockEnvironmentOperation(review) || snapshot.RecoveryScopes.Any(s => s.Id == "clock-environment" && !s.Available && s.AffectedCount == 0))
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
            Message = outcome.Message + (verified || checkpoint ? "" : "\n恢复结果或完整状态未通过复核，旧记录仍保留。"),
            VerifiedSnapshot = outcome.VerifiedSnapshot,
        };
    }

    internal static async Task<IReadOnlyList<FrameIncompletePowerBackup>> ReadPowerReviewAsync(BackendUnconfirmedStore.Review review)
    {
        var current = BackendUnconfirmedStore.ReviewAll().SingleOrDefault(r => r.Module == BackendModules.CpuFrame);
        if (review.Module != BackendModules.CpuFrame || current is null || !current.CanRestore || current.Fingerprint != review.Fingerprint)
            throw new InvalidOperationException("原执行者或记录尚未确认，不能读取处理范围。");
        var caller = new BackendCaller(BackendModules.CpuFrame);
        try
        {
            var result = await caller.InvokeReconcileAsync(BackendCommands.CpuFramePowerIncomplete, new { }, null, CancellationToken.None);
            return BackendResults.RequireData<List<FrameIncompletePowerBackup>>(result, "读取旧电源备份");
        }
        finally { if (!await caller.ResetAsync()) throw new InvalidOperationException("电源核对后端尚未安全退出。"); }
    }
}
