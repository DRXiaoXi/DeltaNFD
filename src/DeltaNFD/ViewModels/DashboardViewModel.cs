using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaNFD.Services;

namespace DeltaNFD.ViewModels;
public partial class DashboardViewModel : ObservableObject
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private readonly ISystemOptimizer _optimizer = ServiceLocator.SystemOptimizer;
    /// <summary>帧格服务：供主界面「一键帧格模式」卡片直接绑定。</summary>
    public IFrameService Frame { get; } = ServiceLocator.Frame;

    // ---- 优化检测（全量扫描系统优化项目，预读项目实际状态；结果顺延到系统优化页） ----
    private bool isScanning;
    public bool IsScanning { get => isScanning; set => SetProperty(ref isScanning, value); }
    private string scanProgressText = "正在检测…";
    public string ScanProgressText { get => scanProgressText; set => SetProperty(ref scanProgressText, value); }
    private string scanCompletedText = "";
    public string ScanCompletedText { get => scanCompletedText; set => SetProperty(ref scanCompletedText, value); }
    // ---- 组件健康检测（与着色器 / ACE / 运行库服务的扫描结果同步） ----
    private string shaderStatusText = "检测中…";
    public string ShaderStatusText { get => shaderStatusText; set => SetProperty(ref shaderStatusText, value); }
    private DashboardHealthState shaderHealthState = DashboardHealthState.Checking;
    public DashboardHealthState ShaderHealthState
    {
        get => shaderHealthState;
        set
        {
            if (EqualityComparer<DashboardHealthState>.Default.Equals(shaderHealthState, value)) return;
            OnPropertyChanging(nameof(ShaderHealthState));
            shaderHealthState = value;
            OnShaderHealthStateChanged(value);
            OnPropertyChanged(nameof(ShaderHealthState));
        }
    }
    partial void OnShaderHealthStateChanged(DashboardHealthState value);
    private bool shaderCacheNeedsAttention;
    public bool ShaderCacheNeedsAttention
    {
        get => shaderCacheNeedsAttention;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(shaderCacheNeedsAttention, value)) return;
            OnPropertyChanging(nameof(ShaderCacheNeedsAttention));
            OnPropertyChanging(nameof(ShaderActionText));
            shaderCacheNeedsAttention = value;
            OnPropertyChanged(nameof(ShaderCacheNeedsAttention));
            OnPropertyChanged(nameof(ShaderActionText));
        }
    }
    public string ShaderActionText => ShaderCacheNeedsAttention ? "缓存修复与预热" : "去处理";
    private bool shaderActionAllowed = true;
    public bool ShaderActionAllowed
    {
        get => shaderActionAllowed;
        set { if (SetProperty(ref shaderActionAllowed, value)) OnPropertyChanged(nameof(ShaderActionVisible)); }
    }
    public bool ShaderActionVisible => ShaderActionAllowed && (ShaderHealthState is DashboardHealthState.Warning
        or DashboardHealthState.Abnormal or DashboardHealthState.Failed or DashboardHealthState.Disabled);

    private string aceStatusText = "检测中…";
    public string AceStatusText { get => aceStatusText; set => SetProperty(ref aceStatusText, value); }
    private DashboardHealthState aceHealthState = DashboardHealthState.Checking;
    public DashboardHealthState AceHealthState
    {
        get => aceHealthState;
        set
        {
            if (EqualityComparer<DashboardHealthState>.Default.Equals(aceHealthState, value)) return;
            OnPropertyChanging(nameof(AceHealthState));
            aceHealthState = value;
            OnAceHealthStateChanged(value);
            OnPropertyChanged(nameof(AceHealthState));
        }
    }
    partial void OnAceHealthStateChanged(DashboardHealthState value);
    private string runtimeStatusText = "检测中…";
    public string RuntimeStatusText { get => runtimeStatusText; set => SetProperty(ref runtimeStatusText, value); }
    private DashboardHealthState runtimeHealthState = DashboardHealthState.Normal;
    public DashboardHealthState RuntimeHealthState
    {
        get => runtimeHealthState;
        set
        {
            if (EqualityComparer<DashboardHealthState>.Default.Equals(runtimeHealthState, value)) return;
            OnPropertyChanging(nameof(RuntimeHealthState));
            runtimeHealthState = value;
            OnRuntimeHealthStateChanged(value);
            OnPropertyChanged(nameof(RuntimeHealthState));
        }
    }
    partial void OnRuntimeHealthStateChanged(DashboardHealthState value);
    private bool runtimePolicyBlocked;
    public bool RuntimePolicyBlocked
    {
        get => runtimePolicyBlocked;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(runtimePolicyBlocked, value)) return;
            OnPropertyChanging(nameof(RuntimePolicyBlocked));
            runtimePolicyBlocked = value;
            OnRuntimePolicyBlockedChanged(value);
            OnPropertyChanged(nameof(RuntimePolicyBlocked));
        }
    }
    partial void OnRuntimePolicyBlockedChanged(bool value);
    private bool isHealthScanning;
    public bool IsHealthScanning
    {
        get => isHealthScanning;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(isHealthScanning, value)) return;
            OnPropertyChanging(nameof(IsHealthScanning));
            isHealthScanning = value;
            OnIsHealthScanningChanged(value);
            OnPropertyChanged(nameof(IsHealthScanning));
        }
    }
    partial void OnIsHealthScanningChanged(bool value);
    public bool CanRefreshHealth => !IsHealthScanning && Services.Backends.PageBackendSessions.Manager.DeltaRunning == false;
    public bool RuntimeUiEnabled => ReleaseFeatures.RuntimeUiEnabled;
    public bool IsLimitedTestBuild => !RuntimeUiEnabled;
    public bool RuntimeAlertVisible => RuntimeUiEnabled && RuntimeHealthState == DashboardHealthState.Warning;
    public string RuntimeActionText => "查看运行库";
    public string HealthSummary => Services.Backends.PageBackendSessions.Manager.DeltaRunning != false
        ? DashboardMemoryHealth.PausedSummary(MemoryHealthState, HealthPauseReason)
        : DashboardHealthPresentation.Summary(IsHealthScanning, ShaderHealthState, AceHealthState, RuntimeAlertVisible ? RuntimeHealthState : DashboardHealthState.Normal, MemoryHealthState);

    private string targetName = "三角洲";
    public string TargetName { get => targetName; set => SetProperty(ref targetName, value); }
    private string targetModeText = "三角洲模式";
    public string TargetModeText { get => targetModeText; set => SetProperty(ref targetModeText, value); }
    private string targetPathText = "三角洲默认主进程";
    public string TargetPathText { get => targetPathText; set => SetProperty(ref targetPathText, value); }
    private string lastHealthCheckText = "尚未检测";
    public string LastHealthCheckText { get => lastHealthCheckText; set => SetProperty(ref lastHealthCheckText, value); }
    private bool _displayedCustomTarget;
    public void RefreshTargetDisplay()
    {
        var target = ServiceLocator.GameTarget.Current;
        TargetName = target.DisplayName;
        TargetModeText = target.IsCustom ? "自定义目标" : "三角洲模式";
        TargetPathText = target.IsCustom ? target.ExecutablePath : "三角洲默认主进程";
        if (target.IsCustom)
        {
            ShaderHealthState = AceHealthState = DashboardHealthState.NotApplicable;
            ShaderStatusText = AceStatusText = GameTargetService.DeltaOnlyMessage;
        }
        else if (_displayedCustomTarget)
        {
            ShaderHealthState = AceHealthState = DashboardHealthState.Information;
            ShaderStatusText = AceStatusText = "目标已变化，等待重新检测。";
        }

        _displayedCustomTarget = target.IsCustom;
    }

    partial void OnShaderHealthStateChanged(DashboardHealthState value)
    {
        if (value is DashboardHealthState.Checking or DashboardHealthState.Disabled) ShaderActionAllowed = true;
        OnPropertyChanged(nameof(ShaderActionVisible));
        OnPropertyChanged(nameof(HealthSummary));
    }
    partial void OnAceHealthStateChanged(DashboardHealthState value) => OnPropertyChanged(nameof(HealthSummary));
    partial void OnRuntimeHealthStateChanged(DashboardHealthState value)
    {
        OnPropertyChanged(nameof(RuntimeAlertVisible));
        OnPropertyChanged(nameof(HealthSummary));
        OnPropertyChanged(nameof(RuntimeActionText));
    }
    partial void OnRuntimePolicyBlockedChanged(bool value) => OnPropertyChanged(nameof(RuntimeActionText));

    public void RefreshRuntimeDetectionPreference()
    {
        // Retired preference cannot suppress the new minimum-version reminder.
        if (RuntimeHealthState is DashboardHealthState.Disabled or DashboardHealthState.Checking)
            RuntimeHealthState = DashboardHealthState.Normal;
    }

    partial void OnIsHealthScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRefreshHealth));
        OnPropertyChanged(nameof(HealthSummary));
    }

    private string powerPlanText = "--";
    public string PowerPlanText { get => powerPlanText; set => SetProperty(ref powerPlanText, value); }
    private string enabledOptimizesText = "0 / 0";
    public string EnabledOptimizesText { get => enabledOptimizesText; set => SetProperty(ref enabledOptimizesText, value); }
    private bool isOptimizing;
    public bool IsOptimizing { get => isOptimizing; set => SetProperty(ref isOptimizing, value); }
    private double optimizeProgress;
    public double OptimizeProgress { get => optimizeProgress; set => SetProperty(ref optimizeProgress, value); }
    private string optimizeStatusText = "就绪";
    public string OptimizeStatusText { get => optimizeStatusText; set => SetProperty(ref optimizeStatusText, value); }
    public DashboardViewModel()
    {
        RefreshTargetDisplay();
        _ = InitializeAsync();
    }

    /// <summary>
     
    /// 启动（或续接）优化全量扫描：主页展示优化项目状态，扫描缓存顺延到系统优化页（HANDOFF §25）。
     
    /// 订阅静态扫描事件；VM 随页面重建，Detach 由页面 Unloaded 调用防泄漏。
     
    /// </summary>
    public void StartOptimizationScan()
    {
        OptimizationScan.Changed -= OnScanChanged;
        OptimizationScan.Changed += OnScanChanged;
        OnScanChanged();
        _ = OptimizationScan.RunIfNotScannedAsync();
    }

    public void DetachScanListener() => OptimizationScan.Changed -= OnScanChanged;
    private void OnScanChanged()
    {
        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(OnScanChanged);
            return;
        }

        IsScanning = OptimizationScan.IsScanning;
        ScanProgressText = OptimizationScan.ProgressText + (OptimizationScan.Error.Length > 0 ? "；" + OptimizationScan.Error : "");
        ScanCompletedText = OptimizationScan.CompletedTime is null ? "" : $"最近预读 {OptimizationScan.CompletedTime:HH:mm:ss}";
    }

    private Task InitializeAsync() => Task.CompletedTask;
    private async Task LoadShaderHealthAsync(int generation)
    {
        if (ServiceLocator.GameTarget.IsCustom)
        {
            ShaderHealthState = DashboardHealthState.NotApplicable;
            ShaderStatusText = GameTargetService.DeltaOnlyMessage;
            return;
        }

        var result = await DashboardShaderHealth.CheckAsync(ServiceLocator.Shader, ServiceLocator.NvProfile, () => CanApplyHealth(generation));
        if (!CanApplyHealth(generation))
            return;
        ShaderCacheNeedsAttention = result.OpenCacheRecovery;
        ShaderActionAllowed = result.ShowAction;
        ShaderStatusText = result.Text;
        ShaderHealthState = result.State;
    }

    private async Task LoadAceHealthAsync(int generation)
    {
        if (ServiceLocator.GameTarget.IsCustom)
        {
            AceHealthState = DashboardHealthState.NotApplicable;
            AceStatusText = GameTargetService.DeltaOnlyMessage;
            return;
        }

        // 与 ACE 页共用归属检测及“未关联黄、有效手动关联后仍过多红”的计数策略。
        var scan = await ServiceLocator.Ace.ScanAsync();
        if (!CanApplyHealth(generation))
            return;
        var core = await ServiceLocator.Ace.CheckCoreFilesAsync();
        if (!CanApplyHealth(generation))
            return;
        // 必须先判"已暂停"：三角洲运行中时后端跳过检测（未读取任何 ACE 信息），
        // 空的 Services/Paths 若照常往下走会被误读成"本机很干净"。
        if (scan.BlockedByGame || core.Level == AceCoreCheckLevel.Blocked)
        {
            AceHealthState = DashboardHealthState.NotApplicable;
            AceStatusText = "三角洲进程运行中，已暂停 ACE 检测（未读取 ACE 组件）；退出游戏后重新检测。";
            return;
        }

        if (core.Level == AceCoreCheckLevel.Failed)
        {
            AceHealthState = DashboardHealthState.Failed;
            AceStatusText = core.Summary;
            return;
        }

        if (core.Level == AceCoreCheckLevel.Abnormal)
        {
            AceHealthState = DashboardHealthPresentation.AceCore(core.Level, core.HasExcessCoreFiles);
            AceStatusText = core.Summary;
            return;
        }

        if (core.Level == AceCoreCheckLevel.NeedsReview)
        {
            AceHealthState = DashboardHealthPresentation.AceCore(core.Level, core.HasExcessCoreFiles);
            AceStatusText = core.Summary;
            return;
        }

        AceHealthState = DashboardHealthState.Normal;
        var runningAceProcesses = scan.RunningProcesses.Except(scan.BlockingProcesses, StringComparer.OrdinalIgnoreCase).ToList();
        if (!scan.AnythingFound && runningAceProcesses.Count == 0 && core.Games.Count == 0 && core.SysCount == 0)
        {
            AceStatusText = scan.BlockingProcesses.Count > 0 ? "未检测到 ACE 残留组件（游戏/守卫进程正在运行）" : "未检测到 ACE 组件";
            return;
        }

        var serviceCount = scan.Services.Count(item => item.Exists);
        var pathCount = scan.Paths.Count(item => item.Exists);
        AceStatusText = $"检测到 {serviceCount} 个 ACE 服务、{pathCount} 个文件目录、{scan.RegistryKeys.Count} 个注册表项" + (runningAceProcesses.Count > 0 ? $"、{runningAceProcesses.Count} 个 ACE 进程运行中" : "") + "；" + core.Summary;
    }

    private async Task LoadRuntimeHealthAsync(int generation)
    {
        var versions = await DeltaNFD.Inspection.RuntimeVersionInspection.ReadAsync();
        if (!CanApplyHealth(generation)) return;
        RuntimeHealthState = versions.RequiresUpdate ? DashboardHealthState.Warning : DashboardHealthState.Normal;
        RuntimeStatusText = versions.RequiresUpdate
            ? "C++ 运行库低于 14.44：" + versions.OlderVersions + "；可在运行库页选择 14.44 / 14.52 基线。"
            : "";
        if (versions.Issues.Count > 0) Log.Warn("运行库版本读取：" + string.Join("；", versions.Issues));
    }

    [RelayCommand]
    private async Task QuickOptimizeAsync()
    {
        if (IsOptimizing)
        {
            return;
        }

        IsOptimizing = true;
        OptimizeProgress = 0;
        var progress = new Progress<string>(s =>
        {
            OptimizeStatusText = s;
            OptimizeProgress = Math.Min(100, OptimizeProgress + 100.0 / _optimizer.FullOptimizeStepCount);
        });
        await _optimizer.RunFullOptimizeAsync(progress);
        OptimizeProgress = 100;
        OptimizeStatusText = "优化完成";
        IsOptimizing = false;
        // 重读真实状态，避免展示与实际不符的占位数据
        await InitializeAsync();
    }
}
