using System.Text.Json;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

/// <summary>
/// 通用进程持久规则（P2）：按完整 EXE 路径匹配（不做名称通配），同一目标同一属性仅一条规则。
/// 规则文件独立于游戏帧格设置，原子写入 + 跨进程互斥；保存逻辑处理器描述（组掩码），
/// 启动后重新映射当前拓扑。规则引擎由 ProcmanEngine 持有：后端运行期间按 tick 应用到新实例。
/// </summary>
public sealed class ProcmanRuleStore
{
    public sealed class RuleFile
    {
        public List<ProcmanRuleEntry> Rules { get; set; } = [];
        public int SchemaVersion { get; set; } = 1;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private readonly Mutex _mutex;

    private readonly object _cacheGate = new();
    private List<ProcmanRuleEntry>? _cache;
    private (DateTime Utc, long Length, bool Exists) _cacheStamp;

    /// <summary>规则缓存重新载入次数（诊断与测试用）。</summary>
    internal int ParseCount { get; private set; }

    public ProcmanRuleStore(string filePath)
    {
        _filePath = filePath;
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(filePath).ToUpperInvariant())));
        _mutex = new Mutex(false, @"Global\DeltaNFD.ProcmanRules." + digest);
    }

    /// <summary>全部规则（读取失败返回空表，不抛——规则列表是只读展示）。</summary>
    public List<ProcmanRuleEntry> Load()
    {
        try
        {
            // 先取文件指纹再读取：读取期间若被其他进程改写，缓存带的是旧指纹，下一次必然重读。
            var stamp = ReadStamp();
            lock (_cacheGate)
            {
                if (_cache is not null && _cacheStamp == stamp) return _cache.Select(Copy).ToList();
            }
            List<ProcmanRuleEntry> rules = [];
            if (stamp.Exists)
            {
                using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                rules = JsonSerializer.Deserialize<RuleFile>(stream, JsonOptions)?.Rules ?? [];
            }
            lock (_cacheGate)
            {
                _cache = rules;
                _cacheStamp = stamp;
                ParseCount++;
            }
            return rules.Select(Copy).ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("进程规则文件无法读取，已暂停规则；原文件保留。", ex);
        }
    }

    /// <summary>保存规则（合并修改）：同一 EXE 路径 + 同一能力只保留一条；编辑时替换而非追加。</summary>
    public void Save(ProcmanRuleEntry rule)
    {
        if (string.IsNullOrWhiteSpace(rule.ExecutablePath) || !Path.IsPathRooted(rule.ExecutablePath))
            throw new ArgumentException("规则必须按完整 EXE 路径保存。");
        if (rule.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == false)
            throw new ArgumentException("规则目标必须是 EXE。");
        rule.ExecutablePath = Path.GetFullPath(rule.ExecutablePath);
        if (ProcmanProcessProtection.IsAceProtected(rule.ExecutablePath))
            throw new ArgumentException(ProcmanProcessProtection.AceReason);
        rule.UpdatedUtc = DateTimeOffset.UtcNow;

        using var lease = AcquireWriteLease();
        lock (this)
        {
            var file = LoadStrict();
            var existing = file.Rules.FirstOrDefault(r =>
                string.Equals(r.ExecutablePath, rule.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Capability, rule.Capability, StringComparison.Ordinal));
            if (existing is not null) file.Rules.Remove(existing);
            file.Rules.Add(rule);
            WriteAll(file);
        }
    }

    public bool Delete(string executablePath, string capability)
    {
        using var lease = AcquireWriteLease();
        lock (this)
        {
            var file = LoadStrict();
            var removed = file.Rules.RemoveAll(r =>
                string.Equals(r.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Capability, capability, StringComparison.Ordinal)) > 0;
            if (removed) WriteAll(file);
            return removed;
        }
    }

    /// <summary>启用规则时应用的候选能力集合（供引擎枚举目标进程后逐项应用）。</summary>
    public List<ProcmanRuleEntry> EnabledRules() => Load().Where(r => r.Enabled
        && !ProcmanProcessProtection.IsAceProtected(r.ExecutablePath)).ToList();

    private RuleFile LoadStrict()
    {
        if (!File.Exists(_filePath)) return new RuleFile();
        using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return JsonSerializer.Deserialize<RuleFile>(stream, JsonOptions) ?? new RuleFile();
    }

    private (DateTime Utc, long Length, bool Exists) ReadStamp()
    {
        var info = new FileInfo(_filePath);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length, true) : (DateTime.MinValue, 0L, false);
    }

    private void Invalidate()
    {
        lock (_cacheGate) _cache = null;
    }

    // 返回副本：调用方修改返回值不会污染缓存。
    private static ProcmanRuleEntry Copy(ProcmanRuleEntry rule) => new()
    {
        ExecutablePath = rule.ExecutablePath, Capability = rule.Capability, Value = rule.Value,
        Enabled = rule.Enabled, AutoTakeover = rule.AutoTakeover, UpdatedUtc = rule.UpdatedUtc,
    };

    private void WriteAll(RuleFile file)
    {
        // 写入前失效缓存；即使写入中途失败，指纹变化也会让下一次读取重新解析。
        Invalidate();
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tempPath = _filePath + ".tmp";
        using (var stream = File.Create(tempPath))
            JsonSerializer.Serialize(stream, file, JsonOptions);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private IDisposable AcquireWriteLease()
    {
        try
        {
            bool acquired;
            try { acquired = _mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("另一进程正在保存进程规则，请稍后重试。");
            return new WriteLease(_mutex);
        }
        catch (IOException) { throw; }
        catch { throw new IOException("获取进程规则锁失败。"); }
    }

    private sealed class WriteLease(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _mutex, null);
            if (value is null) return;
            try { value.ReleaseMutex(); } finally { }
        }
    }
}
