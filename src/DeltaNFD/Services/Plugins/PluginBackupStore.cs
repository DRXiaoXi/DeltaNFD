using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaNFD.Services.Plugins;

public enum PluginBackupStatus
{
    /// <summary>修改已做、原值已保存，等待恢复（持久，插件退出不清除）。</summary>
    PendingRestore,
    /// <summary>恢复请求已发出，结果未确认（崩溃/超时后停在此态）。</summary>
    Restoring,
    /// <summary>后端确认恢复成功。</summary>
    Restored,
    /// <summary>恢复失败（后端报告失败/冲突/身份未知）；证据保留，不清除标记。</summary>
    RestoreFailed,
}

/// <summary>备份记录（规范第 7 节）：修改前首次原值 + 独立 backupId。</summary>
public sealed record PluginBackupEntry
{
    public int SchemaVersion { get; init; } = 1;
    public required string BackupId { get; init; }
    public required string PluginId { get; init; }
    public required string PluginVersion { get; init; }
    public required string PackageSha256 { get; init; }
    public required string OperationId { get; init; }
    public required string ResourceId { get; init; }
    /// <summary>宿主记录时间（UTC ISO 8601）。</summary>
    public required string CreatedUtc { get; init; }
    /// <summary>原值描述（由后端 result 提供的结构化文本；宿主不解释内容，只保存证据）。</summary>
    public string OriginalValueJson { get; init; } = "";
    /// <summary>最后写入值描述；恢复前比对第三方是否已改。</summary>
    public string LastWrittenValueJson { get; init; } = "";
    public string BackendReport { get; init; } = "";
    /// <summary>目标身份（targetScoped 操作的快照；非目标操作为 null）。</summary>
    public PluginIpcTarget? Target { get; init; }
    public bool IsInvocationIntent { get; init; }
    public IReadOnlyList<string> ResourceIds { get; init; } = [];
    public PluginBackupStatus Status { get; init; } = PluginBackupStatus.PendingRestore;
    /// <summary>最近一次恢复尝试的结论。</summary>
    public string RestoreAttemptResult { get; init; } = "";
}

/// <summary>
/// 插件备份仓库（规范第 7 节）：`PluginData\backups\backups.json`，原子写入 + 互斥锁。
/// 只保存宿主可见的证据（原值/最后写入值由后端报告，宿主不能独立证实其真实性）。
/// </summary>
public sealed class PluginBackupStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly string _path;
    private readonly string _mutexName;

    public string PathName => _path;

    public PluginBackupStore(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("备份仓库路径必须是绝对路径。", nameof(path));
        _path = Path.GetFullPath(path);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())))[..20];
        _mutexName = $"Local\\DeltaNFD_PluginBackups_{digest}";
    }

    /// <summary>记录一次修改前备份。同一 backupId 不重复写（首次原值优先）。</summary>
    public bool TryRecord(PluginBackupEntry entry, out string error)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsValid(entry, out error)) return false;
        var actionError = "";
        var lockError = "";
        var succeeded = WithMutex(() =>
        {
            if (!TryReadUnlocked(out var entries, out actionError)) return false;
            if (entries.Any(b => b.PluginId == entry.PluginId && b.BackupId == entry.BackupId))
            { actionError = $"backupId“{entry.BackupId}”已存在（首次原值不可覆盖）。"; return false; }
            entries.Add(entry);
            return TryWriteUnlocked(entries, out actionError);
        }, out lockError);
        error = succeeded ? "" : string.IsNullOrWhiteSpace(lockError) ? actionError : lockError;
        return succeeded;
    }

    /// <summary>更新状态（恢复流转）。只允许 PendingRestore→Restoring→Restored/RestoreFailed 与失败重试 Restoring。</summary>
    public bool TryUpdateStatus(string backupId, PluginBackupStatus status, string attemptResult, out PluginBackupEntry? updated, out string error)
        => UpdateStatus(null, backupId, status, attemptResult, out updated, out error);

    public bool TryUpdateStatus(string pluginId, string backupId, PluginBackupStatus status, string attemptResult,
        out PluginBackupEntry? updated, out string error) => UpdateStatus(pluginId, backupId, status, attemptResult, out updated, out error);

    private bool UpdateStatus(string? pluginId, string backupId, PluginBackupStatus status, string attemptResult,
        out PluginBackupEntry? updated, out string error)
    {
        updated = null;
        if (string.IsNullOrEmpty(backupId)) { error = "backupId 为空。"; return false; }
        var actionError = "";
        var lockError = "";
        var updatedEntry = (PluginBackupEntry?)null;
        var succeeded = WithMutex(() =>
        {
            if (!TryReadUnlocked(out var entries, out actionError)) return false;
            var matching = entries.Where(b => b.BackupId == backupId && (pluginId is null || b.PluginId == pluginId)).ToList();
            if (matching.Count > 1) { actionError = "备份标识有歧义，必须指定插件 ID。"; return false; }
            var index = entries.FindIndex(b => b.BackupId == backupId && (pluginId is null || b.PluginId == pluginId));
            if (index < 0) { actionError = $"backupId“{backupId}”不存在。"; return false; }
            var current = entries[index];
            var valid = (current.Status, status) switch
            {
                (PluginBackupStatus.PendingRestore, PluginBackupStatus.Restoring) => true,
                (PluginBackupStatus.Restoring, PluginBackupStatus.Restored) => true,
                (PluginBackupStatus.Restoring, PluginBackupStatus.RestoreFailed) => true,
                (PluginBackupStatus.RestoreFailed, PluginBackupStatus.Restoring) => true, // 失败重试
                (PluginBackupStatus.Restoring, PluginBackupStatus.Restoring) => true, // 中断后的幂等重试
                (PluginBackupStatus.Restored, PluginBackupStatus.Restored) => true,      // 幂等确认
                _ => false,
            };
            if (!valid)
            { actionError = $"备份状态流转不允许：{current.Status} → {status}。"; return false; }
            entries[index] = current with { Status = status, RestoreAttemptResult = attemptResult };
            updatedEntry = entries[index];
            return TryWriteUnlocked(entries, out actionError);
        }, out lockError);
        updated = updatedEntry;
        error = succeeded ? "" : string.IsNullOrWhiteSpace(lockError) ? actionError : lockError;
        return succeeded;
    }

    public bool TryList(out IReadOnlyList<PluginBackupEntry> entries, out string error)
    {
        var loaded = new List<PluginBackupEntry>();
        var readError = "";
        var ok = WithMutex(() => TryReadUnlocked(out loaded, out readError), out var lockError);

        entries = loaded;
        error = ok ? readError : string.IsNullOrWhiteSpace(lockError) ? readError : lockError;
        return ok;
    }

    public bool TryGet(string backupId, out PluginBackupEntry? entry, out string error)
        => Get(null, backupId, out entry, out error);

    public bool TryGet(string pluginId, string backupId, out PluginBackupEntry? entry, out string error) => Get(pluginId, backupId, out entry, out error);

    private bool Get(string? pluginId, string backupId, out PluginBackupEntry? entry, out string error)
    {
        entry = null;
        if (!TryList(out var entries, out error)) return false;
        var matching = entries.Where(b => b.BackupId == backupId && (pluginId is null || b.PluginId == pluginId)).ToList();
        if (matching.Count > 1) { error = "备份标识有歧义，必须指定插件 ID。"; return false; }
        entry = matching.FirstOrDefault();
        return true;
    }

    /// <summary>某插件当前仍需恢复的备份（PendingRestore/Restoring/RestoreFailed）。</summary>
    public bool TryListPending(string pluginId, out IReadOnlyList<PluginBackupEntry> entries, out string error)
    {
        if (!TryList(out var all, out error)) { entries = []; return false; }
        entries = all.Where(b => b.PluginId == pluginId &&
            b.Status is PluginBackupStatus.PendingRestore or PluginBackupStatus.Restoring or PluginBackupStatus.RestoreFailed)
            .ToList();
        return true;
    }

    /// <summary>仅确认恢复完成后清理（规范：进程退出不等同恢复成功；恢复完成的记录保留供审计，不删除）。</summary>
    public bool TryMarkRestored(string backupId, out PluginBackupEntry? updated, out string error) =>
        TryUpdateStatus(backupId, PluginBackupStatus.Restored, "恢复完成", out updated, out error);

    public bool TryResolveIntent(string intentId, IReadOnlyList<string> backendIds, out string error)
        => ResolveIntent(null, intentId, backendIds, out error);

    public bool TryResolveIntent(string pluginId, string intentId, IReadOnlyList<string> backendIds, out string error)
        => ResolveIntent(pluginId, intentId, backendIds, out error);

    private bool ResolveIntent(string? pluginId, string intentId, IReadOnlyList<string> backendIds, out string error)
    {
        var actionError = "";
        var ok = WithMutex(() =>
        {
            if (!TryReadUnlocked(out var entries, out actionError)) return false;
            var matching = entries.Where(e => e.BackupId == intentId && e.IsInvocationIntent && (pluginId is null || e.PluginId == pluginId)).ToList();
            if (matching.Count > 1) { actionError = "执行意图有歧义，必须指定插件 ID。"; return false; }
            var intent = matching.FirstOrDefault();
            if (intent is null || backendIds.Count == 0 || backendIds.Any(id => !entries.Any(e => e.BackupId == id &&
                !e.IsInvocationIntent && e.PluginId == intent.PluginId && e.PackageSha256 == intent.PackageSha256 &&
                e.OperationId == intent.OperationId && e.Status == PluginBackupStatus.PendingRestore)))
            { actionError = "缺少已持久化的对应后端备份，执行意图保留。"; return false; }
            entries.Remove(intent);
            return TryWriteUnlocked(entries, out actionError);
        }, out var lockError);
        error = ok ? "" : string.IsNullOrEmpty(lockError) ? actionError : lockError;
        return ok;
    }

    internal static bool IsValid(PluginBackupEntry entry, out string error)
    {
        error = "";
        if (entry.SchemaVersion != 1) { error = "备份记录版本不受支持。"; return false; }
        if (entry.BackupId.Length is < 1 or > 128) { error = "backupId 长度非法。"; return false; }
        if (entry.PluginId.Length is < 3 or > 80 || !entry.PluginId.Contains('.')) { error = "备份记录插件 ID 非法。"; return false; }
        if (!PluginManifestParser.TryParseVersion(entry.PluginVersion, out _)) { error = "备份记录插件版本非法。"; return false; }
        if (entry.PackageSha256.Length != 64) { error = "备份记录包哈希非法。"; return false; }
        if (entry.OperationId.Length == 0 || !PluginContract.OperationResourceIds.Contains(entry.ResourceId))
        { error = "备份记录操作或资源标识非法。"; return false; }
        if (entry.CreatedUtc.Length == 0) { error = "备份记录缺少时间。"; return false; }
        if (!Enum.IsDefined(entry.Status) || entry.ResourceIds is null || entry.ResourceIds.Any(r => !PluginContract.OperationResourceIds.Contains(r)))
        { error = "备份记录状态或资源列表非法。"; return false; }
        return true;
    }

    private bool TryReadUnlocked(out List<PluginBackupEntry> entries, out string error)
    {
        entries = [];
        error = "";
        try
        {
            try
            {
                if ((File.GetAttributes(_path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidDataException("备份路径不是普通文件。");
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("插件备份仓库过大。");
            entries = JsonSerializer.Deserialize<List<PluginBackupEntry>>(stream, JsonOptions)
                ?? throw new InvalidDataException("备份仓库不允许 null。");
            if (entries.Any(e => e is null)) throw new InvalidDataException("备份仓库包含 null 条目。");
            foreach (var entry in entries)
                if (!IsValid(entry, out error)) { entries = []; return false; }
            if (entries.GroupBy(e => (e.PluginId, e.BackupId)).Any(g => g.Count() > 1))
            { entries = []; error = "插件备份仓库包含重复 backupId。"; return false; }
            return true;
        }
        catch (Exception ex)
        {
            entries = [];
            error = "插件备份仓库不可读或损坏，已阻止自动处理：" + ex.Message;
            return false;
        }
    }

    private bool TryWriteUnlocked(List<PluginBackupEntry> entries, out string error)
    {
        error = "";
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory)) { error = "插件备份仓库没有父目录。"; return false; }
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
            error = "保存插件备份仓库失败：" + ex.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
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
                if (!acquired) { lockError = "等待插件备份仓库锁超时。"; return false; }
                return action();
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            lockError = "无法锁定插件备份仓库：" + ex.Message;
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
