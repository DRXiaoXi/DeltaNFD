using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaNFD.Services;

namespace DeltaNFD.ViewModels;

/// <summary>主页组件检测的真实结果级别，用于区分信息、异常、不可用和扫描失败。</summary>
public enum DashboardHealthState
{
    Checking,
    Normal,
    Information,
    Warning,
    Abnormal,
    NotApplicable,
    Failed,
}

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
    [ObservableProperty] private string aceStatusText = "检测中…";
    [ObservableProperty] private DashboardHealthState aceHealthState = DashboardHealthState.Checking;
    [ObservableProperty] private string runtimeStatusText = "检测中…";
    [ObservableProperty] private DashboardHealthState runtimeHealthState = DashboardHealthState.Checking;
    [ObservableProperty] private bool isHealthScanning;

    public bool CanRefreshHealth => !IsHealthScanning;

    partial void OnIsHealthScanningChanged(bool value) => OnPropertyChanged(nameof(CanRefreshHealth));

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
        _ = InitializeAsync();
        StartOptimizationScan();
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
        finally
        {
            IsHealthScanning = false;
        }
    }

    private async Task LoadShaderHealthAsync()
    {
        if (ServiceLocator.GameTarget.IsCustom)
        {
            ShaderHealthState = DashboardHealthState.NotApplicable;
            ShaderStatusText = GameTargetService.DeltaOnlyMessage;
            return;
        }
        // 与着色器页读取同一后端：PSOCache GameVer 通用临时判定 + NVIDIA 驱动体检。
        var diag = await ServiceLocator.Shader.DiagnoseAsync();
        var status = await ServiceLocator.Shader.GetStatusAsync();
        var driverBad = status.DriverState is ShaderDriverState.TooOld or ShaderDriverState.ProblemSeries;

        if (diag.Level is ShaderDiagLevel.Abnormal or ShaderDiagLevel.AbnormalDriverIssue)
        {
            ShaderHealthState = DashboardHealthState.Abnormal;
            ShaderStatusText = diag.Summary;
        }
        else if (driverBad)
        {
            ShaderHealthState = DashboardHealthState.Abnormal;
            ShaderStatusText = status.DriverState == ShaderDriverState.TooOld
                ? $"驱动过老（{status.DriverVersion}）"
                : $"问题驱动（{status.DriverVersion}）";
        }
        else
        {
            ShaderHealthState = diag.Level switch
            {
                ShaderDiagLevel.Normal => DashboardHealthState.Normal,
                ShaderDiagLevel.Info => DashboardHealthState.Information,
                _ => DashboardHealthState.NotApplicable,
            };
            ShaderStatusText = diag.Level == ShaderDiagLevel.Normal && status.IsNvidia && status.DriverVersion.Length > 0
                ? $"正常（{status.DriverVersion}）"
                : diag.Summary;
        }
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
        // 与运行库页共用同一检测结果：问题 v14 包为异常，缺失/低于推荐版本为提醒。
        var report = await ServiceLocator.Guard.ScanVcRedistsAsync();
        RuntimeHealthState = report.HasAbnormal
            ? DashboardHealthState.Abnormal
            : report.HasSuboptimal
                ? DashboardHealthState.Warning
                : DashboardHealthState.Normal;
        RuntimeStatusText = report.HasAbnormal
            ? $"检测到 {report.Abnormal.Count} 个 v14 问题运行库"
            : report.HasSuboptimal
                ? $"{report.Suboptimal.Count} 项运行库非最适版本（低于推荐版本或未安装）"
                : "各分支运行库均为最适版本";
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
