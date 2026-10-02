using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Runtime.InteropServices;

namespace DeltaNFD.Services;

/// <summary>脱机模式切换、恢复和短时助手交接。</summary>
public sealed class OfflineModeCoordinator
{
    private const string AutoStartTaskName = "DeltaNFD_AutoStart";
    private const string LegacyAutoStartTaskName = "DeltaOptimizer_AutoStart";
    private const string FrameTaskName = "DeltaNFD_FrameMode";
    private const string LegacyFrameTaskName = "DeltaOptimizer_FrameMode";
    private readonly OfflineModeStateStore _store;

    public OfflineModeCoordinator() : this(OfflineModeStateStore.Default) { }
    internal OfflineModeCoordinator(OfflineModeStateStore store) => _store = store;

    public bool TryReadState(out OfflineModeState state, out string error)
    {
        if (!_store.TryRead(out state, out error)) return false;
        OfflineHelperCompletion.TryConfirmExited(_store);
        return _store.TryRead(out state, out error);
    }

    public async Task<OperationResult> EnableAsync(CancellationToken cancellationToken = default)
    {
        using var control = await OfflineModeExecutionLock.WaitControlAsync(_store, TimeSpan.FromSeconds(10), cancellationToken);
        if (control is null) return OperationResult.Fail("另一个脱机切换操作尚未完成。");
        using var lease = await OfflineModeExecutionLock.WaitAcquireAsync(_store, TimeSpan.FromSeconds(10), cancellationToken);
        if (lease is null) return OperationResult.Fail("有其他脱机操作正在运行，请稍后重试。");
        if (!_store.TryRead(out var state, out var error)) return OperationResult.Fail(error);
        if (state.OfflineModeEnabled) return OperationResult.Ok("脱机模式已经启用。");
        if (state.SavedPreferences is not null || state.Status is not OfflineModeStatus.Off)
            return OperationResult.Fail("上次脱机模式切换尚未恢复；请先关闭脱机模式并完成恢复。");
        if (state.CpuSetChanges.Count != 0)
            return OperationResult.Fail("仍有脱机 CPU Sets 恢复记录；请先处理恢复记录。");

        var captured = await CapturePreferencesAsync();
        if (!captured.Success || captured.Preferences is null) return OperationResult.Fail(captured.Message);
        var preferences = captured.Preferences;
        var settings = AppSettingsStore.Read();
        if (settings.TempSpoofRestorePending)
            return OperationResult.Fail("临时显卡伪装仍待登录还原；为避免移除还原任务，暂不能启用脱机模式。");
        if (settings.FramePowerRestorePending || File.Exists(Path.Combine(AppDataPaths.Root, "frame_power_scheme_snapshot.json")))
            return OperationResult.Fail("帧格电源计划仍待恢复；完成恢复后再启用脱机模式。");

        if (!_store.TryUpdate(s =>
            {
                s.OfflineModeEnabled = false;
                s.Status = OfflineModeStatus.Preparing;
                s.CancelRequested = false;
                s.SavedPreferences = preferences.Copy();
                s.HelperProcessId = null;
                s.HelperStartTimeUtcTicks = null;
                s.HelperOperationCompleted = false;
                s.Message = "正在停用普通帧格、CPU 调度、自启动与托盘驻留。";
            }, out error))
            return OperationResult.Fail(error);

        var result = await PrepareForOfflineAsync(preferences, cancellationToken);
        if (!result.Success)
        {
            SetState(OfflineModeStatus.Failed, result.Message, enabled: false);
            return result;
        }

        if (!_store.TryUpdate(s =>
            {
                s.OfflineModeEnabled = true;
                s.Status = OfflineModeStatus.Ready;
                s.CancelRequested = false;
                s.Message = "脱机模式就绪。启动游戏后点击“准备脱机启动 / 应用当前游戏”；工具不代替游戏平台启动游戏。";
            }, out error))
        {
            SetState(OfflineModeStatus.Failed, "普通功能已停用，但脱机就绪状态无法保存：" + error, enabled: false);
            return OperationResult.Fail("普通功能已停用，但脱机就绪状态无法保存：" + error);
        }

        Log.Info("脱机模式已启用；普通帧格、亲和性/调度、自启动和托盘偏好已保存并停用。");
        return OperationResult.Ok("脱机模式已启用；普通帧格与 CPU 调度已还原，自启动和托盘已关闭。原偏好已保存。");
    }

    public async Task<OperationResult> DisableAsync(CancellationToken cancellationToken = default)
    {
        using var control = await OfflineModeExecutionLock.WaitControlAsync(_store, TimeSpan.FromSeconds(10), cancellationToken);
        if (control is null) return OperationResult.Fail("另一个脱机切换操作尚未完成。");
        if (!_store.TryRead(out var state, out var error)) return OperationResult.Fail(error);
        if (state.SavedPreferences is null && !state.OfflineModeEnabled && state.CpuSetChanges.Count == 0)
            return OperationResult.Ok("脱机模式本就未启用。");
        if (state.SavedPreferences is null)
            return OperationResult.Fail("缺少普通模式原配置快照；为避免猜测状态，未退出脱机模式。");

        var preferences = state.SavedPreferences.Copy();
        var helperPid = state.HelperProcessId;
        var helperStartTicks = state.HelperStartTimeUtcTicks;
        var helperPath = Path.Combine(AppContext.BaseDirectory, "OfflineHelper", "DeltaNFD.OfflineHelper.exe");

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

        if (CpuTopologyService.DualCcdSchedulingActive)
        {
            var result = await ServiceLocator.Cpu.RevertDualCcdSchedulingAsync();
            if (!result.Success || CpuTopologyService.DualCcdSchedulingActive)
            {
                SetState(OfflineModeStatus.RestorePending, "普通双 CCD 调度仍未恢复：" + result.Message, enabled: false);
                return OperationResult.Fail("普通双 CCD 调度仍未恢复：" + result.Message);
            }
        }
        if (CpuTopologyService.SingleCcdExcludeActive)
        {
            var result = await ServiceLocator.Cpu.RestoreGameFullCoresAsync();
            if (!result.Success || CpuTopologyService.SingleCcdExcludeActive)
            {
                SetState(OfflineModeStatus.RestorePending, "普通单 CCD 锁核仍未恢复：" + result.Message, enabled: false);
                return OperationResult.Fail("普通单 CCD 锁核仍未恢复：" + result.Message);
            }
        }
        if (CpuTopologyService.HasAffinityRuleRestorePending || preferences.GameAffinityRuleEnabled)
        {
            if (!CpuTopologyService.CanSafelyRestoreAffinityRuleForOffline(preferences.GameAffinityRuleMask, out var affinityError))
            {
                SetState(OfflineModeStatus.RestorePending, "CPU 亲和性归属无法确认：" + affinityError, enabled: false);
                return OperationResult.Fail("CPU 亲和性归属无法确认：" + affinityError);
            }
            CpuTopologyService.RestoreAffinityRule();
            if (CpuTopologyService.HasAffinityRuleRestorePending)
            {
                SetState(OfflineModeStatus.RestorePending, "CPU 亲和性原值还原失败；恢复记录保留。", enabled: false);
                return OperationResult.Fail("CPU 亲和性原值还原失败；恢复记录保留。");
            }
        }

        var cpuRestore = new OfflineCpuSetScheduler().RestoreRecordedAssignments();
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

    public async Task<OperationResult> StartHelperAsync(CancellationToken cancellationToken = default)
    {
        using var control = await OfflineModeExecutionLock.WaitControlAsync(_store, TimeSpan.FromSeconds(10), cancellationToken);
        if (control is null) return OperationResult.Fail("另一个脱机控制操作尚未完成。");
        using var operation = await OfflineModeExecutionLock.WaitAcquireAsync(_store, TimeSpan.FromSeconds(5), cancellationToken);
        if (operation is null) return OperationResult.Fail("脱机应用或恢复仍在进行。");
        if (!_store.TryRead(out var state, out var error)) return OperationResult.Fail(error);
        if (!state.OfflineModeEnabled || state.CancelRequested || state.SavedPreferences is null ||
            state.Status is OfflineModeStatus.Preparing or OfflineModeStatus.RestorePending)
            return OperationResult.Fail("请先启用脱机模式并完成恢复检查。");
        if (state.CpuSetChanges.Any(x => x.Status != OfflineCpuSetRecordStatus.Applied))
            return OperationResult.Fail("存在待人工处理的 CPU Sets 恢复冲突；完成恢复前不能重复应用。");
        var helperPath = Path.Combine(AppContext.BaseDirectory, "OfflineHelper", "DeltaNFD.OfflineHelper.exe");
        if (state.HelperProcessId is int helperPid && state.HelperStartTimeUtcTicks is long helperTicks &&
            IsSameLiveProcess(helperPid, helperTicks, helperPath))
            return OperationResult.Fail("脱机助手仍在运行；请等待本轮完成。");

        if (!File.Exists(helperPath)) return OperationResult.Fail("未找到脱机助手；请先完成 x64 构建或重新安装包含助手的版本。");
        using (var available = OfflineModeHelperInstanceLease.TryAcquire(_store, out var instanceError))
            if (available is null) return OperationResult.Fail(instanceError);

        var runId = Guid.NewGuid();
        if (!_store.TryUpdate(s =>
        {
            if (!s.OfflineModeEnabled || s.CancelRequested || s.SavedPreferences is null)
                throw new InvalidOperationException("模式已变化，未启动助手。");
            s.HelperRunId = runId;
            s.HelperProcessId = null;
            s.HelperStartTimeUtcTicks = null;
            s.HelperOperationCompleted = false;
            s.HelperCompletionStatus = OfflineModeStatus.Ready;
            s.Status = OfflineModeStatus.WaitingForGame;
            s.Message = "正在启动本轮脱机助手。";
        }, out error)) return OperationResult.Fail(error);

        var pipeName = "DeltaNFD_Offline_" + Guid.NewGuid().ToString("N");
        Process? process = null;
        var handedOff = false;
        try
        {
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo
            {
                FileName = helperPath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--offline-helper");
            start.ArgumentList.Add("--pipe");
            start.ArgumentList.Add(pipeName);
            start.ArgumentList.Add("--operation-id");
            start.ArgumentList.Add(runId.ToString("N"));
            process = Process.Start(start);
            if (process is null) return OperationResult.Fail("无法启动脱机助手。");

            try { await server.WaitForConnectionAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
            catch (Exception ex)
            {
                TryStopHelper(process);
                return OperationResult.Fail("等待脱机助手启动确认超时：" + ex.Message);
            }
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var clientPid) || clientPid != process.Id)
            {
                TryStopHelper(process);
                return OperationResult.Fail("管道客户端不是本次启动的助手，拒绝交接。");
            }

            using var reader = new StreamReader(server, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024, leaveOpen: true);
            string? response;
            try { response = await reader.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); }
            catch (Exception ex)
            {
                TryStopHelper(process);
                return OperationResult.Fail("无法读取脱机助手启动状态：" + ex.Message);
            }

            if (response != "READY")
            {
                TryStopHelper(process);
                return OperationResult.Fail(response is { } text && text.StartsWith("ERROR|", StringComparison.Ordinal)
                    ? text[6..]
                    : "脱机助手未确认就绪。");
            }

            if (!_store.TryRead(out var registered, out error) || !OfflineHelperSession.CanExecute(registered, runId) ||
                registered.HelperProcessId != process.Id || registered.HelperStartTimeUtcTicks != process.StartTime.ToUniversalTime().Ticks)
            {
                TryStopHelper(process);
                return OperationResult.Fail("助手登记未持久化或交接已取消：" + error);
            }
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("START");
            handedOff = true;
            return OperationResult.Ok("脱机助手已确认启动。请用原游戏平台启动游戏；本工具不会绕过启动器或反作弊。主界面可以退出，助手完成本轮后会自行结束。");
        }
        catch (Exception ex)
        {
            if (process is not null) TryStopHelper(process);
            return OperationResult.Fail("启动脱机助手失败：" + ex.Message);
        }
        finally
        {
            if (!handedOff)
                _store.TryUpdate(s =>
                {
                    if (s.HelperRunId != runId || s.CancelRequested) return;
                    s.Status = OfflineModeStatus.Failed;
                    s.Message = "助手交接未完成，主程序未退出，恢复记录已保留。";
                }, out _);
            process?.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

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
        if (restoreCpuSets)
        {
            var restored = new OfflineCpuSetScheduler().RestoreRecordedAssignments();
            if (!restored.Success) return restored;
        }
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

    private async Task<(bool Success, OfflineModePreferences? Preferences, string Message)> CapturePreferencesAsync()
    {
        var settings = AppSettingsStore.Read();
        var tasks = new[]
        {
            await OfflineTaskOwnership.InspectAsync(AutoStartTaskName),
            await OfflineTaskOwnership.InspectAsync(LegacyAutoStartTaskName),
            await OfflineTaskOwnership.InspectAsync(FrameTaskName),
            await OfflineTaskOwnership.InspectAsync(LegacyFrameTaskName),
        };
        if (tasks.Any(t => t.State == OfflineScheduledTaskState.ForeignOrUnknown))
            return (false, null, "存在同名但无法确认归属的计划任务；为避免删除他人任务，未启用脱机模式。 " +
                string.Join("；", tasks.Where(t => t.State == OfflineScheduledTaskState.ForeignOrUnknown).Select(t => t.Message)));

        var auto = tasks[0..2];
        var frame = tasks[2..4];
        return (true, new OfflineModePreferences
        {
            FrameModeWasActive = settings.FrameModeActive || ServiceLocator.Frame.FrameModeActive,
            DwmRestartOnGameStart = settings.DwmRestartOnGameStart,
            FramePowerLockEnabled = settings.FramePowerLockEnabled,
            FramePowerLockTargetGuid = settings.FramePowerLockTargetGuid,
            GamePriorityEnabled = settings.GamePriorityEnabled,
            GameAffinityRuleEnabled = settings.GameAffinityRuleEnabled,
            GameAffinityRuleMask = settings.GameAffinityRuleMask,
            SingleCcdExcludeCpu0Enabled = settings.SingleCcdExcludeCpu0Enabled,
            DualCcdImmediateEnabled = settings.DualCcdImmediateEnabled,
            DualCcdArmed = settings.DualCcdArmed,
            DualCcdFrameEnabled = settings.DualCcdFrameEnabled,
            DualCcdGameCcdIndex = settings.DualCcdGameCcdIndex,
            AutoStartTaskWasPresent = auto.Any(t => t.State == OfflineScheduledTaskState.Owned),
            AutoStartEnabled = auto.Any(t => t.State == OfflineScheduledTaskState.Owned && t.Enabled),
            FrameAutostartTaskWasPresent = frame.Any(t => t.State == OfflineScheduledTaskState.Owned),
            FrameAutostartEnabled = frame.Any(t => t.State == OfflineScheduledTaskState.Owned && t.Enabled),
            CloseToTrayEnabled = settings.CloseToTrayEnabled,
        }, "");
    }

    private async Task<OperationResult> PrepareForOfflineAsync(OfflineModePreferences preferences, CancellationToken cancellationToken)
    {
        var target = ServiceLocator.GameTarget;
        if (!await target.WaitForIdleAsync(TimeSpan.FromSeconds(10), cancellationToken))
            return OperationResult.Fail("目标进程操作尚未排空，拒绝切换脱机模式。");
        using var targetLease = target.BeginOperation();

        var frame = ServiceLocator.Frame;
        if (frame.FrameModeActive || AppSettingsStore.Read().FrameModeActive)
        {
            var result = await frame.DeactivateFrameModeAsync();
            if (!result.Success || frame.FrameModeActive || AppSettingsStore.Read().FrameModeActive)
                return OperationResult.Fail("停用并还原普通帧格失败：" + result.Message);
        }

        if (preferences.DualCcdImmediateEnabled && !CpuTopologyService.DualCcdSchedulingActive)
            return OperationResult.Fail("双 CCD 偏好仍开启，但当前进程无法确认其运行时调度归属；请先在 CPU 实验室手动恢复。");
        if (preferences.SingleCcdExcludeCpu0Enabled && !CpuTopologyService.SingleCcdExcludeActive)
            return OperationResult.Fail("单 CCD 偏好仍开启，但当前进程无法确认其运行时锁核归属；请先在 CPU 实验室手动恢复。");
        if (preferences.GameAffinityRuleEnabled &&
            !CpuTopologyService.CanSafelyRestoreAffinityRuleForOffline(preferences.GameAffinityRuleMask, out var affinityReason))
            return OperationResult.Fail("无法确认 CPU 亲和性规则原值：" + affinityReason);

        if (CpuTopologyService.DualCcdSchedulingActive)
        {
            var restoreDual = await ServiceLocator.Cpu.RevertDualCcdSchedulingAsync();
            if (!restoreDual.Success || CpuTopologyService.DualCcdSchedulingActive)
                return OperationResult.Fail("双 CCD 调度未能确认还原：" + restoreDual.Message);
        }
        if (CpuTopologyService.SingleCcdExcludeActive)
        {
            var restoreSingle = await ServiceLocator.Cpu.RestoreGameFullCoresAsync();
            if (!restoreSingle.Success || CpuTopologyService.SingleCcdExcludeActive)
                return OperationResult.Fail("单 CCD CPU0 排除未能确认还原：" + restoreSingle.Message);
        }
        if (preferences.GameAffinityRuleEnabled || CpuTopologyService.HasAffinityRuleRestorePending)
        {
            CpuTopologyService.RestoreAffinityRule();
            if (CpuTopologyService.HasAffinityRuleRestorePending)
                return OperationResult.Fail("CPU 亲和性原值还原失败；恢复记录已保留。");
        }

        if (preferences.GamePriorityEnabled)
        {
            var priorityRestore = ServiceLocator.GameProcess.DisableGamePriorityForOffline();
            if (!priorityRestore.Success) return OperationResult.Fail("自动游戏优先级未能安全停用：" + priorityRestore.Message);
        }
        var frameService = ServiceLocator.Frame;
        frameService.DwmRestartOnGameStart = false;
        AppSettingsStore.Update(s =>
        {
            s.GameAffinityRuleEnabled = false;
            s.SingleCcdExcludeCpu0Enabled = false;
            s.DualCcdImmediateEnabled = false;
            s.DualCcdArmed = false;
            s.DwmRestartOnGameStart = false;
            s.GamePriorityEnabled = false;
        });
        frameService.RefreshDualCcdArmState();

        var taskNames = new[] { AutoStartTaskName, LegacyAutoStartTaskName, FrameTaskName, LegacyFrameTaskName };
        foreach (var taskName in taskNames)
        {
            var remove = await OfflineTaskOwnership.RemoveOwnedTaskAsync(taskName);
            if (!remove.Success) return OperationResult.Fail("脱机模式拒绝进入：" + remove.Message);
        }

        ServiceLocator.AppControl.SetCloseToTray(false);
        var finalSettings = AppSettingsStore.Read();
        if (frameService.FrameModeActive || finalSettings.FrameModeActive || finalSettings.DwmRestartOnGameStart ||
            finalSettings.GamePriorityEnabled || finalSettings.GameAffinityRuleEnabled ||
            finalSettings.SingleCcdExcludeCpu0Enabled || finalSettings.DualCcdImmediateEnabled ||
            CpuTopologyService.DualCcdSchedulingActive || CpuTopologyService.SingleCcdExcludeActive ||
            CpuTopologyService.HasAffinityRuleRestorePending || ServiceLocator.AppControl.CloseToTrayEnabled)
            return OperationResult.Fail("停用后的状态复核未通过，脱机模式未就绪。");

        var blockers = target.GetSwitchBlocker();
        if (!string.IsNullOrWhiteSpace(blockers)) return OperationResult.Fail("仍有系统还原义务：" + blockers);

        foreach (var taskName in taskNames)
        {
            var task = await OfflineTaskOwnership.InspectAsync(taskName);
            if (task.State != OfflineScheduledTaskState.Missing)
                return OperationResult.Fail($"计划任务 {taskName} 仍存在或归属未知：{task.Message}");
        }

        return OperationResult.Ok("已停用并复核普通帧格、锁核/调度、自启动和托盘。");
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

    private static void TryStopHelper(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
        catch (Exception ex) { Log.Warn("无法结束未确认就绪的脱机助手：" + ex.Message); }
    }
}
