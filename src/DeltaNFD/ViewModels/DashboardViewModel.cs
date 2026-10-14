using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaNFD.Services;

namespace DeltaNFD.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly ISystemOptimizer _optimizer = ServiceLocator.SystemOptimizer;

    /// <summary>帧格服务：供主界面「一键帧格模式」卡片直接绑定。</summary>
    public IFrameService Frame { get; } = ServiceLocator.Frame;

    // ---- 优化检测（全量扫描系统优化项目，计算优化分数；结果顺延到系统优化页） ----
    [ObservableProperty] private double optimizeScore;
    [ObservableProperty] private int optimizeApplied;
    [ObservableProperty] private int optimizeTotal;
    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private string scanProgressText = "正在检测…";
    [ObservableProperty] private string scanCompletedText = "";

    /// <summary>优化分数环的颜色语义：≥80 绿色 / ≥50 黄色 / 低于 50 红色（页面函数绑定取色）。</summary>
    public bool ScoreGood => OptimizeScore >= 80;

    /// <summary>优化分数环的颜色语义：中等（黄色）。</summary>
    public bool ScoreWarn => OptimizeScore >= 50 && OptimizeScore < 80;

    // ---- 组件健康检测（与着色器 / ACE / 运行库服务的扫描结果同步） ----
    [ObservableProperty] private string shaderStatusText = "检测中…";
    [ObservableProperty] private DashboardHealthState shaderHealthState = DashboardHealthState.Checking;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShaderActionText))]
    private bool shaderCacheNeedsAttention;
    public string ShaderActionText => ShaderCacheNeedsAttention ? "缓存修复与预热" : "去处理";
    [ObservableProperty] private string aceStatusText = "检测中…";
    [ObservableProperty] private DashboardHealthState aceHealthState = DashboardHealthState.Checking;
    [ObservableProperty] private string runtimeStatusText = "检测中…";
    [ObservableProperty] private DashboardHealthState runtimeHealthState = DashboardHealthState.Checking;
    [ObservableProperty] private bool isHealthScanning;

    public bool CanRefreshHealth => !IsHealthScanning;

    public string HealthSummary => DashboardHealthPresentation.Summary(IsHealthScanning, ShaderHealthState, AceHealthState, RuntimeHealthState);
    [ObservableProperty] private string targetName = "三角洲";
    [ObservableProperty] private string targetModeText = "三角洲模式";
    [ObservableProperty] private string targetPathText = "三角洲默认主进程";
    [ObservableProperty] private string lastHealthCheckText = "尚未检测";
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

    partial void OnShaderHealthStateChanged(DashboardHealthState value) => OnPropertyChanged(nameof(HealthSummary));
    partial void OnAceHealthStateChanged(DashboardHealthState value) => OnPropertyChanged(nameof(HealthSummary));
    partial void OnRuntimeHealthStateChanged(DashboardHealthState value) => OnPropertyChanged(nameof(HealthSummary));
    partial void OnIsHealthScanningChanged(bool value)
    { OnPropertyChanged(nameof(CanRefreshHealth)); OnPropertyChanged(nameof(HealthSummary)); }

    partial void OnOptimizeScoreChanged(double value)
    {
        OnPropertyChanged(nameof(ScoreGood));
        OnPropertyChanged(nameof(ScoreWarn));
    }

    [ObservableProperty] private string powerPlanText = "--";
    [ObservableProperty] private string enabledOptimizesText = "0 / 0";
    [ObservableProperty] private bool isOptimizing;
    [ObservableProperty] private double optimizeProgress;
    [ObservableProperty] private string optimizeStatusText = "就绪";

    public DashboardViewModel()
    {
        RefreshTargetDisplay();
        _ = InitializeAsync();
    }

    /// <summary>
    /// 启动（或续接）优化全量扫描：主页展示优化分数，扫描缓存顺延到系统优化页（HANDOFF §25）。
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
        OptimizeScore = OptimizationScan.Score;
        OptimizeApplied = OptimizationScan.Applied;
        OptimizeTotal = OptimizationScan.Total;
        IsScanning = OptimizationScan.IsScanning;
        ScanProgressText = OptimizationScan.ProgressText.Length == 0 ? "正在检测…" : OptimizationScan.ProgressText;
        ScanCompletedText = OptimizationScan.CompletedTime is null
            ? ""
            : $"检测完成于 {OptimizationScan.CompletedTime:HH:mm:ss} · 结果已同步到系统优化页";
    }

    private async Task InitializeAsync()
    {
        var overview = await _optimizer.GetSystemOverviewAsync();

        PowerPlanText = overview.PowerPlan;

        var items = await _optimizer.GetOptimizeItemsAsync();
        EnabledOptimizesText = $"{items.Count(i => i.IsEnabled)} / {items.Count}";
    }

    /// <summary>刷新主页组件状态；调用真实页面后端使用的同一组扫描服务。</summary>
    public async Task RefreshHealthAsync()
    {
        if (IsHealthScanning)
        {
            return;
        }

        IsHealthScanning = true;
        ShaderCacheNeedsAttention = false;
        ShaderHealthState = AceHealthState = RuntimeHealthState = DashboardHealthState.Checking;
        ShaderStatusText = AceStatusText = RuntimeStatusText = "检测中…";

        try
        {
            await LoadShaderHealthAsync();
        }
        catch (Exception ex)
        {
            ShaderHealthState = DashboardHealthState.Failed;
            ShaderStatusText = "检测失败：" + ex.Message;
            Log.Error("主页：着色器组件检测失败", ex);
        }

        try
        {
            await LoadAceHealthAsync();
        }
        catch (Exception ex)
        {
            AceHealthState = DashboardHealthState.Failed;
            AceStatusText = "检测失败：" + ex.Message;
            Log.Error("主页：ACE 组件检测失败", ex);
        }

        try
        {
            await LoadRuntimeHealthAsync();
        }
        catch (Exception ex)
        {
            RuntimeHealthState = DashboardHealthState.Failed;
            RuntimeStatusText = "检测失败：" + ex.Message;
            Log.Error("主页：运行库组件检测失败", ex);
        }

        LastHealthCheckText = "最近检测 " + DateTimeOffset.Now.ToString("HH:mm:ss");
        RefreshTargetDisplay();
        IsHealthScanning = false;
    }

    private async Task LoadShaderHealthAsync()
    {
        if (ServiceLocator.GameTarget.IsCustom)
        {
            ShaderHealthState = DashboardHealthState.NotApplicable;
            ShaderStatusText = GameTargetService.DeltaOnlyMessage;
            return;
        }
        var result = await DashboardShaderHealth.CheckAsync(ServiceLocator.Shader, ServiceLocator.NvProfile);
        ShaderCacheNeedsAttention = result.OpenCacheRecovery;
        ShaderStatusText = result.Text;
        ShaderHealthState = result.State;
    }

    private async Task LoadAceHealthAsync()
    {
        if (ServiceLocator.GameTarget.IsCustom)
        {
            AceHealthState = DashboardHealthState.NotApplicable;
            AceStatusText = GameTargetService.DeltaOnlyMessage;
            return;
        }
        // 与 ACE 页一致执行完整的组件/服务/目录/注册表扫描，并补充 ACE-CORE 冗余检查。
        var scan = await ServiceLocator.Ace.ScanAsync();
        var core = await ServiceLocator.Ace.CheckCoreFilesAsync();

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
            AceHealthState = DashboardHealthState.Abnormal;
            AceStatusText = core.Summary;
            return;
        }

        AceHealthState = DashboardHealthState.Normal;
        var runningAceProcesses = scan.RunningProcesses
            .Except(scan.BlockingProcesses, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!scan.AnythingFound && runningAceProcesses.Count == 0)
        {
            AceStatusText = scan.BlockingProcesses.Count > 0
                ? "未检测到 ACE 残留组件（游戏/守卫进程正在运行）"
                : "未检测到 ACE 组件";
            return;
        }

        var serviceCount = scan.Services.Count(item => item.Exists);
        var pathCount = scan.Paths.Count(item => item.Exists);
        AceStatusText = $"检测到 {serviceCount} 个 ACE 服务、{pathCount} 个文件目录、{scan.RegistryKeys.Count} 个注册表项" +
                        (runningAceProcesses.Count > 0 ? $"、{runningAceProcesses.Count} 个 ACE 进程运行中" : "") +
                        (core.Level == AceCoreCheckLevel.Normal ? "；ACE-CORE 驱动正常" : "；未发现 ACE-CORE 驱动文件");
    }

    private async Task LoadRuntimeHealthAsync()
    {
        var status = await ServiceLocator.Guard.GetStatusAsync();
        var scan = await ServiceLocator.Guard.Scan14Async();
        RuntimeHealthState = scan.HasAbnormal ? DashboardHealthState.Abnormal : scan.HasWarning || status.GuardIssue.Length > 0 || status.IfeoRestorePending || status.Ue4RestorePending || status.Ue4PrereqDenied || status.IfeoManagedCount > 0 && !status.IfeoApplied
            ? DashboardHealthState.Warning : DashboardHealthState.Normal;
        RuntimeStatusText = scan.HasAbnormal ? "14.x 兼容性异常：检测到 14.51 或 Visual C++ v14，请进入运行库页核对"
            : scan.HasWarning ? "14.x 版本非 14.42 或状态待核对，请进入运行库页查看"
            : status.GuardIssue.Length > 0 ? "防护状态待核对：" + status.GuardIssue
            : status.IfeoRestorePending || status.Ue4RestorePending ? "旧防护有待恢复记录"
            : status.IfeoApplied ? $"防护已开启，拦截 {status.ScopeManagedCount}/{status.IfeoTotal} 个安装器"
            : status.IfeoManagedCount > 0 || status.Ue4PrereqDenied ? "防护部分生效或有残留，可进入防护页核对"
            : "14.x 未发现版本异常；防护未开启";
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
