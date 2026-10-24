using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

namespace DeltaNFD.ViewModels;

/// <summary>ACE 清理（安全与隐私清理向）的页面状态。</summary>
public partial class AceViewModel : ObservableObject
{
    private readonly IAceService _ace = ServiceLocator.Ace;
    private readonly ISystemTweakService _tweaks = ServiceLocator.SystemTweaks;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isCleaning;
    [ObservableProperty] private string scanSummary = "正在扫描 ACE 组件…";
    [ObservableProperty] private string blockingText = "";
    [ObservableProperty] private string servicesText = "";
    [ObservableProperty] private string pathsText = "";
    [ObservableProperty] private string registryText = "";
    [ObservableProperty] private bool canClean;
    [ObservableProperty] private string coreCheckText = "正在检测 ACE 游戏与 ACE-CORE 组件归属…";
    [ObservableProperty] private bool coreCheckBusy;
    [ObservableProperty] private string coreCleanupResultText = "";
    [ObservableProperty] private bool hasBlockers;
    [ObservableProperty] private bool cleanupNeedsReview;
    [ObservableProperty] private string cleanupReviewMessage = "";

    // ---- 虚拟化环境检测 ----
    [ObservableProperty] private string virtSummaryText = "尚未检测。点「检测虚拟化环境」检查本机是否符合 ACE 反作弊的虚拟化要求。";
    [ObservableProperty] private string hardwareCheckText = "";
    [ObservableProperty] private string systemCheckText = "";
    [ObservableProperty] private bool virtCheckBusy;

    /// <summary>是否存在可一键修复的系统层冲突项（Hyper-V / VBS / HVCI 等）。</summary>
    [ObservableProperty] private bool hasFixableConflict;
    [ObservableProperty] private bool virtFixBusy;
    [ObservableProperty] private string virtFixText = "";

    /// <summary>三角洲进程运行中 → 已暂停 ACE 检测（不读取任何 ACE 信息）。</summary>
    [ObservableProperty] private bool detectionPaused;

    /// <summary>暂停原因提示（仅在 <see cref="DetectionPaused"/> 为 true 时有意义）。</summary>
    [ObservableProperty] private string detectionPauseText = "";

    private const string PausedNotice =
        "三角洲进程正在运行——已按需求暂停 ACE 组件检测，未读取任何 ACE 服务 / 目录 / 注册表项。" +
        "请完全退出游戏后点「重新扫描」。";

    public bool CleanRefreshEnabled => !IsBusy && !IsCleaning;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CleanRefreshEnabled));
        RefreshFlags();
    }

    public bool CleanEnabled => !IsBusy && !IsCleaning && !CoreCheckBusy && CanClean && !CleanupNeedsReview;

    public bool CoreCleanEnabled => CleanEnabled && !DetectionPaused;

    public string CoreCleanButtonText => IsCleaning ? "清理中…" : "仅清理 ACE-CORE SYS";

    public bool CanRefresh => !IsBusy && !IsCleaning;

    public string CleanButtonText => IsCleaning ? "清理中…" : "一键清除 ACE";

    public string CoreCheckButtonText => CoreCheckBusy ? "检测中…" : "开始检测";

    public bool CanCheckCore => !CoreCheckBusy && !DetectionPaused && !IsCleaning && !IsBusy;

    public string VirtCheckButtonText => VirtCheckBusy ? "检测中…" : "检测虚拟化环境";

    /// <summary>虚拟化检测不触碰 ACE 组件，游戏运行中也可进行——只与自身忙态互斥。</summary>
    public bool CanCheckVirt => !VirtCheckBusy && !VirtFixBusy;

    /// <summary>一键修复按钮：存在系统层 Fail 项且不在修复中。</summary>
    public bool CanFixVirt => HasFixableConflict && !VirtFixBusy && !VirtCheckBusy;

    public string VirtFixButtonText => VirtFixBusy ? "修复中…" : "一键修复系统层冲突";

    partial void OnVirtCheckBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(VirtCheckButtonText));
        OnPropertyChanged(nameof(CanCheckVirt));
        OnPropertyChanged(nameof(CanFixVirt));
    }

    partial void OnHasFixableConflictChanged(bool value) => OnPropertyChanged(nameof(CanFixVirt));

    partial void OnVirtFixBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(VirtFixButtonText));
        OnPropertyChanged(nameof(CanCheckVirt));
        OnPropertyChanged(nameof(CanFixVirt));
    }

    partial void OnCoreCheckBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CoreCheckButtonText));
        OnPropertyChanged(nameof(CanCheckCore));
        OnPropertyChanged(nameof(CleanEnabled));
        OnPropertyChanged(nameof(CoreCleanEnabled));
    }

    partial void OnDetectionPausedChanged(bool value) => RefreshFlags();

    // ---------------- 随游戏进程状态自动暂停 / 恢复 ----------------

    /// <summary>
    /// 订阅共享的游戏进程状态：三角洲启动 → 立刻切到"已暂停检测"并禁用检测按钮；
    /// 三角洲退出 → 自动重新检测。页面用 Loaded/Unloaded 调用（导航每次新建页面实例）。
    /// </summary>
    public void AttachGameState()
    {
        var gameProcess = ServiceLocator.GameProcess;
        gameProcess.PropertyChanged -= OnGameProcessChanged;
        gameProcess.PropertyChanged += OnGameProcessChanged;
    }

    public void DetachGameState() => ServiceLocator.GameProcess.PropertyChanged -= OnGameProcessChanged;

    private async void OnGameProcessChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IGameProcessService.IsGameRunning))
        {
            return;
        }

        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Log.Error("ACE 页跟随游戏进程状态刷新失败", ex);
        }
    }

    partial void OnIsCleaningChanged(bool value) => RefreshFlags();

    partial void OnCanCleanChanged(bool value) => RefreshFlags();
    partial void OnCleanupNeedsReviewChanged(bool value) => RefreshFlags();

    public void RefreshCleanupReview()
    {
        CleanupNeedsReview = AceCleanupReadiness.RequiresReview;
        CleanupReviewMessage = AceCleanupReadiness.DescribeBlocker();
    }

    private void RefreshFlags()
    {
        OnPropertyChanged(nameof(CleanEnabled));
        OnPropertyChanged(nameof(CleanButtonText));
        OnPropertyChanged(nameof(CleanRefreshEnabled));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanCheckCore));
        OnPropertyChanged(nameof(CoreCleanEnabled));
        OnPropertyChanged(nameof(CoreCleanButtonText));
    }

    public async Task LoadAsync()
    {
        if (IsBusy || IsCleaning)
        {
            return;
        }

        IsBusy = true;
        try
        {
            RefreshCleanupReview();
            var scan = await _ace.ScanAsync();

            if (scan.BlockedByGame)
            {
                DetectionPaused = true;
                DetectionPauseText = PausedNotice;
                HasBlockers = false;
                ServicesText = "ACE 服务：已暂停检测（三角洲运行中）";
                PathsText = "ACE 文件目录：已暂停检测（三角洲运行中）";
                RegistryText = "ACE 注册表项：已暂停检测（三角洲运行中）";
                ScanSummary = "已暂停 ACE 组件检测：三角洲进程运行中。";
                CanClean = false;

                // 同一闸门刷新 ACE-CORE 卡片：后端会立即返回 Blocked，不读取 ACE 目录。
                await CheckCoreFilesAsync();
                return;
            }

            DetectionPaused = false;
            DetectionPauseText = "";

            if (scan.BlockingProcesses.Count > 0)
            {
                HasBlockers = true;
                BlockingText = "⛔ 检测到反作弊守卫/游戏进程正在运行：" +
                               string.Join("、", scan.BlockingProcesses) + " —— 已禁止清理，请完全退出游戏后重试。";
            }
            else
            {
                HasBlockers = false;
                BlockingText = "✓ 无阻止进程";
            }

            var services = scan.Services.Where(s => s.Exists).ToList();
            ServicesText = services.Count == 0
                ? "ACE 服务：未安装"
                : "ACE 服务：" + string.Join("、", services.Select(s => $"{s.Name}（{s.StartMode}）"));

            var paths = scan.Paths.Where(p => p.Exists).ToList();
            PathsText = paths.Count == 0
                ? "ACE 文件目录：未找到"
                : "ACE 目录：" + string.Join("；",
                    paths.Select(p => $"{p.Name} {p.SizeMb:0.0} MB"));

            RegistryText = scan.RegistryKeys.Count == 0
                ? "ACE 注册表项：未找到"
                : "ACE 注册表登记：" + string.Join("；", scan.RegistryKeys);

            ScanSummary = scan.CanClean
                ? (scan.AnythingFound
                    ? $"扫描完成：发现 {scan.Services.Count(s => s.Exists)} 个服务、{paths.Count} 个目录、{scan.RegistryKeys.Count} 个注册表登记。组件存在不代表异常。"
                    : "未发现共享 ACE 组件或三角洲目录中的 ACE 组件。")
                : "存在阻止进程，无法清理。";
            CanClean = scan.CanClean && scan.AnythingFound;

            // 进页读取 ACE 游戏安装位置与驱动归属，不按文件数量判断异常。
            await CheckCoreFilesAsync();
        }
        catch (Exception ex)
        {
            HasBlockers = false;
            ScanSummary = $"扫描失败：{ex.Message}";
            CanClean = false;
        }
        finally
        {
            RefreshCleanupReview();
            IsBusy = false;
        }
    }

    /// <summary>一键清除。返回后端结果供页面弹窗；完成后自动重扫。</summary>
    public Task<OperationResult?> CleanAsync() => CleanAsync(coreOnly: false);

    public Task<OperationResult?> CleanCoreFilesAsync() => CleanAsync(coreOnly: true);

    private async Task<OperationResult?> CleanAsync(bool coreOnly)
    {
        if (IsCleaning || IsBusy || CoreCheckBusy)
        {
            return null;
        }

        IsCleaning = true;
        OperationResult result;
        try
        {
            var progress = new Progress<string>(s =>
            {
                if (coreOnly) CoreCleanupResultText = s;
                else ScanSummary = s;
            });
            result = coreOnly ? await _ace.CleanCoreFilesAsync(progress) : await _ace.CleanAsync(progress);
        }
        catch (Exception ex)
        {
            Log.Error("ACE 清理页面操作未完成", ex);
            result = OperationResult.Fail("ACE 清理未完成：" + ex.Message);
        }
        finally
        {
            IsCleaning = false;
        }
        if (coreOnly) CoreCleanupResultText = result.Message;
        await LoadAsync();
        return result;
    }

    /// <summary>ACE 游戏及驱动归属检测；多款游戏正常共存不构成清理依据。</summary>
    public async Task CheckCoreFilesAsync()
    {
        if (CoreCheckBusy)
        {
            return;
        }

        CoreCheckBusy = true;
        try
        {
            var result = await _ace.CheckCoreFilesAsync();

            if (result.Level == AceCoreCheckLevel.Blocked)
            {
                // 三角洲在扫描/检测期间启动也会走到这里——保持"已暂停"一致。
                DetectionPaused = true;
                DetectionPauseText = PausedNotice;
                CoreCheckText = "⏸ " + result.Summary + "\n👉 " + result.Advice;
                return;
            }

            var text = new StringBuilder();
            text.AppendLine(DescribeLevel(result.Level) + result.Summary);
            foreach (var line in result.Files)
            {
                text.AppendLine(line);
            }

            text.AppendLine("\n检测到的 ACE 游戏：");
            if (result.Games.Count == 0) text.AppendLine("未确认游戏安装位置；不代表这些驱动是残留。");
            foreach (var game in result.Games)
            {
                text.AppendLine($"· {game.Name}（{game.Source}）");
                text.AppendLine($"  安装目录：{game.InstallDirectory}");
                foreach (var directory in game.AceDirectories) text.AppendLine($"  ACE 目录：{directory}");
            }
            if (result.Associations.Count > 0) text.AppendLine("\n驱动匹配：");
            foreach (var association in result.Associations)
                text.AppendLine($"· {association.Path}\n  {(association.Games.Count > 0 ? string.Join("；", association.Games) : "归属待核对")}\n  {association.Evidence}");
            foreach (var note in result.Notes) text.AppendLine("· " + note);

            text.Append("👉 " + result.Advice);
            CoreCheckText = text.ToString();
        }
        catch (Exception ex)
        {
            Log.Error("ACE-CORE 检测未完成", ex);
            CoreCheckText = "检测未完成：" + ex.Message;
        }
        finally
        {
            CoreCheckBusy = false;
        }
    }

    private static string DescribeLevel(AceCoreCheckLevel level) => level switch
    {
        AceCoreCheckLevel.Normal => "✓ ",
        AceCoreCheckLevel.Abnormal => "⚠ ",
        AceCoreCheckLevel.Failed => "⛔ ",
        AceCoreCheckLevel.Blocked => "⏸ ",
        AceCoreCheckLevel.NeedsReview => "ℹ ",
        _ => "ℹ ",
    };

    /// <summary>
    /// 虚拟化环境检测：硬件层（BIOS 中 VT-x/SVM、VT-d/IOMMU）+ 系统层（VBS / HVCI / Hyper-V / 沙盒等）。
    /// 不触碰 ACE 组件，游戏运行中也可使用。
    /// </summary>
    public async Task CheckVirtualizationAsync()
    {
        if (VirtCheckBusy)
        {
            return;
        }

        VirtCheckBusy = true;
        try
        {
            var result = await _ace.CheckVirtualizationAsync();

            VirtSummaryText = result.HasFailure
                ? "⛔ " + result.OverallSummary
                : result.HasUnknown || result.HasWarnings
                    ? "⚠ " + result.OverallSummary
                    : "✓ " + result.OverallSummary;

            HardwareCheckText = FormatItems(result.Hardware, result.BoardInfo);
            SystemCheckText = FormatItems(result.System, "");
            // 系统层存在 Fail 项才允许一键修复（Hyper-V/VBS/HVCI/沙盒等冲突）。
            HasFixableConflict = result.HasFixableConflict;
        }
        catch (Exception ex)
        {
            Log.Error("虚拟化环境检测未完成", ex);
            VirtSummaryText = "⛔ 检测未完成：" + ex.Message;
            HardwareCheckText = "";
            SystemCheckText = "";
            HasFixableConflict = false;
        }
        finally
        {
            VirtCheckBusy = false;
        }
    }

    /// <summary>
    /// 一键修复系统层虚拟化冲突：等效 UP 推荐的「关闭 Hyper-V / VBS（含内核隔离）」，
    /// 走 <see cref="SystemTweak.HyperVAndVbs"/> 完整流程（bcdedit off + DISM 禁功能 + 服务改手动 + DeviceGuard 注册表）。
    /// 修复完成后自动重新检测。
    /// </summary>
    public async Task<OperationResult?> FixVirtualizationConflictsAsync()
    {
        if (VirtFixBusy || VirtCheckBusy)
        {
            return null;
        }

        VirtFixBusy = true;
        VirtFixText = "";
        try
        {
            var current = await _ace.CheckVirtualizationAsync();
            if (!current.HasFixableConflict)
            {
                VirtFixText = "当前未确认有可修复的系统运行冲突，未修改系统设置。请重新检测并核对详情。";
                HasFixableConflict = false;
                await CheckVirtualizationAsync();
                return OperationResult.Skip(VirtFixText);
            }
            var result = await _tweaks.DisableAsync(SystemTweak.HyperVAndVbs);
            VirtFixText = (result.Success ? "✓ " : "⛔ ") + result.Message
                          + (result.RequiresReboot ? "\n请先重启电脑，重启后再点「检测虚拟化环境」复核。" : "");
            // 修复完自动复核（状态刷新依赖重启，未重启时部分项仍会显示原状）。
            await CheckVirtualizationAsync();
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("虚拟化冲突一键修复未完成", ex);
            VirtFixText = "⛔ 修复未完成：" + ex.Message;
            return OperationResult.Fail("虚拟化冲突修复未完成：" + ex.Message);
        }
        finally
        {
            VirtFixBusy = false;
        }
    }

    private static string FormatItems(List<AceVirtCheckItem> items, string headerLine)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(headerLine))
        {
            text.AppendLine(headerLine);
        }

        foreach (var item in items)
        {
            text.AppendLine(DescribeVirtStatus(item.Status) + item.Name + "：" + item.Detail);
            if (item.Status is AceVirtStatus.Fail or AceVirtStatus.Warn or AceVirtStatus.Unknown
                && !string.IsNullOrWhiteSpace(item.Advice))
            {
                text.AppendLine("    👉 " + item.Advice);
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string DescribeVirtStatus(AceVirtStatus status) => status switch
    {
        AceVirtStatus.Pass => "✓ ",
        AceVirtStatus.Fail => "⛔ ",
        AceVirtStatus.Warn => "⚠ ",
        _ => "? ",
    };
}
