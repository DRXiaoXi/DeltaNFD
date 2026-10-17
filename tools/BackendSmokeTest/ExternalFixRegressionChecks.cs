using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeltaNFD.Services;
using Microsoft.Win32;

internal static class ExternalFixRegressionChecks
{
    internal static async Task RunAsync()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-external-fix-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        int count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
        await AceCleanupScopeChecks.RunAsync();
        Check(!AceRegistryPolicy.IsCandidatePath(@"SOFTWARE\AppDataLow"), "共享根永不进入删除候选");
        Check(!AceRegistryPolicy.IsCandidatePath(@"SOFTWARE\AppDataLow\OtherVendor\ACE-Preferences"), "其他厂商同名键排除");
        Check(!AceRegistryPolicy.IsCandidatePath(@"SOFTWARE\AppDataLow\A_B\ACE")
            && !AceRegistryPolicy.IsCandidatePath(@"SOFTWARE\AppDataLow\A\B\ACE"), "旧备份碰撞路径排除");
        Check(AceRegistryPolicy.MatchesInstallRoot(@"C:\Program Files\AntiCheatExpert\", [@"C:\Program Files\AntiCheatExpert"]), "已知安装目录匹配");
        Check(!AceRegistryPolicy.MatchesInstallRoot(@"C:\Program Files\AntiCheatExpertExtra", [@"C:\Program Files\AntiCheatExpert"]), "目录前缀不当作归属证明");
        Check(!AceRegistryPolicy.MatchesInstallRoot(@"C:\Program Files\AntiCheatExpert\..\Other", [@"C:\Program Files\AntiCheatExpert"]), "路径规范化后仍须精确匹配");
        var exportPaths = new List<string>();
        Task<(int, string, string)> Export(string _, string args, TimeSpan __)
        {
            var m = Regex.Match(args, "^export \"([^\"]+)\" \"([^\"]+)\" /y$");
            if (!m.Success) throw new Exception("unexpected export arguments");
            exportPaths.Add(m.Groups[2].Value);
            File.WriteAllText(m.Groups[2].Value, "Windows Registry Editor Version 5.00\r\n\r\n[" + m.Groups[1].Value + "]\r\n\"InstallDir\"=\"fixture\"\r\n", Encoding.Unicode);
            return Task.FromResult((0, "", ""));
        }
        var backupRoot = Path.Combine(root, "backup-fixtures");
        const string keyA = @"SOFTWARE\AppDataLow\Software\ACE-A:B";
        const string keyB = @"SOFTWARE\AppDataLow\Software\ACE-A_B";
        var first = await AceRegistryBackups.ExportAsync(backupRoot, RegistryHive.LocalMachine, keyA, Export);
        var second = await AceRegistryBackups.ExportAsync(backupRoot, RegistryHive.LocalMachine, keyB, Export);
        var again = await AceRegistryBackups.ExportAsync(backupRoot, RegistryHive.LocalMachine, keyA, Export);
        Check(first.Success && second.Success && again.Success && exportPaths.Distinct().Count() == 3, "同秒/同名/重复键使用独立备份文件");
        using (var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(exportPaths[0])!, "entry.json"))))
        {
            Check(index.RootElement.GetProperty("Path").GetString() == keyA, "备份索引保留原始键路径");
            Check(index.RootElement.GetProperty("Sha256").GetString() == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exportPaths[0]))).ToLowerInvariant(), "备份证据哈希对应导出文件");
        }
        var absent = await AceRegistryBackups.ExportAsync(backupRoot, RegistryHive.LocalMachine, keyA,
            (_, _, _) => Task.FromResult((0, "", "")));
        Check(!absent.Success, "退出码成功但无备份文件仍拒绝删除");
        var wrong = await AceRegistryBackups.ExportAsync(backupRoot, RegistryHive.LocalMachine, keyA, async (f, a, t) =>
        { await Export(f, a, t); File.WriteAllText(exportPaths[^1], "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\Wrong]\r\n", Encoding.Unicode); return (0, "", ""); });
        Check(!wrong.Success, "备份内容路径不符拒绝删除");
        Check(!(await AceRegistryBackups.ExportAsync(backupRoot, RegistryHive.LocalMachine, @"SOFTWARE\AppDataLow", Export)).Success, "共享根不能备份后伪装为允许删除");

        var thumbnails = Path.Combine(root, "thumbnail-fixture"); Directory.CreateDirectory(thumbnails);
        File.WriteAllBytes(Path.Combine(thumbnails, "thumbcache_32.db"), new byte[128]);
        File.WriteAllText(Path.Combine(thumbnails, "iconcache_32.db"), "keep");
        Directory.CreateDirectory(Path.Combine(thumbnails, "nested"));
        File.WriteAllText(Path.Combine(thumbnails, "nested", "thumbcache_other.db"), "keep");
        typeof(CleanupService).GetMethod("CleanMatchingFiles", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [thumbnails, "thumbcache_*.db"]);
        Check(!File.Exists(Path.Combine(thumbnails, "thumbcache_32.db")) && File.Exists(Path.Combine(thumbnails, "iconcache_32.db"))
            && File.Exists(Path.Combine(thumbnails, "nested", "thumbcache_other.db")), "缩略图只清顶层目标文件");

        var config = UpdateService.LoadConfig(null);
        var manualProxy = "https://gh-proxy.com/https://raw.githubusercontent.com/DRXiaoXi/DeltaNFD/main/update.json";
        config.ManifestUrls = [manualProxy, UpdateService.OfficialManifestUrl];
        var service = new UpdateService(config);
        Check(service.Config.ManifestUrls!.SequenceEqual(UpdateService.DefaultManifestUrls), "历史代理还原官方源并按 GitHub、自建服务器排序");
        Check(!UpdateService.IsAllowedUrl(manualProxy, config, out _), "旧加速主机不再允许访问");
        var seen = new List<string>();
        await UpdateService.FetchUpdateSourceAsync("https://gh-proxy.com/" + GitHubReleaseSource.ReleasesUrl, config, CancellationToken.None,
            (url, _, _) => { seen.Add(url); return Task.FromResult(seen.Count == 1 ? "[" + string.Join(",", Enumerable.Repeat("{}", 100)) + "]" : "[]"); });
        Check(seen.Count == 2 && seen.All(u => u.StartsWith(GitHubReleaseSource.ReleasesUrl)) && seen[1].EndsWith("&page=2"), "历史代理 Release 分页恢复官方直连");
        Console.WriteLine($"External fix regression checks: {count} passed; ACE scope checks also passed. All system mutation simulated or isolated.");
    }
}
