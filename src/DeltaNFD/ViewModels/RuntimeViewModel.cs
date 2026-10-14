using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DeltaNFD.Services;

namespace DeltaNFD.ViewModels;

/// <summary>运行库页（安装记录/版本检测、Beta0.9 AIO 修复、新版防护模式）的页面状态。</summary>
public partial class RuntimeViewModel : ObservableObject
{
    public static RuntimeViewModel Current { get; } = new();
    [ObservableProperty] private bool uninstallAllPreviewBusy;
    partial void OnUninstallAllPreviewBusyChanged(bool value)
    {
        foreach (var name in new[] { nameof(UninstallAllEnabled), nameof(UninstallV14Enabled), nameof(RepairEnabled),
            nameof(CanCheck), nameof(GuardToggleEnabled), nameof(GuardModeEnabled), nameof(GuardCleanupEnabled) }) OnPropertyChanged(name);
    }
    private readonly IRuntimeGuardService _guard = ServiceLocator.Guard;
    private bool _detectionFailed;

    // ---------------- 检测 ----------------

    [ObservableProperty] private bool isChecking;
    [ObservableProperty] private string checkText = "正在检测运行库（v14 问题版本 + 分支最适版本）…";
    [ObservableProperty] private bool hasIssue;

    /// <summary>存在非最适版本运行库（低于推荐版本或未安装，黄色提醒）。</summary>
    [ObservableProperty] private bool hasSuboptimal;

    public bool CanCheck => !IsChecking && !IsRepairing && !UninstallV14Busy && !UninstallAllBusy && !UninstallAllPreviewBusy && !GuardIsBusy;

    public string CheckButtonText => IsChecking ? "检测中…" : "重新检测";

    partial void OnIsCheckingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CheckButtonText));
        // RepairEnabled = !IsRepairing && !IsChecking：IsChecking 变化时必须同步通知，
        // 否则进页自动检测把按钮判灰后，检测完成按钮永远不会再亮（x:Bind 不会自动追踪依赖属性）
        OnPropertyChanged(nameof(RepairEnabled));
        OnPropertyChanged(nameof(UninstallV14Enabled));
        OnPropertyChanged(nameof(UninstallAllEnabled));
    }

    partial void OnHasIssueChanged(bool value) => OnPropertyChanged(nameof(RepairButtonText));

    partial void OnHasSuboptimalChanged(bool value) => OnPropertyChanged(nameof(RepairButtonText));

    public async Task LoadAsync()
    {
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        try
        {
            var report = await _guard.ScanVcRedistsAsync();
            _detectionFailed = false;
            HasIssue = report.HasAbnormal;
            HasSuboptimal = report.HasSuboptimal;

            var text = new StringBuilder();
            if (report.HasAbnormal)
            {
                text.AppendLine($"⚠ 检测到 {report.Abnormal.Count} 个 v14 命名运行库（三角洲掉帧兼容性异常）：");
                foreach (var e in report.Abnormal)
                {
                    text.AppendLine($"· {e.Architecture}  {e.DisplayVersion}  （{e.DisplayName}）");
                }
            }
            else
            {
                text.AppendLine("✓ 未检测到 v14 运行库。");
            }

            if (report.HasSuboptimal)
            {
                text.AppendLine($"⚠ 检测到 {report.Suboptimal.Count} 项安装记录/推荐版本提醒：");
                foreach (var s in report.Suboptimal)
                {
                    text.AppendLine(s.Reason == "version-unknown"
                        ? $"· {s.Branch}：已有安装记录，版本无法核对；请查看诊断日志"
                        : string.IsNullOrEmpty(s.InstalledVersion)
                        ? $"· {s.Branch}：未检测到安装记录（推荐 {s.RecommendedVersion}）"
                        : $"· {s.Branch}：{s.InstalledVersion}（推荐 ≥ {s.RecommendedVersion}；版本建议不等于损坏）");
                }
            }
            else
            {
                text.AppendLine("✓ 各分支安装记录与推荐版本核对通过；不代表 DLL 完整性验证。");
            }

            text.Append("👉 " + report.Advice);
            CheckText = text.ToString();
        }
        catch (Exception ex)
        {
            _detectionFailed = true;
            HasIssue = true;
            HasSuboptimal = false;
            CheckText = "运行库检测失败，不能确认安装状态；不应据此执行全量卸载：" + ex.Message;
            Log.Error("运行库页面检测失败", ex);
        }
        finally
        {
            IsChecking = false;
        }
    }

    // ---------------- 修复 / 重装 ----------------

    [ObservableProperty] private bool isRepairing;
    [ObservableProperty] private bool strongRepairMode;
    public string StrongRepairDescription => StrongRepairConfirmation;
    public const string StrongRepairConfirmation = "开启后，两种修复都会先使用 DISM／SFC 修复 Windows 系统组件，并抽查 API Sets／UCRT；此阶段可能需要联网及等待数十分钟，界面显示工具实际进度，百分比停留不等于卡死。停止等待后系统工具可能继续运行，其结束前不能重试。随后 C++ 跳过卸载，直接逐包覆盖安装／修复，同版本文件也重新写入并复检；较新产品保留并验证。兼容性修复另含 DirectX 旧组件，部分情况需重启。";
    [ObservableProperty] private string repairStatusText = "";
    [ObservableProperty] private string compatibilityRepairStatusText = "";
    [ObservableProperty] private bool compatibilityRepairSelected;
    public bool IsHighFpsRepairing => IsRepairing && !CompatibilityRepairSelected;
    public bool IsCompatibilityRepairing => IsRepairing && CompatibilityRepairSelected;
    public bool CanCancelHighFpsRepair => CanCancelRepair && !CompatibilityRepairSelected;
    public bool CanCancelCompatibilityRepair => CanCancelRepair && CompatibilityRepairSelected;
    public string CompatibilityRepairButtonText => IsCompatibilityRepairing ? "兼容性修复中…" : "兼容性修复";
    [ObservableProperty] private bool repairCancelRequested;
    private CancellationTokenSource? _repairCancellation;
    public bool CanCancelRepair => IsRepairing && !RepairCancelRequested;
    public RuntimeRepairMode RepairMode => RuntimeRepairMode.FullReinstall;
    partial void OnRepairCancelRequestedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancelRepair));
        OnPropertyChanged(nameof(CanCancelHighFpsRepair));
        OnPropertyChanged(nameof(CanCancelCompatibilityRepair));
    }
    partial void OnCompatibilityRepairSelectedChanged(bool value) => NotifyRepairModes();
    private void NotifyRepairModes()
    {
        OnPropertyChanged(nameof(IsHighFpsRepairing)); OnPropertyChanged(nameof(IsCompatibilityRepairing));
        OnPropertyChanged(nameof(CanCancelHighFpsRepair)); OnPropertyChanged(nameof(CanCancelCompatibilityRepair));
        OnPropertyChanged(nameof(RepairButtonText)); OnPropertyChanged(nameof(CompatibilityRepairButtonText));
    }
    public void CancelRepair()
    {
        if (!CanCancelRepair) return;
        RepairCancelRequested = true;
        if (CompatibilityRepairSelected) CompatibilityRepairStatusText = "已请求停止后续兼容性修复；当前事务可能仍在运行，执行证据保留。";
        else RepairStatusText = "已请求停止后续步骤；当前安装事务可能仍在运行，不会强杀，也不代表已还原。";
        _repairCancellation?.Cancel();
    }

    public bool RepairEnabled => !IsRepairing && !IsChecking && !UninstallV14Busy && !UninstallAllBusy && !UninstallAllPreviewBusy && !GuardIsBusy;

    /// <summary>Beta0.9 完整 AIO 修复；防护继续使用新版实现。</summary>
    public string RepairButtonText => IsHighFpsRepairing ? "高帧性修复中…" : "高帧性修复";

    partial void OnIsRepairingChanged(bool value)
    {
        NotifyRepairModes();
        OnPropertyChanged(nameof(CanCancelRepair));
        OnPropertyChanged(nameof(RepairEnabled));
        OnPropertyChanged(nameof(RepairButtonText));
        OnPropertyChanged(nameof(CanCheck));
        // 修复期间必须同时禁用防护开关（否则 IFEO 会拦截修复包自身的安装器）
        OnPropertyChanged(nameof(GuardToggleEnabled));
        OnPropertyChanged(nameof(GuardModeEnabled));
        OnPropertyChanged(nameof(GuardCleanupEnabled));
        OnPropertyChanged(nameof(UninstallV14Enabled));
        OnPropertyChanged(nameof(UninstallAllEnabled));
    }

    /// <summary>静默重装运行库，逐包记录安装结果，并在完成后复检。</summary>
    public Task<OperationResult?> RepairAsync(RuntimeRepairMode? confirmedMode = null, bool? confirmedStrongMode = null)
        => RepairCoreAsync(false, confirmedMode, confirmedStrongMode ?? StrongRepairMode);
    public Task<OperationResult?> RepairCompatibilityAsync(bool? confirmedStrongMode = null)
        => RepairCoreAsync(true, null, confirmedStrongMode ?? StrongRepairMode);
    private async Task<OperationResult?> RepairCoreAsync(bool compatibility, RuntimeRepairMode? confirmedMode, bool strongMode)
    {
        if (!RepairEnabled)
        {
            return null;
        }

        using var cancellation = new CancellationTokenSource();
        _repairCancellation = cancellation;
        RepairCancelRequested = false;
        CompatibilityRepairSelected = compatibility;
        IsRepairing = true;
        if (compatibility) CompatibilityRepairStatusText = "正在准备兼容性修复…";
        else RepairStatusText = "正在准备高帧性修复…";
        bool operationEnded = false;
        try
        {
            var progress = new Progress<string>(s =>
            {
                if (!operationEnded && IsRepairing && !RepairCancelRequested && ReferenceEquals(_repairCancellation, cancellation))
                { if (compatibility) CompatibilityRepairStatusText = s; else RepairStatusText = s; }
            });
            var result = compatibility ? await _guard.RepairCompatibilityAsync(progress, cancellation.Token, strongMode)
                : await _guard.RepairVcRedistAsync(progress, confirmedMode ?? RepairMode, cancellation.Token, strongMode);
            operationEnded = true;
            var status = result.Success ? "修复流程及组件复检通过。" : "修复未完成，请查看失败明细及诊断目录。";
            if (compatibility) CompatibilityRepairStatusText = status; else RepairStatusText = status;
            if (result.OutcomeUnknown || RepairCancelRequested)
                return result;
            try { await LoadAsync(); }
            catch (Exception ex)
            {
                Log.Error("运行库修复：页面刷新检测失败", ex);
                CheckText = "页面检测刷新失败：" + ex.Message;
                return new OperationResult { Success = false, RequiresReboot = result.RequiresReboot,
                    DiagnosticsDirectory = result.DiagnosticsDirectory, Message = result.Message + "\n页面检测刷新失败：" + ex.Message };
            }
            if (_detectionFailed)
                return new OperationResult { Success = false, RequiresReboot = result.RequiresReboot,
                    DiagnosticsDirectory = result.DiagnosticsDirectory, Message = result.Message + "\n" + CheckText };
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("运行库修复请求未完成", ex);
            var error = "运行库操作未完成：" + ex.Message;
            if (compatibility) CompatibilityRepairStatusText = error; else RepairStatusText = error;
            return OperationResult.Fail(error);
        }
        finally
        {
            if (ReferenceEquals(_repairCancellation, cancellation)) _repairCancellation = null;
            IsRepairing = false;
        }
    }

    // ---------------- 单独卸载 V14 运行库 ----------------

    [ObservableProperty] private bool uninstallV14Busy;
    [ObservableProperty] private string uninstallV14StatusText = "";

    public bool UninstallV14Enabled => !UninstallV14Busy && !IsRepairing && !IsChecking && !UninstallAllBusy && !UninstallAllPreviewBusy && !GuardIsBusy;

    partial void OnUninstallV14BusyChanged(bool value)
    {
        OnPropertyChanged(nameof(UninstallV14Enabled));
        OnPropertyChanged(nameof(RepairEnabled));
        OnPropertyChanged(nameof(CanCheck));
        // 卸载期间禁用防护开关：中途开启 IFEO 会拦截尚未执行的卸载器
        OnPropertyChanged(nameof(GuardToggleEnabled));
        OnPropertyChanged(nameof(GuardModeEnabled));
        OnPropertyChanged(nameof(GuardCleanupEnabled));
        OnPropertyChanged(nameof(UninstallAllEnabled));
    }

    /// <summary>确认弹窗用的 v14 条目清单（空串 = 无条目）。</summary>
    public async Task<string> BuildV14ListTextAsync()
    {
        var entries = await _guard.GetV14EntriesAsync();
        return string.Join("\n", entries.Select(e =>
            $"· {e.Architecture}  {e.DisplayVersion}  （{e.DisplayName}）"));
    }

    /// <summary>单独卸载名字带「v14」的运行库（年份命名条目不动）；完成后自动重跑检测。</summary>
    public async Task<OperationResult?> UninstallV14Async()
    {
        if (!UninstallV14Enabled)
        {
            return null;
        }

        UninstallV14Busy = true;
        try
        {
            var progress = new Progress<string>(s => UninstallV14StatusText = s);
            var result = await _guard.UninstallV14Async(progress);
            if (!result.OutcomeUnknown) await LoadAsync();
            if (!result.OutcomeUnknown && _detectionFailed)
                return new OperationResult { Success = false, RequiresReboot = result.RequiresReboot, IsPartial = result.IsPartial,
                    DiagnosticsDirectory = result.DiagnosticsDirectory, Message = result.Message + "\n页面复检失败：" + CheckText };
            return result;
        }
        finally
        {
            UninstallV14Busy = false;
        }
    }

    // ---------------- 防护模式 ----------------
    [ObservableProperty] private bool uninstallAllBusy;
    [ObservableProperty] private bool uninstallAllAllowNonstandard;
    [ObservableProperty] private string uninstallAllStatusText = "";
    [ObservableProperty] private bool uninstallAllCancelRequested;
    private CancellationTokenSource? _allCancellation;
    public bool UninstallAllEnabled => RepairEnabled;
    public bool CanCancelUninstallAll => UninstallAllBusy && !UninstallAllCancelRequested;
    partial void OnUninstallAllCancelRequestedChanged(bool value) => OnPropertyChanged(nameof(CanCancelUninstallAll));
    partial void OnUninstallAllBusyChanged(bool value)
    {
        foreach (var name in new[] { nameof(UninstallAllEnabled), nameof(CanCancelUninstallAll), nameof(UninstallV14Enabled),
            nameof(RepairEnabled), nameof(CanCheck), nameof(GuardToggleEnabled), nameof(GuardModeEnabled), nameof(GuardCleanupEnabled) }) OnPropertyChanged(name);
    }
    public Task<RuntimeUninstallPreview> PreviewUninstallAllAsync() => _guard.PreviewUninstallAllAsync(UninstallAllAllowNonstandard);
    public void CancelUninstallAll()
    {
        if (!CanCancelUninstallAll) return;
        UninstallAllCancelRequested = true;
        UninstallAllStatusText = "已请求停止后续卸载；不会强杀安装事务，已卸载的组件不会自动还原。";
        _allCancellation?.Cancel();
    }
    public async Task<OperationResult?> UninstallAllAsync(string fingerprint, bool allowNonstandard = false)
    {
        if (!UninstallAllEnabled) return null;
        UninstallAllBusy = true; UninstallAllCancelRequested = false;
        using var cancellation = new CancellationTokenSource(); _allCancellation = cancellation;
        bool operationEnded = false;
        UninstallAllStatusText = "正在核验已确认的卸载清单…";
        try
        {
            var progress = new Progress<string>(message =>
            { if (!operationEnded && UninstallAllBusy && !UninstallAllCancelRequested && ReferenceEquals(_allCancellation, cancellation)) UninstallAllStatusText = message; });
            var result = await _guard.UninstallAllAsync(fingerprint, allowNonstandard, progress, cancellation.Token);
            operationEnded = true;
            UninstallAllStatusText = result.Success ? "全部卸载及复检通过。" : result.OutcomeUnknown ? "卸载事务未确认，请核对诊断记录。" : "卸载未全部完成，详见结果。";
            if (!result.OutcomeUnknown && !UninstallAllCancelRequested) await LoadAsync();
            if (!result.OutcomeUnknown && !UninstallAllCancelRequested && _detectionFailed)
                return new OperationResult { Success = false, RequiresReboot = result.RequiresReboot, IsPartial = result.IsPartial,
                    DiagnosticsDirectory = result.DiagnosticsDirectory, Message = result.Message + "\n页面复检失败：" + CheckText };
            return result;
        }
        catch (Exception ex)
        {
            UninstallAllStatusText = "卸载未完成：" + ex.Message;
            Log.Error("全量运行库卸载请求失败", ex);
            return new OperationResult { Success = false, OutcomeUnknown = ex is OperationCanceledException or IOException,
                Message = UninstallAllStatusText + (ex is OperationCanceledException or IOException ? "\n不能据此确认后端安装事务已结束，请核对诊断记录。" : "") };
        }
        finally { _allCancellation = null; UninstallAllBusy = false; }
    }

    [ObservableProperty] private bool guardIsEnabled;
    [ObservableProperty] private bool guardIsBusy;
    [ObservableProperty] private bool guardIsPartial;
    [ObservableProperty] private string guardStatusText = "正在读取防护状态…";
    private bool _guardStateKnown;
    public bool CanRepairLegacyUe4Acl { get; private set; }
    public string GuardUe4Path { get; private set; } = "";
    public RuntimeGuardMode GuardMode { get; private set; }
    public int GuardModeIndex => GuardMode == RuntimeGuardMode.Full ? 1 : 0;
    public bool GuardModeEnabled => GuardToggleEnabled && !GuardIsEnabled && _legacyIfeo.Count == 0;
    private IReadOnlyList<RuntimeGuardLegacyEntry> _legacyIfeo = [];
    public bool CanCleanupLegacyIfeo => _legacyIfeo.Count > 0;
    public bool GuardCleanupEnabled => !GuardIsBusy && !IsRepairing && !UninstallV14Busy && !UninstallAllBusy && !UninstallAllPreviewBusy;
    public string LegacyIfeoNames => string.Join("\n", _legacyIfeo.Select(e => e.Name));

    public bool GuardToggleEnabled => _guardStateKnown && !GuardIsBusy && !IsRepairing && !UninstallV14Busy && !UninstallAllBusy && !UninstallAllPreviewBusy;

    public string GuardToggleText => GuardIsBusy ? "切换中…" : GuardIsPartial ? "防护部分生效"
        : GuardIsEnabled ? "防护已开启" : "防护未开启";

    partial void OnGuardIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(GuardToggleEnabled));
        OnPropertyChanged(nameof(GuardModeEnabled));
        OnPropertyChanged(nameof(GuardCleanupEnabled));
        OnPropertyChanged(nameof(GuardToggleText));
        OnPropertyChanged(nameof(UninstallAllEnabled)); OnPropertyChanged(nameof(UninstallV14Enabled));
        OnPropertyChanged(nameof(RepairEnabled)); OnPropertyChanged(nameof(CanCheck));
    }

    partial void OnGuardIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(GuardToggleText));
        OnPropertyChanged(nameof(GuardModeEnabled));
    }
    partial void OnGuardIsPartialChanged(bool value) => OnPropertyChanged(nameof(GuardToggleText));

    /// <summary>读取防护模式状态（busy 守卫版，供进页/手动刷新调用）。</summary>
    public async Task LoadGuardAsync()
    {
        if (GuardIsBusy)
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
        if (GuardIsBusy || IsRepairing || UninstallV14Busy || UninstallAllBusy || (enable && !_guardStateKnown))
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
