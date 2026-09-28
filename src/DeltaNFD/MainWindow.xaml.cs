using DeltaNFD.Native;
using DeltaNFD.Services;
using DeltaNFD.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeltaNFD;

public sealed partial class MainWindow : Window
{
    private readonly IAppControlService _appControl = ServiceLocator.AppControl;

    /// <summary>托盘图标（开启「关闭时最小化到托盘」后使用）。</summary>
    private TrayIconWindow? _trayIcon;

    private bool _closePromptInProgress;
    private bool _exitRequested;

    /// <summary>当前公告版本标识（发布新公告时更新此值，老用户会再收到一次弹窗）。</summary>
    private const string CurrentAnnouncementVersion = "OpenAlphaV0.82";

    /// <summary>本次进程是否已检查过公告（Activated 每次激活都会触发，只处理一次）。</summary>
    private bool _announcementChecked;

    private bool _startupInitializationStarted;

    /// <summary>启动时的游戏目录检查完成后，主页导航才开始自动扫描组件。</summary>
    internal bool StartupGameDirectoryCheckCompleted { get; private set; }

    public MainWindow()
    {
        InitializeComponent();

        Title = "三角帧不掉洲 · 三角洲行动优化工具";
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

        if (AppSettingsStore.Read().CloseToTrayEnabled)
        {
            CreateTrayIcon();
        }

        // 默认选中主页并导航
        NavView.SelectedItem = NavView.MenuItems[0];

        // 背景图：启动应用一次，并监听设置页的实时变更
        ApplyBackgroundImage();
        BackgroundManager.Changed += ApplyBackgroundImage;
    }

    private async void ContentFrame_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startupInitializationStarted)
        {
            return;
        }

        _startupInitializationStarted = true;

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

            var dialog = new ContentDialog
            {
                XamlRoot = ContentFrame.XamlRoot,
                Title = "公告",
                Content = "欢迎使用我的软件，本次为 " + CurrentAnnouncementVersion +
                          " 测试版，如有 bug 请反馈给晓夕！",
                CloseButtonText = "开始使用",
                DefaultButton = ContentDialogButton.Close,
            };
            await dialog.ShowAsync();

            // 展示成功后才记录：展示失败（如异常）时下次启动重试
            AppSettingsStore.Update(s => s.AnnouncementVersionSeen = CurrentAnnouncementVersion);
            Services.Log.Info("公告：首启公告已展示并记录（" + CurrentAnnouncementVersion + "）");
        }
        catch (Exception ex)
        {
            // 公告失败不影响启动
            Services.Log.Error("公告：首启公告展示失败", ex);
        }
    }

    private async Task EnsureGameDirectoryOnStartupAsync()
    {
        if (DeltaForceLocator.FindRoots().Any())
        {
            return;
        }

        var prompt = new ContentDialog
        {
            XamlRoot = ContentFrame.XamlRoot,
            Title = "未检测到三角洲游戏目录",
            Content = "请定位三角洲行动的游戏安装目录。保存后，着色器、ACE 清理、运行库保护等功能会共用这个目录。",
            PrimaryButtonText = "立即定位",
            CloseButtonText = "稍后设置",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await prompt.ShowAsync() != ContentDialogResult.Primary)
        {
            Services.Log.Info("启动：用户选择稍后设置三角洲游戏目录");
            return;
        }

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
                }.ShowAsync();
                return;
            }

            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            if (DeltaForceLocator.TryGetValidGameRoot(selectedPath, out var gameRoot))
            {
                // 所有依赖游戏路径的功能都通过 DeltaForceLocator 读取这一共享设置。
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

            if (await retry.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
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

        var settings = AppSettingsStore.Read();
        if (settings.CloseToTrayEnabled)
        {
            // 已启用托盘驻留时，关闭窗口直接隐藏；注册失败则放行关闭，避免用户无法退出。
            var tray = GetOrCreateTrayIcon();
            if (tray is null || !tray.Show())
            {
                Log.Error("托盘：图标注册失败，本次关闭将直接退出应用（驻留托盘不可用）");
                return;
            }

            args.Cancel = true;
            DispatcherQueue.TryEnqueue(HideToTray);
            return;
        }

        if (settings.SuppressBackgroundRunPrompt || !HasBackgroundWorkToPreserve(settings))
        {
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

            var choice = await dialog.ShowAsync();
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
                    }.ShowAsync();
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
            || CpuTopologyService.SingleCcdExcludeActive
            || CpuTopologyService.DualCcdSchedulingActive;

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
    }

    /// <summary>快捷方式再次启动时，恢复托盘中的主窗口并将其激活。</summary>
    public void ActivateFromSecondLaunch() => RestoreFromTray();

    /// <summary>托盘菜单「退出」：先移除图标再退出应用（不留幽灵图标）。</summary>
    private void ExitApplication()
    {
        _exitRequested = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Application.Current.Exit();
    }

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
        var target = EnumerateNavItems().FirstOrDefault(i => i.Tag?.ToString() == tag);
        if (target is not null)
        {
            NavView.SelectedItem = target; // SelectionChanged 里完成实际导航
        }
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
            "settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage),
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            Services.Log.Info($"导航 → {item.Tag}");
            ContentFrame.Navigate(pageType);
            ContentFrame.BackStack.Clear();
        }
    }
}
