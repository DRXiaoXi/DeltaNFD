using System.IO.Compression;
using System.Text;
using DeltaNFD.Services;
using DeltaNFD.Services.Plugins;

namespace BackendSmokeTest;

/// <summary>
/// 侧栏入口 B1-B4 逻辑验证（待做清单第六节 B1→B4）。
/// 覆盖：schema 2 navigation 合同（合法/非法字段、旧宿主拒绝、缺省路径）、宿主菜单目录构建
/// （同名不同 ID 不串、失败无幽灵菜单、schema 1 无入口）、按插件 ID 路由与状态文案。
/// 全部在临时目录；不启动、不调用任何插件后端，不触碰本机插件目录。
/// </summary>
internal static class PluginNavigationChecks
{
    private static int _failures;
    private static int _checks;
    private static readonly List<string> CreatedPackages = [];

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_PluginNav_" + Guid.NewGuid().ToString("N"));
        var pluginsRoot = Path.Combine(root, "Plugins");
        Directory.CreateDirectory(pluginsRoot);
        try
        {
            RunManifestNavigationChecks();
            RunCatalogChecks(pluginsRoot);
            RunLifecycleChecks(Path.Combine(root, "Lifecycle"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            foreach (var leftover in CreatedPackages)
                try { File.Delete(leftover); } catch (IOException) { }
            CreatedPackages.Clear();
        }
        Console.WriteLine($"插件侧栏入口检查完成：{_checks} 项，失败 {_failures} 项。");
        if (_failures > 0) Environment.ExitCode = 1;
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        _checks++;
        if (condition) return;
        _failures++;
        Console.WriteLine($"  [失败] {name}{(detail is null ? "" : "：" + detail)}");
    }

    private static void CheckReject(string name, string json)
    {
        _checks++;
        try { PluginManifestParser.Parse(Encoding.UTF8.GetBytes(json)); }
        catch (PluginContractException) { return; }
        _failures++;
        Console.WriteLine($"  [失败] {name}：应拒绝但未拒绝。");
    }

    // ---------- B1：navigation 合同 ----------

    private const string ReportOperation =
        "{ \"id\": \"report\", \"title\": \"生成报告\", \"mutating\": false, \"reversible\": false, \"targetScoped\": false, \"timeoutSeconds\": 30 }";

    /// <summary>生成合法 manifest；navigation 为 null 时不写该字段（schema 2 可选、schema 1 禁止）。</summary>
    private static string Manifest(string id, string name = "硬件报告", int schema = 2, string? navigation = "NAV_DEFAULT")
    {
        if (navigation == "NAV_DEFAULT")
            navigation = "{ \"label\": \"硬件报告\", \"icon\": \"report\", \"pageId\": \"main\" }";
        var navField = navigation is null ? "" : ",\n          \"navigation\": " + navigation;
        return $$"""
            {
              "schemaVersion": {{schema}},
              "id": "{{id}}",
              "name": "{{name}}",
              "author": "Example Author",
              "version": "1.0.0",
              "protocolVersion": "1.0",
              "hostCompatibility": { "minInclusive": "0.90.0", "maxExclusive": "1.0.0" },
              "backend": { "entry": "backend/Example.Plugin.HardwareReport.exe", "architecture": "x64" },
              "permissions": [
                { "id": "hardware.read", "purpose": "生成硬件摘要" }
              ],
              "capabilities": { "continuous": false, "offlineAutonomous": false },
              "operations": [
                {{ReportOperation}}
              ]{{navField}}
            }
            """;
    }

    private static PluginManifest ParseManifest(string json) => PluginManifestParser.Parse(Encoding.UTF8.GetBytes(json));

    private static void RunManifestNavigationChecks()
    {
        Console.WriteLine("== B1 manifest schema 2 navigation ==");

        var basic = ParseManifest(Manifest("org.example.report"));
        Check("navigation 解析", basic.SchemaVersion == 2 && basic.Navigation is { Label: "硬件报告", Icon: "report", PageId: "main" });

        var defaultIcon = ParseManifest(Manifest("org.example.report", navigation: "{ \"label\": \"工具\", \"pageId\": \"main\" }"));
        Check("icon 缺省为 puzzle", defaultIcon.Navigation is { Icon: "puzzle" });

        var noNav = ParseManifest(Manifest("org.example.report", navigation: null));
        Check("schema 2 省略 navigation", noNav.Navigation is null);

        var longLabel = new string('硬', 48);
        Check("48 字符 label 合法",
            ParseManifest(Manifest("org.example.report", navigation: $$"""{ "label": "{{longLabel}}", "pageId": "main" }""")).Navigation!.Label.Length == 48);
        CheckReject("拒绝：label 超过 48 字符",
            Manifest("org.example.report", navigation: $$"""{ "label": "{{longLabel}}硬", "pageId": "main" }"""));
        CheckReject("拒绝：label 空白", Manifest("org.example.report", navigation: "{ \"label\": \"   \", \"pageId\": \"main\" }"));
        CheckReject("拒绝：label 含换行", Manifest("org.example.report", navigation: "{ \"label\": \"a\\nb\", \"pageId\": \"main\" }"));
        CheckReject("拒绝：label 含制表符", Manifest("org.example.report", navigation: "{ \"label\": \"a\\tb\", \"pageId\": \"main\" }"));
        CheckReject("拒绝：label 含双向控制符", Manifest("org.example.report", navigation: "{ \"label\": \"a\\u202Eb\", \"pageId\": \"main\" }"));
        CheckReject("拒绝：未知 icon", Manifest("org.example.report", navigation: "{ \"label\": \"x\", \"icon\": \"rocket\", \"pageId\": \"main\" }"));
        CheckReject("拒绝：icon 为路径", Manifest("org.example.report", navigation: "{ \"label\": \"x\", \"icon\": \"C:/icon.png\", \"pageId\": \"main\" }"));
        CheckReject("拒绝：pageId 非 main", Manifest("org.example.report", navigation: "{ \"label\": \"x\", \"pageId\": \"other\" }"));
        CheckReject("拒绝：navigation 未知字段", Manifest("org.example.report", navigation: "{ \"label\": \"x\", \"pageId\": \"main\", \"order\": 1 }"));
        CheckReject("拒绝：navigation 为数组", Manifest("org.example.report", navigation: "[{ \"label\": \"x\", \"pageId\": \"main\" }]"));
        CheckReject("拒绝：navigation 缺 pageId", Manifest("org.example.report", navigation: "{ \"label\": \"x\" }"));

        // 旧宿主只支持 schema 1：必须拒绝 schema 2 而不是静默忽略入口。
        _checks++;
        try
        {
            PluginManifestParser.Parse(Encoding.UTF8.GetBytes(Manifest("org.example.report")), "manifest.json", 1);
            _failures++;
            Console.WriteLine("  [失败] 旧宿主拒绝 schema 2：应拒绝但未拒绝。");
        }
        catch (PluginContractException) { }
        CheckReject("拒绝：schema 1 塞 navigation", Manifest("org.example.report", schema: 1));
        CheckReject("拒绝：schema 3", Manifest("org.example.report", schema: 3));

        var schema1 = ParseManifest(Manifest("org.example.report", schema: 1, navigation: null));
        Check("schema 1 回归", schema1.SchemaVersion == 1 && schema1.Navigation is null);

        Check("Tag 路由往返", PluginNavigationCatalog.ResolvePluginId(PluginNavigationCatalog.TagFor("org.example.report")) == "org.example.report");
        Check("非插件 Tag 不解析", PluginNavigationCatalog.ResolvePluginId("plugins") is null && PluginNavigationCatalog.ResolvePluginId(null) is null);
        Console.WriteLine("  B1 检查完成。");
    }

    // ---------- B2：宿主动态菜单目录 ----------

    private static void RunCatalogChecks(string pluginsRoot)
    {
        Console.WriteLine("== B2 动态菜单目录 ==");
        var manager = new PluginManagerService(pluginsRoot);
        var host = new Version(0, 90, 0);

        Check("导入 A", manager.Importer.Import(BuildPackage("org.example.alpha", Manifest("org.example.alpha")), pluginsRoot).Succeeded);
        Check("导入 B", manager.Importer.Import(BuildPackage("org.example.beta", Manifest("org.example.beta")), pluginsRoot).Succeeded);
        Check("导入 schema 1", manager.Importer.Import(BuildPackage("org.example.schema1", Manifest("org.example.schema1", schema: 1, navigation: null)), pluginsRoot).Succeeded);
        Check("导入 schema 2 无入口", manager.Importer.Import(BuildPackage("org.example.nonav", Manifest("org.example.nonav", navigation: null)), pluginsRoot).Succeeded);

        var catalog = PluginNavigationCatalog.Build(manager, host, out var error);
        Check("目录读取成功", error.Length == 0, error);
        Check("仅导航插件进入目录", catalog.Count == 2, $"实际 {catalog.Count}");
        Check("同名不同 ID 不合并", catalog.Select(e => e.PluginId).OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual(["org.example.alpha", "org.example.beta"]));
        Check("入口携带作者与状态", catalog.All(e => e.Author == "Example Author" && e.StatusText.Contains("未授权")));
        Check("入口携带图标", catalog.All(e => e.Icon == "report"));
        manager.Index.TryGet("org.example.beta", out var betaEntry, out _);
        var betaManifest = Path.Combine(betaEntry!.InstallDirectory, "manifest.json");
        var originalBytes = File.ReadAllBytes(betaManifest);
        using (var snapshot = PluginPackageIntegrity.VerifyAndLock(betaEntry))
        {
            Check("渲染快照等于校验字节", snapshot.ReadJson("manifest.json").SequenceEqual(originalBytes));
            try { File.WriteAllText(betaManifest, "changed"); Check("快照持有期阻止改写", false); }
            catch (IOException) { Check("快照持有期阻止改写", true); }
        }
        File.WriteAllText(betaManifest, Manifest("org.example.beta", navigation: "{ \"label\": \"合法但被改写的入口\", \"pageId\": \"main\" }"));
        Check("合法 JSON 改写也不进入菜单", PluginNavigationCatalog.Build(manager, host, out _).All(e => e.PluginId != betaEntry.Id));
        File.WriteAllBytes(betaManifest, originalBytes);

        // 包内 manifest 损坏：该插件不生成入口（无幽灵菜单），其余入口保留。
        var alpha = manager.Index.TryGet("org.example.alpha", out var alphaEntry, out _) ? alphaEntry! : null;
        File.WriteAllText(Path.Combine(alpha!.InstallDirectory, "manifest.json"), "{ not json");
        var afterCorrupt = PluginNavigationCatalog.Build(manager, host, out _);
        Check("损坏包不生成入口", afterCorrupt.Count == 1 && afterCorrupt[0].PluginId == "org.example.beta");

        // 导入失败：目录不变。
        var failed = manager.Importer.Import(BuildPackage("org.example.broken", Manifest("org.example.broken",
            navigation: "{ \"label\": \"坏包\", \"pageId\": \"other\" }")), pluginsRoot);
        Check("非法 pageId 导入失败", !failed.Succeeded);
        Check("失败导入无幽灵入口", PluginNavigationCatalog.Build(manager, host, out _).Count == 1);

        // 不兼容宿主版本：仍在目录，但状态标记不兼容（菜单存在不代表可运行）。
        var incompatible = PluginNavigationCatalog.Build(manager, new Version(0, 89, 0), out _);
        Check("不兼容版本标记", incompatible.Count == 1 && incompatible[0].StatusText.Contains("不兼容"));

        // 索引损坏：返回空与错误，不抛异常、不误删。
        var indexFile = Path.Combine(pluginsRoot, "index.json");
        File.WriteAllText(indexFile, "{ broken");
        var brokenIndex = PluginNavigationCatalog.Build(manager, host, out var brokenError);
        Check("索引损坏返回空与错误", brokenIndex.Count == 0 && brokenError.Length > 0);
        File.Delete(indexFile);
        Console.WriteLine("  B2 检查完成。");
    }

    // ---------- B4：升级 / 卸载 / 状态与菜单刷新 ----------

    private static void RunLifecycleChecks(string root)
    {
        Console.WriteLine("== B4 升级/卸载与菜单刷新 ==");
        var pluginsRoot = Path.Combine(root, "Plugins");
        Directory.CreateDirectory(pluginsRoot);
        var manager = new PluginManagerService(pluginsRoot);
        var host = new Version(0, 90, 0);

        // 变更事件：导入成功触发菜单刷新；失败导入不触发（无幽灵菜单）。
        var changes = 0;
        manager.Changed += () => changes++;
        var first = manager.Importer.Import(BuildPackage("org.example.up", Manifest("org.example.up", "旧名",
            navigation: "{ \"label\": \"旧入口\", \"pageId\": \"main\" }")), pluginsRoot);
        Check("导入成功", first.Succeeded, first.Error);
        manager.NotifyChanged();
        Check("导入后菜单刷新一次", changes == 1);
        Check("入口显示旧标签", PluginNavigationCatalog.Build(manager, host, out _).Single().Label == "旧入口");

        // 升级（同 ID 不同哈希/标签）：旧授权失效，入口刷新为新标签与新版本。
        var checklist = PluginTrust.AuthorizationChecklist(first.Entry!, first.Entry!.PackageSha256, signed: false);
        Check("授权旧版", manager.TryAuthorize("org.example.up", checklist.ToList(), out _, out _));
        var beforeUpgrade = changes;
        var upgraded = manager.Importer.Import(BuildPackage("org.example.up", Manifest("org.example.up", "新名",
            navigation: "{ \"label\": \"新入口\", \"icon\": \"tools\", \"pageId\": \"main\" }")), pluginsRoot);
        Check("升级导入成功", upgraded.Succeeded, upgraded.Error);
        Check("升级后为未授权", upgraded.Entry!.State == PluginPackageState.ImportedDisabled);
        manager.NotifyChanged();
        var afterUpgrade = PluginNavigationCatalog.Build(manager, host, out _).Single();
        Check("入口刷新为新标签/图标", afterUpgrade.Label == "新入口" && afterUpgrade.Icon == "tools");
        Check("升级不复用旧授权", afterUpgrade.StatusText.Contains("未授权") && changes == beforeUpgrade + 1);

        // 替换失败（非法 pageId）：保留旧入口，不显示新版本成功。
        var badReplace = manager.Importer.Import(BuildPackage("org.example.up", Manifest("org.example.up", "坏名",
            navigation: "{ \"label\": \"坏入口\", \"pageId\": \"other\" }")), pluginsRoot);
        Check("替换失败", !badReplace.Succeeded);
        var afterFailedReplace = PluginNavigationCatalog.Build(manager, host, out _).Single();
        Check("失败替换保留旧入口", afterFailedReplace.Label == "新入口" && afterFailedReplace.Version == upgraded.Entry.Version);

        // 授权成功 → 状态更新为已启用（菜单存在不代表运行）。
        var upChecklist = PluginTrust.AuthorizationChecklist(upgraded.Entry!, upgraded.Entry!.PackageSha256, signed: false);
        Check("授权新版", manager.TryAuthorize("org.example.up", upChecklist.ToList(), out _, out _));
        Check("授权后入口状态已启用",
            PluginNavigationCatalog.Build(manager, host, out _).Single().StatusText.Contains("已启用"));

        // 待恢复状态：入口保留但显示待恢复（不隐藏恢复义务）。
        manager.Index.TryUpsert(upgraded.Entry! with { State = PluginPackageState.PendingRestore }, out _);
        Check("待恢复入口保留并标注", PluginNavigationCatalog.Build(manager, host, out _).Single().StatusText.Contains("待恢复"));
        manager.Index.TryUpsert(upgraded.Entry! with { State = PluginPackageState.Authorized }, out _);

        // 卸载：入口被移除；同名不同 ID 的另一插件入口不受影响。
        Check("导入第二个同名插件", manager.Importer.Import(BuildPackage("org.example.other",
            Manifest("org.example.other", "新名", navigation: "{ \"label\": \"新入口\", \"pageId\": \"main\" }")), pluginsRoot).Succeeded);
        manager.NotifyChanged();
        Check("同名入口并存", PluginNavigationCatalog.Build(manager, host, out _).Count == 2);
        Check("卸载成功", manager.TryUninstall("org.example.up", out _));
        var afterUninstall = PluginNavigationCatalog.Build(manager, host, out _);
        Check("卸载后入口移除", afterUninstall.Count == 1 && afterUninstall[0].PluginId == "org.example.other");

        // 卸载失败（待恢复）：保留入口与待恢复证据，不冒充卸载成功。
        var other = manager.Index.TryGet("org.example.other", out var otherEntry, out _) ? otherEntry! : null;
        manager.Index.TryUpsert(other! with { State = PluginPackageState.PendingRestore }, out _);
        Check("待恢复阻止卸载", !manager.TryUninstall("org.example.other", out _));
        var afterFailedUninstall = PluginNavigationCatalog.Build(manager, host, out _);
        Check("卸载失败保留入口", afterFailedUninstall.Count == 1 && afterFailedUninstall[0].StatusText.Contains("待恢复"));

        Console.WriteLine("  B4 检查完成。");
    }

    // ---------- 夹具 ----------

    private static string BuildPackage(string id, string manifestJson)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nav-pkg-{Guid.NewGuid():N}.dnfdplugin");
        CreatedPackages.Add(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        void Write(string entryName, string content)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        Write("manifest.json", manifestJson);
        Write("ui.json", """
            {
              "schemaVersion": 1,
              "controls": [
                { "id": "title", "type": "text", "text": "硬件报告" },
                { "id": "run", "type": "button", "label": "生成报告", "operationId": "report" }
              ]
            }
            """);
        Write("README.md", "# 示例");
        Write("LICENSE.txt", "示例许可。");
        Write("backend/Example.Plugin.HardwareReport.exe", "MZ placeholder");
        return path;
    }
}
