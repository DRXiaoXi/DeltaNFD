using CommunityToolkit.Mvvm.ComponentModel;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

namespace DeltaNFD.ViewModels;

/// <summary>运行库防护、14.x 兼容性检测与仅修复 14.x（固定 14.44 内置基线）。</summary>
public partial class RuntimeViewModel : ObservableObject
{
    public Task<RuntimeFileUseScan> ScanFileUseAsync(CancellationToken token, RuntimeFileUseScope scope = RuntimeFileUseScope.Vc14) => _guard.ScanFileUseAsync(token, scope);
    public Task<RuntimeFileUseCloseResult> CloseFileUseAsync(RuntimeFileUseCloseArgs args, CancellationToken token) => _guard.CloseFileUseAsync(args, token);
    // Each entered page owns its view model; late callbacks retain their departed lease.
    public static RuntimeViewModel Current => PageBackendSessions.Manager.Current is { Route: "runtime" } owner
        ? owner.Service(static () => new RuntimeViewModel())
        : throw new PageBackendUnavailableException("运行库栏目会话不可用，请重新进入运行库页。");
    private readonly IRuntimeGuardService _guard = ServiceLocator.Guard;
    private bool _runtimePolicyChecking;
    private string _runtimePolicyReason = "";
    private bool _runtimeDriverUnknown;
    public bool RuntimePolicyBlocked => _runtimePolicyReason.Length > 0;
    public string RuntimePolicyReason => _runtimePolicyReason;
    public bool RuntimeDriverUnknown => _runtimeDriverUnknown;
    public string RuntimeDriverUnknownText => "无法完整读取显卡驱动，无法判断是否适用运行库紧急策略；请核对驱动版本。当前未因未知版本禁用运行库。";
    public bool RuntimeOperationsAvailable => !_runtimePolicyChecking && !RuntimePolicyBlocked;
    public bool CanReviewRuntimeRecovery => !GuardIsBusy && !RepairIsBusy;

    public void SetRuntimePolicyChecking(bool checking)
    {
        _runtimePolicyChecking = checking;
        NotifyRuntimePolicyChanged();
    }

    public void SetRuntimePolicyReason(string? reason)
    {
        _runtimePolicyReason = reason ?? "";
        if (RuntimePolicyBlocked)
        {
            AbnormalText = WarningText = EvaluationText = "";
            DetectionStatusText = _runtimePolicyReason;
        }
        NotifyRuntimePolicyChanged();
    }

    public void SetRuntimeDriverUnknown(bool unknown)
    {
        _runtimeDriverUnknown = unknown;
        OnPropertyChanged(nameof(RuntimeDriverUnknown));
    }

    private void NotifyRuntimePolicyChanged()
    {
        OnPropertyChanged(nameof(RuntimePolicyBlocked));
        OnPropertyChanged(nameof(RuntimePolicyReason));
        OnPropertyChanged(nameof(RuntimeOperationsAvailable));
        OnPropertyChanged(nameof(CanChangeRuntimeDetection));
        OnPropertyChanged(nameof(GuardCleanupEnabled));
        OnPropertyChanged(nameof(CanUseBaselineActions));
        OnPropertyChanged(nameof(GuardToggleEnabled));
        OnPropertyChanged(nameof(GuardModeEnabled));
    }
    private bool repairIsBusy;
    public bool RepairIsBusy
    {
        get => repairIsBusy;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(repairIsBusy, value)) return;
            OnPropertyChanging(nameof(RepairIsBusy));
            repairIsBusy = value;
            OnRepairIsBusyChanged(value);
            OnPropertyChanged(nameof(RepairIsBusy));
        }
    }
    partial void OnRepairIsBusyChanged(bool value);
    private int baselineIndex;
    public int BaselineIndex
    {
        get => baselineIndex;
        set
        {
            if (EqualityComparer<int>.Default.Equals(baselineIndex, value)) return;
            OnPropertyChanging(nameof(BaselineIndex));
            baselineIndex = value;
            OnBaselineIndexChanged(value);
            OnPropertyChanged(nameof(BaselineIndex));
        }
    }
    partial void OnBaselineIndexChanged(int value);
    private bool baselineDriverChecking = true;
    public bool BaselineDriverChecking
    {
        get => baselineDriverChecking;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(baselineDriverChecking, value)) return;
            OnPropertyChanging(nameof(BaselineDriverChecking));
            baselineDriverChecking = value;
            OnBaselineDriverCheckingChanged(value);
            OnPropertyChanged(nameof(BaselineDriverChecking));
        }
    }
    partial void OnBaselineDriverCheckingChanged(bool value);
    private bool hide144Baseline;
    public bool Hide144Baseline
    {
        get => hide144Baseline;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(hide144Baseline, value)) return;
            OnPropertyChanging(nameof(Hide144Baseline));
            hide144Baseline = value;
            OnHide144BaselineChanged(value);
            OnPropertyChanged(nameof(Hide144Baseline));
        }
    }
    partial void OnHide144BaselineChanged(bool value);
    private string baselineDriverNotice = "正在检查显卡驱动版本…";
    public string BaselineDriverNotice { get => baselineDriverNotice; set => SetProperty(ref baselineDriverNotice, value); }
    private string repairStatusText = "修复前将预览全部 14.x 卸载清单；请先关闭运行库防护。";
    public string RepairStatusText { get => repairStatusText; set => SetProperty(ref repairStatusText, value); }
    public Vc14Baseline SelectedBaseline => Vc14Baseline.V144;
    public bool CanUseBaselineActions => !BaselineDriverChecking && GuardCleanupEnabled;
    public string SelectedBaselineLabel => SelectedBaseline.Label();
    public string SelectedBaselineVersion => SelectedBaseline.FullVersion();
    public string SelectedBaselineDescription => SelectedBaseline.Description();
    public string ContinueInstallLabel => "继续安装 " + SelectedBaseline.Display();
    partial void OnBaselineIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedBaseline));
        OnPropertyChanged(nameof(SelectedBaselineLabel));
        OnPropertyChanged(nameof(SelectedBaselineVersion));
        OnPropertyChanged(nameof(SelectedBaselineDescription));
        OnPropertyChanged(nameof(ContinueInstallLabel));
    }
    partial void OnBaselineDriverCheckingChanged(bool value) => OnPropertyChanged(nameof(CanUseBaselineActions));
    partial void OnHide144BaselineChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectedBaseline));
        OnPropertyChanged(nameof(SelectedBaselineLabel));
        OnPropertyChanged(nameof(SelectedBaselineVersion));
        OnPropertyChanged(nameof(SelectedBaselineDescription));
        OnPropertyChanged(nameof(ContinueInstallLabel));
    }

    public void BeginBaselineDriverCheck()
    {
        BaselineDriverChecking = true;
        BaselineDriverNotice = "正在检查显卡驱动版本…";
    }

    public void CompleteBaselineDriverCheck(bool confirmed596, bool driverUnknown)
    {
        Hide144Baseline = false;
        BaselineDriverNotice = confirmed596
            ? "已检测到 NVIDIA 596 系列驱动；使用固定 14.44 基线。"
            : driverUnknown ? "无法完整读取显卡驱动；不影响使用固定 14.44 基线。" : "";
        BaselineDriverChecking = false;
    }
    private string abnormalText = "";
    public string AbnormalText
    {
        get => abnormalText;
        set
        {
            if (EqualityComparer<string>.Default.Equals(abnormalText, value)) return;
            OnPropertyChanging(nameof(AbnormalText));
            abnormalText = value;
            OnAbnormalTextChanged(value);
            OnPropertyChanged(nameof(AbnormalText));
        }
    }
    partial void OnAbnormalTextChanged(string value);
    private string abnormalTitle = "C++ 14.x 运行库异常";
    public string AbnormalTitle { get => abnormalTitle; set => SetProperty(ref abnormalTitle, value); }
    private string warningText = "";
    public string WarningText
    {
        get => warningText;
        set
        {
            if (EqualityComparer<string>.Default.Equals(warningText, value)) return;
            OnPropertyChanging(nameof(WarningText));
            warningText = value;
            OnWarningTextChanged(value);
            OnPropertyChanged(nameof(WarningText));
        }
    }
    partial void OnWarningTextChanged(string value);
    private string evaluationText = "";
    public string EvaluationText
    {
        get => evaluationText;
        set
        {
            if (EqualityComparer<string>.Default.Equals(evaluationText, value)) return;
            OnPropertyChanging(nameof(EvaluationText));
            evaluationText = value;
            OnPropertyChanged(nameof(HasEvaluation));
            OnPropertyChanged(nameof(EvaluationText));
        }
    }
    private string detectionStatusText = "尚未检测。";
    public string DetectionStatusText { get => detectionStatusText; set => SetProperty(ref detectionStatusText, value); }
    private string manualRepairGuidanceText = "";
    public string ManualRepairGuidanceText
    {
        get => manualRepairGuidanceText;
        set
        {
            if (EqualityComparer<string>.Default.Equals(manualRepairGuidanceText, value)) return;
            OnPropertyChanging(nameof(ManualRepairGuidanceText));
            manualRepairGuidanceText = value;
            OnManualRepairGuidanceTextChanged(value);
            OnPropertyChanged(nameof(ManualRepairGuidanceText));
        }
    }
    partial void OnManualRepairGuidanceTextChanged(string value);
    public bool HasManualRepairGuidance => ManualRepairGuidanceText.Length > 0;
    public bool RuntimeDetectionEnabled => RuntimeDetectionPreference.Shared.Enabled;
    public bool CanChangeRuntimeDetection => RuntimeOperationsAvailable && !RepairIsBusy;
    partial void OnManualRepairGuidanceTextChanged(string value) => OnPropertyChanged(nameof(HasManualRepairGuidance));

    public void SynchronizeRuntimeDetectionPreference()
    {
        RuntimeDetectionPreference.Shared.Reload();
        OnPropertyChanged(nameof(RuntimeDetectionEnabled));
        if (!RuntimeDetectionEnabled)
        {
            AbnormalText = WarningText = EvaluationText = "";
            DetectionStatusText = "运行库检测已关闭，不再显示检测提醒；可随时重新开启。";
        }
    }

    public async Task SetRuntimeDetectionEnabledAsync(bool enabled)
    {
        if (!CanChangeRuntimeDetection) return;
        RuntimeDetectionPreference.Shared.SetEnabled(enabled);
        SynchronizeRuntimeDetectionPreference();
        if (enabled) await RefreshCompatibilityAsync();
    }

    public string RecordRepairResult(OperationResult result, bool cancelled = false)
    {
        ManualRepairGuidanceText = RuntimeRepairGuidance.ForResult(result, cancelled);
        return RuntimeRepairGuidance.DialogText(RuntimeRepairGuidance.OriginalText(result), ManualRepairGuidanceText);
    }

    public void RecordRepairBlocker(string blocker)
    {
        RepairStatusText = blocker;
        RecordRepairResult(OperationResult.Fail(blocker));
    }

    public bool HasAbnormal => AbnormalText.Length > 0;
    public bool HasWarning => WarningText.Length > 0;
    public bool HasEvaluation => EvaluationText.Length > 0;
    partial void OnAbnormalTextChanged(string value) => OnPropertyChanged(nameof(HasAbnormal));
    partial void OnWarningTextChanged(string value) => OnPropertyChanged(nameof(HasWarning));
    partial void OnRepairIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanReviewRuntimeRecovery));
        OnPropertyChanged(nameof(CanChangeRuntimeDetection));
        OnPropertyChanged(nameof(GuardToggleEnabled));
        OnPropertyChanged(nameof(GuardModeEnabled));
        OnPropertyChanged(nameof(GuardCleanupEnabled));
        OnPropertyChanged(nameof(CanUseBaselineActions));
    }
    public Task<Vc14ReinstallPreview> PreviewRepairAsync(Vc14RepairMode mode = Vc14RepairMode.Full, bool allowTemporaryInstaller = false)
        => _guard.Preview14RepairAsync(mode, allowTemporaryInstaller, SelectedBaseline);
    public Task<RuntimeHighFpsPreview> PreviewHighFpsAsync(bool strongMode) => _guard.PreviewHighFpsAsync(strongMode, SelectedBaseline);
    public Task<OperationResult> RepairHighFpsAsync(string fingerprint, bool strongMode, CancellationToken token)
        => _guard.RepairHighFpsAsync(fingerprint, strongMode, new Progress<string>(s => RepairStatusText = s), token, SelectedBaseline);
    public Task<NonCppRuntimePreview> PreviewNonCppAsync(CancellationToken token) => _guard.PreviewNonCppAsync(token);
    public Task<OperationResult> RepairNonCppAsync(string fingerprint, IProgress<string>? progress, CancellationToken token)
        => _guard.RepairNonCppAsync(fingerprint, progress, token);
    public Task<OperationResult> RestoreNonCppAsync(IProgress<string>? progress, CancellationToken token)
        => _guard.RestoreNonCppAsync(progress, token);
    public Task<RuntimeSafeUninstallPreview> PreviewSafeUninstallAsync(CancellationToken token)
        => _guard.PreviewSafeUninstallAsync(token);
    public Task<OperationResult> SafeUninstallAllCppAsync(string fingerprint, bool allowTemporaryInstaller, CancellationToken token)
        => _guard.SafeUninstallAllCppAsync(fingerprint, allowTemporaryInstaller, new Progress<string>(s => RepairStatusText = s), token);
    public Task<OperationResult> RepairAsync(string fingerprint, CancellationToken token, Vc14RepairMode mode = Vc14RepairMode.Full, bool allowTemporaryInstaller = false)
        => _guard.Repair14Async(fingerprint, new Progress<string>(s => RepairStatusText = s), token, mode, allowTemporaryInstaller, SelectedBaseline);
    public Task<Vc14ForceReplacePreview> PreviewForceReplaceAsync(CancellationToken token) => _guard.PreviewForceReplace14Async(token, SelectedBaseline);
    public Task<OperationResult> ForceReplaceAsync(string fingerprint, CancellationToken token)
        => _guard.ForceReplace14Async(fingerprint, new Progress<string>(s => RepairStatusText = s), token, SelectedBaseline);
    public Task<OperationResult> RestoreTemporaryInstallerAsync() => _guard.RestoreTemporaryInstallerAsync();
    private string installedText = "尚未读取安装登记。";
    public string InstalledText { get => installedText; set => SetProperty(ref installedText, value); }
    private bool guardIsEnabled;
    public bool GuardIsEnabled
    {
        get => guardIsEnabled;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(guardIsEnabled, value)) return;
            OnPropertyChanging(nameof(GuardIsEnabled));
            guardIsEnabled = value;
            OnGuardIsEnabledChanged(value);
            OnPropertyChanged(nameof(GuardIsEnabled));
        }
    }
    partial void OnGuardIsEnabledChanged(bool value);
    private bool guardIsBusy;
    public bool GuardIsBusy
    {
        get => guardIsBusy;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(guardIsBusy, value)) return;
            OnPropertyChanging(nameof(GuardIsBusy));
            guardIsBusy = value;
            OnGuardIsBusyChanged(value);
            OnPropertyChanged(nameof(GuardIsBusy));
        }
    }
    partial void OnGuardIsBusyChanged(bool value);
    private bool guardIsPartial;
    public bool GuardIsPartial
    {
        get => guardIsPartial;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(guardIsPartial, value)) return;
            OnPropertyChanging(nameof(GuardIsPartial));
            guardIsPartial = value;
            OnGuardIsPartialChanged(value);
            OnPropertyChanged(nameof(GuardIsPartial));
        }
    }
    partial void OnGuardIsPartialChanged(bool value);
    private string guardStatusText = "正在读取防护状态…";
    public string GuardStatusText { get => guardStatusText; set => SetProperty(ref guardStatusText, value); }
    private bool _guardStateKnown;
    public bool CanRepairLegacyUe4Acl { get; private set; }
    public string GuardUe4Path { get; private set; } = "";
    public RuntimeGuardMode GuardMode { get; private set; }
    public int GuardModeIndex => GuardMode == RuntimeGuardMode.Full ? 1 : 0;
    public bool GuardModeEnabled => RuntimeOperationsAvailable && GuardToggleEnabled && !GuardIsEnabled && _legacyIfeo.Count == 0;
    private IReadOnlyList<RuntimeGuardLegacyEntry> _legacyIfeo = [];
    public bool CanCleanupLegacyIfeo => _legacyIfeo.Count > 0;
    public bool GuardCleanupEnabled => RuntimeOperationsAvailable && !GuardIsBusy && !RepairIsBusy;
    public string LegacyIfeoNames => string.Join("\n", _legacyIfeo.Select(e => e.Name));

    public bool GuardToggleEnabled => _guardStateKnown && !GuardIsBusy && !RepairIsBusy
        && (RuntimeOperationsAvailable || GuardIsEnabled);

    public string GuardToggleText => GuardIsBusy ? "切换中…" : GuardIsPartial ? "防护部分生效"
        : GuardIsEnabled ? "防护已开启" : "防护未开启";

    partial void OnGuardIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanReviewRuntimeRecovery));
        OnPropertyChanged(nameof(GuardToggleEnabled));
        OnPropertyChanged(nameof(GuardModeEnabled));
        OnPropertyChanged(nameof(GuardCleanupEnabled));
        OnPropertyChanged(nameof(CanUseBaselineActions));
        OnPropertyChanged(nameof(GuardToggleText));
    }

    partial void OnGuardIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(GuardToggleText));
        OnPropertyChanged(nameof(GuardToggleEnabled));
        OnPropertyChanged(nameof(GuardModeEnabled));
    }
    partial void OnGuardIsPartialChanged(bool value) => OnPropertyChanged(nameof(GuardToggleText));

    /// <summary>读取防护模式状态（busy 守卫版，供进页/手动刷新调用）。</summary>
    public async Task LoadGuardAsync()
    {
        if (GuardIsBusy || RepairIsBusy)
        {
            return;
        }

        GuardIsBusy = true;
        try
        {
            await RefreshGuardCoreAsync();
        }
        catch (Exception ex)
        {
            Log.Error("运行库防护：读取实际状态失败", ex);
            _guardStateKnown = false;
            GuardStatusText = $"防护状态读取失败：{ex.Message}；请重新进入此页重试。";
            OnPropertyChanged(nameof(GuardToggleEnabled));
        }
        finally
        {
            GuardIsBusy = false;
        }
    }

    /// <summary>重读防护状态核心逻辑（无 busy 守卫——SetGuardAsync 切换后必须重读，
    /// 否则状态/开关文字停留在旧值，用户回拨开关会被一致性守卫静默吞掉）。</summary>
    private async Task RefreshGuardCoreAsync()
    {
        var status = await _guard.GetStatusAsync();
        InstalledText = status.InstalledRedists.Count == 0 ? "未读取到 Visual C++ 安装登记；这不是 DLL 缺失判断。" : string.Join("\n", status.InstalledRedists.Select(e => e.DisplayName + " · " + e.Architecture + " · " + e.Version));
        _guardStateKnown = true;
        CanRepairLegacyUe4Acl = status.CanRepairLegacyUe4Acl;
        GuardMode = status.Mode;
        _legacyIfeo = status.LegacyIfeo;
        OnPropertyChanged(nameof(GuardModeIndex));
        GuardUe4Path = status.Ue4PrereqPath;
        GuardIsEnabled = status.IfeoManagedCount > 0 || status.Ue4PrereqDenied || status.Ue4RestorePending || status.IfeoRestorePending;
        GuardIsPartial = GuardIsEnabled && (status.CanRepairLegacyUe4Acl || !status.IfeoApplied || status.IfeoExternalCount > 0);
        OnPropertyChanged(nameof(GuardToggleEnabled));
        OnPropertyChanged(nameof(GuardModeEnabled));
        OnPropertyChanged(nameof(GuardCleanupEnabled));
        GuardStatusText = status.IfeoApplied
            ? $"{(status.Mode == RuntimeGuardMode.BasicV14 ? "基础 V14" : "完全防护")}：已拦截 {status.ScopeManagedCount}/{status.IfeoTotal} 个安装器"
              + (status.Mode == RuntimeGuardMode.Full ? "（含 UE4 前置包）" : "")
            : GuardIsEnabled
                ? $"防护部分生效或有残留：当前范围已拦截 {status.ScopeManagedCount}/{status.IfeoTotal}；关闭防护即可清理残留"
                : status.IfeoCount > 0
                    ? $"本工具防护未开启；检测到 {status.IfeoCount} 项其他程序设置的安装器拦截"
                    : "本工具防护未开启";
        var externalOther = status.IfeoExternalCount - _legacyIfeo.Count;
        if (CanCleanupLegacyIfeo) GuardStatusText += $"；{_legacyIfeo.Count} 项由独立防护工具写入，需「强制清理拦截」移除";
        if (GuardIsEnabled && externalOther > 0) GuardStatusText += $"；另有 {externalOther} 项其他程序的 Debugger";
        if (status.CanRepairLegacyUe4Acl) GuardStatusText += "；UE4 前置包残留旧版拒绝执行权限，关闭防护或强制清理时自动移除";
        if (status.GuardIssue.Length > 0) GuardStatusText += "；" + status.GuardIssue + "（可用「强制清理拦截」重置）";
        if (!RuntimeOperationsAvailable)
        {
            AbnormalText = WarningText = EvaluationText = "";
            DetectionStatusText = RuntimePolicyBlocked ? RuntimePolicyReason : "正在核对运行库紧急策略…";
            return;
        }
        await RefreshCompatibilityAsync();
    }

    private async Task RefreshCompatibilityAsync()
    {
        if (!RuntimeOperationsAvailable) return;
        SynchronizeRuntimeDetectionPreference();
        if (RuntimeDetectionEnabled) DetectionStatusText = "正在检测运行库…";
        var read = await RuntimeDetectionPreference.Shared.ReadAsync(() => _guard.Scan14Async());
        if (!RuntimeOperationsAvailable || !RuntimeDetectionPreference.Shared.IsCurrent(read.Snapshot)) return;
        if (read.Error is { } error)
        {
            AbnormalText = EvaluationText = "";
            WarningText = "14.x 检测未完成，不能视为正常：" + error.Message;
            DetectionStatusText = "检测未完成，可手动刷新重试。";
        }
        else if (read.Value is { } scan)
        {
            string Format(Vc14Notice n) => n.Name + " · " + n.Architecture + " · " + n.Version + "\n" + n.Detail;
            AbnormalTitle = scan.AbnormalTitle;
            AbnormalText = string.Join("\n\n", scan.GetAbnormalNotices().Select(Format));
            WarningText = string.Join("\n\n", scan.Notices.Where(n => n.Severity == Vc14Severity.Warning).Select(Format).Concat(scan.Issues));
            EvaluationText = scan.HasAbnormal ? "" : string.Join("\n\n", scan.Notices.Where(n => n.Severity == Vc14Severity.Evaluation).Select(Format));
            DetectionStatusText = "最近检测 " + DateTime.Now.ToString("HH:mm:ss");
        }
        else
        {
            AbnormalText = EvaluationText = "";
            WarningText = "14.x 检测未完成，未返回检测结果，不能视为正常。";
            DetectionStatusText = "检测未完成，可手动刷新重试。";
        }
    }

    public async Task<OperationResult?> ChangeGuardModeAsync(int index)
    {
        if (!GuardModeEnabled || index is < 0 or > 1) return null;
        GuardIsBusy = true;
        try
        {
            var result = await _guard.SelectModeAsync(index == 1 ? RuntimeGuardMode.Full : RuntimeGuardMode.BasicV14);
            await RefreshGuardCoreAsync();
            return result;
        }
        finally { GuardIsBusy = false; }
    }

    /// <summary>强制清理全部安装器拦截（不依赖防护记录，兼容旧版本与独立防护工具）。不要求先读到状态，作为最终兜底入口。</summary>
    public async Task<OperationResult?> ForceCleanupGuardAsync()
    {
        if (!GuardCleanupEnabled) return null;
        GuardIsBusy = true;
        try
        {
            var result = await _guard.CleanupLegacyIfeoAsync([]);
            try { await RefreshGuardCoreAsync(); }
            catch (Exception ex)
            {
                Log.Error("运行库防护：强制清理后读取状态失败", ex);
                _guardStateKnown = false;
                GuardStatusText = "清理后无法读取防护状态：" + ex.Message;
                OnPropertyChanged(nameof(GuardToggleEnabled));
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("运行库防护：强制清理异常", ex);
            return OperationResult.Fail("强制清理异常：" + ex.Message);
        }
        finally { GuardIsBusy = false; }
    }

    /// <summary>切换防护模式。返回后端结果供页面弹窗；完成后重读真实状态。</summary>
    public async Task<OperationResult?> SetGuardAsync(bool enable)
    {
        // Closing also serves as an explicit retry when the previous status read failed.
        if (GuardIsBusy || RepairIsBusy || (enable && (!_guardStateKnown || !RuntimeOperationsAvailable)))
        {
            return null;
        }

        GuardIsBusy = true;
        try
        {
            var result = enable
                ? await _guard.EnableAsync()
                : await _guard.DisableAsync();
            try
            {
                await RefreshGuardCoreAsync();
                return result;
            }
            catch (Exception ex)
            {
                Log.Error("运行库防护：切换后读取实际状态失败", ex);
                _guardStateKnown = false;
                GuardStatusText = $"切换后无法读取实际防护状态：{ex.Message}";
                OnPropertyChanged(nameof(GuardToggleEnabled));
                return OperationResult.Fail(result.Message + "；" + GuardStatusText);
            }
        }
        catch (Exception ex)
        {
            Log.Error("运行库防护：切换过程异常", ex);
            GuardStatusText = $"防护切换异常：{ex.Message}";
            return OperationResult.Fail(GuardStatusText);
        }
        finally
        {
            GuardIsBusy = false;
        }
    }
}
