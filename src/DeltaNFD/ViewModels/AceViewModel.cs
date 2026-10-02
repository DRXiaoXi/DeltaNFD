using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DeltaNFD.Services;

namespace DeltaNFD.ViewModels;

/// <summary>ACE 清理（安全与隐私清理向）的页面状态。</summary>
public partial class AceViewModel : ObservableObject
{
    private readonly IAceService _ace = ServiceLocator.Ace;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isCleaning;
    [ObservableProperty] private string scanSummary = "正在扫描 ACE 组件…";
    [ObservableProperty] private string blockingText = "";
    [ObservableProperty] private string servicesText = "";
    [ObservableProperty] private string pathsText = "";
    [ObservableProperty] private string registryText = "";
    [ObservableProperty] private bool canClean;
    [ObservableProperty] private string coreCheckText = "正在检测 ACE-CORE 冗余文件…";
    [ObservableProperty] private bool coreCheckBusy;
    [ObservableProperty] private bool hasBlockers;

    /// <summary>三角洲进程运行中 → 已暂停 ACE 检测（不读取任何 ACE 信息）。</summary>
    [ObservableProperty] private bool detectionPaused;

    /// <summary>暂停原因提示（仅在 <see cref="DetectionPaused"/> 为 true 时有意义）。</summary>
    [ObservableProperty] private string detectionPauseText = "";

    private const string PausedNotice =
        "三角洲进程正在运行——已按需求暂停 ACE 组件检测，未读取任何 ACE 服务 / 目录 / 注册表项。" +
        "请完全退出游戏后点「重新扫描」。";

    public bool CleanRefreshEnabled => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CleanRefreshEnabled));
        RefreshFlags();
    }

    public bool CleanEnabled => !IsBusy && !IsCleaning && CanClean;

    public bool CanRefresh => !IsBusy;

    public string CleanButtonText => IsCleaning ? "清理中…" : "一键清除 ACE";

    public string CoreCheckButtonText => CoreCheckBusy ? "检测中…" : "开始检测";

    public bool CanCheckCore => !CoreCheckBusy && !DetectionPaused;

    partial void OnCoreCheckBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CoreCheckButtonText));
        OnPropertyChanged(nameof(CanCheckCore));
    }

    partial void OnDetectionPausedChanged(bool value) => OnPropertyChanged(nameof(CanCheckCore));

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

    private void RefreshFlags()
    {
        OnPropertyChanged(nameof(CleanEnabled));
        OnPropertyChanged(nameof(CleanButtonText));
    }

    public async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
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
                ? "ACE 注册表项：无残留"
                : "注册表残留：" + string.Join("；", scan.RegistryKeys);

            ScanSummary = scan.CanClean
                ? (scan.AnythingFound
                    ? $"扫描完成：发现 {scan.Services.Count(s => s.Exists)} 个服务、{paths.Count} 个目录、{scan.RegistryKeys.Count} 个注册表残留，可清理。"
                    : "未发现 ACE 组件（本机很干净）。")
                : "存在阻止进程，无法清理。";
            CanClean = scan.CanClean && scan.AnythingFound;

            // 进页自动做一次 ACE-CORE 冗余检测
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
            IsBusy = false;
        }
    }

    /// <summary>一键清除。返回后端结果供页面弹窗；完成后自动重扫。</summary>
    public async Task<OperationResult?> CleanAsync()
    {
        if (IsCleaning)
        {
            return null;
        }

        IsCleaning = true;
        try
        {
            var progress = new Progress<string>(s => ScanSummary = s);
            var result = await _ace.CleanAsync(progress);
            await LoadAsync();
            return result;
        }
        finally
        {
            IsCleaning = false;
        }
    }

    /// <summary>ACE-CORE 冗余文件检测（清除 ACE 后会重装，此检测帮助发现多版本残留）。</summary>
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

            text.Append("👉 " + result.Advice);
            CoreCheckText = text.ToString();
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
        _ => "ℹ ",
    };
}
