using DeltaNFD.Native;
using DeltaNFD.Services;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DeltaNFD;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    internal static bool ExitAlreadyPrepared { get; set; }
    internal static (string Session, string Nonce)? PendingFileFallback { get; private set; }

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

        // 前后端分离 C5：进程退出前排空官方后端（异步启动，短时间宽限后允许退出）。
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (ExitAlreadyPrepared) return;
            // 优化：启动排空任务但不同步等待，给予 5 秒宽限期后允许进程退出
            var drainTask = Task.Run(async () =>
            {
                try
                {
                    var remaining = await Services.Backends.BackendLifecycle.PrepareHandoverAsync("进程退出", TimeSpan.FromSeconds(30));
                    if (remaining.Count == 0) Log.Info("进程退出：后端排空完成");
                    else Log.Warn("进程退出：仍有未确认状态，证据保留：" + string.Join("、", remaining));
                }
                catch (Exception ex)
                {
                    Log.Warn("进程退出：后端排空失败 - " + ex.Message);
                }
            });

            // 给予 5 秒宽限期，避免阻塞进程退出过久
            if (!drainTask.Wait(TimeSpan.FromSeconds(5)))
            {
                Log.Info("进程退出：后端排空未在 5 秒内完成，允许进程退出");
            }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Length == 4 && commandLine[1] == "--resume-file-update-fallback")
            PendingFileFallback = (commandLine[2], commandLine[3]);
        if (commandLine.Length == 4 && commandLine[1] == "--verify-file-update")
        {
            ExitAlreadyPrepared = true;
            // Boot verification deliberately precedes settings, frame services and backend startup.
            Environment.Exit(FileUpdateHealth.Verify(commandLine[2], commandLine[3]));
            return;
        }
        if (commandLine.Contains("--prepare-deployment", StringComparer.Ordinal)
            || commandLine.Contains("--restore-runtime-guard-for-uninstall", StringComparer.Ordinal))
        {
            ExitAlreadyPrepared = true;
            _ = RestoreRuntimeGuardForUninstallAsync();
            return;
        }
        if (SingleInstanceGuard.DeploymentInProgress())
        {
            ExitAlreadyPrepared = true;
            Log.Info("安装或卸载正在进行，本次启动已拒绝；请等待完成后重新打开工具。");
            Application.Current.Exit();
            return;
        }
        if (!SingleInstanceGuard.TryBecomePrimary(activateExisting: !StartupPresentation.IsLogonLaunch(commandLine)))
        {
            ExitAlreadyPrepared = true;
            Application.Current.Exit();
            return;
        }

        Log.Startup();
        UpdateChannelInstallation.ApplyCurrentInstallation();
        Log.Info("主窗口启动");
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        if (PendingFileFallback is null) _ = ServiceLocator.Frame;
        if (PendingFileFallback is null && AppSettingsStore.Read().GamePriorityEnabled)
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
        DeploymentPipe.Start(dispatcherQueue, mainWindow.PrepareDeploymentAsync, mainWindow.ExitForDeployment);
        mainWindow.PresentOnStartup(StartupPresentation.ShouldStartHidden(commandLine,
            AppSettingsStore.Read().SilentStartupEnabled,
            OfflineModeGuard.BlocksNormalAutomation || AppDataPaths.MigrationWarning is not null || PendingFileFallback is not null));
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

    /// <summary>卸载前恢复的分项报告文件（与卸载日志同目录，供安装器读取并分项列出阻塞子系统）。</summary>
    private static string UninstallReportPath =>
        Path.Combine(AppDataPaths.Root, "logs", "uninstall-restore-report.txt");

    /// <summary>无 UI 的安装/卸载恢复入口；请求身份写入报告，避免安装器读取旧结果。</summary>
    private static async Task RestoreRuntimeGuardForUninstallAsync()
    {
        var args = Environment.GetCommandLineArgs();
        string? Argument(string name) => Array.IndexOf(args, name) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var request = Argument("--deployment-request");
        var uninstall = Argument("--deployment-mode") != "install";
        var code = 2;
        var detail = "交接未完成。";
        DeploymentHandover? permit = null;
        try
        {
            // A standalone recovery process cannot restore the original main process's in-memory state.
            // The installer must complete cooperative exit before reaching this entry point.
            permit = await DeploymentHandover.PrepareAsync(uninstall);
            code = permit.ExitCode;
            detail = permit.Detail;
        }
        catch (Exception ex) { Log.Error("安装/卸载前恢复异常", ex); detail = ex.Message; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UninstallReportPath)!);
            File.WriteAllText(UninstallReportPath, "交接结果=" + code + Environment.NewLine + detail);
            if (Guid.TryParse(request, out var requestId))
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(UninstallReportPath)!, "deployment-" + requestId.ToString("N") + ".txt"),
                    requestId.ToString("N") + "|" + code + Environment.NewLine + detail);
        }
        catch (Exception ex) { Log.Error("交接报告写入失败", ex); code = 2; }
        // Keep the successful permit alive through ProcessExit; do not admit fresh automatic calls.
        Environment.Exit(code);
        GC.KeepAlive(permit);
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
                    // Keep the existing translucency; darken the accent so white body text stays readable.
                    if (dict["HomeBrandBrush"] is SolidColorBrush brand)
                    {
                        var shade = Scale(accent, 0.28);
                        brand.Color = Windows.UI.Color.FromArgb(themeKey == "Light" ? (byte)0xC8 : (byte)0xC0,
                            shade.R, shade.G, shade.B);
                    }
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
