using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaNFD.Services.Plugins;

public enum PluginHandoffState
{
    /// <summary>prepare 已发出，等待端点确认。</summary>
    Preparing,
    /// <summary>commit 已确认，插件自主运行（非零进程）。</summary>
    Autonomous,
    /// <summary>中途断线/确认失败；记录保留，不声称退出也不声称在跑。</summary>
    Unknown,
}

/// <summary>脱机移交记录（规范第 8 节）：先落盘再发 prepare；身份不明保留记录。</summary>
public sealed record PluginHandoffEntry
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid RunId { get; init; }
    public required string PluginId { get; init; }
    public required string PluginVersion { get; init; }
    public required string PackageSha256 { get; init; }
    public required string EntryExecutable { get; init; }
    /// <summary>后端进程 PID（十进制）。</summary>
    public required int ProcessId { get; init; }
    /// <summary>后端启动 UTC ticks（十进制字符串，避免 JSON 数字精度损失）。</summary>
    public required string StartTimeUtcTicks { get; init; }
    /// <summary>插件自建的重连管道端点名。</summary>
    public required string ReconnectPipeName { get; init; }
    public PluginHandoffState State { get; init; } = PluginHandoffState.Preparing;
    public required string CreatedUtc { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>
/// 插件脱机移交仓库：`PluginData\offline-handoff.json`。原子写入 + 互斥锁；损坏 fail-closed。
/// 规范第 8 节：宿主先保存记录再发 offline.prepare；校验端点与身份后才 commit；
 /// 中途失败保留记录与证据。
/// </summary>
public sealed class PluginHandoffStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly string _path;
    private readonly string _mutexName;

    public PluginHandoffStore(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("移交仓库路径必须是绝对路径。", nameof(path));
        _path = Path.GetFullPath(path);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())))[..20];
        _mutexName = $"Local\\DeltaNFD_PluginHandoff_{digest}";
    }

    public string PathName => _path;

    public bool TryUpsert(PluginHandoffEntry entry, out string error)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsValid(entry, out error)) return false;
        var actionError = "";
        var lockError = "";
        var succeeded = WithMutex(() =>
        {
            if (!TryReadUnlocked(out var entries, out actionError)) return false;
            if (entries.Any(e => e.PluginId == entry.PluginId && e.RunId != entry.RunId))
            { actionError = "插件已有其他移交记录，必须先处理旧记录。"; return false; }
            var next = entries.Where(e => !(e.PluginId == entry.PluginId && e.RunId == entry.RunId))
                .Append(entry).OrderBy(e => e.PluginId, StringComparer.Ordinal).ToList();
            if (!TryWriteUnlocked(next, out actionError)) return false;
            actionError = "";
            return true;
        }, out lockError);
        error = succeeded ? "" : string.IsNullOrWhiteSpace(lockError) ? actionError : lockError;
        return succeeded;
    }

    public bool TryList(out IReadOnlyList<PluginHandoffEntry> entries, out string error)
    {
        var loaded = new List<PluginHandoffEntry>();
        var readError = "";
        var ok = WithMutex(() => TryReadUnlocked(out loaded, out readError), out var lockError);
        entries = loaded;
        error = ok ? readError : string.IsNullOrWhiteSpace(lockError) ? readError : lockError;
        return ok;
    }

    /// <summary>某插件当前记录（同插件只保留最新 runId 的记录）。</summary>
    public bool TryGet(string pluginId, out PluginHandoffEntry? entry, out string error)
    {
        if (!TryList(out var entries, out error)) { entry = null; return false; }
        entry = entries.Where(e => e.PluginId == pluginId).OrderByDescending(e => e.CreatedUtc).FirstOrDefault();
        return true;
    }

    public bool TryRemove(string pluginId, Guid runId, out string error)
    {
        var actionError = "";
        var lockError = "";
        var succeeded = WithMutex(() =>
        {
            if (!TryReadUnlocked(out var entries, out actionError)) return false;
            var next = entries.Where(e => !(e.PluginId == pluginId && e.RunId == runId)).ToList();
            if (next.Count == entries.Count) { actionError = "移交记录不存在。"; return false; }
            return TryWriteUnlocked(next, out actionError);
        }, out lockError);
        error = succeeded ? "" : string.IsNullOrWhiteSpace(lockError) ? actionError : lockError;
        return succeeded;
    }

    internal static bool IsValid(PluginHandoffEntry entry, out string error)
    {
        error = "";
        if (entry.SchemaVersion != 1) { error = "移交记录版本不受支持。"; return false; }
        if (entry.RunId == Guid.Empty) { error = "runId 不能为零。"; return false; }
        if (entry.PluginId is null || entry.PluginId.Length is < 3 or > 80 || !entry.PluginId.Contains('.') ||
            entry.PluginId.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'))) { error = "移交记录插件 ID 非法。"; return false; }
        if (!PluginManifestParser.TryParseVersion(entry.PluginVersion, out _)) { error = "移交记录版本非法。"; return false; }
        if (entry.PackageSha256 is null || entry.PackageSha256.Length != 64 || !entry.PackageSha256.All(Uri.IsHexDigit)) { error = "移交记录包哈希非法。"; return false; }
        if (!Enum.IsDefined(entry.State) || !DateTimeOffset.TryParse(entry.CreatedUtc, out _)) { error = "移交记录状态或时间非法。"; return false; }
        if (entry.ProcessId <= 0) { error = "移交记录 PID 非法。"; return false; }
        if (!long.TryParse(entry.StartTimeUtcTicks, out var ticks) || ticks <= 0) { error = "移交记录启动时间非法。"; return false; }
        if (string.IsNullOrWhiteSpace(entry.ReconnectPipeName) || entry.ReconnectPipeName.Contains("..", StringComparison.Ordinal)) { error = "移交记录端点非法。"; return false; }
        if (!Path.IsPathFullyQualified(entry.EntryExecutable)) { error = "移交记录入口路径非法。"; return false; }
        return true;
    }

    private bool TryReadUnlocked(out List<PluginHandoffEntry> entries, out string error)
    {
        entries = [];
        error = "";
        try
        {
            ValidateAncestors();
            try
            {
                if ((File.GetAttributes(_path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidDataException("移交路径不是普通文件。");
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 1024 * 1024) throw new InvalidDataException("插件移交仓库过大。");
            entries = JsonSerializer.Deserialize<List<PluginHandoffEntry>>(stream, JsonOptions)
                ?? throw new InvalidDataException("移交仓库为 null。");
            if (entries.Any(e => e is null) || entries.GroupBy(e => e.PluginId).Any(g => g.Count() > 1))
                throw new InvalidDataException("移交仓库含空条目或重复插件记录。");
            foreach (var entry in entries)
                if (!IsValid(entry, out error)) { entries = []; return false; }
            return true;
        }
        catch (Exception ex)
        {
            entries = [];
            error = "插件移交仓库不可读或损坏，已阻止自动处理：" + ex.Message;
            return false;
        }
    }

    private bool TryWriteUnlocked(List<PluginHandoffEntry> entries, out string error)
    {
        error = "";
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory)) { error = "移交仓库没有父目录。"; return false; }
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            ValidateAncestors();
            Directory.CreateDirectory(directory);
            ValidateAncestors();
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
            error = "保存移交仓库失败：" + ex.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private void ValidateAncestors()
    {
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(_path)!); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("移交仓库祖先含重解析目录。");
        if (Directory.Exists(_path)) throw new IOException("移交仓库路径被目录占用。");
        if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("移交仓库含重解析文件。");
    }

    private bool WithMutex(Func<bool> action, out string lockError)
    {
        lockError = "";
        try
        {
            using var mutex = new Mutex(false, _mutexName);
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) { lockError = "等待移交仓库锁超时。"; return false; }
                return action();
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            lockError = "无法锁定移交仓库：" + ex.Message;
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
