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
///   · 更新任务属于应用；页面只挂接当前确认框，不拥有下载的生命周期。
/// </summary>
public partial class UpdateViewModel : ObservableObject
{
    private static readonly Lazy<UpdateViewModel> Shared = new(() => new UpdateViewModel());
    public static UpdateViewModel Current => Shared.Value;
    internal static bool HasCurrentWork => Shared.IsValueCreated && Shared.Value.IsBusy;

    private readonly IUpdateService _update;
    private readonly DispatcherQueue? _dispatcher;

    private UpdateManifest? _manifest;
    private CancellationTokenSource? _downloadCts;
    private object? _confirmationOwner;
    private Func<string, string, string, Task<bool>>? _confirmHook;
    private long _confirmationVersion;
    private long _statusVersion;
    private long _progressVersion;

    private string currentVersionText = "";
    public string CurrentVersionText { get => currentVersionText; set => SetProperty(ref currentVersionText, value); }

    private string statusText = "尚未检查更新。";
    public string StatusText { get => statusText; set => SetProperty(ref statusText, value); }

    private string availableVersionText = "";
    public string AvailableVersionText { get => availableVersionText; set => SetProperty(ref availableVersionText, value); }

    private string notes = "";
    public string Notes { get => notes; set => SetProperty(ref notes, value); }

    private string releasePageUrl = "";
    public string ReleasePageUrl { get => releasePageUrl; set => SetProperty(ref releasePageUrl, value); }

    private string progressText = "";
    public string ProgressText { get => progressText; set => SetProperty(ref progressText, value); }

    private string actionButtonText = "检查更新";
    public string ActionButtonText { get => actionButtonText; set => SetProperty(ref actionButtonText, value); }

    private double progressPercent;
    public double ProgressPercent { get => progressPercent; set => SetProperty(ref progressPercent, value); }

    private bool updateAvailable;
    public bool UpdateAvailable
    {
        get => updateAvailable;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(updateAvailable, value)) return;
            OnPropertyChanging(nameof(UpdateAvailable));
            OnPropertyChanging(nameof(ActionEnabled));
            OnPropertyChanging(nameof(SkipEnabled));
            OnPropertyChanging(nameof(DetailsVisible));
            updateAvailable = value;
            OnPropertyChanged(nameof(UpdateAvailable));
            OnPropertyChanged(nameof(ActionEnabled));
            OnPropertyChanged(nameof(SkipEnabled));
            OnPropertyChanged(nameof(DetailsVisible));
        }
    }

    private bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(isBusy, value)) return;
            OnPropertyChanging(nameof(IsBusy));
            OnPropertyChanging(nameof(ActionEnabled));
            OnPropertyChanging(nameof(SkipEnabled));
            OnPropertyChanging(nameof(CheckEnabled));
            OnPropertyChanging(nameof(ChannelEnabled));
            OnPropertyChanging(nameof(ServerEnabled));
            OnPropertyChanging(nameof(ModeEnabled));
            isBusy = value;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(ActionEnabled));
            OnPropertyChanged(nameof(SkipEnabled));
            OnPropertyChanged(nameof(CheckEnabled));
            OnPropertyChanged(nameof(ChannelEnabled));
            OnPropertyChanged(nameof(ServerEnabled));
            OnPropertyChanged(nameof(ModeEnabled));
        }
    }

    private bool isDownloading;
    public bool IsDownloading
    {
        get => isDownloading;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(isDownloading, value)) return;
            OnPropertyChanging(nameof(IsDownloading));
            OnPropertyChanging(nameof(CancelEnabled));
            isDownloading = value;
            OnPropertyChanged(nameof(IsDownloading));
            OnPropertyChanged(nameof(CancelEnabled));
        }
    }

    private bool showProgress;
    public bool ShowProgress { get => showProgress; set => SetProperty(ref showProgress, value); }

    private bool autoCheck = true;
    public bool AutoCheck
    {
        get => autoCheck;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(autoCheck, value)) return;
            OnPropertyChanging(nameof(AutoCheck));
            autoCheck = value;
            OnAutoCheckChanged(value);
            OnPropertyChanged(nameof(AutoCheck));
        }
    }
    partial void OnAutoCheckChanged(bool value);

    private bool emergencyPolicyEnabled = true;
    public bool EmergencyPolicyEnabled
    {
        get => emergencyPolicyEnabled;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(emergencyPolicyEnabled, value)) return;
            OnPropertyChanging(nameof(EmergencyPolicyEnabled));
            emergencyPolicyEnabled = value;
            OnEmergencyPolicyEnabledChanged(value);
            OnPropertyChanged(nameof(EmergencyPolicyEnabled));
        }
    }
    partial void OnEmergencyPolicyEnabledChanged(bool value);

    public string EmergencyPolicyStatus => EmergencyPolicyUpdateService.Status;

    private int updateChannelIndex;
    public int UpdateChannelIndex
    {
        get => updateChannelIndex;
        set
        {
            if (IsBusy || value is not (0 or 1) || updateChannelIndex == value) return;
            OnPropertyChanging(nameof(UpdateChannelIndex));
            OnPropertyChanging(nameof(UpdateChannelDescription));
            updateChannelIndex = value;
            OnUpdateChannelIndexChanged(value);
            OnPropertyChanged(nameof(UpdateChannelIndex));
            OnPropertyChanged(nameof(UpdateChannelDescription));
        }
    }
    partial void OnUpdateChannelIndexChanged(int value);

    public bool ChannelEnabled => !IsBusy;
    public string UpdateChannelDescription => UpdateChannelIndex == 0
        ? "接收 Alpha、Beta 和 Release 更新。" : "只接收 Beta 和 Release 更新。";

    private int updateServerIndex;
    public int UpdateServerIndex
    {
        get => updateServerIndex;
        set
        {
            if (IsBusy || value is < 0 or > 2 || updateServerIndex == value) return;
            OnPropertyChanging(nameof(UpdateServerIndex));
            OnPropertyChanging(nameof(UpdateServerDescription));
            OnPropertyChanging(nameof(SourceHint));
            updateServerIndex = value;
            OnUpdateServerIndexChanged(value);
            OnPropertyChanged(nameof(UpdateServerIndex));
            OnPropertyChanged(nameof(UpdateServerDescription));
            OnPropertyChanged(nameof(SourceHint));
        }
    }
    partial void OnUpdateServerIndexChanged(int value);

    public bool ServerEnabled => !IsBusy;
    public string UpdateServerDescription => UpdateServerPolicy.Description((UpdateServer)UpdateServerIndex);

    private int updateModeIndex;
    public int UpdateModeIndex
    {
        get => updateModeIndex;
        set
        {
            if (IsBusy || value is not (0 or 1) || updateModeIndex == value) return;
            OnPropertyChanging(nameof(UpdateModeIndex));
            OnPropertyChanging(nameof(UpdateModeDescription));
            OnPropertyChanging(nameof(SourceHint));
            updateModeIndex = value;
            OnUpdateModeIndexChanged(value);
            OnPropertyChanged(nameof(UpdateModeIndex));
            OnPropertyChanged(nameof(UpdateModeDescription));
            OnPropertyChanged(nameof(SourceHint));
        }
    }
    partial void OnUpdateModeIndexChanged(int value);
    public bool ModeEnabled => !IsBusy;
    public string UpdateModeDescription => UpdateModePolicy.Description((UpdateMode)UpdateModeIndex);

    private bool mandatory;
    public bool Mandatory
    {
        get => mandatory;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(mandatory, value)) return;
            OnPropertyChanging(nameof(Mandatory));
            OnPropertyChanging(nameof(SkipEnabled));
            mandatory = value;
            OnPropertyChanged(nameof(Mandatory));
            OnPropertyChanged(nameof(SkipEnabled));
        }
    }

    private InfoBarSeverity severity = InfoBarSeverity.Informational;
    public InfoBarSeverity Severity { get => severity; set => SetProperty(ref severity, value); }

    private bool infoBarOpen;
    public bool InfoBarOpen { get => infoBarOpen; set => SetProperty(ref infoBarOpen, value); }

    /// <summary>当前版本说明（关于卡与更新卡共用同一来源：csproj 的版本属性）。</summary>
    public string CurrentVersionDetail => $"当前版本 {AppVersion.Text}（{AppVersion.NumericText}）";

    /// <summary>下载目录（用户可手动取用/排查）。</summary>
    public string DownloadFolderPath => _update.UpdateRootDirectory;

    /// <summary>更新源说明。</summary>
    public string SourceHint =>
        $"更新服务器：{UpdateServerPolicy.Name((UpdateServer)UpdateServerIndex)}。更新方式：{UpdateModePolicy.Name((UpdateMode)UpdateModeIndex)}。";

    public bool ActionEnabled => UpdateAvailable && !IsBusy;

    public bool SkipEnabled => UpdateAvailable && !IsBusy && !Mandatory;

    public bool CheckEnabled => !IsBusy;

    public bool CancelEnabled => IsDownloading;

    public bool DetailsVisible => UpdateAvailable && (Notes.Length > 0 || ReleasePageUrl.Length > 0);

    private UpdateViewModel() : this(ServiceLocator.Update, DispatcherQueue.GetForCurrentThread()) { }

    internal UpdateViewModel(IUpdateService update, DispatcherQueue? dispatcher)
    {
        _update = update;
        _dispatcher = dispatcher;
        // 直接写字段：构造期赋值不应触发 OnAutoCheckChanged 的持久化
        autoCheck = AppSettingsStore.Read().AutoUpdateCheckEnabled;
        emergencyPolicyEnabled = AppSettingsStore.Read().EmergencyPolicyEnabled;
        updateChannelIndex = UpdateChannelPolicy.Normalize(AppSettingsStore.Read().UpdateChannel) == UpdateChannel.Internal ? 0 : 1;
        updateServerIndex = (int)UpdateServerPolicy.Normalize(AppSettingsStore.Read().UpdateServer);
        updateModeIndex = (int)UpdateModePolicy.Normalize(AppSettingsStore.Read().UpdateMode);
        CurrentVersionText = AppVersion.Text;
        RenderLastResult();
        // 与共享控制器同寿命：离开设置页后仍接收状态，不保留页面引用。
        _update.AvailabilityChanged += OnAvailabilityChanged;
        EmergencyPolicyUpdateService.Changed += OnEmergencyPolicyChanged;
    }

    /// <summary>页面只提供当前确认框；进度、取消入口和任务在页面重建后继续共用。</summary>
    public void Attach(object owner, Func<string, string, string, Task<bool>> confirm)
    {
        _confirmationOwner = owner;
        _confirmHook = confirm;
        _confirmationVersion++;
    }

    public void Detach(object owner)
    {
        if (!ReferenceEquals(_confirmationOwner, owner)) return;
        _confirmationOwner = null;
        _confirmHook = null;
        _confirmationVersion++;
    }

    private void OnAvailabilityChanged(bool available)
    {
        var version = Volatile.Read(ref _statusVersion);
        var result = _update.LastResult;
        _dispatcher?.TryEnqueue(() =>
        {
            if (!IsBusy && version == _statusVersion && ReferenceEquals(result, _update.LastResult)) RenderLastResult();
        });
    }

    private void OnEmergencyPolicyChanged() => _dispatcher?.TryEnqueue(() => OnPropertyChanged(nameof(EmergencyPolicyStatus)));

    partial void OnEmergencyPolicyEnabledChanged(bool value)
    {
        AppSettingsStore.Update(s => s.EmergencyPolicyEnabled = value);
        OnPropertyChanged(nameof(EmergencyPolicyStatus));
        if (value) _ = RefreshEmergencyPolicyAsync();
    }

    private async Task RefreshEmergencyPolicyAsync()
    {
        try { await EmergencyPolicyUpdateService.CheckAsync(_update.IsInstalledCopy); }
        catch (Exception ex) { Log.Warn("紧急策略检查未完成：" + ex.Message); }
    }

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
        Interlocked.Increment(ref _statusVersion);
        try { await CheckCoreAsync(); }
        finally
        {
            IsBusy = false;
            Interlocked.Increment(ref _statusVersion);
        }
    }

    private async Task CheckCoreAsync()
    {
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
    }

    [RelayCommand]
    private async Task PrimaryActionAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Interlocked.Increment(ref _statusVersion);
        try
        {
            var hadUpdate = UpdateAvailable && _manifest is not null;

            // Refresh before confirmation so an old notification does not install a superseded release.
            await CheckCoreAsync();
            if (!hadUpdate || !UpdateAvailable || _manifest is null) return;
            var manifest = _manifest;
            var sizeHint = manifest.Installers is { Count: > 0 } installers && installers[0].SizeBytes > 0
                ? $"（约 {FormatBytes(installers[0].SizeBytes)}）"
                : "";

            var confirmedSelection = AppSettingsStore.Read();
            var confirmedMode = UpdateModePolicy.Normalize(confirmedSelection.UpdateMode);
            var confirmationVersion = _confirmationVersion;
            var confirm = _confirmHook;
            var confirmed = confirm is not null &&
                            await confirm(
                                "下载并安装更新",
                                $"将更新到 {AvailableVersionText}。" + (confirmedMode == UpdateMode.FullInstaller
                                    ? $"通过所选服务器下载完整安装包{sizeHint}。" : $"优先下载并替换变化文件；失败时自动从所选服务器下载完整安装包{sizeHint}并更新。") + "\n\n" +
                                "安装前本工具会退出，安装完成后请从快捷方式重新打开；设置、备份与日志保存在 %APPDATA%，不会被删除。\n" +
                                "更新期间帧格 / CPU 调度等后台监控会暂停，请等待更新完成。",
                                "下载并安装");

            if (!confirmed || confirmationVersion != _confirmationVersion)
            {
                Log.Info("更新：用户在确认框取消");
                return;
            }
            if (!UpdateModePolicy.SameSelection(confirmedSelection, AppSettingsStore.Read()))
            { StatusText = "更新方式、服务器或通道已变化，请重新确认。"; return; }

            await RunConfirmedUpdateAsync(manifest);
        }
        catch (Exception ex)
        {
            Log.Error("更新：确认更新失败", ex);
            StatusText = "无法确认更新：" + ex.Message;
            Severity = InfoBarSeverity.Error;
        }
        finally
        {
            IsBusy = false;
            Interlocked.Increment(ref _statusVersion);
        }
    }

    public Task ResumeFullFallbackAsync(string session, string nonce) => IsBusy ? Task.CompletedTask
        : RunConfirmedUpdateAsync(null, session, nonce);

    private async Task RunConfirmedUpdateAsync(UpdateManifest? manifest, string? resumeSession = null, string? nonce = null)
    {
        IsBusy = true;
        Interlocked.Increment(ref _statusVersion);
        using var cts = new CancellationTokenSource();
        _downloadCts = cts;
        var token = cts.Token;

        try
        {
            InfoBarOpen = true;
            Severity = InfoBarSeverity.Informational;
            StatusText = resumeSession is not null ? "文件替换失败，正在核对回滚结果并回退全量更新…" : "正在准备更新…";
            IsDownloading = true;
            ShowProgress = true;
            ProgressPercent = 0;
            ProgressText = "";

            if (resumeSession is not null)
                manifest = await _update.ResumeFullFallbackAsync(resumeSession, nonce!, token);
            if (manifest is null) throw new InvalidOperationException("缺少更新版本信息。");
            var selection = AppSettingsStore.Read();
            var progress = CreateDownloadProgress(cts, token);
            var download = resumeSession is null
                ? await _update.DownloadAsync(manifest, progress, token)
                : await _update.DownloadFullFallbackAsync(manifest, progress, token);
            IsDownloading = false;
            token.ThrowIfCancellationRequested();

            if (!download.Success)
            {
                StatusText = download.Message;
                Severity = InfoBarSeverity.Error;
                Log.Warn("更新：下载失败 —— " + download.Message);
                return;
            }

            ActionButtonText = "重新尝试安装";
            StatusText = download.Message + (download.IsIncremental ? " 正在启动独立更新器…" : " 正在启动安装程序…");

            token.ThrowIfCancellationRequested();
            var launch = await _update.LaunchInstallerAsync(manifest, download.InstallerPath);
            if (!launch.Success && download.IsIncremental && launch.CanFallbackToFull
                && UpdateModePolicy.SameSelection(selection, AppSettingsStore.Read()))
            {
                token.ThrowIfCancellationRequested();
                StatusText = launch.Message;
                IsDownloading = true;
                ProgressPercent = 0;
                ProgressText = "正在回退全量更新…";
                download = await _update.DownloadFullFallbackAsync(manifest, CreateDownloadProgress(cts, token), token);
                IsDownloading = false;
                token.ThrowIfCancellationRequested();
                if (!download.Success)
                { StatusText = download.Message; Severity = InfoBarSeverity.Error; return; }
                token.ThrowIfCancellationRequested();
                StatusText = download.Message + " 正在启动安装程序…";
                launch = await _update.LaunchInstallerAsync(manifest, download.InstallerPath);
            }
            if (!launch.Success)
            {
                StatusText = launch.Message;
                Severity = InfoBarSeverity.Error;
                return;
            }

            StatusText = launch.Message;
            Severity = InfoBarSeverity.Success;

            // The installer requests cooperative exit. A timed UI exit must not interrupt its recovery.
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
            _downloadCts = null;
            Interlocked.Increment(ref _statusVersion);
        }
    }

    [RelayCommand]
    private void CancelDownload()
    {
        if (IsDownloading && _downloadCts is { IsCancellationRequested: false } cts)
        {
            StatusText = "正在取消下载…";
            cts.Cancel();
        }
    }

    [RelayCommand]
    private void SkipVersion()
    {
        if (IsBusy || _manifest is not { } manifest)
        {
            return;
        }

        Log.Info("更新：用户本次暂缓版本 " + manifest.Version + "，下次启动仍会检查并提醒。");

        UpdateAvailable = false;
        StatusText = $"本次暂缓 {AvailableVersionText}；下次启动或手动检查仍会提醒。";
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

    partial void OnUpdateChannelIndexChanged(int value)
    {
        if (value is not (0 or 1)) return;
        var channel = value == 0 ? UpdateChannel.Internal : UpdateChannel.Public;
        AppSettingsStore.Update(s => s.UpdateChannel = channel);
        OnPropertyChanged(nameof(EmergencyPolicyStatus));
        _ = RefreshEmergencyPolicyAsync();
        _update.ResetCheckResult();
        RenderLastResult();
        StatusText = "已切换到" + UpdateChannelPolicy.Name(channel) + "，请重新检查更新。";
        Severity = InfoBarSeverity.Informational;
        Log.Info("更新：更新通道 = " + channel);
    }

    partial void OnUpdateServerIndexChanged(int value)
    {
        if (value is < 0 or > 2) return;
        var server = (UpdateServer)value;
        AppSettingsStore.Update(s => s.UpdateServer = server);
        _update.ResetCheckResult();
        RenderLastResult();
        StatusText = "更新服务器已切换为" + UpdateServerPolicy.Name(server) + "，请重新检查更新。";
        Severity = InfoBarSeverity.Informational;
        Log.Info("更新：更新服务器 = " + server);
    }

    partial void OnUpdateModeIndexChanged(int value)
    {
        if (value is not (0 or 1)) return;
        AppSettingsStore.Update(s => s.UpdateMode = (UpdateMode)value);
        _update.ResetCheckResult();
        RenderLastResult();
        StatusText = "更新方式已切换为" + UpdateModePolicy.Name((UpdateMode)value) + "，请重新检查更新。";
        Severity = InfoBarSeverity.Informational;
    }

    private void OnProgress(UpdateProgressInfo info)
    {
        ProgressPercent = info.TotalBytes > 0 ? info.Fraction * 100 : 0;
        ProgressText = info.TotalBytes > 0
            ? $"{FormatBytes(info.BytesReceived)} / {FormatBytes(info.TotalBytes)}（{info.Fraction:P0}）· {FormatBytes((long)info.BytesPerSecond)}/s"
            : $"{FormatBytes(info.BytesReceived)} · {FormatBytes((long)info.BytesPerSecond)}/s";
        if (info.Stage.Length > 0) StatusText = info.Stage;
    }

    private IProgress<UpdateProgressInfo> CreateDownloadProgress(CancellationTokenSource cts, CancellationToken token)
    {
        var version = ++_progressVersion;
        return new DispatcherProgress(_dispatcher, info =>
        {
            // 已排队的旧进度不能覆盖取消、完成或新一轮/全量回退的状态。
            if (ReferenceEquals(_downloadCts, cts) && IsDownloading && !token.IsCancellationRequested
                && version == _progressVersion) OnProgress(info);
        });
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
