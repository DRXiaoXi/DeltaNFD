using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeltaNFD.Services.Plugins;

public sealed record PluginManagedRun(Guid SessionId, string PluginId, string Version, string PackageSha256,
    string EntryExecutable, int ProcessId, long StartTimeUtcTicks, int HostProcessId, long HostStartTimeUtcTicks);

// This journal is evidence and a duplicate-start barrier, not a reconnect authorization.
public sealed class PluginRunStore(string path)
{
    private readonly string _path = Path.GetFullPath(path);

    public bool TryList(out IReadOnlyList<PluginManagedRun> entries, out string error)
    {
        List<PluginManagedRun> loaded = [];
        var ok = Execute(() => loaded = Read(), out error);
        entries = loaded;
        return ok;
    }

    public bool TryAdd(PluginManagedRun entry, out string error) => Execute(() =>
    {
        Validate(entry);
        var entries = Read();
        if (entries.Any(e => e.PluginId == entry.PluginId)) throw new IOException("插件存在未确认的旧运行记录，禁止启动第二份。");
        entries.Add(entry);
        Write(entries);
    }, out error);

    public bool TryRemove(string id, Guid sessionId, out string error) => Execute(() =>
    {
        var entries = Read();
        if (entries.RemoveAll(e => e.PluginId == id && e.SessionId == sessionId) != 1)
            throw new IOException("运行记录身份不符或缺失，未清除。");
        Write(entries);
    }, out error);

    private bool Execute(Action action, out string error)
    {
        try
        {
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())))[..20];
            using var mutex = new Mutex(false, "Global\\DeltaNFD_PluginRuns_" + digest);
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new TimeoutException("运行记录仓库忙。");
                action();
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
            error = ""; return true;
        }
        catch (Exception ex) { error = "插件运行记录未确认：" + ex.Message; return false; }
    }

    private void CheckPath()
    {
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(_path)!); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("运行记录含重解析目录。");
        try
        {
            if ((File.GetAttributes(_path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("运行记录不是普通文件。");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private List<PluginManagedRun> Read()
    {
        CheckPath();
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 1024 * 1024) throw new InvalidDataException("运行记录过大。");
            var entries = JsonSerializer.Deserialize<List<PluginManagedRun>>(stream) ?? throw new InvalidDataException("运行记录为 null。");
            foreach (var entry in entries) Validate(entry);
            if (entries.GroupBy(e => e.PluginId).Any(g => g.Count() != 1)) throw new InvalidDataException("运行记录重复。");
            return entries;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private static void Validate(PluginManagedRun entry)
    {
        if (entry is null || entry.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(entry.PluginId) ||
            !PluginManifestParser.TryParseVersion(entry.Version, out _) || entry.PackageSha256 is null ||
            entry.PackageSha256.Length != 64 || !entry.PackageSha256.All(Uri.IsHexDigit) ||
            !Path.IsPathFullyQualified(entry.EntryExecutable) || entry.ProcessId <= 0 || entry.StartTimeUtcTicks <= 0 ||
            entry.HostProcessId <= 0 || entry.HostStartTimeUtcTicks <= 0)
            throw new InvalidDataException("运行记录身份无效。");
    }

    private void Write(List<PluginManagedRun> entries)
    {
        CheckPath();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        CheckPath();
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, entries); stream.Flush(true); }
            File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
