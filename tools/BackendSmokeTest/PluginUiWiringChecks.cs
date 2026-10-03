using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class PluginUiWiringChecks
{
    public static void Run()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "DeltaNFD", "Views", "PluginPage.xaml.cs"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("未找到待验证的插件页面源码。");

        string Source(params string[] parts) => File.ReadAllText(Path.Combine(root.FullName, Path.Combine(parts)));
        var pagePath = Path.Combine(root.FullName, "src", "DeltaNFD", "Views", "PluginPage.xaml.cs");
        var page = File.ReadAllText(pagePath);
        var pageXaml = XDocument.Load(Path.ChangeExtension(pagePath, null)!);
        var entry = Source("src", "DeltaNFD", "Views", "PluginEntryPage.xaml.cs");
        var entryXaml = XDocument.Load(Path.Combine(root.FullName, "src", "DeltaNFD", "Views", "PluginEntryPage.xaml"));
        var view = Source("src", "DeltaNFD", "Views", "PluginDeclarativeView.xaml.cs");
        var main = Source("src", "DeltaNFD", "MainWindow.xaml.cs");
        var catalog = Source("src", "DeltaNFD", "Services", "Plugins", "PluginNavigationCatalog.cs");

        // 管理页：保留选中项、恢复失败有提示、表单由共享控件渲染。
        Require(page.Contains("PluginList.SelectedItem = Rows.FirstOrDefault") && page.Contains("if (_suppressPluginSelection) return;"), "refresh preserves selection");
        var restore = page[page.IndexOf("private async void RestoreButton_Click", StringComparison.Ordinal)..page.IndexOf("private async void UninstallButton_Click", StringComparison.Ordinal)];
        Require(restore.Contains("catch (Exception ex)"), "restore UI catches failures");
        Require(restore.IndexOf("RefreshAll();", StringComparison.Ordinal) < restore.IndexOf("DetailStatusText.Text = summary;", StringComparison.Ordinal), "summary survives refresh");
        Require(page.Contains("DeclarativeView.SetEntry") && page.Contains("DeclarativeView.Clear()"), "management page shares the declarative view");

        // B3：共享声明式页面由管理页与独立入口共用；入口只导航，不启动/重连/恢复/移交/停止后端。
        Require(entry.Contains("DeclarativeView.SetEntry") && entry.Contains("DeclarativeView.Clear()"), "entry page shares the declarative view");
        Require(entry.Contains("Unloaded"), "entry page releases the view on unload");
        foreach (var forbidden in new[] { "InvokeOnceAsync", "StartPersistentAsync", "RestoreAsync", "HandoffToOffline", "StopAsync", "DrainOfflinePlugins" })
            Require(!entry.Contains(forbidden, StringComparison.Ordinal), "entry navigation must not call backend: " + forbidden);
        Require(view.Contains("InvokeOnceAsync"), "shared view runs explicit player operations");
        Require(view.Contains("_generation"), "shared view discards stale callbacks after page switch");
        Require(view.Contains("PluginPackageIntegrity.VerifyAndLock"), "shared view verifies package integrity before rendering");
        Require(view.Contains("lease.ReadJson(\"ui.json\")") && !view.Contains("TryReadUi"), "rendering uses verified locked bytes");
        Require(view.Contains("TryReadConfig") && view.Contains("TryWriteConfig") && view.Contains("SaveCurrentConfiguration();"), "configuration loads and survives navigation");
        Require(!view.Contains("Index.TryUpsert"), "page callbacks do not own recovery persistence");
        Require(!entry.Contains("static event"), "entry page must not add static subscriptions");

        // B2/B4：宿主在固定节点下生成子菜单，按插件 ID 路由，集合变更后刷新，不依赖运行时。
        Require(main.Contains("PluginNavigationCatalog.Build") && main.Contains("PluginNavigationCatalog.ResolvePluginId"), "host builds and routes plugin menu by id");
        Require(main.Contains("ServiceLocator.Plugins.Changed"), "menu refreshes after plugin set changes");
        Require(main.Contains("typeof(PluginEntryPage)"), "plugin tags route to the entry page");
        Require(main.Contains("IsStale()"), "stale entry page exits after upgrade/uninstall");
        Require(!catalog.Contains("ServiceLocator.PluginRuntime", StringComparison.Ordinal) &&
            !catalog.Contains("PluginRuntimeService.", StringComparison.Ordinal), "catalog must not depend on the plugin runtime");

        var pageNames = pageXaml.Descendants().Select(e => e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value).ToHashSet();
        Require(pageNames.Contains("PluginFormHost") && pageNames.Contains("DeclarativeView"), "management page XAML targets exist");
        var entryNames = entryXaml.Descendants().Select(e => e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value).ToHashSet();
        Require(entryNames.Contains("DeclarativeView"), "entry page XAML hosts the shared view");

        // B5：可访问名称、长名称完整提示、深浅主题无关（无硬编码颜色）。
        Require(main.Contains("AutomationProperties.SetName"), "plugin menu items expose accessible names");
        Require(main.Contains("ToolTipService.SetToolTip"), "long plugin names have a full tooltip");
        Require(!Source("src", "DeltaNFD", "Views", "PluginDeclarativeView.xaml").Contains('#'), "shared view XAML must not hardcode colors");
        Require(!File.ReadAllText(Path.Combine(root.FullName, "src", "DeltaNFD", "Views", "PluginEntryPage.xaml")).Contains('#'), "entry page XAML must not hardcode colors");
        var entryXamlText = File.ReadAllText(Path.Combine(root.FullName, "src", "DeltaNFD", "Views", "PluginEntryPage.xaml"));
        Require(entryXamlText.Contains("AutomationProperties.Name"), "entry page buttons expose accessible names");
        Require(!Regex.IsMatch(entryXamlText, @"(?<![A-Za-z])(Width|Height)\s*="), "entry page avoids fixed width/height that breaks scaling");

        Console.WriteLine("Plugin UI wiring checks passed. Source/XAML assertions only; not a real GUI/UAC validation.");
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
