using System.Security.Cryptography;
using System.Text;

namespace DeltaNFD.Services.Plugins;

public sealed class PluginPackageLease : IDisposable
{
    private readonly List<FileStream> _streams;
    public string FileTableDigest { get; }
    internal PluginPackageLease(List<FileStream> streams, string digest) { _streams = streams; FileTableDigest = digest; }
    public void Dispose() { foreach (var stream in _streams) stream.Dispose(); _streams.Clear(); }
}

public static class PluginPackageIntegrity
{
    public static PluginPackageLease VerifyAndLock(PluginIndexEntry entry)
    {
        if (!PluginPackageIndex.IsValid(entry, out var error)) throw new InvalidDataException(error);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.InstallDirectory));
        if (!PluginPath.IsWithinRoot(root, entry.EntryExecutable)) throw new InvalidDataException("插件入口越界。");
        var streams = new List<FileStream>();
        try
        {
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("插件路径含重解析祖先。");
            var expected = entry.Files.Select(f => PluginPath.ValidateRelativePath(f.Path, "文件校验表")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actual = new List<string>();
            CollectFiles(new DirectoryInfo(root), root, actual);
            if (!expected.SetEquals(actual)) throw new InvalidDataException("插件文件清单发生变化，需重新导入并确认授权。");
            var canonical = new StringBuilder();
            foreach (var item in entry.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                var relative = PluginPath.ValidateRelativePath(item.Path, "文件校验表");
                var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!PluginPath.IsWithinRoot(root, path)) throw new InvalidDataException("文件校验路径越界。");
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                streams.Add(stream);
                if (stream.Length != item.SizeBytes || !Convert.ToHexString(SHA256.HashData(stream)).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("插件文件已改写：" + relative);
                stream.Position = 0;
                canonical.Append(relative).Append('\0').Append(item.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append('\0').Append(item.Sha256.ToLowerInvariant()).Append('\n');
            }
            return new PluginPackageLease(streams, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant());
        }
        catch { foreach (var stream in streams) stream.Dispose(); throw; }
    }

    private static void CollectFiles(DirectoryInfo directory, string root, List<string> files)
    {
        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("插件包含重解析条目。");
            if (item is DirectoryInfo child) CollectFiles(child, root, files);
            else files.Add(Path.GetRelativePath(root, item.FullName).Replace('\\', '/'));
            if (files.Count > PluginPackageImporter.MaxEntryCount) throw new InvalidDataException("插件包文件数超限。");
        }
    }
}
