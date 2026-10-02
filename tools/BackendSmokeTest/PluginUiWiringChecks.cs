using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class PluginUiWiringChecks
{
    public static void Run()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "DeltaNFD", "Views", "PluginPage.xaml.cs"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("未找到待验证的插件页面源码。");
        var path = Path.Combine(root.FullName, "src", "DeltaNFD", "Views", "PluginPage.xaml.cs");
        var code = File.ReadAllText(path);
        var xaml = XDocument.Load(Path.ChangeExtension(path, null)!);
        Require(Regex.Matches(code, @"\bRenderPluginForm\(").Count > 1, "form renderer must have a caller");
        Require(code.Contains("PluginList.SelectedItem = Rows.FirstOrDefault") && code.Contains("if (_suppressPluginSelection) return;"), "refresh preserves selection");
        var restore = code[code.IndexOf("private async void RestoreButton_Click", StringComparison.Ordinal)..code.IndexOf("private async void UninstallButton_Click", StringComparison.Ordinal)];
        Require(restore.Contains("catch (Exception ex)"), "restore UI catches failures");
        Require(restore.IndexOf("RefreshAll();", StringComparison.Ordinal) < restore.IndexOf("DetailStatusText.Text = summary;", StringComparison.Ordinal), "summary survives refresh");
        var names = xaml.Descendants().Select(e => e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value).ToHashSet();
        Require(names.Contains("PluginFormHost") && names.Contains("PluginFormPanel"), "form XAML targets exist");
        Console.WriteLine("Plugin UI wiring checks passed. Source/XAML assertions only; not a real GUI/UAC validation.");
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
