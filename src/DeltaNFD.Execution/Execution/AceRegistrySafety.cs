using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>共享 AppDataLow 中仅检查受限位置；名称只能筛候选，不能证明 ACE 所有权。</summary>
internal static class AceRegistryPolicy
{
    internal static readonly (RegistryHive Hive, string Label, string Path)[] Roots =
    [
        (RegistryHive.LocalMachine, "HKLM", @"SOFTWARE\AppDataLow"),
        (RegistryHive.Users, "HKU", @".DEFAULT\SOFTWARE\AppDataLow"),
    ];
    private static readonly string[] Parents = ["", "Software", @"Software\Tencent"];
    internal static bool IsName(string name) => name.Equals("AntiCheatExpert", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ACE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ACE-", StringComparison.OrdinalIgnoreCase);
    internal static bool IsSharedPath(string path) => Roots.Any(r => path.Equals(r.Path, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(r.Path + "\\", StringComparison.OrdinalIgnoreCase));
    internal static bool IsCandidatePath(string path)
    {
        if (path.IndexOfAny(['"', '\r', '\n', '\0']) >= 0) return false;
        foreach (var root in Roots)
            foreach (var parent in Parents)
            {
                var prefix = root.Path + (parent.Length == 0 ? "" : "\\" + parent) + "\\";
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var leaf = path[prefix.Length..];
                if (!leaf.Contains('\\') && IsName(leaf)) return true;
            }
        return false;
    }
    internal static IReadOnlyList<string> Candidates(Func<string, IReadOnlyList<string>> enumerate, string root)
    {
        if (!Roots.Any(r => r.Path.Equals(root, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("未知共享根。");
        return Parents.SelectMany(parent =>
        {
            var path = root + (parent.Length == 0 ? "" : "\\" + parent);
            return enumerate(path).Where(IsName).Select(name => path + "\\" + name);
        }).Where(IsCandidatePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static bool MatchesInstallRoot(string? value, IEnumerable<string> allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['"', '\r', '\n', '\0']) >= 0) return false;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
            if (!Path.IsPathFullyQualified(expanded)) return false;
            var actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
            return allowedRoots.Any(root => Path.IsPathFullyQualified(root)
                && Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Equals("AntiCheatExpert", StringComparison.OrdinalIgnoreCase)
                && actual.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
}

/// <summary>每个键独立 GUID 目录；导出成功、内容及哈希证据落盘后才允许删除。</summary>
internal static class AceRegistryBackups
{
    internal static async Task<OperationResult> ExportAsync(string root, RegistryHive hive, string path,
        Func<string, string, TimeSpan, Task<(int Code, string StdOut, string StdErr)>> run)
    {
        try
        {
            if (hive is not (RegistryHive.LocalMachine or RegistryHive.Users) || !AceRegistryPolicy.IsCandidatePath(path))
                return OperationResult.Fail("拒绝备份白名单外的共享注册表路径。");
            var directory = Path.Combine(Path.GetFullPath(root), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            EnsureSafeDirectory(directory);
            var file = Path.Combine(directory, "registry.reg");
            var fullKey = (hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_USERS") + "\\" + path;
            var (code, _, error) = await run(Path.Combine(Environment.SystemDirectory, "reg.exe"),
                $"export \"{fullKey}\" \"{file}\" /y", TimeSpan.FromSeconds(30));
            if (code != 0) return OperationResult.Fail("注册表导出失败：" + error);
            EnsureNoReparsePoints(directory);
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return OperationResult.Fail("备份文件是链接，已拒绝删除原键。");
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 32) return OperationResult.Fail("备份文件为空或不完整。");
            using (var reader = new StreamReader(stream, Encoding.Unicode, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
            {
                if (reader.ReadLine() != "Windows Registry Editor Version 5.00") return OperationResult.Fail("备份文件格式无法确认。");
                bool found = false;
                for (int i = 0; i < 16 && reader.ReadLine() is { } line; i++)
                    if (line.Equals("[" + fullKey + "]", StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                if (!found) return OperationResult.Fail("备份文件的注册表路径不符。");
            }
            stream.Position = 0;
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var evidence = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Hive = hive.ToString(), Path = path,
                RegistryView = "Registry64", ExportFile = "registry.reg", Sha256 = hash, LengthBytes = stream.Length, CreatedUtc = DateTime.UtcNow });
            using (var index = new FileStream(Path.Combine(directory, "entry.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { index.Write(evidence); index.Flush(flushToDisk: true); }
            return OperationResult.Ok("注册表备份已核验：" + directory);
        }
        catch (Exception ex) { return OperationResult.Fail("注册表备份未完成，保留原键：" + ex.Message); }
    }

    private static void EnsureSafeDirectory(string path)
    {
        EnsureNoReparsePoints(path); Directory.CreateDirectory(path); EnsureNoReparsePoints(path);
    }
    private static void EnsureNoReparsePoints(string path)
    {
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(dir.FullName)) throw new IOException("备份目录路径包含文件。");
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("备份目录路径包含链接。");
        }
    }
}
