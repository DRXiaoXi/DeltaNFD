using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services.Plugins;

public sealed record PluginLogEntry
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset TimeUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string Phase { get; init; }
    public string Status { get; init; } = "";
    public string PluginId { get; init; } = "";
    public string PluginVersion { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string RequestId { get; init; } = "";
    public string OperationId { get; init; } = "";
    public string BackupId { get; init; } = "";
    public long? ElapsedMs { get; init; }
    public string ErrorCode { get; init; } = "";
    public string ExceptionType { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed record PluginHistorySnapshot(IReadOnlyList<PluginLogEntry> Entries, string Warning, bool Truncated);

public static class PluginDiagnostics
{
    internal const int MaxFileBytes = 1024 * 1024;
    internal const int MaxArchives = 3;
    internal const int MaxReadEntries = 1000;
    private const int MaxLineChars = 16 * 1024;
    private static readonly AsyncLocal<string?> DirectoryScope = new();
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> WriteErrors = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string LogDirectory => DirectoryScope.Value ?? Path.Combine(Log.LogDirectory, "plugins");
    public static string CurrentPath => Path.Combine(LogDirectory, "plugins-current.jsonl");

    internal static IDisposable UseDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("日志路径须为绝对路径。");
        var old = DirectoryScope.Value;
        DirectoryScope.Value = Path.GetFullPath(directory);
        return new Scope(() => DirectoryScope.Value = old);
    }

    private sealed class Scope(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    public static string GetWriteError(string directory)
    {
        lock (Gate) return WriteErrors.GetValueOrDefault(Path.GetFullPath(directory), "");
    }

    public static void Record(PluginLogEntry entry)
    {
        try
        {
            var root = LogDirectory;
            WithMutex(root, () =>
            {
                ValidatePath(root);
                Directory.CreateDirectory(root);
                ValidatePath(root);
                var bytes = Encoding.UTF8.GetBytes(FormatLine(entry) + "\n");
                var path = Path.Combine(root, "plugins-current.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaxFileBytes)
                    File.Move(path, Path.Combine(root, "plugins-history-" + DateTime.UtcNow.Ticks + "-" + Guid.NewGuid().ToString("N") + ".jsonl"));
                foreach (var old in Archives(root).Skip(MaxArchives))
                    if ((old.Attributes & FileAttributes.ReparsePoint) == 0) old.Delete();
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            });
            lock (Gate) WriteErrors.Remove(root);
        }
        catch (Exception ex)
        {
            // Exception messages may include private paths or backend data.
            lock (Gate)
            {
                if (WriteErrors.Count >= 32) WriteErrors.Remove(WriteErrors.Keys.First());
                WriteErrors[LogDirectory] = "插件诊断日志写入失败：" + ex.GetType().Name + "（操作结果不受影响）";
            }
        }
    }

    public static void Record(string phase, string status, string detail = "", string pluginId = "", string pluginVersion = "",
        string sessionId = "", string requestId = "", string operationId = "", string backupId = "", long? elapsedMs = null,
        string errorCode = "", string exceptionType = "") => Record(new PluginLogEntry
        {
            Phase = phase, Status = status, Detail = detail, PluginId = pluginId, PluginVersion = pluginVersion,
            SessionId = sessionId, RequestId = requestId, OperationId = operationId, BackupId = backupId,
            ElapsedMs = elapsedMs, ErrorCode = errorCode, ExceptionType = exceptionType,
        });

    public static List<PluginLogEntry> ReadByRequest(string requestId) => string.IsNullOrEmpty(requestId) ? [] : ReadRecent(requestId);

    public static List<PluginLogEntry> ReadRecent(string? requestId = null) => ReadSnapshot(requestId).Entries.ToList();

    public static PluginHistorySnapshot ReadSnapshot(string? requestId = null)
    {
        var result = new PriorityQueue<PluginLogEntry, long>();
        var invalid = 0;
        var skipped = 0;
        var matched = 0;
        var warning = "";
        try
        {
            var root = LogDirectory;
            WithMutex(root, () =>
            {
                ValidatePath(root);
                if (!Directory.Exists(root)) return;
                var paths = Archives(root).Take(MaxArchives).Select(f => f.FullName).Prepend(Path.Combine(root, "plugins-current.jsonl"));
                foreach (var path in paths)
                {
                    if (!File.Exists(path)) continue;
                    if (new FileInfo(path).Length > MaxFileBytes || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                    foreach (var line in File.ReadLines(path))
                    {
                        if (!TryParse(line, out var entry)) { invalid++; continue; }
                        if (requestId is null || entry.RequestId == requestId)
                        {
                            matched++;
                            result.Enqueue(entry, entry.TimeUtc.UtcTicks);
                            if (result.Count > MaxReadEntries) result.Dequeue();
                        }
                    }
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        { warning = "历史读取未完成：" + ex.GetType().Name; }
        if (invalid > 0 || skipped > 0) warning += $" 跳过 {invalid} 条损坏记录、{skipped} 个不可读取的日志文件。";
        return new(result.UnorderedItems.Select(i => i.Element).OrderByDescending(e => e.TimeUtc).ToArray(), warning.Trim(), matched > MaxReadEntries);
    }

    internal static string FormatLine(PluginLogEntry entry) => JsonSerializer.Serialize(Sanitize(entry), JsonOptions);

    internal static bool TryParse(string line, out PluginLogEntry entry)
    {
        entry = null!;
        if (string.IsNullOrEmpty(line) || line.Length > MaxLineChars) return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1)) return false;
            if (!document.RootElement.TryGetProperty("timeUtc", out _) || !document.RootElement.TryGetProperty("schemaVersion", out _)) return false;
            var parsed = JsonSerializer.Deserialize<PluginLogEntry>(line, JsonOptions);
            if (parsed is null || parsed.SchemaVersion != 1 || string.IsNullOrWhiteSpace(parsed.Phase) ||
                parsed.TimeUtc == default || parsed.ElapsedMs < 0) return false;
            entry = Sanitize(parsed);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return false; }
    }

    private static PluginLogEntry Sanitize(PluginLogEntry entry)
    {
        static string Clean(string? text, int length) => new((text ?? "").Where(c => !char.IsControl(c)).Take(length).ToArray());
        static string Identifier(string? text) => Regex.IsMatch(text ?? "", @"\A[a-zA-Z0-9_.-]{0,128}\z", RegexOptions.CultureInvariant)
            ? text ?? "" : "invalid-field";
        return entry with
        {
            SchemaVersion = 1, Phase = Identifier(entry.Phase), Status = Identifier(entry.Status), PluginId = Identifier(entry.PluginId),
            PluginVersion = Identifier(entry.PluginVersion), SessionId = Identifier(entry.SessionId), RequestId = Identifier(entry.RequestId),
            OperationId = Identifier(entry.OperationId), BackupId = string.IsNullOrEmpty(entry.BackupId) ? "" :
                Regex.IsMatch(entry.BackupId, @"\Abackup-[0-9a-f]{20}\z", RegexOptions.CultureInvariant) ? entry.BackupId :
                    "backup-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.BackupId))).ToLowerInvariant()[..20],
            ExceptionType = Identifier(entry.ExceptionType),
            ErrorCode = Identifier(entry.ErrorCode), Detail = Clean(Redact(Clean(entry.Detail, 4096)), 512),
            ElapsedMs = entry.ElapsedMs is < 0 ? null : entry.ElapsedMs,
        };
    }

    internal static string BackendCode(string code) => code switch
    {
        "OK" or "INVALID_PACKAGE" or "INCOMPATIBLE" or "UNTRUSTED" or "PROTOCOL_ERROR" or "UNAUTHORIZED" or "BUSY" or
        "TARGET_CHANGED" or "TIMEOUT" or "CANCELLED" or "BACKEND_CRASHED" or "IDENTITY_UNKNOWN" or "RESTORE_FAILED" or
        "OFFLINE_UNSUPPORTED" or "RESULT_UNKNOWN" => code,
        _ => "PLUGIN_OTHER",
    };

    private static string Redact(string text)
    {
        try
        {
            var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            var timeout = TimeSpan.FromMilliseconds(50);
            text = Regex.Replace(text, @"\bBearer\s+\S+", "Bearer [redacted]", options, timeout);
            text = Regex.Replace(text, "(?:nonce|token|password|secret|api[_-]?key)\\s*[\"']?\\s*[:=]\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s,;]+)", "[redacted]", options, timeout);
            text = Regex.Replace(text, @"(?:[A-Za-z]:[\\/]|\\\\)[^\r\n""<>|]*", "[path]", options, timeout);
            return text;
        }
        catch (RegexMatchTimeoutException) { return "[redacted]"; }
    }

    private static IEnumerable<FileInfo> Archives(string root) => new DirectoryInfo(root).EnumerateFiles("plugins-history-*.jsonl")
        .Take(256)
        .Where(f => Regex.IsMatch(f.Name, @"\Aplugins-history-[0-9]{1,19}-[0-9a-f]{32}\.jsonl\z", RegexOptions.CultureInvariant))
        .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal);

    private static void ValidatePath(string root)
    {
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("日志路径含重解析目录。");
        if (File.Exists(root)) throw new IOException("日志目录被文件占用。");
        var path = Path.Combine(root, "plugins-current.jsonl");
        if (Directory.Exists(path) || File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("日志文件路径无效。");
    }

    private static void WithMutex(string root, Action action)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..20];
        using var mutex = new Mutex(false, "Local\\DeltaNFD_PluginLogs_" + digest);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("日志仓库忙。");
            action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    internal static void ConfigureForTests(string directory) => DirectoryScope.Value = Path.GetFullPath(directory);
}
