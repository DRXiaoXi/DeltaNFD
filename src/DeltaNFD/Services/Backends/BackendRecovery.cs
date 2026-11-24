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
    private const string DisplayContainerScope = NvDisplayContainerChange.RecoveryScope;

    internal static bool IsStartupOperation(BackendUnconfirmedStore.Review review) => review.Module == BackendModules.System
        && review.Command is BackendCommands.SystemStartupChange or BackendCommands.SystemStartupRestoreAll;
    internal static bool IsClockEnvironmentOperation(BackendUnconfirmedStore.Review review) => review.Module == BackendModules.System
        && review.Command == BackendCommands.SystemClockEnvironmentChange;
    internal static bool IsDisplayInterruptOperation(BackendUnconfirmedStore.Review review) => review.Module == BackendModules.System
        && review.Command == BackendCommands.SystemDisplayInterruptChange;
    internal static bool IsRuntimeFileReplacement(BackendUnconfirmedStore.Review review) =>
        review.Module == BackendModules.Runtime && review.Command == BackendCommands.Vc14ForceReplace;
    internal static string? NvSettingsReviewScope(BackendUnconfirmedStore.Review review) => review.Module == BackendModules.Gpu
        ? review.Command switch
        {
            BackendCommands.GpuDlssIndicatorSet => "dlss-indicator",
            BackendCommands.GpuNvDisplayContainerChange => DisplayContainerScope,
            BackendCommands.GpuDriverApply or BackendCommands.GpuDriverRestore or BackendCommands.GpuDriverProfileReset => "driver-settings",
            _ => null,
        } : review.Module == BackendModules.System ? review.Command switch
        {
            BackendCommands.SystemNvDisplayContainerChange or BackendCommands.SystemNvDisplayContainerRecover => DisplayContainerScope,
            BackendCommands.SystemNvTelemetryChange or BackendCommands.SystemNvTelemetryRecover => NvTelemetryContainer.RecoveryScope,
            BackendCommands.SystemNvLocalSystemChange or BackendCommands.SystemNvLocalSystemRecover => NvLocalSystemContainer.RecoveryScope,
            _ => null,
        } : null;

    internal static string? NvServiceReviewScope(BackendUnconfirmedStore.Review review)
    {
        var scope = NvSettingsReviewScope(review);
        return scope is DisplayContainerScope or NvTelemetryContainer.RecoveryScope or NvLocalSystemContainer.RecoveryScope ? scope : null;
    }
    private static bool IsNonSpoofGpuOperation(BackendUnconfirmedStore.Review review) => review.Module == BackendModules.Gpu &&
        (NvSettingsReviewScope(review) is not null || review.Command is BackendCommands.GpuDriverProfileReset
            or BackendCommands.GpuDriverLock or BackendCommands.GpuShaderCacheApply);

    internal static bool CanKeepCurrentState(BackendUnconfirmedStore.Review review, ReconcileSnapshot? snapshot) =>
        review.CanAcknowledge && !IsRuntimeInstallation(review) && (NvServiceReviewScope(review) is { } scope
            ? snapshot is { Complete: true } && IsNvServiceSnapshot(snapshot, review.Module, scope)
            : IsDisplayInterruptOperation(review)
                ? snapshot is { Complete: true, Module: BackendModules.System,
                    RecoveryScopes: [{ Id: DisplayInterruptPriorityPolicy.RecoveryScope }] }
                : review.Module != BackendModules.Ace || snapshot is { Complete: true } && snapshot.Module == review.Module);

    private static bool IsNvServiceSnapshot(ReconcileSnapshot snapshot, string module, string scope) => snapshot.Module == module
        && snapshot.Items is [{ Kind: "service" } item] && item.Id == scope;
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
        if (NvServiceReviewScope(review) is { } serviceScope)
            return snapshot is { Complete: true, Items: [{ HasBackup: true }] } && IsNvServiceSnapshot(snapshot, module, serviceScope)
                && snapshot.RecoveryScopes is [{ Available: true } choice] && choice.Id == serviceScope
                && NvDisplayContainerChange.IsFingerprint(choice.Fingerprint) ? [choice] : [];
        // DRS/NGX changes cannot be reconciled by restoring a display-name spoof.
        if (IsNonSpoofGpuOperation(review)) return [];
        if (snapshot is null || !snapshot.Complete || snapshot.Module != module) return [];
        if (IsStartupOperation(review)) return snapshot.RecoveryScopes.Where(s => s.Available && s.Id == "startup").ToArray();
        if (BackendUnconfirmedStore.IsRetiredDriverPriorityOperation(review)) return [];
        if (IsClockEnvironmentOperation(review)) return snapshot.RecoveryScopes.Where(s => s.Available && s.Id == "clock-environment").ToArray();
        if (IsDisplayInterruptOperation(review)) return snapshot.RecoveryScopes.Where(s => s.Available && s.Id == DisplayInterruptPriorityPolicy.RecoveryScope).ToArray();
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
        if (moduleId == BackendModules.System && scope is NvTelemetryContainer.RecoveryScope or NvLocalSystemContainer.RecoveryScope)
        {
            var reconcile = scope == NvTelemetryContainer.RecoveryScope
                ? BackendCommands.SystemNvTelemetryReconcile : BackendCommands.SystemNvLocalSystemReconcile;
            var snapshot = await ReadSnapshotAsync(moduleId, reconcile, new { }, cancellationToken);
            if (!IsNvServiceSnapshot(snapshot, moduleId, scope))
                throw new InvalidDataException("后端返回了不匹配的 NVIDIA 服务核对范围。");
            return snapshot;
        }
        if (scope == DisplayContainerScope && moduleId is BackendModules.Gpu or BackendModules.System)
        {
            try
            {
                var independent = await ReadSnapshotAsync(BackendModules.System,
                    BackendCommands.SystemNvDisplayContainerReconcile, new NvDisplayContainerReviewArgs(moduleId), cancellationToken);
                ValidateDisplayContainerSnapshot(independent, moduleId);
                Log.Info($"显示容器已通过系统维护后端完成独立只读读取；状态已确认={independent.Complete}。");
                return independent;
            }
            catch (Exception ex) when (moduleId == BackendModules.Gpu && !cancellationToken.IsCancellationRequested)
            {
                Log.Warn("显示容器独立核对不可用，尝试显卡后端只读核对：" + ex.Message);
                try
                {
                    var fallback = await ReadSnapshotAsync(moduleId, command, new RecoverArgs { Scope = scope }, cancellationToken);
                    ValidateDisplayContainerSnapshot(fallback, moduleId);
                    return fallback;
                }
                catch (Exception fallback) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException($"两条显示容器只读核对路径均失败。系统维护：{ex.Message}；显卡：{fallback.Message}", fallback);
                }
            }
        }
        return await ReadSnapshotAsync(moduleId, command, new RecoverArgs { Scope = scope ?? "" }, cancellationToken);
    }

    private static void ValidateDisplayContainerSnapshot(ReconcileSnapshot snapshot, string module)
    {
        if (!IsNvServiceSnapshot(snapshot, module, DisplayContainerScope))
            throw new InvalidDataException("后端返回了不匹配的显示容器核对范围。");
    }

    private static async Task<ReconcileSnapshot> ReadSnapshotAsync(string moduleId, string command, object args,
        CancellationToken cancellationToken)
    {
        var caller = new BackendCaller(moduleId);
        try
        {
            var result = await caller.InvokeReconcileAsync(command, args, null, cancellationToken);
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
        CancellationToken cancellationToken = default, IReadOnlyList<FrameIncompletePowerBackup>? confirmedPower = null,
        ReconcileSnapshot? reviewedSnapshot = null)
    {
        if (IsNonSpoofGpuOperation(review) && NvServiceReviewScope(review) is null)
            throw new InvalidOperationException("此 NVIDIA 设置操作不能通过显卡型号备份恢复，请核对实际状态。");
        if (IsRuntimeFileReplacement(review))
            throw new InvalidOperationException("系统 DLL 替换不能使用运行库防护恢复。请按强制替换备份目录中的 restore.ps1/README 手动恢复并核对文件，再确认保留当前状态；旧记录保留。");
        if (review.Module == BackendModules.System && review.Command == BackendCommands.SystemPendulumDriverPrepare)
            throw new InvalidOperationException("驱动准备涉及证书与启动配置，不能通过无关系统优化范围解除记录。请核对准备子进程、专用记录与BCD备份；确认保留当前状态后可按原授权继续准备。");
        if (IsStartupOperation(review) && scope != "startup")
            throw new InvalidOperationException("自启动操作只能按自启动原始备份核对恢复，不能通过其他系统优化范围解除阻塞。");
        if (BackendUnconfirmedStore.IsRetiredDriverPriorityOperation(review))
            throw new InvalidOperationException("本版本已移除驱动优先级恢复。请用原版本与原始备份恢复设备中断设置。");
        if (IsClockEnvironmentOperation(review) && scope != "clock-environment")
            throw new InvalidOperationException("计时源操作只能按计时源与 HPET 原始备份核对恢复。");
        if (IsDisplayInterruptOperation(review) && scope != DisplayInterruptPriorityPolicy.RecoveryScope)
            throw new InvalidOperationException("显卡中断操作仅可按显卡中断优先级备份恢复。");
        var current = BackendUnconfirmedStore.ReviewAll().SingleOrDefault(r => r.Module == review.Module);
        if (current is null || current.Fingerprint != review.Fingerprint || !current.CanRestore)
            throw new InvalidOperationException("记录已变化或原后端、子进程尚未确认退出，请重新核对。");
        var nvServiceScope = NvServiceReviewScope(current);
        NvDisplayContainerRecoveryArgs? nvServiceArgs = null;
        if (nvServiceScope is not null)
        {
            if (scope != nvServiceScope || RecoveryChoices(current, reviewedSnapshot) is not [{ Fingerprint: { } fingerprint }])
                throw new InvalidOperationException("请先核对并确认对应 NVIDIA 服务的可恢复备份。");
            if (current.Module != BackendModules.System && BackendUnconfirmedStore.PendingModules().Contains(BackendModules.System))
                throw new InvalidOperationException("请先处理系统维护后端的待核对记录，再恢复显示容器。");
            nvServiceArgs = new NvDisplayContainerRecoveryArgs(current.Module, fingerprint);
        }
        if (nvServiceScope is null && IsNonSpoofGpuOperation(current))
            throw new InvalidOperationException("此 NVIDIA 设置操作不能通过显卡型号备份恢复，请核对实际状态。");
        if (review.Module == BackendModules.CpuFrame && (scope != "all" || !CanRestoreCpuRecord(review.Command)))
            throw new InvalidOperationException("该 CPU/帧格记录需要对应的可信完整恢复路径。单个子范围、不可回滚操作或缺少命令的旧记录不能自动解除阻塞，请人工核对。");
        if (review.Module == BackendModules.Runtime && !(IsRuntimeInstallation(review) ? IsRuntimeInstallationScope(scope) : scope == "guard"))
            throw new InvalidOperationException("运行库安装记录必须核对事务结束；防护记录必须按备份恢复，不能混用解除路径。");
        var command = nvServiceScope switch
        {
            DisplayContainerScope => BackendCommands.SystemNvDisplayContainerRecover,
            NvTelemetryContainer.RecoveryScope => BackendCommands.SystemNvTelemetryRecover,
            NvLocalSystemContainer.RecoveryScope => BackendCommands.SystemNvLocalSystemRecover,
            _ => BackendModules.RecoverCommand(review.Module)
                ?? throw new InvalidOperationException($"模块 {review.Module} 没有可信备份恢复路径。"),
        };
        if (confirmedPower is not null && (review.Module != BackendModules.CpuFrame || scope != "all" || confirmedPower.Count is < 1 or > 3))
            throw new InvalidOperationException("电源记录确认范围无效，未处理。");
        var caller = new BackendCaller(nvServiceScope is not null ? BackendModules.System : review.Module);
        RecoverOutcome outcome;
        try
        {
            object args = (object?)nvServiceArgs ?? (confirmedPower is null ? (object)new RecoverArgs { Scope = scope }
                : new { Scope = "power-preserve", Confirmed = confirmedPower });
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
            && (nvServiceScope is null || IsNvServiceSnapshot(snapshot, review.Module, nvServiceScope) && snapshot.Items is [{ HasBackup: false }]
                && snapshot.RecoveryScopes.Count == 0)
            && (!IsStartupOperation(review) || snapshot.RecoveryScopes.Any(s => s.Id == "startup" && !s.Available && s.AffectedCount == 0))
            && (!IsClockEnvironmentOperation(review) || snapshot.RecoveryScopes.Any(s => s.Id == "clock-environment" && !s.Available && s.AffectedCount == 0))
            && (!IsDisplayInterruptOperation(review) || snapshot.RecoveryScopes is [{ Id: DisplayInterruptPriorityPolicy.RecoveryScope, Available: false, AffectedCount: 0 }])
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

    internal static bool CanAutoRestoreFrame(BackendUnconfirmedStore.Review review) =>
        review.Module == BackendModules.CpuFrame && review.CanRestore && !review.ChildrenAlive && !review.ChildrenUnknown
        && review.Command is BackendCommands.CpuFrameRestore or BackendCommands.CpuFrameRecover
            or BackendCommands.CpuFramePowerLock or BackendCommands.CpuFramePowerUnlock
            or BackendCommands.CpuFrameTweaksApply or BackendCommands.CpuFrameTweaksRevert;

    internal static async Task<bool> TryRecoverStartupFrameAsync()
    {
        using var pauseGate = BackendOperationGate.BlockNewOperations();
        using var control = BackendOperationGate.AllowControlOperations();
        var reviews = await BackendLifecycle.PrepareReviewAsync([BackendModules.CpuFrame]);
        await BackendOperationGate.Handover.WaitAsync();
        try { return await RecoverStoppedFrameAsync(reviews.SingleOrDefault()); }
        finally { BackendOperationGate.Handover.Release(); }
    }

    private static async Task<bool> RecoverStoppedFrameAsync(BackendUnconfirmedStore.Review? review)
    {
        if (review is not null && !CanAutoRestoreFrame(review)) return false;
        if (BackendSessions.Snapshot().Any(c => c.ModuleId == BackendModules.CpuFrame && !c.ExitConfirmed)
            || HasUntrackedFrameBackend()) return false;

        var caller = new BackendCaller(BackendModules.CpuFrame);
        try
        {
            var read = await caller.InvokeReconcileAsync(BackendCommands.CpuFramePowerIncomplete, new { }, null, default);
            if (BackendResults.RequireData<List<FrameIncompletePowerBackup>>(read, "检查电源恢复记录").Count != 0)
                return false; // The user's consent is required; never archive missing originals automatically.
            if (review is null)
            {
                var result = await caller.InvokeReconcileAsync(BackendCommands.CpuFrameRecover,
                    new RecoverArgs { Scope = "all" }, null, default);
                var outcome = BackendResults.RequireData<RecoverOutcome>(result, "恢复上次帧格状态");
                return outcome is { Scope: "all", Success: true, Verified: true, VerifiedSnapshot: { Complete: true } snapshot }
                    && snapshot.Module == BackendModules.CpuFrame && !snapshot.RecoveryScopes.Any(s => s.Available);
            }
        }
        finally
        {
            if (!await caller.ResetAsync()) throw new InvalidOperationException("帧格恢复后端未确认退出，保留记录。");
        }
        return (await RestoreAsync(review, "all")).Verified;
    }

    private static bool HasUntrackedFrameBackend()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName(
            Path.GetFileNameWithoutExtension(BackendModules.Require(BackendModules.CpuFrame).ExecutableName));
        try { return processes.Any(p => !p.HasExited); }
        catch (Exception ex) { Log.Warn("无法确认旧帧格后端已经退出：" + ex.Message); return true; }
        finally { foreach (var process in processes) process.Dispose(); }
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
