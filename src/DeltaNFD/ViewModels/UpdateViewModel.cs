using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaNFD.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.ViewModels;

/// <summary>
/// 设置页「软件更新」卡的后端：检查更新 / 下载安装包 / 交给安装器并退出。
///
/// 行为约定（见 HANDOFF §32）：
///   · 只提醒，不自动装——下载与安装都必须用户点确认；
///   · 安装包必须先通过 SHA256 与长度校验才会被执行；
///   · 确认框由页面通过 <see cref="ConfirmHook"/> 注入（ViewModel 不能直接弹 ContentDialog，§3.2）。
/// </summary>
public partial class UpdateViewModel : ObservableObject
{
    private readonly IUpdateService _update = ServiceLocator.Update;
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();

    private UpdateManifest? _manifest;
    private CancellationTokenSource? _downloadCts;
    private bool _attached;

    /// <summary>确认框钩子（由页面构造时赋值）：title, message, primaryButtonText → 是否确认。</summary>
    public Func<string, string, string, Task<bool>>? ConfirmHook { get; set; }

    [ObservableProperty]
    private string currentVersionText = "";

    [ObservableProperty]
    private string statusText = "尚未检查更新。";

    [ObservableProperty]
    private string availableVersionText = "";

    [ObservableProperty]
    private string notes = "";

    [ObservableProperty]
    private string releasePageUrl = "";

    [ObservableProperty]
    private string progressText = "";

    [ObservableProperty]
    private string actionButtonText = "检查更新";

    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionEnabled))]
    [NotifyPropertyChangedFor(nameof(SkipEnabled))]
    [NotifyPropertyChangedFor(nameof(DetailsVisible))]
    private bool updateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionEnabled))]
    [NotifyPropertyChangedFor(nameof(SkipEnabled))]
    [NotifyPropertyChangedFor(nameof(CheckEnabled))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelEnabled))]
    private bool isDownloading;

    [ObservableProperty]
    private bool showProgress;

    [ObservableProperty]
    private bool autoCheck = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SkipEnabled))]
    private bool mandatory;

    [ObservableProperty]
    private InfoBarSeverity severity = InfoBarSeverity.Informational;

    [ObservableProperty]
    private bool infoBarOpen;

    /// <summary>当前版本说明（关于卡与更新卡共用同一来源：csproj 的版本属性）。</summary>
    public string CurrentVersionDetail => $"当前版本 {AppVersion.Text}（{AppVersion.NumericText}）";

    /// <summary>下载目录（用户可手动取用/排查）。</summary>
    public string DownloadFolderPath => _update.UpdateRootDirectory;

    /// <summary>更新源说明 + 加镜像指引（显示在卡片底部）。</summary>
    public string SourceHint =>
        $"更新源：{_update.ConfigSourceDescription}。要加国内镜像，点「更新源配置」编辑 " +
        "%APPDATA%\\Delta NFD\\update-source.json（升级不会覆盖），把镜像主机写进 allowedHosts 即可。";

    public bool ActionEnabled => UpdateAvailable && !IsBusy;

    public bool SkipEnabled => UpdateAvailable && !IsBusy && !Mandatory;

    public bool CheckEnabled => !IsBusy;

    public bool CancelEnabled => IsDownloading;

    public bool DetailsVisible => UpdateAvailable && (Notes.Length > 0 || ReleasePageUrl.Length > 0);

    public UpdateViewModel()
    {
        // 直接写字段：构造期赋值不应触发 OnAutoCheckChanged 的持久化
        autoCheck = AppSettingsStore.Read().AutoUpdateCheckEnabled;
        CurrentVersionText = AppVersion.Text;
        RenderLastResult();
    }

    /// <summary>页面挂树时订阅服务广播（页面会随主题色切换重建，必须成对挂接/解挂）。</summary>
    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _update.AvailabilityChanged += OnAvailabilityChanged;
    }

    public void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        _update.AvailabilityChanged -= OnAvailabilityChanged;
    }

    private void OnAvailabilityChanged(bool available) => _dispatcher?.TryEnqueue(RenderLastResult);

    private void RenderLastResult()
    {
        if (_update.LastResult is { } result)
        {
            Apply(result);
        }
    }

    private void Apply(UpdateCheckResult result)
    {
        _manifest = result.Manifest;
        UpdateAvailable = result.HasUpdate;
        AvailableVersionText = result.AvailableVersionText;
        Notes = result.Notes ?? "";
        ReleasePageUrl = result.ReleasePageUrl ?? "";
        Mandatory = result.Mandatory;
        StatusText = result.Message;

        Severity = result.Status switch
        {
            UpdateCheckStatus.UpdateAvailable => result.Mandatory ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
            UpdateCheckStatus.UpToDate => InfoBarSeverity.Success,
            UpdateCheckStatus.Skipped => InfoBarSeverity.Informational,
            UpdateCheckStatus.NotInstalled => InfoBarSeverity.Informational,
            _ => InfoBarSeverity.Error,
        };

        // 「已关闭自动检查 / 未到节流间隔」是内部状态，不必打扰用户
        InfoBarOpen = result.Status is not (UpdateCheckStatus.Disabled or UpdateCheckStatus.Throttled);

        ActionButtonText = UpdateAvailable ? "下载并安装" : "检查更新";
    }

    // ---------------------------------------------------------------- 命令

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        InfoBarOpen = true;
        Severity = InfoBarSeverity.Informational;
        StatusText = "正在检查更新…";

        try
        {
            var result = await _update.CheckAsync(manual: true);
            Apply(result);
            Log.Info("更新：手动检查 → " + result.Status + "（" + result.Message + "）");
        }
        catch (Exception ex)
        {
            Log.Error("更新：手动检查失败", ex);
            _manifest = null;
            UpdateAvailable = false;
            StatusText = "检查更新失败：" + ex.Message;
            Severity = InfoBarSeverity.Error;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PrimaryActionAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!UpdateAvailable || _manifest is null)
        {
            await CheckAsync();
            return;
        }

        // Refresh before confirmation so an old notification does not install a superseded release.
        await CheckAsync();
        if (!UpdateAvailable || _manifest is null) return;
        var manifest = _manifest;
        var sizeHint = manifest.Installers is { Count: > 0 } installers && installers[0].SizeBytes > 0
            ? $"（约 {FormatBytes(installers[0].SizeBytes)}）"
            : "";

        var confirmed = ConfirmHook is not null &&
                        await ConfirmHook(
                            "下载并安装更新",
                            $"将下载 {AvailableVersionText}{sizeHint} 并静默安装。\n\n" +
                            "安装前本工具会退出，安装完成后自动重新打开；设置、备份与日志保存在 %APPDATA%，不会被删除。\n" +
                            "更新期间帧格 / CPU 调度等后台监控会中断约 1 分钟。",
                            "下载并安装");

        if (!confirmed)
        {
            Log.Info("更新：用户在确认框取消");
            return;
        }

        IsBusy = true;
        _downloadCts = new CancellationTokenSource();
        var progress = new DispatcherProgress(_dispatcher, OnProgress);

        try
        {
            InfoBarOpen = true;
            Severity = InfoBarSeverity.Informational;
            StatusText = "正在下载安装包…";
            IsDownloading = true;
            ShowProgress = true;
            ProgressPercent = 0;
            ProgressText = "";

            var download = await _update.DownloadAsync(manifest, progress, _downloadCts.Token);
            IsDownloading = false;

            if (!download.Success)
            {
                StatusText = download.Message;
                Severity = InfoBarSeverity.Error;
                Log.Warn("更新：下载失败 —— " + download.Message);
                return;
            }

            ActionButtonText = "重新尝试安装";
            StatusText = "下载完成并通过校验，正在启动安装程序…";

            var offline = await new OfflineModeCoordinator().PrepareForRemovalAsync(restoreCpuSets: false);
            if (!offline.Success)
            {
                StatusText = "更新暂缓：" + offline.Message;
                Severity = InfoBarSeverity.Error;
                return;
            }

            var launch = await _update.LaunchInstallerAsync(manifest, download.InstallerPath);
            if (!launch.Success)
            {
                StatusText = launch.Message;
                Severity = InfoBarSeverity.Error;
                return;
            }

            StatusText = launch.Message;
            Severity = InfoBarSeverity.Success;

            // 留一拍让用户看到提示，然后退出交给安装器（安装器装完会重新拉起新版本）
            await Task.Delay(900);
            App.MainWindow?.RequestExitForUpdate();
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消下载。";
            Severity = InfoBarSeverity.Informational;
            Log.Info("更新：用户取消下载");
        }
        catch (Exception ex)
        {
            Log.Error("更新：下载/安装流程失败", ex);
            StatusText = "更新失败：" + ex.Message;
            Severity = InfoBarSeverity.Error;
        }
        finally
        {
            IsDownloading = false;
            IsBusy = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    private void CancelDownload()
    {
        if (_downloadCts is { IsCancellationRequested: false } cts)
        {
            cts.Cancel();
            StatusText = "正在取消下载…";
        }
    }

    [RelayCommand]
    private void SkipVersion()
    {
        if (_manifest is not { } manifest)
        {
            return;
        }

        AppSettingsStore.Update(s => s.SkippedUpdateVersion = manifest.Version);
        Log.Info("更新：用户跳过版本 " + manifest.Version);

        UpdateAvailable = false;
        StatusText = $"已跳过 {AvailableVersionText}；下次发布新版本时仍会提示。";
        Severity = InfoBarSeverity.Informational;
    }

    [RelayCommand]
    private void OpenReleasePage() =>
        OpenUrl(ReleasePageUrl.Length > 0 ? ReleasePageUrl : _update.FallbackReleasePageUrl);

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(DownloadFolderPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = DownloadFolderPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("更新：打开下载目录失败", ex);
        }
    }

    /// <summary>生成（首次）并打开用户级更新源配置，方便用户自己加国内镜像。</summary>
    [RelayCommand]
    private void OpenUpdateSourceConfig()
    {
        var path = _update.UserConfigPath;
        try
        {
            _update.EnsureUserConfigTemplate();
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                StatusText = "已打开更新源配置：改完保存后点「检查更新」即可生效。";
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("更新：打开更新源配置失败", ex);
        }

        // 打不开文件（没有关联程序等）时退回打开所在目录
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = directory,
                    UseShellExecute = true,
                });
                StatusText = "已打开配置所在目录：" + directory;
            }
        }
        catch (Exception ex)
        {
            Log.Error("更新：打开更新源配置目录失败", ex);
        }
    }

    partial void OnAutoCheckChanged(bool value)
    {
        AppSettingsStore.Update(s => s.AutoUpdateCheckEnabled = value);
        Log.Info("更新：启动时自动检查更新 = " + value);
        if (!value)
        {
            StatusText = "已关闭启动时自动检查更新（仍可手动检查）。";
        }
    }

    private void OnProgress(UpdateProgressInfo info)
    {
        ProgressPercent = info.TotalBytes > 0 ? info.Fraction * 100 : 0;
        ProgressText = info.TotalBytes > 0
            ? $"{FormatBytes(info.BytesReceived)} / {FormatBytes(info.TotalBytes)}（{info.Fraction:P0}）· {FormatBytes((long)info.BytesPerSecond)}/s"
            : $"{FormatBytes(info.BytesReceived)} · {FormatBytes((long)info.BytesPerSecond)}/s";
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("更新：打开链接失败 " + url, ex);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
    }

    /// <summary>把后台下载进度切回 UI 线程（不依赖 SynchronizationContext，与 FrameService 同款做法）。</summary>
    private sealed class DispatcherProgress(DispatcherQueue? queue, Action<UpdateProgressInfo> handler)
        : IProgress<UpdateProgressInfo>
    {
        public void Report(UpdateProgressInfo value)
        {
            if (queue is null)
            {
                return;
            }

            queue.TryEnqueue(() => handler(value));
        }
    }
}
