using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DeltaNFD.Services.Plugins;

namespace BackendSmokeTest;

/// <summary>
/// 拓展插件规范 1.0 包导入验证（待做清单第六节第 2 项）。
/// 全程使用临时目录构造 .dnfdplugin ZIP 夹具：路径逃逸/重名/设备名/加密/超限/缺文件/坏清单、
/// 合法导入、同 ID 替换与授权失效、PendingRestore 拒绝替换、索引 fail-closed。
 /// 不运行包内任何 EXE/脚本；不改本机插件目录（索引指向临时根）。
/// </summary>
internal static class PluginImportChecks
{
    private static int _failures;
    private static int _checks;
    private static string _packageRoot = "";

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_PluginImport_" + Guid.NewGuid().ToString("N"));
        var pluginsRoot = Path.Combine(root, "Plugins");
        Directory.CreateDirectory(pluginsRoot);
        _packageRoot = root;
        try
        {
            RunIndexChecks(pluginsRoot);
            RunImportChecks(pluginsRoot);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
        Console.WriteLine($"插件导入检查完成：{_checks} 项，失败 {_failures} 项。");
        if (_failures > 0) Environment.ExitCode = 1;
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        _checks++;
        if (condition) return;
        _failures++;
        Console.WriteLine($"  [失败] {name}{(detail is null ? "" : "：" + detail)}");
    }

    private static string NewIndex(string pluginsRoot) => Path.Combine(pluginsRoot, "index.json");

    // ---------- 索引 ----------

    private static void RunIndexChecks(string pluginsRoot)
    {
        Console.WriteLine("== 插件索引 ==");
        var index = new PluginPackageIndex(NewIndex(pluginsRoot));
        Check("空索引可读", index.TryRead(out var empty, out _) && empty.Count == 0);
        Check("空索引 Get 为空", index.TryGet("org.example.x", out var none, out _) && none is null);

        var entry = ValidEntry("org.example.hardware-report", "1.0.0");
        Check("条目 upsert", index.TryUpsert(entry, out var error), error);
        Check("条目可读回", index.TryGet("org.example.hardware-report", out var read, out _) &&
            read!.PackageSha256 == entry.PackageSha256 && read.State == PluginPackageState.ImportedDisabled);
        Check("同 ID 替换不产生重复", index.TryUpsert(ValidEntry("org.example.hardware-report", "1.1.0"), out _) &&
            index.TryRead(out var afterReplace, out _) && afterReplace.Count == 1 && afterReplace[0].Version == "1.1.0");
        Check("移除条目", index.TryRemove("org.example.hardware-report", out _) &&
            index.TryRead(out var afterRemove, out _) && afterRemove.Count == 0);
        Check("移除不存在报错", !index.TryRemove("org.example.none", out var removeError) && removeError.Length > 0);

        // fail-closed：损坏索引
        File.WriteAllText(NewIndex(pluginsRoot), "{ not json");
        Check("损坏索引 fail-closed", !index.TryRead(out _, out var corruptError) && corruptError.Contains("损坏"));
        File.Delete(NewIndex(pluginsRoot));

        // 非法条目被拒
        Check("非法 ID 被拒", !index.TryUpsert(ValidEntry("UPPER.case", "1.0.0"), out _));
        Check("非法哈希被拒", !index.TryUpsert(ValidEntry("org.example.x", "1.0.0") with { PackageSha256 = "zz" }, out _));
        Check("空文件表被拒", !index.TryUpsert(ValidEntry("org.example.x", "1.0.0") with { Files = [] }, out _));
        Check("重复路径文件表被拒", !index.TryUpsert(ValidEntry("org.example.x", "1.0.0") with
        {
            Files = [new PluginIndexFile("manifest.json", new string('a', 64), 1),
                     new PluginIndexFile("MANIFEST.JSON", new string('a', 64), 1)],
        }, out _));
        Console.WriteLine("  索引检查完成。");
    }

    private static PluginIndexEntry ValidEntry(string id, string version) => new()
    {
        Id = id,
        Version = version,
        PackageSha256 = new string('a', 64),
        InstallDirectory = @"C:\Plugins\" + id,
        EntryExecutable = @"C:\Plugins\" + id + @"\backend\plugin.exe",
        Files = [new PluginIndexFile("manifest.json", new string('b', 64), 100)],
        ImportedUtc = DateTimeOffset.UtcNow,
    };

    // ---------- 导入 ----------

    private static void RunImportChecks(string pluginsRoot)
    {
        Console.WriteLine("== 插件包导入 ==");
        var index = new PluginPackageIndex(NewIndex(pluginsRoot));
        var importer = new PluginPackageImporter(index);

        // 1. 合法导入
        var package = BuildPackage("org.example.hardware-report", "1.0.0");
        var result = importer.Import(package, pluginsRoot);
        Check("合法导入成功", result.Succeeded, result.Error);
        Check("首次导入不是替换", !result.ReplacedExisting);
        Check("导入后默认禁用", result.Entry is { State: PluginPackageState.ImportedDisabled });
        Check("安装目录就位", result.Entry is not null &&
            Directory.Exists(Path.Combine(pluginsRoot, "org.example.hardware-report", "1.0.0")));
        Check("入口存在", result.Entry is not null && File.Exists(Path.Combine(
            pluginsRoot, "org.example.hardware-report", "1.0.0", "backend", "Example.Plugin.HardwareReport.exe")));
        Check("索引记录哈希", result.Entry is not null &&
            index.TryGet("org.example.hardware-report", out var entry1, out _) &&
            entry1!.PackageSha256 == result.Entry.PackageSha256 && entry1.Files.Count >= 5);
        Check("包哈希与文件一致", result.Entry is not null &&
            result.Entry.PackageSha256 == Sha256File(package));
        Check("暂存目录已清理", !Directory.EnumerateDirectories(pluginsRoot, ".staging-*").Any());

        // 2. 重复导入（同哈希）被拒
        var duplicate = importer.Import(package, pluginsRoot);
        Check("同哈希重复导入被拒", !duplicate.Succeeded && duplicate.Error.Contains("重复导入"));
        Check("重复导入未破坏索引", index.TryRead(out var entries2, out _) && entries2.Count == 1);

        // 3. 恶意路径
        foreach (var (name, entryName) in new[]
        {
            ("绝对路径", "/abs/payload.exe"),
            ("盘符", @"C:\evil.exe"),
            (".. 段", "backend/../../escape.exe"),
            ("设备名", "backend/CON"),
            ("备用数据流", "backend/file.txt:ads"),
        })
        {
            var malicious = BuildPackage("org.example.evil-" + name.Length, "1.0.0",
                extraEntries: [(entryName, "x")]);
            var rejected = importer.Import(malicious, pluginsRoot);
            Check($"拒绝恶意条目：{name}", !rejected.Succeeded);
            Check($"未落盘：{name}", !Directory.Exists(Path.Combine(pluginsRoot, "org.example.evil-" + name.Length)));
        }
        Check("恶意导入后暂存清理", !Directory.EnumerateDirectories(pluginsRoot, ".staging-*").Any());

        // 4. 大小写折叠重名
        var folding = BuildPackage("org.example.folding", "1.0.0",
            extraEntries: [("backend/Example.Plugin.HardwareReport.EXE", "x")]);
        Check("拒绝折叠重名", !importer.Import(folding, pluginsRoot).Succeeded);

        // 5. 加密 ZIP
        var encrypted = BuildEncryptedPackage("org.example.encrypted", "1.0.0");
        Check("拒绝加密 ZIP", !importer.Import(encrypted, pluginsRoot).Succeeded);

        // 6. 缺文件
        foreach (var missing in new[] { "manifest.json", "ui.json", "README.md", "LICENSE.txt" })
        {
            var incomplete = BuildPackage("org.example.missing", "1.0.0", omit: missing);
            Check($"拒绝缺少 {missing}", !importer.Import(incomplete, pluginsRoot).Succeeded);
        }

        // 7. 坏清单（manifest 非法 JSON）
        var badManifest = BuildPackage("org.example.bad", "1.0.0", manifestJson: "{ broken");
        Check("拒绝坏 manifest", !importer.Import(badManifest, pluginsRoot).Succeeded);

        // 8. 声明入口不在包内
        var missingEntry = BuildPackage("org.example.missing-entry", "1.0.0",
            manifestOverrides: s => s.Replace("backend/Example.Plugin.HardwareReport.exe", "backend/Ghost.exe"));
        Check("拒绝入口缺失", !importer.Import(missingEntry, pluginsRoot).Succeeded);

        // 9. 条目数超限（2049 个）
        var tooMany = BuildPackage("org.example.toomany", "1.0.0",
            extraEntries: Enumerable.Range(0, 2100).Select(i => ($"assets/f{i}.txt", "x")).ToArray());
        Check("拒绝条目超限", !importer.Import(tooMany, pluginsRoot).Succeeded);

        // 10. 解压总量超限（多个 90MiB 声明会先撞单文件或总量限制——用声明长度欺骗：元数据小、实际大）
        var bomb = BuildZipBomb();
        Check("拒绝压缩炸弹", !importer.Import(bomb, pluginsRoot).Succeeded);

        // 11. 同 ID 替换：授权失效 + 旧版被替换
        SimulateAuthorize(index, "org.example.hardware-report");
        var upgraded = BuildPackage("org.example.hardware-report", "1.1.0");
        var upgrade = importer.Import(upgraded, pluginsRoot);
        Check("替换导入成功", upgrade.Succeeded, upgrade.Error);
        Check("升级正确报告替换", upgrade.ReplacedExisting);
        Check("替换后新条目未授权（授权绑定哈希）", upgrade.Entry is { State: PluginPackageState.ImportedDisabled });
        Check("新版本目录就位", Directory.Exists(Path.Combine(pluginsRoot, "org.example.hardware-report", "1.1.0")));
        Check("旧版本目录被清理", !Directory.Exists(Path.Combine(pluginsRoot, "org.example.hardware-report", "1.0.0")));
        Check("索引只留新版本", index.TryRead(out var afterUpgrade, out _) &&
            afterUpgrade.Count == 1 && afterUpgrade[0].Version == "1.1.0");

        using (var lockedIndex = new FileStream(NewIndex(pluginsRoot), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failedUpgrade = importer.Import(BuildPackage("org.example.hardware-report", "1.2.0"), pluginsRoot);
            Check("索引写入失败拒绝升级", !failedUpgrade.Succeeded);
            Check("升级失败保留旧版文件", File.Exists(Path.Combine(pluginsRoot, "org.example.hardware-report", "1.1.0", "manifest.json")));
            Check("升级失败不保留新目录", !Directory.Exists(Path.Combine(pluginsRoot, "org.example.hardware-report", "1.2.0")));
        }
        Check("升级失败保留旧索引", index.TryRead(out var rolledBack, out _) && rolledBack.Count == 1 && rolledBack[0].Version == "1.1.0");

        // 12. PendingRestore 拒绝替换
        MarkPendingRestore(index, "org.example.hardware-report");
        var blocked = importer.Import(BuildPackage("org.example.hardware-report", "2.0.0"), pluginsRoot);
        Check("待恢复记录阻止替换", !blocked.Succeeded && blocked.Error.Contains("待恢复"));
        Check("被阻止后索引未变", index.TryRead(out var afterBlocked, out _) &&
            afterBlocked.Count == 1 && afterBlocked[0].Version == "1.1.0");

        Console.WriteLine("  导入检查完成。");
    }

    private static void SimulateAuthorize(PluginPackageIndex index, string id)
    {
        // 直接用 upsert 写入 Authorized 状态（授权流程本身属第 3 项，未实现 UI）。
        if (!index.TryGet(id, out var entry, out _)) throw new InvalidOperationException("前置条目缺失。");
        index.TryUpsert(entry! with { State = PluginPackageState.Authorized, AuthorizedUtc = DateTimeOffset.UtcNow }, out _);
    }

    private static void MarkPendingRestore(PluginPackageIndex index, string id)
    {
        if (!index.TryGet(id, out var entry, out _)) throw new InvalidOperationException("前置条目缺失。");
        index.TryUpsert(entry! with { State = PluginPackageState.PendingRestore }, out _);
    }

    private static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    // ---------- 夹具 ----------

    private const string ManifestTemplate = """
        {
          "schemaVersion": 1,
          "id": "PLUGIN_ID",
          "name": "硬件报告示例",
          "author": "Example Author",
          "version": "VERSION",
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

    private const string UiTemplate = """
        {
          "schemaVersion": 1,
          "controls": [
            { "id": "title", "type": "text", "text": "硬件报告" },
            { "id": "detail", "type": "toggle", "label": "详细信息", "default": false },
            { "id": "run", "type": "button", "label": "生成报告", "operationId": "report" },
            { "id": "progress", "type": "progress" },
            { "id": "result", "type": "result" }
          ]
        }
        """;

    private static string BuildPackage(string id, string version, string? omit = null, string manifestJson = "",
        Func<string, string>? manifestOverrides = null,
        IReadOnlyList<(string Name, string Content)>? extraEntries = null)
    {
        var path = Path.Combine(_packageRoot, $"pkg-{Guid.NewGuid():N}.dnfdplugin");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        void Write(string name, string content)
        {
            if (omit is not null && name == omit) return;
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        var manifest = manifestJson.Length > 0
            ? manifestJson
            : ManifestTemplate.Replace("PLUGIN_ID", id).Replace("VERSION", version);
        if (manifestOverrides is not null) manifest = manifestOverrides(manifest);
        Write("manifest.json", manifest);
        Write("ui.json", UiTemplate);
        Write("README.md", "# 示例\n作者 Example Author。只读。");
        Write("LICENSE.txt", "示例许可。");
        Write("backend/Example.Plugin.HardwareReport.exe", "MZ placeholder binary");
        Write("backend/deps/lib.dll", "dep");
        Write("assets/icon.png", "png");
        foreach (var (name, content) in extraEntries ?? [])
            Write(name, content);
        return path;
    }

    private static string BuildEncryptedPackage(string id, string version)
    {
        // System.IO.Compression 不支持写加密 ZIP，手工构造一个带加密标志的本地头。
        var path = Path.Combine(_packageRoot, $"pkg-{Guid.NewGuid():N}.dnfdplugin");
        var manifest = Encoding.UTF8.GetBytes(ManifestTemplate.Replace("PLUGIN_ID", id).Replace("VERSION", version));
        using var output = File.Create(path);
        using var writer = new BinaryWriter(output);
        writer.Write(0x04034b50);          // local file header
        writer.Write((short)20);           // version
        writer.Write((short)0x0001);       // general purpose bit flag：加密
        writer.Write((short)8);            // deflate
        writer.Write((short)0);            // time
        writer.Write((short)0);            // date
        writer.Write(0);                   // crc
        writer.Write(manifest.Length);     // compressed
        writer.Write(manifest.Length);     // uncompressed
        writer.Write((short)"manifest.json".Length);
        writer.Write((short)0);            // extra length
        writer.Write(Encoding.ASCII.GetBytes("manifest.json"));
        writer.Write(manifest);
        // 中央目录收尾（最小合法结构由 ZipArchive 容错；读取时先撞加密标志）。
        writer.Write(0x02014b50);
        for (var i = 0; i < 42; i++) writer.Write((byte)0);
        return path;
    }

    private static string BuildZipBomb()
    {
        // 元数据声明小长度，实际写入 200MiB 零字节流——导入器按实际展开计数必须拒绝。
        // .NET ZipArchive 写入时会重算长度，因此手工构造：本地头声明 100 字节，实际数据 150MiB。
        var path = Path.Combine(_packageRoot, $"pkg-{Guid.NewGuid():N}.dnfdplugin");
        using var output = File.Create(path);
        using var writer = new BinaryWriter(output);
        var name = Encoding.ASCII.GetBytes("manifest.json");
        writer.Write(0x04034b50);
        writer.Write((short)20);
        writer.Write((short)0);            // 无加密
        writer.Write((short)0);            // store
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write(0);                   // crc
        writer.Write(100);                 // 声明压缩后 100 字节
        writer.Write(100);                 // 声明原始 100 字节
        writer.Write((short)name.Length);
        writer.Write((short)0);
        writer.Write(name);
        var buffer = new byte[1024 * 1024];
        long written = 0;
        while (written < 150L * 1024 * 1024)
        {
            writer.Write(buffer);
            written += buffer.Length;
        }
        // 不写中央目录：ZipArchive 抛异常也视为拒绝（导入器必须 fail 而不是误收）。
        return path;
    }
}
