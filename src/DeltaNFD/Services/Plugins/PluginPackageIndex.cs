using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaNFD.Services.Plugins;

public enum PluginPackageState
{
    /// <summary>已导入，未授权（默认状态，导入不等于启用）。</summary>
    ImportedDisabled,
    /// <summary>用户已确认授权（绑定 ID+版本+包哈希）。</summary>
    Authorized,
    /// <summary>存在待恢复/未知运行记录，替换与卸载被阻止。</summary>
    PendingRestore,
}

/// <summary>已导入包的索引记录。权限不依赖此索引（它可被管理员改写），只作管理用途。</summary>
public sealed record PluginIndexEntry
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public string PackageSha256 { get; init; } = "";
    public long PackageSizeBytes { get; init; } = 1;
    public string InstallDirectory { get; init; } = "";
    public string EntryExecutable { get; init; } = "";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    /// <summary>权限用途冗余（来自 manifest，供详情展示；运行校验以包内 manifest 为准）。</summary>
    public IReadOnlyList<PluginIndexPermission> Permissions { get; init; } = [];
    public PluginPackageState State { get; init; } = PluginPackageState.ImportedDisabled;
    /// <summary>包内每个文件的相对路径与 SHA256（文件校验表）。</summary>
    public IReadOnlyList<PluginIndexFile> Files { get; init; } = [];
    public DateTimeOffset ImportedUtc { get; init; }
    /// <summary>授权时间；未授权为 null。授权绑定 ID+版本+哈希，替换包后失效。</summary>
    public DateTimeOffset? AuthorizedUtc { get; init; }
    /// <summary>脱机自主运行的逐插件授权时间（绑定当前包哈希）；null = 未单独授权。</summary>
    public DateTimeOffset? OfflineAuthorizedUtc { get; init; }
}

public sealed record PluginIndexFile(string Path, string Sha256, long SizeBytes);

public sealed record PluginIndexPermission(string Id, string Purpose);

/// <summary>
/// 插件管理索引（规范第 2 节）：`%LOCALAPPDATA%\Delta NFD\Plugins\index.json`，
/// 命名互斥锁 + 同目录原子替换，损坏时 fail-closed（返回失败而不是回退默认值）。
/// </summary>
public sealed class PluginPackageIndex
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly string _path;
    private readonly string _mutexName;

    public PluginPackageIndex(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("索引路径必须是绝对路径。", nameof(path));
        _path = Path.GetFullPath(path);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())))[..20];
        _mutexName = $"Local\\DeltaNFD_PluginIndex_{digest}";
    }

    public string PathName => _path;

    public bool TryRead(out IReadOnlyList<PluginIndexEntry> entries, out string error)
    {
        var loaded = new List<PluginIndexEntry>();
        var readError = "";
        var ok = WithMutex(() => TryReadUnlocked(out loaded, out readError), out var lockError);
        entries = loaded;
        error = ok ? readError : string.IsNullOrWhiteSpace(lockError) ? readError : lockError;
        return ok;
    }

    /// <summary>按 ID 取条目；同 ID 只允许一条（导入器保证）。</summary>
    public bool TryGet(string pluginId, out PluginIndexEntry? entry, out string error)
    {
        if (!TryRead(out var entries, out error)) { entry = null; return false; }
        entry = entries.FirstOrDefault(e => e.Id == pluginId);
        return true;
    }

    /// <summary>新增或替换条目（导入成功提交时调用）。替换同 ID 旧条目；授权绑定 ID+版本+哈希，替换包后旧授权不再匹配。</summary>
    public bool TryUpsert(PluginIndexEntry entry, out string error)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsValid(entry, out error)) return false;
        var actionError = "";
        var lockError = "";
        var succeeded = WithMutex(() =>
        {
            if (!TryReadUnlocked(out var entries, out var readError)) { actionError = readError; return false; }
            var next = entries.Where(e => e.Id != entry.Id).Append(entry)
                .OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
            if (!TryWriteUnlocked(next, out actionError)) return false;
            actionError = "";
            return true;
        }, out lockError);
        error = succeeded ? "" : string.IsNullOrWhiteSpace(lockError) ? actionError : lockError;
        return succeeded;
    }

    /// <summary>移除条目（卸载成功后调用）。</summary>
    public bool TryRemove(string pluginId, out string error)
    {
        var actionError = "";
        var lockError = "";
        var succeeded = WithMutex(() =>
        {
            if (!TryReadUnlocked(out var entries, out var readError)) { actionError = readError; return false; }
            var next = entries.Where(e => e.Id != pluginId).ToList();
            if (next.Count == entries.Count) { actionError = $"索引中没有插件“{pluginId}”。"; return false; }
            if (!TryWriteUnlocked(next, out actionError)) return false;
            actionError = "";
            return true;
        }, out lockError);
        error = succeeded ? "" : string.IsNullOrWhiteSpace(lockError) ? actionError : lockError;
        return succeeded;
    }

    private bool TryReadUnlocked(out List<PluginIndexEntry> entries, out string error)
    {
        entries = [];
        error = "";
        try
        {
            try
            {
                if ((File.GetAttributes(_path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidDataException("索引路径不是普通文件。");
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("插件索引文件过大。");
            entries = (JsonSerializer.Deserialize<List<PluginIndexEntry>>(stream, JsonOptions) ?? [])
                .Where(e => e is not null).ToList();
            foreach (var entry in entries)
                if (!IsValid(entry, out error)) { entries = []; return false; }
            if (entries.GroupBy(e => e.Id).Any(g => g.Count() > 1))
            { entries = []; error = "插件索引包含重复 ID。"; return false; }
            return true;
        }
        catch (Exception ex)
        {
            entries = [];
            error = "插件索引不可读或损坏，已阻止自动处理：" + ex.Message;
            return false;
        }
    }

    private bool TryWriteUnlocked(List<PluginIndexEntry> entries, out string error)
    {
        error = "";
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory)) { error = "插件索引没有父目录。"; return false; }
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, entries, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = "保存插件索引失败：" + ex.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    internal static bool IsValid(PluginIndexEntry entry, out string error)
    {
        error = "";
        if (entry.SchemaVersion != 1) { error = "插件索引版本不受支持。"; return false; }
        if (entry.Id.Length is < 3 or > 80 || !entry.Id.Contains('.') ||
            entry.Id.Any(c => c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.')))
        { error = "插件索引 ID 无效。"; return false; }
        if (!PluginManifestParser.TryParseVersion(entry.Version, out _)) { error = "插件索引版本号无效。"; return false; }
        if (entry.PackageSha256.Length != 64 || !IsHex(entry.PackageSha256))
        { error = "插件索引包哈希无效。"; return false; }
        if (entry.PackageSizeBytes <= 0) { error = "插件索引包大小无效。"; return false; }
        if (!Path.IsPathFullyQualified(entry.InstallDirectory) || !Path.IsPathFullyQualified(entry.EntryExecutable))
        { error = "插件索引安装路径无效。"; return false; }
        if (entry.Files.Count == 0) { error = "插件索引文件校验表为空。"; return false; }
        foreach (var file in entry.Files)
        {
            if (file is null || !PluginPath.IsSafeRelativePath(file.Path) ||
                file.Sha256.Length != 64 || !IsHex(file.Sha256) ||
                file.SizeBytes < 0)
            { error = "插件索引文件校验表包含无效条目。"; return false; }
        }
        if (entry.Files.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        { error = "插件索引文件校验表包含重复路径。"; return false; }
        return true;
    }

    private static bool IsHex(string text)
    {
        foreach (var c in text)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'))) return false;
        return true;
    }

    private bool WithMutex(Func<bool> action, out string error)
    {
        error = "";
        try
        {
            using var mutex = new Mutex(false, _mutexName);
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) { error = "等待插件索引锁超时。"; return false; }
                return action();
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            error = "无法锁定插件索引文件：" + ex.Message;
            return false;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
}
