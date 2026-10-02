using DeltaNFD.Native;
using DeltaNFD.Services;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DeltaNFD;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>默认主题色（天空蓝，与 App.xaml 内置资源一致）。</summary>
    public static readonly Windows.UI.Color DefaultAccent = Windows.UI.Color.FromArgb(255, 0x5E, 0xB0, 0xFF);

    public App()
    {
        InitializeComponent();

        // 全局异常落日志（其他电脑排障用；处理后不再让默认崩溃对话框吞掉细节）
        UnhandledException += (s, e) =>
        {
            Log.Error($"WinUI 未处理异常：{e.Message}", e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Log.Error("AppDomain 未处理异常（进程级）", e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Log.Error("Task 未观察异常", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Contains("--restore-runtime-guard-for-uninstall", StringComparer.Ordinal))
        {
            _ = RestoreRuntimeGuardForUninstallAsync();
            return;
        }
        if (!SingleInstanceGuard.TryBecomePrimary())
        {
            Application.Current.Exit();
            return;
        }

        Log.Startup();
        Log.Info("主窗口启动");
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        _ = ServiceLocator.Frame;
        if (AppSettingsStore.Read().GamePriorityEnabled)
        {
            _ = ServiceLocator.GameProcess;
        }
        var dispatcherQueue = mainWindow.DispatcherQueue;
        SingleInstanceGuard.StartActivationListener(() =>
        {
            if (!dispatcherQueue.TryEnqueue(mainWindow.ActivateFromSecondLaunch))
            {
                Log.Warn("单实例：主窗口 Dispatcher 已停止，无法恢复窗口");
            }
        });
        mainWindow.Activate();
        if (AppDataPaths.MigrationWarning is { } migrationWarning)
        {
            Log.Warn(migrationWarning);
            dispatcherQueue.TryEnqueue(() =>
                _ = MessageBoxW(nint.Zero, migrationWarning + "\n\n请先核对旧版设置和备份，再使用系统优化功能。", "三角帧不掉洲", 0x30));
        }

        // 启动时恢复持久化的明暗主题与主题色
        // 背景图片模式开启时界面锁定深色主题（背景图按深色调设计，浅色下文字不可读）
        var s = AppSettingsStore.Read();
        SetAppTheme(s.BackgroundImageEnabled
            ? ElementTheme.Dark
            : s.AppThemeIndex switch
            {
                0 => ElementTheme.Light,
                1 => ElementTheme.Dark,
                _ => ElementTheme.Default,
            });
        SetAccentColor(TryParseColor(s.AccentColor, out var c) ? c : DefaultAccent, persist: false);
    }

    private static async Task RestoreRuntimeGuardForUninstallAsync()
    {
        var code = 2;
        try
        {
            var offline = await new OfflineModeCoordinator().PrepareForRemovalAsync(restoreCpuSets: true);
            Log.Info("卸载前脱机恢复：" + offline.Message);
            if (!offline.Success) { Environment.Exit(2); return; }
            var result = await new RuntimeGuardService().DisableAsync();
            Log.Info("卸载前运行库防护恢复：" + result.Message);
            code = result.Success ? 0 : 2;
        }
        catch (Exception ex) { Log.Error("卸载前运行库防护恢复异常", ex); }
        Environment.Exit(code);
    }

    /// <summary>切换应用主题（设置页调用）。</summary>
    public static void SetAppTheme(ElementTheme theme)
    {
        if (MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }
    }

    /// <summary>
    /// 切换工具主题色：即时更新全部共享画刷（AppAccentBrush / Hero 渐变 / 半透明包底），
    /// 并替换系统强调色资源——已加载页面上的默认控件（ToggleSwitch 等）在下次导航时取新值，
    /// persist=true 时写入 settings.json。
    /// </summary>
    public static void SetAccentColor(Windows.UI.Color accent, bool persist = true)
    {
        try
        {
            if (Current.Resources["AppAccentBrush"] is SolidColorBrush accentBrush)
            {
                accentBrush.Color = accent;
            }

            if (Current.Resources["AppAccentSoftBrush"] is SolidColorBrush soft)
            {
                soft.Color = Windows.UI.Color.FromArgb(0x26, accent.R, accent.G, accent.B);
            }

            if (Current.Resources["AppAccentChipBrush"] is SolidColorBrush chip)
            {
                chip.Color = Windows.UI.Color.FromArgb(0x2E, accent.R, accent.G, accent.B);
            }

            // Color 资源（AccentButtonStyle 的 hover/pressed 动画目标）：替换资源值，
            // 新加载的控件取新值；已有按钮的动画终点在页面重建后刷新。
            Current.Resources["AppAccentColor"] = accent;
            Current.Resources["AppAccentHoverColor"] = Lighten(accent, 0.14);
            Current.Resources["AppAccentPressedColor"] = Darken(accent, 0.14);

            // Hero 渐变（半透明，透出背景图）：在共享画刷实例上改 GradientStop，所有引用处实时生效
            if (Current.Resources["HeroGradientBrush"] is LinearGradientBrush hero)
            {
                var dark = Scale(accent, 0.28);
                var mid = Scale(accent, 0.55);
                hero.GradientStops[0].Color = Windows.UI.Color.FromArgb(0xC0, dark.R, dark.G, dark.B);
                hero.GradientStops[1].Color = Windows.UI.Color.FromArgb(0xA8, mid.R, mid.G, mid.B);
                hero.GradientStops[2].Color = Windows.UI.Color.FromArgb(0x80, accent.R, accent.G, accent.B);
            }

            // 系统强调色（默认控件）：写入各主题字典，导航到新页面后生效
            var derived = new (string Key, Windows.UI.Color Color)[]
            {
                ("SystemAccentColor", accent),
                ("SystemAccentColorDark1", Darken(accent, 0.08)),
                ("SystemAccentColorDark2", Darken(accent, 0.16)),
                ("SystemAccentColorDark3", Darken(accent, 0.28)),
                ("SystemAccentColorLight1", Lighten(accent, 0.10)),
                ("SystemAccentColorLight2", Lighten(accent, 0.20)),
                ("SystemAccentColorLight3", Lighten(accent, 0.30)),
            };

            foreach (var themeKey in new[] { "Default", "Light" })
            {
                if (Current.Resources.ThemeDictionaries[themeKey] is ResourceDictionary dict)
                {
                    foreach (var (key, color) in derived)
                    {
                        dict[key] = color;
                    }
                }
            }
        }
        catch
        {
            // 主题色失败不影响功能
        }

        if (persist)
        {
            AppSettingsStore.Update(s => s.AccentColor = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}");
        }
    }

    /// <summary>解析 "#RRGGBB"；失败返回 false。</summary>
    public static bool TryParseColor(string? hex, out Windows.UI.Color color)
    {
        color = DefaultAccent;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var t = hex.Trim().TrimStart('#');
        if (t.Length != 6)
        {
            return false;
        }

        var span = t.AsSpan();
        if (!byte.TryParse(span[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(span[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(span[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return false;
        }

        color = Windows.UI.Color.FromArgb(255, r, g, b);
        return true;
    }

    private static Windows.UI.Color Lighten(Windows.UI.Color c, double factor)
    {
        factor = Math.Clamp(factor, 0, 1);
        return Windows.UI.Color.FromArgb(255,
            (byte)Math.Round(c.R + (255 - c.R) * factor),
            (byte)Math.Round(c.G + (255 - c.G) * factor),
            (byte)Math.Round(c.B + (255 - c.B) * factor));
    }

    private static Windows.UI.Color Darken(Windows.UI.Color c, double factor)
    {
        factor = Math.Clamp(factor, 0, 1);
        return Windows.UI.Color.FromArgb(255,
            (byte)Math.Round(c.R * (1 - factor)),
            (byte)Math.Round(c.G * (1 - factor)),
            (byte)Math.Round(c.B * (1 - factor)));
    }

    private static Windows.UI.Color Scale(Windows.UI.Color c, double factor) => Darken(c, 1 - Math.Clamp(factor, 0, 1));

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
}
