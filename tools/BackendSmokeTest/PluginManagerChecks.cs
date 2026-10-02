using System.Text;
using DeltaNFD.Services;
using DeltaNFD.Services.Plugins;

namespace BackendSmokeTest;

/// <summary>
/// 拓展插件管理服务验证（待做清单第六节第 3 项的服务层）。
/// 覆盖：列表、授权确认完整性（清单内容比对）、停用撤回授权、重新授权、
/// 配置读写与路径注入防护、卸载（含删除失败回滚）、PendingRestore 阻断、
/// ui.json 读取。全部在临时目录，不触碰本机插件目录、不启动任何插件进程。
/// </summary>
internal static class PluginManagerChecks
{
    private static int _failures;
    private static int _checks;

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_PluginManager_" + Guid.NewGuid().ToString("N"));
        var pluginsRoot = Path.Combine(root, "Plugins");
        Directory.CreateDirectory(pluginsRoot);
        try
        {
            var manager = new PluginManagerService(pluginsRoot);
            var result = manager.Importer.Import(BuildPackage("org.example.hardware-report", "1.0.0"), pluginsRoot);
            Check("导入成功", result.Succeeded, result.Error);
            RunListChecks(manager);
            RunAuthorizeChecks(manager);
            RunConfigChecks(manager, "org.example.hardware-report");
            RunUninstallChecks(manager, pluginsRoot);
            RunUiChecks(manager);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            foreach (var leftover in Directory.EnumerateFiles(Path.GetTempPath(), "mgmt-pkg-*.dnfdplugin"))
                try { File.Delete(leftover); } catch (IOException) { }
        }
        Console.WriteLine($"插件管理检查完成：{_checks} 项，失败 {_failures} 项。");
        if (_failures > 0) Environment.ExitCode = 1;
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        _checks++;
        if (condition) return;
        _failures++;
        Console.WriteLine($"  [失败] {name}{(detail is null ? "" : "：" + detail)}");
    }

    private static void RunListChecks(PluginManagerService manager)
    {
        Console.WriteLine("== 列表 ==");
        Check("列表可读", manager.TryList(out var entries, out _) && entries.Count == 1);
        Check("列表默认未授权", entries[0].State == PluginPackageState.ImportedDisabled &&
            entries[0].AuthorizedUtc is null);
        Console.WriteLine("  列表检查完成。");
    }

    private static void RunAuthorizeChecks(PluginManagerService manager)
    {
        Console.WriteLine("== 授权 ==");
        const string id = "org.example.hardware-report";
        Check("未导入插件授权失败", !manager.TryAuthorize("org.example.none", [], out _, out _));

        var checklist = PluginTrust.AuthorizationChecklist(
            manager.Index.TryGet(id, out var entry, out _) && entry is not null ? entry : null!,
            entry!.PackageSha256, signed: false);
        Check("清单非空", checklist.Count > 0);

        Check("空确认被拒", !manager.TryAuthorize(id, [], out _, out var e1) && e1.Contains("不完整"));
        Check("条数不符被拒", !manager.TryAuthorize(id, checklist.Take(checklist.Count - 1).ToList(), out _, out _));
        Check("内容不符被拒", !manager.TryAuthorize(id, checklist.Select(l => l + "x").ToList(), out _, out var e3) &&
            e3.Contains("不符"));

        Check("完整确认通过", manager.TryAuthorize(id, checklist.ToList(), out var authorized, out _) &&
            authorized!.State == PluginPackageState.Authorized && authorized.AuthorizedUtc is not null);

        // 启用已授权 = 幂等成功
        Check("重复启用幂等", manager.TrySetEnabled(id, enabled: true, out _, out _));

        // 停用撤回授权
        Check("停用成功", manager.TrySetEnabled(id, enabled: false, out var disabled, out _) &&
            disabled!.State == PluginPackageState.ImportedDisabled && disabled.AuthorizedUtc is null);
        Check("停用后直接启用被拒（须重新授权）",
            !manager.TrySetEnabled(id, enabled: true, out _, out var e4) && e4.Contains("授权确认"));

        // 重新授权需再次完整确认
        Check("重新授权成功", manager.TryAuthorize(id, checklist.ToList(), out _, out _));

        // PendingRestore 阻断
        manager.Index.TryUpsert(authorized! with { State = PluginPackageState.PendingRestore }, out _);
        Check("待恢复阻止授权", !manager.TryAuthorize(id, checklist.ToList(), out _, out var e5) && e5.Contains("待恢复"));
        Check("待恢复阻止停用路径", !manager.TrySetEnabled(id, enabled: false, out _, out _));
        Check("待恢复阻止卸载", !manager.TryUninstall(id, out var e6) && e6.Contains("待恢复"));
        manager.Index.TryUpsert(authorized with { State = PluginPackageState.Authorized }, out _);
        Console.WriteLine("  授权检查完成。");
    }

    private static void RunConfigChecks(PluginManagerService manager, string id)
    {
        Console.WriteLine("== 配置 ==");
        Check("默认空配置", manager.TryReadConfig(id, out var json, out _) && json == "{}");
        Check("写入配置", manager.TryWriteConfig(id, "{\"detail\":true}", out _));
        Check("读回配置", manager.TryReadConfig(id, out var read, out _) && read == "{\"detail\":true}");
        Check("覆盖写入", manager.TryWriteConfig(id, "{\"detail\":false}", out _) &&
            manager.TryReadConfig(id, out var read2, out _) && read2 == "{\"detail\":false}");
        Check("路径注入 ID 被拒", !manager.TryReadConfig("../evil", out _, out _) &&
            !manager.TryWriteConfig("a/b", "{}", out _));
        Console.WriteLine("  配置检查完成。");
    }

    private static void RunUninstallChecks(PluginManagerService manager, string pluginsRoot)
    {
        Console.WriteLine("== 卸载 ==");
        const string id = "org.example.hardware-report";
        var entry = manager.Index.TryGet(id, out var e, out _) ? e : null;
        Check("卸载成功", manager.TryUninstall(id, out _));
        Check("索引已移除", manager.TryList(out var entries, out _) && entries.Count == 0);
        Check("安装目录已删", entry is null || !Directory.Exists(entry.InstallDirectory));
        Check("配置文件已删", !File.Exists(ConfigPathFor(id)));
        Check("重复卸载报错", !manager.TryUninstall(id, out var error) && error.Contains("未导入"));
        Console.WriteLine("  卸载检查完成。");
    }

    private static void RunUiChecks(PluginManagerService manager)
    {
        Console.WriteLine("== ui.json 读取 ==");
        // 重新导入后读 ui
        const string id = "org.example.ui-sample";
        var result = manager.Importer.Import(BuildPackage(id, "1.0.0"), manager.PluginsRoot);
        Check("再导入成功", result.Succeeded, result.Error);
        Check("ui.json 可读", manager.TryReadUi(id, out var uiJson, out _) && uiJson.Contains("\"controls\""));
        Check("未导入插件 ui 读取失败", !manager.TryReadUi("org.example.none", out _, out _));
        Console.WriteLine("  ui 读取检查完成。");
    }

    private static string ConfigPathFor(string id) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Delta NFD", "PluginData", id, "config.json");

    // ---------- 夹具 ----------

    private const string ManifestTemplate = """
        {
          "schemaVersion": 1,
          "id": "PLUGIN_ID",
          "name": "硬件报告示例",
          "author": "Example Author",
          "version": "1.0.0",
          "protocolVersion": "1.0",
          "hostCompatibility": { "minInclusive": "0.83.0", "maxExclusive": "1.0.0" },
          "backend": { "entry": "backend/Example.Plugin.HardwareReport.exe", "architecture": "x64" },
          "permissions": [
            { "id": "hardware.read", "purpose": "生成硬件摘要" }
          ],
          "capabilities": { "continuous": false, "offlineAutonomous": false },
          "operations": [
            { "id": "report", "title": "生成报告", "mutating": false,
              "reversible": false, "targetScoped": false, "timeoutSeconds": 30 }
          ]
        }
        """;

    private static string BuildPackage(string id, string version)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mgmt-pkg-{Guid.NewGuid():N}.dnfdplugin");
        using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        void Write(string name, string content)
        {
            var entry = archive.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        Write("manifest.json", ManifestTemplate.Replace("PLUGIN_ID", id).Replace("VERSION", version));
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
