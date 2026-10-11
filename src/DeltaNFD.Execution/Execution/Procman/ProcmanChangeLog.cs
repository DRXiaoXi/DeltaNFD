using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaNFD.Execution.Procman;

/// <summary>
/// 通用进程修改记录：进程身份 + 属性 + 原值 + 当前值 + 控制方。
/// 记录按「PID+启动时间+路径+属性」唯一；写入前先落盘（写前意图），还原成功后才移除——
/// 崩溃后重启先核对遗留记录再恢复。原子写入 + 全局互斥（跨进程：前端/后端/恢复进程）。
/// </summary>
public sealed class ProcmanChangeLog
{
    public sealed record ChangeRecord(
        int Pid,
        long StartTimeUtcTicks,
        string ExecutablePath,
        string Capability,
        string OriginalValue,
        string AppliedValue,
        string Owner,
        DateTimeOffset AppliedUtc)
    {
        public string Key => ChangeKey(Pid, StartTimeUtcTicks, ExecutablePath, Capability);
        public ChangeRecord? Parent { get; init; }
    }

    public static string ChangeKey(int pid, long startTimeUtcTicks, string path, string capability)
        => $"{pid}|{startTimeUtcTicks}|{Path.GetFullPath(path).ToUpperInvariant()}|{capability}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private readonly Mutex _mutex;
    private readonly object _gate = new();
    private List<ChangeRecord> _records = [];

    public ProcmanChangeLog(string filePath)
    {
        _filePath = filePath;
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(filePath).ToUpperInvariant())));
        _mutex = new Mutex(false, @"Global\DeltaNFD.ProcmanLog." + digest);
        _records = LoadAll();
    }

    /// <summary>全部记录快照（含上次会话遗留——它们是恢复证据，不能默默清掉）。</summary>
    public IReadOnlyList<ChangeRecord> Records { get { lock (_gate) return _records.ToArray(); } }

    /// <summary>登记修改（写前意图：先落盘再执行写入）。同键已存在时报错——修改前必须先还原或明确覆盖。</summary>
    public void Add(ChangeRecord record)
    {
        using var lease = AcquireWriteLease();
        lock (_gate)
        {
            var all = LoadAll();
            if (all.Any(r => r.Key == record.Key))
                throw new InvalidOperationException("该进程属性已有修改记录，必须先还原或明确接管。");
            all.Add(record);
            WriteAll(all);
            _records = all;
        }
    }

    /// <summary>登记修改且允许覆盖既有记录（接管：原记录的 OriginalValue 保留为真正的原始值）。</summary>
    public void AddOrPreserveOriginal(ChangeRecord record)
    {
        using var lease = AcquireWriteLease();
        lock (_gate)
        {
            var all = LoadAll();
            var existing = all.FirstOrDefault(r => r.Key == record.Key);
            if (existing is not null)
                all.Remove(existing);
            // 接管时保留最早的 OriginalValue 与最早的 Owner（那是需要恢复到的真正原值）。
            var merged = existing is null
                ? record
                : record with { OriginalValue = existing.OriginalValue, Parent = existing.Parent };
            all.Add(merged);
            WriteAll(all);
            _records = all;
        }
    }

    /// <summary>还原成功后移除记录；进程已退出/PID 复用也移除（核对由调用方负责）。</summary>
    public bool Remove(ChangeRecord record)
    {
        using var lease = AcquireWriteLease();
        lock (_gate)
        {
            var all = LoadAll();
            var removed = all.RemoveAll(r => r.Key == record.Key) > 0;
            if (removed) { WriteAll(all); _records = all; }
            return removed;
        }
    }

    public void Replace(ChangeRecord record)
    {
        using var lease = AcquireWriteLease();
        lock (_gate)
        {
            var all = LoadAll();
            all.RemoveAll(r => r.Key.Equals(record.Key, StringComparison.OrdinalIgnoreCase));
            all.Add(record); WriteAll(all); _records = all;
        }
    }

    /// <summary>按进程身份取全部持有记录（身份必须完全一致，PID 复用后不命中）。</summary>
    public List<ChangeRecord> ForProcess(int pid, long startTimeUtcTicks, string path)
    {
        lock (_gate) return _records.Where(r => r.Pid == pid && r.StartTimeUtcTicks == startTimeUtcTicks
            && string.Equals(r.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public ChangeRecord? Find(int pid, long startTimeUtcTicks, string path, string capability)
        => ForProcess(pid, startTimeUtcTicks, path).FirstOrDefault(r => r.Capability == capability);

    private List<ChangeRecord> LoadAll()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                var directory = Path.GetDirectoryName(_filePath)!;
                if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, Path.GetFileName(_filePath) + ".corrupt-*").Any())
                    throw new InvalidDataException("存在历史损坏日志，必须先人工核对，不能视为无恢复记录。");
                return [];
            }
            using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var records = JsonSerializer.Deserialize<List<ChangeRecord>>(stream, JsonOptions) ?? throw new InvalidDataException("修改日志为空对象。");
            if (records.Count > 10000 || records.Any(r => r.Pid <= 0 || r.StartTimeUtcTicks <= 0
                || !Path.IsPathFullyQualified(r.ExecutablePath) || string.IsNullOrWhiteSpace(r.Capability))
                || records.Select(r => r.Key.ToUpperInvariant()).Distinct().Count() != records.Count)
                throw new InvalidDataException("修改日志身份/重复键无效，原文件保留。");
            return records;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("进程修改日志无法读取；禁止新写入，原文件保留，请核对备份。", ex);
        }
    }

    private void WriteAll(List<ChangeRecord> records)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tempPath = _filePath + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, records, JsonOptions);
            stream.Flush(true);
        }
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private IDisposable AcquireWriteLease()
    {
        try
        {
            bool acquired;
            try { acquired = _mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("另一进程正在保存进程修改记录，请稍后重试。");
            return new WriteLease(_mutex);
        }
        catch (IOException) { throw; }
        catch { throw new IOException("获取进程修改记录锁失败。"); }
    }

    private sealed class WriteLease(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _mutex, null);
            if (value is null) return;
            try { value.ReleaseMutex(); } finally { /* Mutex 由 owner 持有到进程结束，不 Dispose */ }
        }
    }
}
