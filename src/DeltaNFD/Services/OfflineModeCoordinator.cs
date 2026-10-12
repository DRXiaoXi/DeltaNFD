using System.Diagnostics;
using RestoreLocks = DeltaNFD.Services.OfflineRestoreHandover.Leases;

namespace DeltaNFD.Services;

/// <summary>已移除功能的旧数据恢复；启用和启动入口仅拒绝执行。</summary>
public sealed class OfflineModeCoordinator
{
    private readonly OfflineModeStateStore _store;

    public OfflineModeCoordinator() : this(OfflineModeStateStore.Default) { }
    internal OfflineModeCoordinator(OfflineModeStateStore store) => _store = store;

    public bool TryReadState(out OfflineModeState state, out string error)
    {
        if (!_store.TryRead(out state, out error)) return false;
        OfflineHelperCompletion.TryConfirmExited(_store);
        return _store.TryRead(out state, out error);
    }

    public Task<OperationResult> EnableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Fail("脱机模式已移除，不能启用。"));

    public async Task<OperationResult> DisableAsync(CancellationToken cancellationToken = default)
    {
        using var backendGate = Backends.BackendOperationGate.BlockNewOperations();
        using var backendControl = Backends.BackendOperationGate.AllowControlOperations();
        using var control = await OfflineModeExecutionLock.WaitControlAsync(_store, TimeSpan.FromSeconds(10), cancellationToken);
        if (control is null) return OperationResult.Fail("另一个脱机切换操作尚未完成。");
        if (!_store.TryRead(out var state, out var error)) return OperationResult.Fail(error);
        if (!state.BlocksNormalAutomation)
            return OperationResult.Ok("脱机模式本就未启用。");
        if (state.SavedPreferences is null)
            return OperationResult.Fail("缺少普通模式原配置快照；为避免猜测状态，未退出脱机模式。");

        var preferences = state.SavedPreferences.Copy();
        var helperPid = state.HelperProcessId;
        var helperStartTicks = state.HelperStartTimeUtcTicks;
        var helperPath = OfficialHelperPaths.ExecutablePath;

        // 先请求取消等待/应用中的短时助手。助手仅在一次性动作阶段持有操作锁，
        // 通过状态文件取消并等待退出，不让普通帧格与助手并发写同一个进程。
        if (!_store.TryUpdate(s =>
            {
                s.OfflineModeEnabled = false;
                s.CancelRequested = true;
                s.Status = OfflineModeStatus.RestorePending;
                s.Message = "已请求脱机助手退出，等待进程结束后恢复普通模式。";
            }, out error))
            return OperationResult.Fail(error);

        using var helperStopped = await WaitForHelperLeaseAsync(_store, TimeSpan.FromSeconds(35), cancellationToken);
        if (helperStopped is null)
        {
            SetState(OfflineModeStatus.RestorePending, "脱机助手未能在 35 秒内退出；普通模式仍保持停用。", enabled: false);
            return OperationResult.Fail("脱机助手未能在 35 秒内退出；普通模式仍保持停用。");
        }

        using var lease = await OfflineModeExecutionLock.WaitAcquireAsync(_store, TimeSpan.FromSeconds(5), cancellationToken);
        if (lease is null)
        {
            SetState(OfflineModeStatus.RestorePending, "脱机操作锁仍被占用；恢复记录保留。", enabled: false);
            return OperationResult.Fail("脱机操作锁仍被占用；请稍后重试恢复。");
        }
        if (!_store.TryRead(out state, out error)) return OperationResult.Fail(error);
        if (state.SavedPreferences is null)
            return OperationResult.Fail("恢复过程中原配置快照消失，未执行猜测性恢复。");
        preferences = state.SavedPreferences.Copy();

        var targetService = ServiceLocator.GameTarget;
        if (!await targetService.WaitForIdleAsync(TimeSpan.FromSeconds(10), cancellationToken))
        {
            SetState(OfflineModeStatus.RestorePending, "目标进程操作尚未排空，恢复暂缓。", enabled: false);
            return OperationResult.Fail("目标进程操作尚未排空，恢复暂缓。");
        }
        using var targetLease = targetService.BeginOperation();

        var frameBeforeRestore = ServiceLocator.Frame;
        if (frameBeforeRestore.FrameModeActive || AppSettingsStore.Read().FrameModeActive)
        {
            var deactivate = await frameBeforeRestore.DeactivateFrameModeAsync();
            if (!deactivate.Success || frameBeforeRestore.FrameModeActive)
            {
                SetState(OfflineModeStatus.RestorePending, "停用部分普通帧格操作失败：" + deactivate.Message, enabled: false);
                return OperationResult.Fail("停用部分普通帧格操作失败：" + deactivate.Message);
            }
        }

        var cpuFrame = Backends.BackendCpuFrameService.Instance;
        if (CpuFrameStatusState.DualCcdSchedulingActive)
        {
            var result = await ServiceLocator.Cpu.RevertDualCcdSchedulingAsync();
            if (!result.Success || CpuFrameStatusState.DualCcdSchedulingActive)
            {
                SetState(OfflineModeStatus.RestorePending, "普通双 CCD 调度仍未恢复：" + result.Message, enabled: false);
                return OperationResult.Fail("普通双 CCD 调度仍未恢复：" + result.Message);
            }
        }
        if (CpuFrameStatusState.SingleCcdExcludeActive)
        {
            var result = await ServiceLocator.Cpu.RestoreGameFullCoresAsync();
            if (!result.Success || CpuFrameStatusState.SingleCcdExcludeActive)
            {
                SetState(OfflineModeStatus.RestorePending, "普通单 CCD 锁核仍未恢复：" + result.Message, enabled: false);
                return OperationResult.Fail("普通单 CCD 锁核仍未恢复：" + result.Message);
            }
        }
        if (await cpuFrame.HasAffinityRestorePendingAsync() || preferences.GameAffinityRuleEnabled)
        {
            var (affinityOk, affinityError) = await cpuFrame.CanRestoreAffinityAsync(preferences.GameAffinityRuleMask);
            if (!affinityOk)
            {
                SetState(OfflineModeStatus.RestorePending, "CPU 亲和性归属无法确认：" + affinityError, enabled: false);
                return OperationResult.Fail("CPU 亲和性归属无法确认：" + affinityError);
            }
            await cpuFrame.ApplyAffinityAsync(0);
            if (await cpuFrame.HasAffinityRestorePendingAsync())
            {
                SetState(OfflineModeStatus.RestorePending, "CPU 亲和性原值还原失败；恢复记录保留。", enabled: false);
                return OperationResult.Fail("CPU 亲和性原值还原失败；恢复记录保留。");
            }
        }

        var (cpuRestore, reacquired) = await RestoreThroughBackendAsync(lease, helperStopped, cancellationToken);
        using var restoredLocks = reacquired;
        if (!cpuRestore.Success)
        {
            SetState(OfflineModeStatus.RestorePending, cpuRestore.Message, enabled: false);
            return OperationResult.Fail(cpuRestore.Message);
        }

        try
        {
            AppSettingsStore.Update(s =>
            {
                s.DwmRestartOnGameStart = false;
                s.FramePowerLockEnabled = preferences.FramePowerLockEnabled;
                s.FramePowerLockTargetGuid = preferences.FramePowerLockTargetGuid;
                s.GamePriorityEnabled = false;
                s.GameAffinityRuleEnabled = false;
                s.GameAffinityRuleMask = preferences.GameAffinityRuleMask;
                s.SingleCcdExcludeCpu0Enabled = false;
                s.DualCcdImmediateEnabled = false;
                s.DualCcdArmed = preferences.DualCcdArmed;
                s.DualCcdFrameEnabled = preferences.DualCcdFrameEnabled;
                s.DualCcdGameCcdIndex = preferences.DualCcdGameCcdIndex;
                s.FrameModeActive = false;
                s.CloseToTrayEnabled = false;
            });
        }
        catch (Exception ex)
        {
            SetState(OfflineModeStatus.RestorePending, "恢复普通设置失败：" + ex.Message, enabled: false);
            return OperationResult.Fail("恢复普通设置失败：" + ex.Message);
        }

        try
        {
            var frame = ServiceLocator.Frame;
            frame.DwmRestartOnGameStart = false;
            frame.RefreshDualCcdArmState();
            ServiceLocator.AppControl.SetCloseToTray(false);
            ServiceLocator.GameProcess.GamePriorityEnabled = false;
            var saved = AppSettingsStore.Read();
            if (saved.FrameModeActive || saved.DwmRestartOnGameStart || saved.GamePriorityEnabled || saved.GameAffinityRuleEnabled ||
                saved.SingleCcdExcludeCpu0Enabled || saved.DualCcdImmediateEnabled || saved.CloseToTrayEnabled ||
                saved.GameAffinityRuleMask != preferences.GameAffinityRuleMask)
                return FailRestore("普通设置保存/停用复核失败，保留脱机快照。");
        }
        catch (Exception ex) { return FailRestore("恢复普通模式时发生异常：" + ex.Message); }

        if (!_store.TryUpdate(s =>
            {
                s.OfflineModeEnabled = false;
                s.Status = OfflineModeStatus.Off;
                s.CancelRequested = false;
                s.PreviousNormalPreferences = preferences.Copy();
                s.SavedPreferences = null;
                s.CpuSetChanges.Clear();
                s.HelperProcessId = null;
                s.HelperStartTimeUtcTicks = null;
                s.HelperOperationCompleted = false;
                s.HelperRunId = Guid.Empty;
                s.Message = "已还原脱机 CPU Sets 并保留普通参数；帧格、锁核、优先级、自启动和托盘需手动开启。";
            }, out error))
            return FailRestore("普通模式已恢复，但无法清理脱机快照：" + error);

        Log.Info("脱机模式已关闭；普通模式偏好恢复，未自动重新应用 CPU Sets。");
        return OperationResult.Ok("脱机模式已关闭；普通模式偏好已恢复。CPU Sets 仅还原到脱机前原值，没有自动补应用普通锁核规则。");
    }

    public Task<OperationResult> StartHelperAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Fail("脱机模式已移除，不再启动助手。"));

    private static async Task<OfflineModeHelperInstanceLease?> WaitForHelperLeaseAsync(
        OfflineModeStateStore store, TimeSpan timeout, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            token.ThrowIfCancellationRequested();
            var lease = OfflineModeHelperInstanceLease.TryAcquire(store, out _);
            if (lease is not null) return lease;
            await Task.Delay(100, token);
        }
        return null;
    }

    /// <summary>更新前只排空助手；卸载前恢复已保留的 CPU 集，不重启任何普通自动功能。</summary>
    public async Task<OperationResult> PrepareForRemovalAsync(bool restoreCpuSets, CancellationToken token = default)
    {
        using var backendGate = Backends.BackendOperationGate.BlockNewOperations();
        using var backendControl = Backends.BackendOperationGate.AllowControlOperations();
        using var control = await OfflineModeExecutionLock.WaitControlAsync(_store, TimeSpan.FromSeconds(10), token);
        if (control is null) return OperationResult.Fail("脱机控制操作尚未结束，暂不能更新或卸载。");
        if (!_store.TryRead(out var state, out var error)) return OperationResult.Fail(error);
        if (!state.BlocksNormalAutomation && state.CpuSetChanges.Count == 0 && state.HelperProcessId is null)
            return OperationResult.Ok("没有脱机恢复义务。");
        var wasEnabled = state.OfflineModeEnabled;
        if (!_store.TryUpdate(s => { s.CancelRequested = true; s.Status = OfflineModeStatus.RestorePending; }, out error))
            return OperationResult.Fail(error);
        using var helperGone = await WaitForHelperLeaseAsync(_store, TimeSpan.FromSeconds(35), token);
        if (helperGone is null) return OperationResult.Fail("助手尚未退出，未继续更新或卸载。");
        using var operation = await OfflineModeExecutionLock.WaitAcquireAsync(_store, TimeSpan.FromSeconds(5), token);
        if (operation is null) return OperationResult.Fail("脱机操作尚未排空，恢复记录保留。");
        (OperationResult Result, RestoreLocks? Locks) restored = (OperationResult.Ok("无需恢复 CPU Sets。"), null);
        if (restoreCpuSets)
        {
            restored = await RestoreThroughBackendAsync(operation, helperGone, token);
            if (!restored.Result.Success) return restored.Result;
        }
        using var restoredLocks = restored.Locks;
        if (!_store.TryUpdate(s =>
        {
            s.HelperProcessId = null;
            s.HelperStartTimeUtcTicks = null;
            s.HelperOperationCompleted = false;
            s.HelperRunId = Guid.Empty;
            s.CancelRequested = false;
            if (restoreCpuSets)
            {
                s.OfflineModeEnabled = false;
                s.PreviousNormalPreferences = s.SavedPreferences?.Copy() ?? s.PreviousNormalPreferences;
                s.SavedPreferences = null;
                s.Status = OfflineModeStatus.Off;
                s.Message = "卸载前已还原脱机 CPU 集合，未重新开启自动功能。";
            }
            else
            {
                s.OfflineModeEnabled = wasEnabled;
                s.Status = wasEnabled ? s.CpuSetChanges.Count > 0 ? OfflineModeStatus.Kept : OfflineModeStatus.Ready : OfflineModeStatus.RestorePending;
                s.Message = "更新前已排空助手，原值记录和脱机配置保留。";
            }
        }, out error)) return OperationResult.Fail(error);
        return OperationResult.Ok(restoreCpuSets ? "脱机恢复完成，可以继续卸载。" : "助手已退出，可以继续更新。");
    }

    private Task<(OperationResult Result, RestoreLocks? Locks)> RestoreThroughBackendAsync(
        OfflineModeExecutionLock operation, OfflineModeHelperInstanceLease helper, CancellationToken token)
    {
        if (!_store.PathName.Equals(OfflineModeStateStore.Default.PathName, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<(OperationResult, RestoreLocks?)>((OperationResult.Fail("官方恢复只接受默认状态仓库。"), null));
        return OfflineRestoreHandover.RunAsync(_store, operation, helper,
            ct => Backends.BackendCpuFrameService.Instance.RestoreOfflineCpuSetsAsync(ct), TimeSpan.FromSeconds(5), token);
    }

    private OperationResult FailRestore(string message)
    {
        SetState(OfflineModeStatus.Failed, message, enabled: false);
        return OperationResult.Fail(message);
    }

    private void SetState(OfflineModeStatus status, string message, bool enabled)
    {
        _store.TryUpdate(s =>
        {
            s.Status = status;
            s.Message = message;
            s.OfflineModeEnabled = enabled;
            s.HelperProcessId = null;
            s.HelperStartTimeUtcTicks = null;
            s.HelperOperationCompleted = false;
            s.HelperCompletionStatus = OfflineModeStatus.Ready;
        }, out _);
    }

    private static bool IsSameLiveProcess(int pid, long startTicks, string expectedPath)
    {
        try
        {
            var expected = GameTargetService.CanonicalPath(expectedPath);
            using var process = Process.GetProcessById(pid);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startTicks) return false;
            var actual = GameTargetService.CanonicalPath(GameTargetService.ReadProcessPath(pid));
            return actual.Equals(expected, StringComparison.OrdinalIgnoreCase) && !process.HasExited &&
                   process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch { return false; }
    }

    private static async Task<bool> WaitForHelperExitAsync(int pid, long startTicks, string expectedPath,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSameLiveProcess(pid, startTicks, expectedPath)) return true;
            await Task.Delay(250, cancellationToken);
        }
        return !IsSameLiveProcess(pid, startTicks, expectedPath);
    }

}
