using DeltaNFD.ViewModels;
using System.Xml.Linq;

internal static class DashboardDesignChecks
{
    public static void Run()
    {
        var states = Enum.GetValues<DashboardHealthState>();
        foreach (var a in states)
        foreach (var b in states)
        foreach (var c in states)
        {
            var summary = DashboardHealthPresentation.Summary(false, a, b, c);
            Require(!summary.Contains("检测正常") || new[] { a, b, c }.All(s => s == DashboardHealthState.Normal), "only confirmed normal components produce normal summary");
            if (new[] { a, b, c }.Any(s => s is DashboardHealthState.Failed or DashboardHealthState.Abnormal))
                Require(summary.Contains("异常"), "failure cannot disappear behind other statuses");
            Require(DashboardHealthPresentation.Summary(true, a, b, c).Contains("正在检测"), "scanning summary");
        }
        Require(DashboardHealthPresentation.Summary(false, DashboardHealthState.NotApplicable).Contains("不适用"), "custom target not applicable");
        Require(DashboardHealthPresentation.Summary(false, DashboardHealthState.Warning).Contains("提醒"), "warning summary");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "DeltaNFD", "Views", "DashboardPage.xaml"))) root = root.Parent;
        if (root is null) throw new Exception("Dashboard source unavailable.");
        var view = Path.Combine(root.FullName, "src", "DeltaNFD", "Views", "DashboardPage.xaml");
        var xml = XDocument.Load(view);
        var text = File.ReadAllText(view);
        Require(!text.Contains("HeroGradientBrush") && !text.Contains("一键完成进程") && !text.Contains("给三角洲"), "marketing graphics removed");
        Require(!xml.Descendants().Any(e => e.Name.LocalName == "Expander") && !text.Contains("HomeBandBrush"), "vertical list restored without large dark cards");
        Require(text.IndexOf("BrandPanel", StringComparison.Ordinal) < text.IndexOf("FramePanel", StringComparison.Ordinal) &&
            text.IndexOf("FramePanel", StringComparison.Ordinal) < text.IndexOf("ComponentPanel", StringComparison.Ordinal), "old homepage order restored");
        foreach (var name in new[] { "ShaderStatusText", "AceStatusText", "RuntimeStatusText" })
            Require(text.Contains(name), "complete diagnostics retained");
        Require(text.Contains("MinHeight=\"220\"") && text.Contains("TargetName") && text.Contains("TargetPathText") && text.Contains("FontSize=\"28\""), "brand and target panel sizing");
        Require(!text.Contains("QuickOptimizeCommand"), "homepage never invokes system optimization");
        var code = File.ReadAllText(view + ".cs");
        Require(code.Contains("GameTarget.Changed += OnTargetChanged") && code.Contains("GameTarget.Changed -= OnTargetChanged"), "paired target subscriptions");
        Require(code.Contains("if (_attached)"), "unloaded callback fence");
        Require(code.Contains("FrameModeFlow.RunToggleAsync"), "existing frame confirmation/rollback retained");
        var app = File.ReadAllText(Path.Combine(root.FullName, "src", "DeltaNFD", "App.xaml"));
        var appCode = File.ReadAllText(Path.Combine(root.FullName, "src", "DeltaNFD", "App.xaml.cs"));
        Require(!text.Contains("x:Key=\"HomeBrandBrush\"") && app.Contains("x:Key=\"HomeBrandBrush\""), "brand brush follows shared theme resources");
        Require(appCode.Contains("dict[\"HomeBrandBrush\"]") && appCode.Contains("brand.Color =") && appCode.Contains("(byte)0xC8 : (byte)0xC0"),
            "theme accent updates live brand brushes and preserves alpha");
        Console.WriteLine("Dashboard checks passed: 343 state combinations, restored vertical layout, brand target panel and lifecycle wiring. Not a GUI or screen-scale validation.");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
