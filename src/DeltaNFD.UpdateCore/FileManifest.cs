using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeltaNFD.Updates;

public sealed class FileUpdateReference
{
    public string Url { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class FileUpdateEntry
{
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public long DownloadSize { get; set; }
    public string DownloadSha256 { get; set; } = "";
    public string Compression { get; set; } = "gzip";
    public List<string> Urls { get; set; } = [];
}

public sealed class FileUpdateRemoval
{
    public string Path { get; set; } = "";
    public string PreviousSha256 { get; set; } = "";
}

public sealed class FileUpdateManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = "";
    public string DisplayVersion { get; set; } = "";
    public string Channel { get; set; } = "";
    public List<FileUpdateEntry> Files { get; set; } = [];
    public List<FileUpdateRemoval> Remove { get; set; } = [];
}

public static class FileUpdateRules
{
    public const int MaximumManifestBytes = 8 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly HashSet<string> Directories = new(StringComparer.OrdinalIgnoreCase)
        { "Assets", "Backends", "Views", "licenses", "Tools", "Microsoft.UI.Xaml", "NpuDetect", "Controls", "Themes" };
    private static readonly string[] Required = ["DeltaNFD.exe", "DeltaNFD.dll", "DeltaNFD.deps.json", "DeltaNFD.runtimeconfig.json",
        "DeltaNFD.Shared.dll", "DeltaNFD.Contracts.dll", "DeltaNFD.UpdateCore.dll", "DeltaNFD.pri", "Tools/DeploymentControl.ps1",
        "Backends/DeltaNFD.FileUpdater.exe", "Backends/DeltaNFD.FileUpdater.dll", "Backends/DeltaNFD.FileUpdater.deps.json", "Backends/DeltaNFD.FileUpdater.runtimeconfig.json"];

    public static bool IsHash(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);
    public static string Hash(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    public static bool Matches(string path, long size, string hash) => File.Exists(path)
        && new FileInfo(path).Length == size && Hash(path).Equals(hash, StringComparison.OrdinalIgnoreCase);

    public static string Relative(string path, bool product = true)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.Contains('\\') || path.StartsWith('/')
            || path.Any(c => c < 32 || ":*?\"<>|".Contains(c))) throw new InvalidDataException("更新相对路径非法。");
        var parts = path.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)", RegexOptions.IgnoreCase)
                || new[] { "logs", "backups", "plugins", "userdata" }.Contains(part, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("更新路径含受保护或保留名称。");
        }
        if (product && (parts[0].Equals("Controls", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("Themes", StringComparison.OrdinalIgnoreCase))
            && (parts.Length != 2 || !parts[1].EndsWith(".xbf", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("UI 更新目录仅允许直接包含编译后的 XBF 资源。");
        if (product && ((parts.Length > 1 && !Directories.Contains(parts[0])
                && !Regex.IsMatch(parts[0], @"^[a-z]{2,3}(-[A-Za-z0-9]{2,8}){0,2}$"))
            || (parts.Length == 1 && (parts[0].StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("settings.json", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("update-source.json", StringComparison.OrdinalIgnoreCase)
                || parts[0].StartsWith("file-update", StringComparison.OrdinalIgnoreCase)))))
            throw new InvalidDataException("更新不能改写用户数据、卸载器或更新事务。");
        return path;
    }

    public static string Under(string root, string relative)
    {
        Relative(relative, product: false);
        var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新路径越界。");
        // Check both existing leaf and every ancestor: junctions must never redirect a replacement.
        for (var p = full; !string.IsNullOrEmpty(p); p = System.IO.Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新路径包含符号链接或联接点。");
        return full;
    }

    public static FileUpdateManifest Read(byte[] bytes, string hash, bool requireProduct = true)
    {
        if (bytes.Length is 0 or > MaximumManifestBytes || !IsHash(hash)
            || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件清单大小或 SHA256 校验失败。");
        var manifest = JsonSerializer.Deserialize<FileUpdateManifest>(bytes, Json) ?? throw new InvalidDataException("文件清单为空。");
        Validate(manifest, requireProduct);
        return manifest;
    }

    public static void Validate(FileUpdateManifest manifest, bool requireProduct = true)
    {
        if (manifest.SchemaVersion != 1 || !Version.TryParse(manifest.Version, out _) || manifest.Files is not { Count: > 0 and <= 10000 }
            || manifest.Remove is null || manifest.Remove.Count > 10000) throw new InvalidDataException("不支持的文件清单。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || !paths.Add(Relative(file.Path)) || !IsHash(file.Sha256) || !IsHash(file.DownloadSha256)
                || file.SizeBytes is < 0 or > 1073741824 || file.DownloadSize is <= 0 or > 1073741824
                || file.Compression is not ("gzip" or "none") || file.Urls is not { Count: > 0 and <= 4 }
                || file.Urls.Any(u => !Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                    || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0))
                throw new InvalidDataException("文件条目、路径重复或内容身份非法。");
            total = checked(total + file.SizeBytes);
        }
        if (total > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("文件清单总量异常。");
        foreach (var removal in manifest.Remove)
            if (removal is null || !paths.Add(Relative(removal.Path)) || !IsHash(removal.PreviousSha256))
                throw new InvalidDataException("旧文件删除条目非法或重复。");
        foreach (var path in paths)
            for (var p = path.LastIndexOf('/'); p >= 0; p = path.LastIndexOf('/', p - 1))
                if (paths.Contains(path[..p])) throw new InvalidDataException("更新文件与目录路径冲突。");
        // Same raw content must have the same compressed representation; cache is keyed by raw hash.
        foreach (var group in manifest.Files.GroupBy(f => f.Sha256, StringComparer.OrdinalIgnoreCase))
            if (group.Select(f => (f.SizeBytes, f.DownloadSize, f.Compression, f.DownloadSha256.ToUpperInvariant())).Distinct().Count() != 1)
                throw new InvalidDataException("相同文件哈希存在冲突的内容声明。");
        if (requireProduct)
        {
            foreach (var path in Required)
                if (!manifest.Files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("完整文件清单缺少必要组件：" + path);
            foreach (var name in new[] { "Ace", "Gpu", "Runtime", "SystemMaintenance", "CpuFrame", "Procman" })
                foreach (var extension in new[] { ".exe", ".dll", ".runtimeconfig.json", ".deps.json" })
                    if (!manifest.Files.Any(f => f.Path.Equals("Backends/DeltaNFD.Backend." + name + extension, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("完整文件清单缺少官方后端。");
        }
    }

    public static void AtomicJson<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        { output.Write(bytes); output.Flush(true); }
        File.Move(temporary, path, overwrite: true);
    }
}
