using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>
/// 界面精简（2026-10-30）的静态回归：只读取 XAML/C# 源码，不启动 GUI、不连接后端。
/// </summary>
internal static class UiDensityChecks
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Ui = "using:DeltaNFD.Controls";
    private static readonly string[] SimplifiedPages =
        { "FramePage", "SettingsPage", "ProcessPage", "LabPage", "RuntimePage", "AcePage", "ShaderPage", "SystemOptimizePage", "NvSettingsPage", "GameModificationsPage" };

    public static void Run()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "DeltaNFD", "Views", "FramePage.xaml"))) root = root.Parent;
        if (root is null) throw new Exception("UI source unavailable.");
        var app = Path.Combine(root.FullName, "src", "DeltaNFD");
        string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { app }.Concat(parts).ToArray()));
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); count++; }

        // 1. 样式基线：字号与圆角统一，新增说明/风险样式存在。
        var styles = XDocument.Parse(Source("Themes", "AppStyles.xaml") );
        var appXaml = XDocument.Parse(Source("App.xaml"));
        string? Setter(XDocument doc, string key, string property) => doc.Descendants()
            .Where(e => e.Name.LocalName == "Style" && (string?)e.Attribute(X + "Key") == key)
            .SelectMany(e => e.Elements()).Where(e => (string?)e.Attribute("Property") == property)
            .Select(e => (string?)e.Attribute("Value")).FirstOrDefault();
        Check(Setter(styles, "H1Style", "FontSize") == "20", "page title is 20");
        Check(Setter(styles, "NoteStyle", "FontSize") == "13", "note text is 13");
        foreach (var key in new[] { "CaptionStyle", "WarningTextStyle", "CriticalTextStyle" })
            Check(styles.Descendants().Any(e => (string?)e.Attribute(X + "Key") == key), "style exists: " + key);
        Check(Setter(appXaml, "CardBorderStyle", "CornerRadius") == "8", "card radius 8");
        Check(Setter(appXaml, "AccentButtonStyle", "CornerRadius") == "4" && Setter(styles, "SecondaryButtonStyle", "CornerRadius") == "4", "button radius 4");
        Check(Setter(appXaml, "ChipBorderStyle", "CornerRadius") == "4", "chip radius 4");
        Check((string?)appXaml.Descendants().Single(e => (string?)e.Attribute(X + "Key") == "CardDescStyle").Attribute("BasedOn") == "{StaticResource NoteStyle}",
            "card description shares the note style");

        // 2. 精简页面：可见长说明收进 HelpTip，状态/风险文字保留，无写死字号/透明度/颜色。
        var helpTips = 0;
        foreach (var page in SimplifiedPages)
        {
            var path = Path.Combine(app, "Views", page + ".xaml");
            var doc = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var e in doc.Descendants())
            {
                var local = e.Name.LocalName;
                Check(local != "HelpTip", $"{page}: help tips are attached to title text, not extra elements");
                if (e.Attribute(Ui + "HelpTip.Text") is { } tip)
                {
                    helpTips++;
                    Check(local == "TextBlock" && !string.IsNullOrWhiteSpace((string?)e.Attribute("Text")) && tip.Value.Length > 0, $"{page}: help tip sits on a titled TextBlock");
                }
                var text = (string?)e.Attribute(local == "InfoBar" ? "Message" : "Text");
                if (local is "TextBlock" or "InfoBar" && text is not null && !text.StartsWith('{') && text.Length > 60 && !AllowedLongText(page, text))
                    throw new InvalidOperationException($"{page}:{((System.Xml.IXmlLineInfo)e).LineNumber} visible long text ({text.Length}): {text[..30]}…");
                if (local == "TextBlock")
                {
                    Check(e.Attribute("Opacity") is null, $"{page}: text opacity comes from styles");
                    var size = (string?)e.Attribute("FontSize");
                    Check(size is null || page == "SettingsPage" && size == "11", $"{page}: text font size comes from styles");
                }
                foreach (var attr in new[] { "Foreground", "Background" })
                    Check(!Regex.IsMatch((string?)e.Attribute(attr) ?? "", "^#[0-9A-Fa-f]{6,8}$") || local == "FontIcon",
                        $"{page}: {local}.{attr} uses theme resources");
            }
        }
        Check(helpTips >= 40, "long explanations are reachable through help tips: " + helpTips);

        var frame = Source("Views", "FramePage.xaml");
        foreach (var status in new[] { "DwmStatusText", "PowerSaveStatusText", "GamePowerStatusText", "GameTimerIgnoreStatusText", "GamePriorityStatusText", "AutomaticMemoryStatusText" })
            Check(frame.Contains(status), "frame status retained: " + status);
        foreach (var warning in new[] { "屏幕会短暂闪烁", "帧格意外退出", "实时 CPU 优先级可能影响输入响应" })
            Check(frame.Contains(warning), "frame risk remains available: " + warning);
        Check(!frame.Contains("FrameSrv.StatusText") && !frame.Contains("ForegroundStatusText"), "document removes generic and foreground configuration status");
        Check(frame.Contains("PowerLockHelpText") && Source("Views", "FramePage.xaml.cs").Contains("亮度相关设置保留用户调整"), "power lock description and warning moved into title help");
        Check(Source("Views", "RuntimePage.xaml").Contains("可能影响其他软件"), "force replace risk stays visible");
        Check(Source("Views", "AcePage.xaml").Contains("可能影响多款 ACE 游戏") && Source("Views", "AcePage.xaml").Contains("sguard64.exe 运行时清理会被拒绝"), "ACE cleanup risks stay visible");
        Check(Source("Views", "SettingsPage.xaml").Contains("未完成的操作必须先核对"), "factory reset recovery note stays visible");
        Check(Source("Views", "SystemOptimizePage.xaml").Contains("点击「应用更改」后才会写入系统"), "batch apply rule stays visible");

        // 3. 延迟加载：不适用的处理器卡片和更新详情在条件成立前不创建。
        var lab = XDocument.Load(Path.Combine(app, "Views", "LabPage.xaml"));
        foreach (var card in new[] { "IntelPcoresCard", "CcdJudgeCard", "DualCcdCard", "SingleCcdCard" })
            Check(((string?)lab.Descendants().Single(e => (string?)e.Attribute(X + "Name") == card).Attribute(X + "Load"))?.StartsWith("{x:Bind ViewModel.Show") == true, "deferred processor card: " + card);
        var settings = XDocument.Load(Path.Combine(app, "Views", "SettingsPage.xaml"));
        foreach (var panel in new[] { "UpdateAvailablePanel", "UpdateNotesExpander", "UpdateProgressPanel" })
            Check(settings.Descendants().Single(e => (string?)e.Attribute(X + "Name") == panel).Attribute(X + "Load") is not null, "deferred update panel: " + panel);
        Check(XDocument.Parse(frame).Descendants().Single(e => (string?)e.Attribute(X + "Name") == "DualCcdFrameOption").Attribute(X + "Load") is not null, "dual CCD frame option deferred");

        // 4. 控件与动画：说明浮层点击才创建；悬停走独立合成属性集；入场动画缩短。
        var help = Source("Controls", "HelpTip.cs");
        Check(help.Contains("block.GetValue(FlyoutProperty) is not Flyout flyout") && help.Contains("new Hyperlink") && !help.Contains(": Button"), "help flyout created on first click; inline link adds no visual element");
        var hover = Source("Controls", "HoverLift.cs");
        Check(!hover.Contains("new Storyboard") && !hover.Contains("EnableDependentAnimation =")
            && hover.Contains("ElementCompositionPreview.SetIsTranslationEnabled") && hover.Contains("Properties.StartAnimation(\"Translation\"")
            && !hover.Contains("element.Translation") && !hover.Contains("TranslationTransition"),
            "hover uses legacy composition translation compatible with page RenderTransform");
        Check(hover.Contains("if (States.TryGetValue(element, out var state)) Restore(state);") && hover.Contains("state.Disabled = true;"),
            "unload touches only owned motion state and decorative failures stay contained");
        Check(hover.Contains("internal static CompositeTransform? MotionTransform"), "page motion transform helper retained");
        var motion = Source("Controls", "PageMotion.cs");
        Check(motion.Contains("Math.Min(index,4)") && motion.Contains("panels is null?200:160"), "page stagger shortened");

        // 5. 主窗口：标题栏不再重复作者；背景按窗口宽度解码，隐藏时释放。
        var mainXaml = Source("MainWindow.xaml");
        Check(!mainXaml.Contains("作者"), "author line moved to About");
        Check(Source("Views", "SettingsPage.xaml").Contains("作者：逐梦之子-晓夕"), "author kept in About");
        var main = Source("MainWindow.xaml.cs");
        Check(main.Contains("DecodePixelWidth = decodeWidth") && main.Contains("Math.Clamp((needed + 319) / 320 * 320, 960, 2560)"), "background decoded to window width");
        Check(main.Contains("if (args.DidVisibilityChange) ReleaseBackgroundImage();") && main.Contains("if (AppWindow.IsVisible) LoadBackgroundBitmap"), "background released while hidden");
        Check(main.Contains("AppWindow.Changed -= AppWindow_BackgroundChanged"), "window change handler detached on close");

        Console.WriteLine($"UI density checks: {count} passed, {helpTips} help tips across {SimplifiedPages.Length} pages. Static source only; no GUI or backend started.");
    }

    // 致谢、外部链接说明等不属于功能说明，保持原样。
    private static bool AllowedLongText(string page, string text) => page == "SettingsPage" &&
        (text.StartsWith("感谢 Henry++") || text.StartsWith("Windows，MacOS") || text.StartsWith("【同时支持兼容"));
}
