using System.Xml.Linq;

internal static class UiDocumentChecks
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Ui = "using:DeltaNFD.Controls";
    internal static void Run(string? baseline)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DeltaNFD.sln"))) root = root.Parent;
        if (root is null) throw new Exception("Repository unavailable");
        var app = Path.Combine(root.FullName, "src/DeltaNFD"); var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; }
        XElement Page(string name) => XElement.Load(Path.Combine(app, "Views", name + ".xaml"));
        static XElement Text(XElement page, string text) => page.Descendants().Single(e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == text);
        static string Tip(XElement element) => (string?)element.Attribute(Ui + "HelpTip.Text") ?? "";
        static bool VisibleText(XElement page, string text) => page.Descendants().Any(e => (string?)e.Attribute("Text") == text);
        var frame = Page("FramePage");
        Check(!VisibleText(frame, "当前状态") && !frame.ToString().Contains("FrameSrv.StatusText"), "frame generic status card removed");
        Check(!VisibleText(frame, "开启后执行所选功能；关闭后停止守护并恢复可还原的临时设置。"), "frame mode secondary description removed");
        Check(Tip(Text(frame, "自动内存回收")) == "内存持续高压时回收后台应用的工作集，退出帧格后停止。", "memory visible description replaces old help body");
        Check(!VisibleText(frame, "内存持续高压时回收后台应用的工作集，退出帧格后停止。"), "memory duplicate description removed");
        Check(Tip(Text(frame, "CPU 优先级")).Contains("影响输入响应") && Tip(Text(frame, "IO 优先级")).Contains("系统限制"), "priority warnings are available on the two relevant controls");
        Check(!VisibleText(frame, "实时 CPU 优先级可能影响输入响应；IO 高优先级可能受系统限制而未生效。"), "duplicate priority warning row removed");
        var dwm = frame.Descendants().Single(e => ((string?)e.Attribute("Text"))?.Contains("x:Bind DwmStatusText") == true);
        Check(((string?)dwm.Attribute("Visibility"))?.Contains("FrameSrv.DwmRestartOnGameStart") == true, "DWM disabled label is hidden without removing enabled status");
        Check(Tip(Text(frame, "持续监测并修复 DWM 高占用")).Contains("屏幕可能短暂闪烁"), "DWM behavior and display warning retained in help");
        Check(Tip(Text(frame, "电源计划锁定")).Contains("PowerLockHelpText"), "power lock description moved to live help");
        Check(!VisibleText(frame, "关闭游戏进程的 EcoQoS 节能，退出帧格时恢复原值。"), "game power duplicate note removed");
        Check(!frame.ToString().Contains("x:Bind ForegroundStatusText"), "foreground configuration status removed");
        Check(!VisibleText(frame, "帧格期间切换为前台优先调度，退出时还原原值。"), "foreground duplicate note removed");
        Check(!VisibleText(frame, "帧格期间关闭网卡、USB 与 PCIe 省电，减少唤醒延迟；笔记本耗电会略增。"), "power-save header note removed");

        var runtime = Page("RuntimePage");
        Check(!runtime.ToString().Contains("ViewModel.DetectionStatusText"), "runtime detection trailing note removed");
        var reinstall = Text(runtime, "完整重装 C++ 运行库").Ancestors().First(e => e.Name.LocalName == "Border");
        Check(!VisibleText(reinstall, "受控卸载后重装 2005–2013 与 14.44 双架构 C++ 运行库。"), "full reinstall secondary description removed");
        var alert = reinstall.Descendants().Single(e => e.Name.LocalName == "InfoBar");
        Check((string?)alert.Attribute("IsOpen") == "True" && (string?)alert.Attribute("Severity") == "Warning"
            && ((string?)alert.Attribute("Message"))?.Contains("停止后续步骤") == true, "shared progress guidance promoted to a persistent warning");
        var order = reinstall.Descendants().ToList();
        Check(order.IndexOf(alert) < order.FindIndex(e => (string?)e.Attribute("Click") == "RepairHighFps_Click"), "reinstall guidance precedes the action");
        Check(!VisibleText(runtime, "注意：需要手动安装运行库时请先临时关闭本开关，装完再开。") && Tip(Text(runtime, "运行库防护模式")).Contains("全系统生效"), "guard note removed while scope remains in help");

        var process = Page("ProcessPage");
        Check(!VisibleText(process, "自动回收在帧格页开启；本页只调整参数、排除名单和手动回收。"), "background header note removed");
        foreach (var range in new[] { "可设置范围：30–95%。", "可设置范围：1–256 个进程。", "可设置范围：1–1440 分钟。" })
            Check(VisibleText(process, range), "memory input range shown: " + range);
        Check(!process.Descendants().Any(e => (string?)e.Attribute(X + "Name") == "MemoryModeDescriptionText")
            && process.Descendants().Any(e => (string?)e.Attribute(X + "Name") == "MemoryModeHelpTitle"), "mode detail lives in header help");
        var manual = process.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "MemoryTrimStatusText");
        Check(manual.Attribute("Text") is null && (string?)manual.Attribute("Visibility") == "Collapsed", "manual memory default gray note is hidden");
        Check(File.ReadAllText(Path.Combine(app, "Views/ProcessPage.xaml.cs")).Contains("MemoryTrimStatusText.Visibility = Visibility.Visible"), "actual memory operation feedback can still become visible");
        Check(!VisibleText(process, "按完整程序路径匹配；可在「进程与属性」中选中应用加入。"), "exclusion duplicate note removed");
        Check(!VisibleText(Page("GpuSpoofPage"), "与常见伪装工具一致的三个型号，直接选用即可"), "preset model note removed");

        var nv = Page("NvSettingsPage"); var shader = Page("ShaderPage");
        Check(!nv.ToString().Contains("ViewModel.ShaderCache"), "NVIDIA page no longer hosts cache scope status");
        var repair = shader.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "ShaderRepairModule");
        Check(VisibleText(repair, "着色器缓存大小") && VisibleText(repair, "全局设置") && VisibleText(repair, "三角洲程序设置"), "shader repair groups the two cache scopes");
        Check(repair.ToString().Contains("ShaderCacheGlobalText") && repair.ToString().Contains("ShaderCacheGameText"), "both read-only scopes bind to actual status properties");
        Check(!repair.Descendants().Any(e => e.Name.LocalName is "ComboBox" or "ToggleSwitch")
            && repair.Descendants().Any(e => (string?)e.Attribute("Click") == "OpenShaderRecovery_Click"), "cache module only shows status and opens confirmed repair flow");
        Check(nv.Descendants().Any(e => Tip(e).Contains("TitleHelpText")) && nv.Descendants().Any(e => ((string?)e.Attribute("Visibility"))?.Contains("ShowDescription") == true), "DLSS row uses title help and conditional description");
        var main = XElement.Load(Path.Combine(app, "MainWindow.xaml"));
        var sidebar = main.Descendants().Single(e => (string?)e.Attribute("Tag") == "shader");
        var title = shader.Descendants().Single(e => e.Name.LocalName == "PageHeader");
        Check((string?)sidebar.Attribute("Content") == (string?)title.Attribute("Title"), "shader sidebar and page title match");

        if (baseline is not null)
        {
            foreach (var name in new[] { "FramePage", "RuntimePage", "ProcessPage", "GpuSpoofPage", "NvSettingsPage", "ShaderPage" })
            {
                var before = XElement.Load(Path.Combine(baseline, "src/DeltaNFD/Views", name + ".xaml"));
                Check(Signatures(before).SequenceEqual(Signatures(Page(name))), name + ": input bindings, action handlers and numeric limits retained");
            }
        }
        Console.WriteLine($"Document UI requirements: {count} passed; source structure only, no desktop or backend started.");
    }

    private static IEnumerable<string> Signatures(XElement root)
    {
        var controls = new[] { "Button", "ToggleSwitch", "ComboBox", "NumberBox", "Slider", "CheckBox", "RadioButtons", "RadioButton", "ToggleButton" };
        var attrs = new[] { "Click", "Toggled", "SelectionChanged", "ValueChanged", "SelectedIndex", "IsOn", "IsChecked", "Value", "Minimum", "Maximum", "StepFrequency", "IsEnabled", "ItemsSource", "Command", "CommandParameter", "Tag" };
        return root.Descendants().Where(e => controls.Contains(e.Name.LocalName) && (string?)e.Attribute("Click") != "OpenShaderRecovery_Click")
            .Select(e => e.Name.LocalName + "|" + string.Join("|", attrs.Select(a => a + "=" + (string?)e.Attribute(a))))
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }
}
