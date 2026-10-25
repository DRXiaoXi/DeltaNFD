using DeltaNFD.Native;
using DeltaNFD.Services;
using DeltaNFD.Services.Plugins;
using DeltaNFD.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeltaNFD;

public sealed partial class MainWindow : Window
{
    private readonly IAppControlService _appControl = ServiceLocator.AppControl;

    /// <summary>托盘图标（开启「关闭时最小化到托盘」后使用）。</summary>
    private TrayIconWindow? _trayIcon;
    private readonly StartupPresentation _startupPresentation = new();

    internal void PresentOnStartup(bool silent)
    {
        _startupPresentation.Present(silent, () => GetOrCreateTrayIcon()?.Show() == true,
            () =>
            {
                // Initialize XAML without taking foreground focus; hide before yielding the UI turn.
                if (_startupPresentation.IsHidden) AppWindow.Show(activateWindow: false);
                else Activate();
            }, () => AppWindow.Hide());
        Log.Info(_startupPresentation.IsHidden ? "静默启动：已驻留托盘，点击托盘或手动打开可显示窗口。" : "启动：显示主窗口。");
    }

    private bool _closePromptInProgress;
    private bool _exitRequested;

    /// <summary>当前公告版本标识（发布新公告时更新此值，老用户会再收到一次弹窗）。</summary>
    private const string CurrentAnnouncementVersion = "welcome-20261003";

    /// <summary>本次进程是否已检查过公告（Activated 每次激活都会触发，只处理一次）。</summary>
    private bool _announcementChecked;

    private bool _startupInitializationStarted;
    private int _backendAttentionQueued;

    private void OnBackendAttention()
    {
        if (_exitRequested || Interlocked.CompareExchange(ref _backendAttentionQueued, 1, 0) != 0) return;
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            var completedNormally = false;
            try
            {
                // Allow the calling page to present its own error first; that dialog gets the same action.
                await Task.Delay(500);
                if (_exitRequested || Content.XamlRoot is null) return;
                if (Services.Backends.BackendAttention.Snapshot(onlyUnshown: true).Count == 0) return;
                ActivateFromSecondLaunch();
                await BackendDialogExtensions.ShowUnconfirmedAsync(Content.XamlRoot);
                completedNormally = true;
            }
            catch (Exception ex) { Log.Error("未确认后端操作引导未能显示", ex); }
            finally
            {
                Volatile.Write(ref _backendAttentionQueued, 0);
                if (completedNormally && !_exitRequested && !BackendReviewFlow.IsReviewing
                    && Services.Backends.BackendAttention.Snapshot(onlyUnshown: true).Count > 0) OnBackendAttention();
            }
        })) Volatile.Write(ref _backendAttentionQueued, 0);
    }
    private bool _gpuWatchReady;
    private bool _gpuWatchInFlight;
    private IReadOnlyList<GpuSpoofInvalidation> _gpuWatchItems = [];
    private readonly HashSet<(string Path, string Stamp, string Name)> _gpuWatchDismissed = [];

    /// <summary>插件侧栏子入口：插件 ID → 导航项。同名插件按 ID 区分，不合并（规范第 4.1 节）。</summary>
    private readonly Dictionary<string, NavigationViewItem> _pluginNavItems = new(StringComparer.Ordinal);
    /// <summary>重建菜单期间抑制选中变化引发的重复导航。</summary>
    private bool _suppressPluginNavSelection;
    private bool _pluginNavRefreshQueued;

    /// <summary>启动时的游戏目录检查完成后，主页导航才开始自动扫描组件。</summary>
    internal bool StartupGameDirectoryCheckCompleted { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        InitializePageBackendNavigation();
        RuntimeNavigationItem.Visibility = ReleaseFeatures.RuntimeUiEnabled ? Visibility.Visible : Visibility.Collapsed;
        Services.Backends.BackendAttention.Changed += OnBackendAttention;
        Closed += (_, _) => Services.Backends.BackendAttention.Changed -= OnBackendAttention;
        ServiceLocator.GameTarget.Changed += OnGameTargetChanged;
        Closed += (_, _) => ServiceLocator.GameTarget.Changed -= OnGameTargetChanged;
        UpdateGameTargetNavigation();

        // 插件侧栏入口：固定“拓展插件”节点下的原生子菜单，仅按已提交索引生成（规范第 4.1 节）。
        ServiceLocator.Plugins.Changed += OnPluginsChanged;
        Closed += (_, _) => ServiceLocator.Plugins.Changed -= OnPluginsChanged;
        RefreshPluginNavigation();

        Title = "三角帧不掉洲 · 三角洲行动优化工具";
        // 标题栏版本号：唯一来源是程序集 InformationalVersion（csproj 的 <InformationalVersion>，
        // 由 AppVersion 统一读取并截断 SourceLink 的 +提交哈希），不要在 XAML/C# 里写死版本字符串。
        AppVersionText.Text = AppVersion.Text;
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));

        // 标题栏 logo：ms-appx 资源加载失败（如 pri 缺失）时回退到绝对路径文件
        AppLogoImage.ImageFailed += (_, _) =>
        {
            try
            {
                var logoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "DeltaNFD.png");
                AppLogoImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(logoPath));
            }
            catch
            {
                // logo 加载失败不影响主功能
            }
        };

        // 拦截关闭：开启托盘驻留时隐藏窗口而不是退出
        AppWindow.Closing += AppWindow_Closing;

        // 首次启动公告：ContentFrame 挂载完成（XamlRoot 就绪）后弹出，每个公告版本只弹一次
        ContentFrame.Loaded += ContentFrame_Loaded;
        Activated += GpuWatch_Activated;
        Closed += (_, _) => { _gpuWatchReady = false; Activated -= GpuWatch_Activated; };

        if (!OfflineModeGuard.BlocksNormalAutomation && AppSettingsStore.Read().CloseToTrayEnabled)
        {
            CreateTrayIcon();
        }

        // A failed update resumes visibly on Settings, without starting game/background automation.
        if (App.PendingFileFallback is not null) NavigateByTag("settings");
        else NavView.SelectedItem = NavView.MenuItems[0];

        // 背景图：启动应用一次，并监听设置页的实时变更
        ApplyBackgroundImage();
        BackgroundManager.Changed += ApplyBackgroundImage;

        // 发现新版本时在「设置」导航项上挂提示徽标（服务可能在后台线程触发）
        ServiceLocator.Update.AvailabilityChanged += OnUpdateAvailabilityChanged;
    }

    private void OnGameTargetChanged(GameTarget target) => DispatcherQueue.TryEnqueue(UpdateGameTargetNavigation);
    private void UpdateGameTargetNavigation()
    {
        UpdatePageAvailability();
    }

    // ---------------------------------------------------------------- 插件侧栏入口

    /// <summary>管理服务报告插件集合已变更（可能来自后台线程）：合并到一次 UI 线程刷新。</summary>
    private void OnPluginsChanged()
    {
        if (_pluginNavRefreshQueued) return;
        _pluginNavRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _pluginNavRefreshQueued = false;
                RefreshPluginNavigation();
            }))
        {
            _pluginNavRefreshQueued = false;
        }
    }

    /// <summary>
    /// 重建“拓展插件”下的子入口。只读取索引与包内导航声明，不启动/重连/调用后端；
    /// 索引读取失败时保留现有菜单（不误删），单包损坏由目录层跳过（无幽灵入口）。
    /// </summary>
    internal void RefreshPluginNavigation()
    {
        var entries = PluginNavigationCatalog.Build(ServiceLocator.Plugins, AppVersion.Current, out var error);
        if (error.Length > 0)
        {
            Services.Log.Warn("插件入口：读取插件索引失败，保留现有菜单：" + error);
            return;
        }

        var parent = FindPluginsNavItem();
        if (parent is null) return;

        var previouslySelected = _pluginNavItems.Keys.FirstOrDefault(id =>
            ReferenceEquals(NavView.SelectedItem, _pluginNavItems[id]));
        var staleEntryPage = false;

        _suppressPluginNavSelection = true;
        try
        {
            foreach (var item in _pluginNavItems.Values) parent.MenuItems.Remove(item);
            _pluginNavItems.Clear();

            foreach (var entry in entries)
            {
                var item = new NavigationViewItem
                {
                    // 路由身份使用插件 ID；显示名仅作展示，长名称由原生模板截断。
                    Tag = PluginNavigationCatalog.TagFor(entry.PluginId),
                    Content = entry.Label,
                };
                item.Icon = new FontIcon { Glyph = NavigationIconGlyph(entry.Icon) };
                ToolTipService.SetToolTip(item, $"{entry.Label}\n{entry.Author} · {entry.PluginId}\n状态：{entry.StatusText}");
                AutomationProperties.SetName(item, $"{entry.Label}（{entry.PluginId}）");
                parent.MenuItems.Add(item);
                _pluginNavItems[entry.PluginId] = item;
            }

            parent.IsExpanded = true;

            // 当前正显示的独立入口若已被卸载或身份（版本/哈希）变化，退出该页回管理页，
            // 不复用旧授权或旧表单（规范第 4.1 节、B4）。
            staleEntryPage = ContentFrame.Content is PluginEntryPage entryPage && entryPage.IsStale();
        }
        finally
        {
            _suppressPluginNavSelection = false;
        }

        // 重建期间抑制选中变化；结束后再恢复选中（会真正导航到该入口，刷新表单），
        // 但若该入口已失效则退回插件管理页，不误选其他插件。
        if (previouslySelected is not null && !staleEntryPage)
        {
            if (_pluginNavItems.TryGetValue(previouslySelected, out var restored))
                NavView.SelectedItem = restored;
            else
                NavigateByTag("plugins");
        }
        else if (staleEntryPage)
        {
            NavigateByTag("plugins");
        }
    }

    private NavigationViewItem? FindPluginsNavItem() =>
        NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag?.ToString() == "plugins");

    /// <summary>navigation.icon 固定枚举 → 宿主内置 Segoe MDL2 字形；不接受路径/URL/字体名。</summary>
    private static string NavigationIconGlyph(string icon) => icon switch
    {
        "report" => "\uE8A5",
        "settings" => "\uE713",
        "activity" => "\uE9D9",
        "tools" => "\uE90F",
        _ => "\uEA86", // puzzle
    };

    private async void ContentFrame_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startupInitializationStarted)
        {
            return;
        }

        _startupInitializationStarted = true;
        if (App.PendingFileFallback is { } fallback)
        {
            _announcementChecked = true;
            if (ContentFrame.Content is SettingsPage settings)
                await settings.Update.ResumeFullFallbackAsync(fallback.Session, fallback.Nonce);
            return;
        }
        Services.Backends.BackendAttention.RefreshPersisted();

        var retiredRecovery = await OfflineModeRetirement.RecoverAsync();
        if (!retiredRecovery.Success)
        {
            RestoreFromTray();
            NavigateByTag("settings");
            Log.Warn("旧版恢复待处理：" + retiredRecovery.Message);
        }

        try
        {
            await EnsureGameDirectoryOnStartupAsync();
        }
        catch (Exception ex)
        {
            Services.Log.Error("启动：检查/选择三角洲游戏目录失败", ex);
        }
        finally
        {
            StartupGameDirectoryCheckCompleted = true;
        }
        if (retiredRecovery.Success) _ = Services.Backends.BackendProcmanService.Instance.ActivateSavedRulesAsync();

        // 启动时主页可能在目录提示之前已创建；路径设置完成后立即使用真实游戏目录重扫。
        if (ContentFrame.Content is DashboardPage dashboard)
        {
            _ = dashboard.ViewModel.RefreshHealthAsync();
        }

        if (_announcementChecked)
        {
            return;
        }

        _announcementChecked = true;
        try
        {
            if (AppSettingsStore.Read().AnnouncementVersionSeen == CurrentAnnouncementVersion)
            {
                return;
            }

            var qr = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "FeedbackGroupQr.png"))),
                MaxWidth = 360, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(qr, "反馈群二维码，群号 1126191382");
            qr.ImageFailed += (_, _) => Log.Warn("公告：反馈群二维码加载失败，可使用群号 1126191382 加群。");
            var content = new StackPanel { Spacing = 16 };
            content.Children.Add(new TextBlock
            {
                Text = "你好，我是逐梦之子-晓夕，这个软件是永久免费的，而且会尽我所能的进行更新，只是为了让各位低配电脑玩家能更流畅地体验游戏而不是被游戏本身的各种卡顿所烦恼，在开发过程中，有很多粉丝对我的开发提供了极大的支持，对此我感激不尽，我想尽我所能回报我的粉丝，这个工具包含了我对三角洲优化的绝大多数理解，希望各位喜欢，如果对软件有疑惑，可以扫描图示二维码加入本群进行反馈，我会尝试进行修复",
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
            });
            content.Children.Add(qr);
            content.Children.Add(new TextBlock
            {
                Text = "反馈群号：1126191382", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            var dialog = new ContentDialog
            {
                XamlRoot = ContentFrame.XamlRoot,
                Title = "公告",
                Content = new ScrollViewer
                {
                    MaxHeight = Math.Max(180, Math.Min(560, ContentFrame.XamlRoot.Size.Height - 180)),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = content,
                },
                CloseButtonText = "开始使用",
                DefaultButton = ContentDialogButton.Close,
            };
            await _startupPresentation.WaitForVisibleAsync();
            await dialog.ShowWithBackendRecoveryAsync();

            // 展示成功后才记录：展示失败（如异常）时下次启动重试
            AppSettingsStore.Update(s => s.AnnouncementVersionSeen = CurrentAnnouncementVersion);
            Services.Log.Info("公告：首启公告已展示并记录（" + CurrentAnnouncementVersion + "）");
        }
        catch (Exception ex)
        {
            // 公告失败不影响启动
            Services.Log.Error("公告：首启公告展示失败", ex);
        }
        finally
        {
            // 更新相关对话框必须排在公告之后（同一时刻只能有一个 ContentDialog）
            await TryHandleUpdateOnStartupAsync();

            // 内存限制警告排在最后：检测到 bcdedit removememory 时给出去除入口。
            await CheckMemoryLimitOnStartupAsync();
        }
    }

    /// <summary>
    /// 启动时检测 12GB 内存限制（bcdedit removememory）并弹窗警告。
    /// 机制保留在后端（DetectMemoryLimitAsync/RemoveMemoryLimitAsync），仅把入口从 CPU 实验室移到启动提示。
    /// 检测/移除失败只提示，不阻塞启动。
    /// </summary>
    private async Task CheckMemoryLimitOnStartupAsync()
    {
        try
        {
            var (detected, removedMb, _) = await ServiceLocator.Cpu.DetectMemoryLimitAsync();
            if (!detected) return;
            await _startupPresentation.WaitForVisibleAsync();

            var warning = new ContentDialog
            {
                XamlRoot = ContentFrame.XamlRoot,
                Title = "检测到 12GB 内存限制",
                Content = $"检测到 bcdedit removememory = {removedMb} MB（可用内存被限制为约 12 GB）。\n\n"
                          + "其他「优化工具」常用这个手段限制可用内存，损失巨大。本工具的显卡伪装方案不需要限制内存。\n\n"
                          + "是否现在移除该限制？将执行 bcdedit /deletevalue removememory，重启电脑后恢复全部可用内存。",
                PrimaryButtonText = "移除限制",
                CloseButtonText = "暂不处理",
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await warning.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary) return;

            var result = await ServiceLocator.Cpu.RemoveMemoryLimitAsync();
            if (result is null) return;
            if (!result.Success)
            {
                await new ContentDialog
                {
                    XamlRoot = ContentFrame.XamlRoot,
                    Title = "移除失败",
                    Content = result.Message,
                    CloseButtonText = "知道了",
                    DefaultButton = ContentDialogButton.Close,
                }.ShowWithBackendRecoveryAsync();
                return;
            }

            if (result.RequiresReboot)
            {
                var reboot = await new ContentDialog
                {
                    XamlRoot = ContentFrame.XamlRoot,
                    Title = "内存限制已移除",
                    Content = result.Message + "\n\n是否立即重启电脑？",
                    PrimaryButtonText = "立即重启",
                    CloseButtonText = "稍后自己重启",
                    DefaultButton = ContentDialogButton.Primary,
                }.ShowWithBackendRecoveryAsync();
                if (reboot == ContentDialogResult.Primary) ServiceLocator.Frame.RebootNow();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("启动内存限制检测失败（不阻塞启动）：" + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 自动更新

    /// <summary>
    /// 启动时的更新流程：先核对「上次登记的更新是否生效」，再按设置自动检查新版本。
    /// 全程只提醒，下载与安装都由用户在设置页确认（见 HANDOFF §32）。
    /// </summary>
    private async Task TryHandleUpdateOnStartupAsync()
    {
        try
        {
            await ReportPendingUpdateResultAsync();
            await CheckUpdatesOnStartupAsync();
        }
        catch (Exception ex)
        {
            Services.Log.Error("更新：启动检查流程失败", ex);
        }
        finally
        {
            _gpuWatchReady = true;
            await CheckGpuSpoofWatchAsync();
        }
    }

    private async void GpuWatch_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_gpuWatchReady && args.WindowActivationState != WindowActivationState.Deactivated)
            await CheckGpuSpoofWatchAsync();
    }

    private async Task CheckGpuSpoofWatchAsync()
    {
        if (_gpuWatchInFlight || !_gpuWatchReady || Services.Backends.PageBackendSessions.Manager.DeltaRunning != false) return;
        _gpuWatchInFlight = true;
        try
        {
            var scanned = await ServiceLocator.GpuSpoof.CheckInvalidationAsync();
            var items = scanned.Where(x => !_gpuWatchDismissed.Contains((x.Entry.RegistryPath, x.CurrentDriverStamp, x.Entry.ExpectedName))).ToList();
            if (!_gpuWatchReady || Services.Backends.PageBackendSessions.Manager.DeltaRunning != false) return;
            _gpuWatchItems = items;
            GpuSpoofWarning.IsOpen = items.Count > 0;
            UpdateGpuWarningVisibility();
            if (items.Count > 0)
            {
                GpuSpoofWarning.Message = string.Join("；", items.Select(x => "原伪装：" + x.Entry.ExpectedName)) +
                    "。建议重新应用伪装，再清理旧着色器缓存并让游戏重新生成；操作需手动确认。";
                ShaderCacheButton.IsEnabled = !ServiceLocator.GameTarget.IsCustom && CanEnterGamePage("shader");
            }
        }
        catch (Exception ex) { Log.Error("显卡伪装失效检查失败，未判断为正常", ex); }
        finally { _gpuWatchInFlight = false; }
    }

    private void OpenGpuSpoof_Click(object sender, RoutedEventArgs e) => NavigateByTag("spoof");
    private void UpdateGpuWarningVisibility() => GpuSpoofWarning.Visibility =
        GpuSpoofWarning.IsOpen && ContentFrame.Content is DashboardPage ? Visibility.Visible : Visibility.Collapsed;
    private void OpenShaderCache_Click(object sender, RoutedEventArgs e) => NavigateByTag("shader");
    private void GpuSpoofWarning_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        sender.Visibility = Visibility.Collapsed;
        if (args.Reason != InfoBarCloseReason.CloseButton) return;
        foreach (var item in _gpuWatchItems)
            _gpuWatchDismissed.Add((item.Entry.RegistryPath, item.CurrentDriverStamp, item.Entry.ExpectedName));
    }

    /// <summary>上次退出时登记过待安装版本 → 这次启动就能判断更新是否成功。</summary>
    private async Task ReportPendingUpdateResultAsync()
    {
        var settings = AppSettingsStore.Read();
        if (string.IsNullOrWhiteSpace(settings.PendingUpdateVersion))
        {
            return;
        }

        var pending = settings.PendingUpdateVersion;
        var display = string.IsNullOrWhiteSpace(settings.PendingUpdateDisplayVersion)
            ? pending
            : settings.PendingUpdateDisplayVersion;
        var current = ServiceLocator.Update.CurrentVersionText;
        var applied = UpdateService.TryParseVersion(pending, out var pendingVersion) &&
                      UpdateService.CompareVersions(ServiceLocator.Update.CurrentVersion, pendingVersion) >= 0;

        Services.Log.Info($"更新：上次登记待安装 {pending}，当前 {current}，判定 {(applied ? "已生效" : "未生效")}");

        var dialog = new ContentDialog
        {
            XamlRoot = ContentFrame.XamlRoot,
            Title = applied ? "更新完成" : "更新似乎未生效",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = applied
                    ? $"已更新到 {current}。本次更新保留了你的设置、备份与日志。"
                    : $"计划更新到 {display}，但当前仍是 {current}。安装可能被取消，或被安全软件拦截。\n\n" +
                      "可以在「设置 → 软件更新」重试，或打开下载文件夹查看残留的安装包与安装日志。" +
                      "当前版本仍可正常使用。",
            },
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        };

        try
        {
            await _startupPresentation.WaitForVisibleAsync();
            await dialog.ShowWithBackendRecoveryAsync();
        }
        catch (Exception ex)
        {
            Services.Log.Error("更新：更新结果提示展示失败", ex);
        }

        // 无论成败都清掉登记与安装包，避免每次启动重复提示
        ServiceLocator.Update.CleanupPendingInstaller();
    }

    /// <summary>启动时的自动检查（只提醒，不下载）。</summary>
    private async Task CheckUpdatesOnStartupAsync()
    {
        EmergencyPolicyUpdateService.Start(ServiceLocator.Update.IsInstalledCopy);
        if (!AppSettingsStore.Read().AutoUpdateCheckEnabled)
        {
            Services.Log.Info("更新：整包启动自动检查已关闭；紧急策略按独立开关处理。");
            return;
        }

        var result = await ServiceLocator.Update.CheckAsync(manual: false);
        Services.Log.Info("更新：启动自动检查结果 → " + result.Status + "（" + result.Message + "）");
        if (result.Status is UpdateCheckStatus.Throttled or UpdateCheckStatus.Disabled)
        {
            return;
        }

        if (!result.HasUpdate)
        {
            ApplyUpdateBadge(false);
            return;
        }

        ApplyUpdateBadge(true);

        var notes = string.IsNullOrWhiteSpace(result.Notes)
            ? "发布页未提供更新说明。"
            : result.Notes.Trim();

        var dialog = new ContentDialog
        {
            XamlRoot = ContentFrame.XamlRoot,
            Title = (result.Mandatory ? "必须更新：" : "发现新版本：") + result.AvailableVersionText,
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"当前版本 {result.CurrentVersionText}。\n\n{notes}",
                },
            },
            PrimaryButtonText = "去设置页更新",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
        };

        await _startupPresentation.WaitForVisibleAsync();
        var choice = await dialog.ShowWithBackendRecoveryAsync();
        if (choice == ContentDialogResult.Primary)
        {
            NavigateByTag("settings");
            return;
        }

    }

    private void OnUpdateAvailabilityChanged(bool available)
    {
        if (!DispatcherQueue.TryEnqueue(() => ApplyUpdateBadge(available)))
        {
            Services.Log.Warn("更新：窗口 Dispatcher 已停止，无法刷新导航徽标");
        }
    }

    private void ApplyUpdateBadge(bool available)
    {
        try
        {
            SettingsNavItem.InfoBadge = available ? new InfoBadge { Value = 1 } : null;
        }
        catch (Exception ex)
        {
            Services.Log.Warn("更新：刷新导航徽标失败：" + ex.Message);
        }
    }

    private async Task EnsureGameDirectoryOnStartupAsync()
    {
        if (ServiceLocator.GameTarget.IsCustom || DeltaForceLocator.FindRoots().Any())
        {
            return;
        }

        await _startupPresentation.WaitForVisibleAsync();
        var prompt = new ContentDialog
        {
            XamlRoot = ContentFrame.XamlRoot,
            Title = "未检测到三角洲游戏目录",
            Content = "请定位三角洲行动的游戏安装目录。保存后，着色器、ACE 清理、运行库保护等功能会共用这个目录。\n\n"
                      + "· 全盘搜索：在所有固定盘递归查找（可能数十秒）\n"
                      + "· 指定目录搜索：先选一个目录（如 D:\\Games），再在该目录内查找\n"
                      + "· 手动选择目录：直接选安装目录",
            PrimaryButtonText = "全盘搜索",
            SecondaryButtonText = "指定目录搜索",
            CloseButtonText = "手动选择目录",
            DefaultButton = ContentDialogButton.Primary,
        };

        var choice = await prompt.ShowWithBackendRecoveryAsync();
        if (choice == ContentDialogResult.Primary)
        {
            await RunGameDirectorySearchAsync(null);
        }
        else if (choice == ContentDialogResult.Secondary)
        {
            var owner = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var root = FolderPickerDialog.Pick(owner, null);
            if (!string.IsNullOrWhiteSpace(root))
            {
                await RunGameDirectorySearchAsync(root);
            }
            else
            {
                await RunManualGameDirectoryPickAsync();
            }
        }
        else if (choice == ContentDialogResult.None)
        {
            // 稍后设置
            Services.Log.Info("启动：用户选择稍后设置三角洲游戏目录");
        }
        else
        {
            await RunManualGameDirectoryPickAsync();
        }
    }

    /// <summary>在全盘或指定子树搜索三角洲安装目录，命中后让用户确认并登记。</summary>
    private async Task RunGameDirectorySearchAsync(string? scopedRoot)
    {
        var cts = new CancellationTokenSource();
        var statusText = new TextBlock { Text = "准备搜索…", TextWrapping = TextWrapping.Wrap };
        var progressDialog = new ContentDialog
        {
            XamlRoot = ContentFrame.XamlRoot,
            Title = "正在搜索三角洲安装目录",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new ProgressBar { IsIndeterminate = true },
                    statusText,
                },
            },
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        progressDialog.CloseButtonClick += (_, _) => cts.Cancel();

        var results = new List<string>();
        var progress = new Progress<string>(p => statusText.Text = p);
        var searchTask = Task.Run(async () =>
        {
            var source = scopedRoot is null
                ? DeltaForceSearcher.SearchAllFixedDrivesAsync(progress, cts.Token)
                : DeltaForceSearcher.SearchDirectoryAsync(scopedRoot, progress, cts.Token);
            await foreach (var hit in source.WithCancellation(cts.Token))
            {
                results.Add(hit);
            }
        });

        _ = progressDialog.ShowWithBackendRecoveryAsync();
        try { await searchTask; }
        catch (OperationCanceledException) { }
        progressDialog.Hide();

        if (cts.IsCancellationRequested)
        {
            Log.Info("启动：用户取消了三角洲目录搜索");
            return;
        }

        if (results.Count == 0)
        {
            var none = await new ContentDialog
            {
                XamlRoot = ContentFrame.XamlRoot,
                Title = "未找到三角洲安装目录",
                Content = "搜索完成，但未找到三角洲游戏文件。是否改为手动选择目录？",
                PrimaryButtonText = "手动选择",
                CloseButtonText = "稍后设置",
                DefaultButton = ContentDialogButton.Primary,
            }.ShowWithBackendRecoveryAsync();
            if (none == ContentDialogResult.Primary) await RunManualGameDirectoryPickAsync();
            return;
        }

        await ConfirmAndRegisterGameRootsAsync(results, ContentFrame.XamlRoot);
    }

    /// <summary>手动选择目录并登记（带重试）。</summary>
    private async Task RunManualGameDirectoryPickAsync()
    {
        while (true)
        {
            string? selectedPath;
            try
            {
                var owner = WinRT.Interop.WindowNative.GetWindowHandle(this);
                selectedPath = FolderPickerDialog.Pick(owner, AppSettingsStore.Read().GameRootOverride);
            }
            catch (Exception ex)
            {
                Services.Log.Error("启动：游戏目录文件夹选择器失败", ex);
                await new ContentDialog
                {
                    XamlRoot = ContentFrame.XamlRoot,
                    Title = "无法打开目录选择器",
                    Content = "请稍后到「设置」页，在三角洲游戏目录一栏手动选择或粘贴游戏路径。",
                    CloseButtonText = "知道了",
                    DefaultButton = ContentDialogButton.Close,
                }.ShowWithBackendRecoveryAsync();
                return;
            }

            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            if (DeltaForceLocator.TryGetValidGameRoot(selectedPath, out var gameRoot))
            {
                AppSettingsStore.Update(s => s.GameRootOverride = gameRoot);
                Services.Log.Info("启动：已设置三角洲游戏目录 " + gameRoot);
                return;
            }

            var retry = new ContentDialog
            {
                XamlRoot = ContentFrame.XamlRoot,
                Title = "这不是有效的三角洲游戏目录",
                Content = "所选目录中没有找到三角洲游戏文件。请选择包含三角洲游戏程序或 Content/Paks 的安装目录。",
                PrimaryButtonText = "重新选择",
                CloseButtonText = "稍后设置",
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await retry.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }
    }

    /// <summary>让用户确认搜索到的安装目录并登记到持久化列表。</summary>
    internal static async Task ConfirmAndRegisterGameRootsAsync(IReadOnlyList<string> roots, XamlRoot xamlRoot)
    {
        if (roots.Count == 0) return;
        var panel = new StackPanel { Spacing = 8 };
        var boxes = new List<(CheckBox Box, string Root)>();
        foreach (var root in roots)
        {
            var box = new CheckBox { Content = root, IsChecked = true };
            boxes.Add((box, root));
            panel.Children.Add(box);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "找到三角洲安装目录",
            Content = new ScrollViewer { MaxHeight = 360, Content = panel },
            PrimaryButtonText = "登记所选目录",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary) return;

        var selected = boxes.Where(t => t.Box.IsChecked == true).Select(t => t.Root).ToList();
        foreach (var root in selected)
        {
            DeltaForceLocator.AddRegisteredRoot(root);
        }

        if (selected.Count > 0)
        {
            Log.Info($"已登记 {selected.Count} 个三角洲安装目录");
        }
    }

    /// <summary>
    /// 应用背景：按 BackgroundManager 解析（自定义 > 内置 Assets\background.*）显示图片，
    /// 并按设置里的遮罩浓度叠暗色遮罩保证可读；无背景时维持 Mica。
    /// </summary>
    private void ApplyBackgroundImage()
    {
        try
        {
            var path = BackgroundManager.ResolveCurrent();
            if (path is null)
            {
                BgImage.Visibility = Visibility.Collapsed;
                BgDim.Visibility = Visibility.Collapsed;
                return;
            }

            BgImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
            BgImage.Visibility = Visibility.Visible;

            var alpha = (byte)Math.Round(255 * BackgroundManager.DimOpacity);
            BgDim.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, 0, 0, 0));
            BgDim.Visibility = alpha == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch
        {
            // 背景加载失败不影响主功能（保持 Mica）
            BgImage.Visibility = Visibility.Collapsed;
            BgDim.Visibility = Visibility.Collapsed;
        }
    }

    private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
        {
            return;
        }
        if (_pluginExitInProgress || _deploymentInProgress)
        {
            args.Cancel = true;
            if (_pluginExitInProgress) RestoreFromTray();
            return;
        }
        if (_factoryResetInProgress)
        {
            args.Cancel = true;
            return;
        }

        if (OfflineModeGuard.BlocksNormalAutomation)
        {
            args.Cancel = true;
            DispatcherQueue.TryEnqueue(ExitApplication);
            return;
        }

        var settings = AppSettingsStore.Read();
        if (settings.CloseToTrayEnabled)
        {
            // 已启用托盘驻留时，关闭窗口直接隐藏；注册失败则放行关闭，避免用户无法退出。
            var tray = GetOrCreateTrayIcon();
            if (tray is null || !tray.Show())
            {
                Log.Error("托盘：图标注册失败，本次关闭将直接退出应用（驻留托盘不可用）");
                args.Cancel = true;
                DispatcherQueue.TryEnqueue(ExitApplication);
                return;
            }

            args.Cancel = true;
            DispatcherQueue.TryEnqueue(HideToTray);
            return;
        }

        if (settings.SuppressBackgroundRunPrompt || !HasBackgroundWorkToPreserve(settings))
        {
            args.Cancel = true;
            DispatcherQueue.TryEnqueue(ExitApplication);
            return;
        }

        // 先同步取消本次关闭，再异步显示对话框；重复点击关闭不会叠出多个对话框。
        args.Cancel = true;
        if (_closePromptInProgress)
        {
            return;
        }

        _closePromptInProgress = true;
        try
        {
            var rememberChoice = new CheckBox { Content = "下次不再询问" };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = "帧格或 CPU 调度功能仍在运行。退出工具后，相关后台监控和自动调度会停止。是否开启关闭后最小化到托盘，让这些功能继续工作？",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(rememberChoice);

            var dialog = new ContentDialog
            {
                XamlRoot = ContentFrame.XamlRoot,
                Title = "仍有后台功能正在运行",
                Content = content,
                PrimaryButtonText = "开启后台运行",
                SecondaryButtonText = "仍然退出",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            var choice = await dialog.ShowWithBackendRecoveryAsync();
            if (choice == ContentDialogResult.Primary)
            {
                var tray = GetOrCreateTrayIcon();
                if (tray is null || !tray.Show())
                {
                    Log.Error("托盘：用户选择开启后台运行，但图标注册失败");
                    await new ContentDialog
                    {
                        XamlRoot = ContentFrame.XamlRoot,
                        Title = "无法开启后台运行",
                        Content = "托盘图标注册失败，工具将保持打开。请稍后重试。",
                        CloseButtonText = "知道了",
                        DefaultButton = ContentDialogButton.Close,
                    }.ShowWithBackendRecoveryAsync();
                    return;
                }

                _appControl.SetCloseToTray(true);
                if (rememberChoice.IsChecked == true)
                {
                    AppSettingsStore.Update(s => s.SuppressBackgroundRunPrompt = true);
                }

                Log.Info("关闭提示：用户选择开启后台驻留" + (rememberChoice.IsChecked == true ? "并记住选择" : ""));
                AppWindow.Hide();
            }
            else if (choice == ContentDialogResult.Secondary)
            {
                if (rememberChoice.IsChecked == true)
                {
                    AppSettingsStore.Update(s => s.SuppressBackgroundRunPrompt = true);
                }

                Log.Info("关闭提示：用户选择退出" + (rememberChoice.IsChecked == true ? "并不再询问" : ""));
                ExitApplication();
            }
        }
        catch (Exception ex)
        {
            Log.Error("关闭提示：无法显示后台运行确认对话框", ex);
        }
        finally
        {
            _closePromptInProgress = false;
        }
    }

    private static bool HasBackgroundWorkToPreserve(AppSettings settings)
    {
        var frameActive = settings.FrameModeActive || ServiceLocator.Frame.FrameModeActive;
        var frameMonitoringActive = frameActive || settings.DwmRestartOnGameStart;
        var cpuSchedulingActive =
            (settings.GameAffinityRuleEnabled && settings.GameAffinityRuleMask != 0)
            || settings.SingleCcdExcludeCpu0Enabled
            || settings.DualCcdImmediateEnabled
            || CpuFrameStatusState.SingleCcdExcludeActive
            || CpuFrameStatusState.DualCcdSchedulingActive
            || CpuFrameStatusState.SingleCcdExcludeActive
            || CpuFrameStatusState.DualCcdSchedulingActive;

        return frameMonitoringActive || cpuSchedulingActive;
    }

    private void HideToTray()
    {
        if (_trayIcon is null || !_trayIcon.Show())
        {
            Log.Error("托盘：图标注册失败，无法驻留托盘");
            return;
        }

        AppWindow.Hide();
    }

    private void RestoreFromTray()
    {
        _trayIcon?.Hide();
        AppWindow.Show();
        Activate();
        _startupPresentation.Reveal();
    }

    /// <summary>快捷方式再次启动时，恢复托盘中的主窗口并将其激活。</summary>
    public void ActivateFromSecondLaunch() => RestoreFromTray();

    private bool _deploymentInProgress, _factoryResetInProgress;
    internal Task<DeploymentHandover> PrepareDeploymentAsync(bool uninstall) => PrepareDeploymentAsync(uninstall, null);

    internal async Task<DeploymentHandover> PrepareDeploymentAsync(bool uninstall, string? purpose)
    {
        if (_pluginExitInProgress || _deploymentInProgress || _factoryResetInProgress) throw new InvalidOperationException("正在处理退出或配置重置，请稍后重试安装/卸载。");
        _deploymentInProgress = true;
        if (Content is UIElement root) root.IsHitTestVisible = false;
        try
        {
            var result = await DeploymentHandover.PrepareAsync(uninstall, purpose);
            if (result.Ready) return result;
            _deploymentInProgress = false;
            if (Content is UIElement content) content.IsHitTestVisible = true;
            return result;
        }
        catch
        {
            _deploymentInProgress = false;
            if (Content is UIElement content) content.IsHitTestVisible = true;
            throw;
        }
    }

    internal void CancelDeploymentPreparation()
    {
        _deploymentInProgress = false;
        if (Content is UIElement content) content.IsHitTestVisible = true;
    }

    internal void ExitForDeployment()
    {
        App.ExitAlreadyPrepared = true;
        _exitRequested = true;
        EmergencyPolicyUpdateService.Stop();
        _trayIcon?.Dispose();
        _trayIcon = null;
        Application.Current.Exit();
    }

    internal void SetFactoryResetInteraction(bool enabled)
    {
        _factoryResetInProgress = !enabled;
        NavView.IsEnabled = enabled;
        ContentFrame.IsEnabled = enabled;
        if (Content is UIElement root) root.IsHitTestVisible = enabled;
    }

    /// <summary>托盘菜单「退出」：显示收尾进度，安全排空后退出并移除图标。</summary>
    private bool _pluginExitInProgress;
    private async void ExitApplication()
    {
        if (_pluginExitInProgress || _deploymentInProgress || _factoryResetInProgress) return;
        _pluginExitInProgress = true;

        IDisposable? pluginBlock = null;
        IDisposable? backendBlock = null;
        Microsoft.UI.Dispatching.DispatcherQueueTimer? exitTimer = null;

        try
        {
            // 托盘退出也恢复可见窗口，避免长时间恢复设置时看起来只剩后台进程。
            RestoreFromTray();
            NavView.IsEnabled = false;
            ExitOverlay.Visibility = Visibility.Visible;
            ExitProgressRing.IsActive = true;
            ExitStageText.Text = "正在停止插件…";
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            ExitElapsedText.Text = "已用时 0 秒";
            exitTimer = DispatcherQueue.CreateTimer();
            exitTimer.Interval = TimeSpan.FromSeconds(1);
            exitTimer.Tick += (_, _) => ExitElapsedText.Text = $"已用时 {(int)elapsed.Elapsed.TotalSeconds} 秒";
            exitTimer.Start();
            Log.Info("用户请求退出：显示收尾进度。");
            pluginBlock = ServiceLocator.PluginRuntime.BlockNewOperations();
            backendBlock = Services.Backends.BackendOperationGate.BlockNewOperations();

            var blocker = await PluginLifecycleGuard.PrepareHandoverAsync(ServiceLocator.Plugins, ServiceLocator.PluginRuntime,
                "退出", restoreChanges: false, allowAutonomous: false);
            if (blocker.Length > 0)
            {
                // 优化：排空失败时显式释放阻止，恢复正常操作
                pluginBlock?.Dispose();
                backendBlock?.Dispose();
                pluginBlock = null;
                backendBlock = null;

                HideExitProgress();
                RestoreFromTray();
                await new ContentDialog { XamlRoot = ContentFrame.XamlRoot, Title = "插件尚未安全停止", Content = blocker,
                    CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync();
                return;
            }
            // 前后端分离 T5：退出前也必须排空官方后端；任一模块未确认退出（含在途/未知操作、
            // 未结束的子进程）就阻止退出，不能只按主进程 PID 判断。
            var exitProgress = new Progress<string>(message =>
            {
                if (_pluginExitInProgress && !_exitRequested) ExitStageText.Text = message;
            });
            var backendFailures = await Services.Backends.BackendLifecycle.PrepareHandoverAsync("退出", progress: exitProgress);
            if (backendFailures.Count > 0)
            {
                var backendBlocker = Services.Backends.BackendLifecycle.DescribeBlocker(backendFailures, "退出");
                Services.Log.Warn(backendBlocker);

                // 优化：排空失败时显式释放阻止，恢复正常操作
                pluginBlock?.Dispose();
                backendBlock?.Dispose();
                pluginBlock = null;
                backendBlock = null;

                HideExitProgress();
                RestoreFromTray();
                await new ContentDialog { XamlRoot = ContentFrame.XamlRoot, Title = "退出前需要核对官方后端",
                    Content = new TextBlock { Text = backendBlocker, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync();
                return;
            }
            // 本次已完成可信排空，ProcessExit 兜底不应再次执行恢复流程。
            App.ExitAlreadyPrepared = true;
            _exitRequested = true;
            EmergencyPolicyUpdateService.Stop();
            _trayIcon?.Dispose();
            _trayIcon = null;
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            Log.Error("退出收尾失败，应用保持打开", ex);

            // 优化：异常时确保释放阻止
            pluginBlock?.Dispose();
            backendBlock?.Dispose();
            pluginBlock = null;
            backendBlock = null;
            HideExitProgress();
            RestoreFromTray();
            try
            {
                await new ContentDialog { XamlRoot = ContentFrame.XamlRoot, Title = "暂时无法安全退出",
                    Content = new TextBlock { Text = "收尾发生错误，应用已恢复显示。请重试退出；若仍失败，请提供日志。\n" + ex.Message,
                        TextWrapping = TextWrapping.Wrap }, CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync();
            }
            catch (Exception dialogError) { Log.Warn("无法显示退出失败说明：" + dialogError.Message); }
        }
        finally
        {
            exitTimer?.Stop();
            if (!_exitRequested) HideExitProgress();
            _pluginExitInProgress = false;

            // 优化：兜底释放阻止
            pluginBlock?.Dispose();
            backendBlock?.Dispose();
        }
    }

    private void HideExitProgress()
    {
        ExitProgressRing.IsActive = false;
        ExitOverlay.Visibility = Visibility.Collapsed;
        NavView.IsEnabled = true;
    }

    /// <summary>脱机助手已完成启动握手后，主界面直接退出，不进入托盘驻留。</summary>

    private TrayIconWindow? GetOrCreateTrayIcon()
    {
        if (_trayIcon is not null)
        {
            return _trayIcon;
        }

        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _trayIcon = new TrayIconWindow(
            "三角帧不掉洲 · 点击恢复窗口",
            () => dispatcher.TryEnqueue(RestoreFromTray),
            () => dispatcher.TryEnqueue(ExitApplication));
        return _trayIcon;
    }

    private void CreateTrayIcon()
    {
        var tray = GetOrCreateTrayIcon();
        // 启动时设置已开启：立即注册图标（窗口可见状态下也常驻，后续 HideToTray 复用）
        if (tray is not null && !tray.Show())
        {
            Log.Error("托盘：启动时图标注册失败（详见上一条日志），驻留托盘功能不可用");
        }
    }

    /// <summary>
    /// 重建当前页面：主题色切换后，让默认控件（ToggleSwitch / Slider 等）
    /// 重新解析 SystemAccentColor，保证全界面颜色一致。
    /// </summary>
    public void RefreshCurrentPage()
    {
        var pageType = ContentFrame.CurrentSourcePageType;
        if (pageType is not null)
        {
            ContentFrame.Navigate(pageType);
            ContentFrame.BackStack.Clear();
        }
    }

    /// <summary>按导航 tag 跳转页面（主页等处的「去处理」快速跳转按钮用），并同步导航栏选中项。</summary>
    public void NavigateByTag(string tag)
    {
        if (tag == "runtime" && !ReleaseFeatures.RuntimeUiEnabled) tag = "settings";
        if (!CanEnterGamePage(tag)) { UpdatePageAvailability(); return; }
        var target = EnumerateNavItems().FirstOrDefault(i => i.Tag?.ToString() == tag);
        if (target is not null)
        {
            NavView.SelectedItem = target; // SelectionChanged 里完成实际导航
        }
    }

    public void NavigateToProcessBackground()
    {
        NavigateByTag("process");
        if (ContentFrame.Content is ProcessPage page) page.ShowBackgroundSettings();
    }

    public void NavigateToFrameMemory()
    {
        NavigateByTag("frame");
        if (ContentFrame.Content is FramePage page) page.ShowAutomaticMemorySettings();
    }

    private IEnumerable<NavigationViewItem> EnumerateNavItems() =>
        NavView.MenuItems.OfType<NavigationViewItem>()
            .Concat(NavView.FooterMenuItems.OfType<NavigationViewItem>());

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }
        if (!CanEnterGamePage(item.Tag?.ToString() ?? ""))
        {
            NavView.SelectedItem = NavView.MenuItems[0];
            return;
        }
        if (item.Tag?.ToString() == "runtime" && !ReleaseFeatures.RuntimeUiEnabled)
        { NavigateByTag("settings"); return; }

        if (ServiceLocator.GameTarget.IsCustom && item.Tag?.ToString() is "shader" or "ace")
        {
            NavView.SelectedItem = NavView.MenuItems[0];
            return;
        }

        // 插件子入口：按插件 ID 导航到共享声明式页面；仅为导航，不启动/重连后端。
        if (PluginNavigationCatalog.ResolvePluginId(item.Tag?.ToString()) is { } pluginId)
        {
            if (_suppressPluginNavSelection) return;
            Services.Log.Info($"导航 → 插件入口 {pluginId}");
            ContentFrame.Navigate(typeof(PluginEntryPage), pluginId);
            ContentFrame.BackStack.Clear();
            UpdateGpuWarningVisibility();
            return;
        }

        Type pageType = item.Tag?.ToString() switch
        {
            "system" => typeof(SystemOptimizePage),
            "spoof" => typeof(GpuSpoofPage),
            "nvsettings" => typeof(NvSettingsPage),
            "frame" => typeof(FramePage),
            "shader" => typeof(ShaderPage),
            "ace" => typeof(AcePage),
            "runtime" => typeof(RuntimePage),
            "lab" => typeof(LabPage),
            "process" => typeof(ProcessPage),
            "plugins" => typeof(PluginPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage),
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            Services.Log.Info($"导航 → {item.Tag}");
            ContentFrame.Navigate(pageType);
            ContentFrame.BackStack.Clear();
        }
        UpdateGpuWarningVisibility();
    }
}
