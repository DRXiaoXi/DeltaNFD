using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DeltaNFD.Services;

internal sealed class Vc14LegacyEnvironment : IVc14LegacyEnvironment
{
    private const string Arp = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private sealed record Value(string Name, int Kind, string Json);
    private sealed record Registration(int View, string Hash, IReadOnlyList<Value> Values);
    private sealed record Snapshot(string Arch, Vc14InfIdentity Identity, byte[] Inf, byte[]? Pnf, IReadOnlyList<Registration> Registrations, bool InfPresent = true)
    {
        public string Hash => Vc14LegacyPolicy.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { Arch, Identity, InfPresent, Inf = Vc14LegacyPolicy.Hash(Inf), Pnf = Pnf is null ? "" : Vc14LegacyPolicy.Hash(Pnf), Registrations })));
    }
    private Dictionary<string, Snapshot> _latest = [], _confirmed = [];
    private readonly string _backup = Path.Combine(Log.LogDirectory, "vc14-legacy-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
    public Vc14LegacyReport Detect()
    {
        var entries = new List<Vc14LegacyEntry>(); var issues = new List<string>(); var hashes = new List<string>();
        _latest = [];
        var coreIssue = "";
        try { _ = CaptureCore(); } catch (Exception ex) { coreIssue = ex.Message; }
        foreach (var arch in new[] { "x64", "x86" })
        {
            try
            {
                var snapshot = Read(arch);
                if (snapshot is null) continue;
                _latest.Add(arch, snapshot); hashes.Add(snapshot.Hash);
                var live = Vc14Catalog.Installed(snapshot.Identity.Minimum) || Vc14Catalog.Installed(snapshot.Identity.Additional);
                var ready = !live && coreIssue.Length == 0;
                entries.Add(new(arch, snapshot.Identity.Name, snapshot.Identity.Version, ready,
                    live ? "旧 INF 关联的 MSI 仍存在，请先执行 14.x 定向修复；仅处理旧包装不会卸载这些组件。"
                    : coreIssue.Length > 0 ? "先补回并核对 14.42，再处理旧包装：" + coreIssue
                    : "旧 INF 模板、登记及产品码已核验；原 MSI 已移除，当前 14.42 已确认。可备份后仅清理此包装和 INF/PNF。"));
            }
            catch (Exception ex) { issues.Add(arch + " 旧包装待核对：" + ex.Message); hashes.Add(arch + ":" + ex.Message); }
        }
        return new(Vc14LegacyPolicy.Hash(Encoding.UTF8.GetBytes(string.Join("\n", hashes))), entries, issues);
    }
    private static string InfPath(string arch) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "vcred14_" + arch + ".inf");
    private static byte[]? ReadFile(string path, int limit)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("旧文件父目录是重解析路径，未处理。");
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > limit) throw new IOException("旧文件为重解析点或大小异常，未处理。");
        return File.ReadAllBytes(path);
    }
    private static Registration? ReadRegistration(string arch, RegistryView view, Vc14InfIdentity? identity)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = hive.OpenSubKey(Arp + "\\vcredist14_" + arch);
        if (key is null) return null;
        if (identity is null) throw new IOException("旧登记仍在，但对应 INF 缺失；保留登记，可查看日志核对来源。");
        if (key.SubKeyCount != 0 || key.ValueCount > 32) throw new IOException("旧包装包含未知子项，未处理。");
        Vc14LegacyPolicy.ValidateRegistration(key.GetValue("DisplayName") as string ?? "", key.GetValue("DisplayVersion") as string ?? "",
            key.GetValue("Publisher") as string ?? "", key.GetValue("UninstallString") as string ?? "", arch, identity);
        var values = new List<Value>();
        foreach (var name in key.GetValueNames().Order(StringComparer.Ordinal))
        {
            if (name is not ("DisplayName" or "DisplayVersion" or "Publisher" or "UninstallString" or "DisplayIcon"
                or "NoModify" or "NoRepair" or "EstimatedSize" or "SystemComponent" or "HelpLink" or "URLInfoAbout"))
                throw new IOException("旧包装存在未知值“" + name + "”，已保留原值。");
            var kind = key.GetValueKind(name);
            var json = JsonSerializer.Serialize(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString or RegistryValueKind.DWord) || json.Length > 8192)
                throw new IOException("旧包装值类型或大小异常，未处理。");
            values.Add(new(name, (int)kind, json));
        }
        return new((int)view, Vc14LegacyPolicy.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))), values);
    }
    private static Snapshot? Read(string arch)
    {
        var inf = ReadFile(InfPath(arch), 65536);
        var infPresent = inf is not null;
        var pnf = ReadFile(Path.ChangeExtension(InfPath(arch), ".PNF"), 4194304);
        // Resume an interrupted metadata-only cleanup from its verified before-backup; never execute backup text.
        if (inf is null && Directory.Exists(Log.LogDirectory))
        {
            foreach (var directory in new DirectoryInfo(Log.LogDirectory).EnumerateDirectories("vc14-legacy-*").OrderByDescending(d => d.LastWriteTimeUtc).Take(20))
            {
                var candidate = ReadFile(Path.Combine(directory.FullName, "vcred14_" + arch + ".inf"), 65536);
                var manifest = ReadFile(Path.Combine(directory.FullName, "before.json"), 16777216);
                if (candidate is null || manifest is null) continue;
                using var json = JsonDocument.Parse(manifest);
                if (!json.RootElement.TryGetProperty(arch, out var saved)) continue;
                var savedInf = saved.GetProperty("Inf").GetBytesFromBase64();
                if (Vc14LegacyPolicy.Hash(savedInf) != Vc14LegacyPolicy.Hash(candidate)) continue;
                var savedPnf = saved.GetProperty("Pnf");
                if (pnf is not null && (savedPnf.ValueKind != JsonValueKind.String || Vc14LegacyPolicy.Hash(savedPnf.GetBytesFromBase64()) != Vc14LegacyPolicy.Hash(pnf))) continue;
                _ = Vc14LegacyPolicy.Validate(Encoding.UTF8.GetString(candidate).TrimStart('\uFEFF'), arch);
                inf = candidate; break;
            }
        }
        var identity = inf is null ? null : Vc14LegacyPolicy.Validate(Encoding.UTF8.GetString(inf).TrimStart('\uFEFF'), arch);
        var registrations = new List<Registration>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            if (ReadRegistration(arch, view, identity) is { } registration) registrations.Add(registration);
        if (inf is null)
        {
            if (ReadFile(Path.ChangeExtension(InfPath(arch), ".PNF"), 4194304) is not null)
                throw new IOException("只剩 PNF、缺少可核验 INF；保留文件，请查看旧包装备份核对。");
            return null;
        }
        if (!infPresent && pnf is null && registrations.Count == 0) return null;
        return new(arch, identity!, inf, pnf, registrations, infPresent);
    }
    public string? Blocker() => new Vc14Environment().Blocker();
    public string CaptureCore()
    {
        var scan = Vc14Catalog.Scan();
        if (scan.Products.Count != 4 || !Vc14InstallPolicy.ArchitectureComplete(scan, "x64") || !Vc14InstallPolicy.ArchitectureComplete(scan, "x86"))
            throw new IOException("x86/x64 四个 14.42 组件尚未确认。");
        new Vc14Environment().VerifyInstalled();
        var hashes = new List<string> { Vc14Catalog.Fingerprint(scan) };
        foreach (var arch in new[] { "x64", "x86" })
        {
            var root = Environment.GetFolderPath(arch == "x64" ? Environment.SpecialFolder.System : Environment.SpecialFolder.SystemX86);
            foreach (var name in arch == "x64" ? new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" } : new[] { "msvcp140.dll", "vcruntime140.dll" })
                hashes.Add(Vc14LegacyPolicy.Hash(File.ReadAllBytes(Path.Combine(root, name))));
        }
        return Vc14LegacyPolicy.Hash(Encoding.UTF8.GetBytes(string.Join("\n", hashes)));
    }
    public string Backup()
    {
        _confirmed = new(_latest);
        Directory.CreateDirectory(_backup);
        foreach (var snapshot in _confirmed.Values)
        {
            File.WriteAllBytes(Path.Combine(_backup, "vcred14_" + snapshot.Arch + ".inf"), snapshot.Inf);
            if (snapshot.Pnf is not null) File.WriteAllBytes(Path.Combine(_backup, "vcred14_" + snapshot.Arch + ".PNF"), snapshot.Pnf);
        }
        File.WriteAllText(Path.Combine(_backup, "before.json"), JsonSerializer.Serialize(_confirmed));
        // Read back every backup before clearing metadata.
        foreach (var snapshot in _confirmed.Values)
        {
            if (Vc14LegacyPolicy.Hash(File.ReadAllBytes(Path.Combine(_backup, "vcred14_" + snapshot.Arch + ".inf"))) != Vc14LegacyPolicy.Hash(snapshot.Inf)
                || snapshot.Pnf is not null && Vc14LegacyPolicy.Hash(File.ReadAllBytes(Path.Combine(_backup, "vcred14_" + snapshot.Arch + ".PNF"))) != Vc14LegacyPolicy.Hash(snapshot.Pnf))
                throw new IOException("旧文件备份校验失败，未清理。");
        }
        var saved = JsonSerializer.Deserialize<Dictionary<string, Snapshot>>(File.ReadAllText(Path.Combine(_backup, "before.json")));
        if (saved is null || _confirmed.Any(p => !saved.TryGetValue(p.Key, out var item) || item.Hash != p.Value.Hash))
            throw new IOException("旧登记备份校验失败，未清理。");
        return _backup;
    }
    public void Remove(Vc14LegacyEntry entry)
    {
        var expected = _confirmed[entry.Architecture];
        var current = Read(entry.Architecture) ?? throw new IOException("旧包装已由其他程序改变。");
        if (current.Hash != expected.Hash || Vc14Catalog.Installed(expected.Identity.Minimum) || Vc14Catalog.Installed(expected.Identity.Additional))
            throw new IOException("旧包装身份或产品安装状态已变化，未继续清理。");
        foreach (var registration in expected.Registrations)
        {
            var view = (RegistryView)registration.View;
            if (ReadRegistration(expected.Arch, view, expected.Identity)?.Hash != registration.Hash) throw new IOException("旧登记原值已变化。");
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var parent = hive.OpenSubKey(Arp, true) ?? throw new IOException("旧登记目录无法访问。");
            parent.DeleteSubKey("vcredist14_" + expected.Arch, false); // fixed leaf only, never recursive
        }
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            if (ReadRegistration(expected.Arch, view, expected.Identity) is not null) throw new IOException("旧登记重新出现，保留 INF 文件。");
        if (expected.InfPresent) DeleteExact(InfPath(expected.Arch), expected.Inf);
        else if (File.Exists(InfPath(expected.Arch))) throw new IOException("出现新的 INF，已保留。");
        if (expected.Pnf is not null) DeleteExact(Path.ChangeExtension(InfPath(expected.Arch), ".PNF"), expected.Pnf);
        else if (File.Exists(Path.ChangeExtension(InfPath(expected.Arch), ".PNF"))) throw new IOException("出现新的 PNF 文件，已保留。");
        File.WriteAllText(Path.Combine(_backup, expected.Arch + "-processed.json"), JsonSerializer.Serialize(new { expected.Hash, Completed = true }));
    }
    private static void DeleteExact(string path, byte[] expected)
    {
        var current = ReadFile(path, 4194304);
        if (current is null || Vc14LegacyPolicy.Hash(current) != Vc14LegacyPolicy.Hash(expected)) throw new IOException("旧文件变化，未删除：" + Path.GetFileName(path));
        File.Delete(path);
    }
}
