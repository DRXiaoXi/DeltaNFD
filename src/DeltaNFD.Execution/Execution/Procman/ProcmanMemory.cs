using System.Diagnostics;
using DeltaNFD.Backends;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

public sealed partial class ProcmanEngine
{
    private int MemoryBatchLimit => _backgroundConfig.MemoryBatchLimit;
    internal Func<ProcmanProcessIdentity, long?>? MemoryReadForTests { get; set; }
    internal Func<IReadOnlyList<ProcmanProcessIdentity>>? MemoryIdentitiesForTests { get; set; }
    internal Func<ProcmanProcessIdentity, bool>? MemorySystemProcessForTests { get; set; }
    internal Func<ProcmanProcessIdentity, Func<bool>, ProcmanMemoryTrimItem>? MemoryTrimForTests { get; set; }
    internal Func<Func<bool>, ProcmanMemoryTrimReport>? MemoryGlobalTrimForTests { get; set; }
    internal TimeSpan MemorySampleDelayForTests { get; set; } = TimeSpan.Zero;
    private ProcmanMemoryPreview? _memoryPreview;
    private readonly Dictionary<string, DateTime> _lastMemoryTrim = new(StringComparer.OrdinalIgnoreCase);
    private static string MemoryKey(ProcmanProcessIdentity i) => $"{i.Pid}:{i.StartTimeUtcTicks}:{i.ExecutablePath}";
    private static ProcmanProcessIdentity MemoryIdentity(ProcessIdentityArgs i)
        => new(i.Pid, new DateTime(i.StartTimeUtcTicks, DateTimeKind.Utc), i.ExecutablePath);

    // 内存回收只枚举身份，不访问 TotalProcessorTime，不调用 CPU 采样器。
    private IReadOnlyList<ProcmanProcessIdentity> ReadMemorySample()
    {
        if (MemoryIdentitiesForTests is { } identities) return identities();
        if (SampleForTests is { } fixture) return fixture().Processes.Select(p => p.Identity).ToArray();
        var result = new List<ProcmanProcessIdentity>();
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                var identity = ProcmanIdentity.TryIdentify(process.Id);
                if (identity is not null) result.Add(identity);
            }
        return result;
    }
    private long? MemorySize(ProcmanProcessIdentity identity)
    {
        if (MemoryReadForTests is { } read) return read(identity);
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            return ProcmanIdentity.Verify(identity.Pid, identity.StartTimeUtcTicks, identity.ExecutablePath) ? process.WorkingSet64 : null;
        }
        catch { return null; }
    }
    private bool MemoryEligible(ProcmanProcessIdentity identity, IReadOnlyList<ProcmanProcessIdentity> sample)
    {
        if (_frozen || _backgroundLoadError.Length > 0 || _backgroundConfig.MemoryMode == ProcmanMemoryModes.Aggressive) return false;
        if (ProcmanProcessProtection.IsAceProtected(identity.ExecutablePath)) return false;
        if (Path.GetFileNameWithoutExtension(identity.ExecutablePath).Equals(DeltaForceLocator.GameProcessName, StringComparison.OrdinalIgnoreCase)) return false;
        if (_backgroundConfig.MemoryMode == ProcmanMemoryModes.DeltaStable)
        {
            if (MemorySystemProcessForTests?.Invoke(identity) ?? ProcmanBackgroundFilter.IsSystemProcess(identity)) return false;
            // A saved rule protects the executable even when disabled; recheck before each native trim.
            if (_rules.Load().Any(r => r.ExecutablePath.Equals(identity.ExecutablePath, StringComparison.OrdinalIgnoreCase))) return false;
        }
        else
        {
            if (IsBackgroundExcluded(identity.ExecutablePath) || ProcmanBackgroundFilter.IsToolProcess(identity.ExecutablePath)) return false;
            var foreground = ForegroundForTests?.Invoke() ?? NativeProcman.ForegroundPid();
            if (foreground <= 0 || identity.Pid == foreground || identity.Pid == Environment.ProcessId) return false;
            if (BackgroundExcludedForTests?.Invoke(identity) ?? ProcmanBackgroundFilter.IsExcluded(identity)) return false;
            if (_rules.EnabledRules().Any(r => r.ExecutablePath.Equals(identity.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                || _changeLog.ForProcess(identity.Pid, identity.StartTimeUtcTicks, identity.ExecutablePath)
                    .Any()) return false;
        }
        if (_lastMemoryTrim.TryGetValue(MemoryKey(identity), out var time)
            && (ClockForTests?.Invoke() ?? DateTime.UtcNow) - time < TimeSpan.FromSeconds(MemoryPolicy().MemoryCooldown(_backgroundConfig.MemoryCooldownSeconds))) return false;
        return sample.Contains(identity) && MemorySize(identity) is >= 0;
    }
    private ProcmanMemoryCandidate[] MemoryCandidates(IReadOnlyList<ProcmanProcessIdentity> sample)
    {
        var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
        foreach (var key in _lastMemoryTrim.Where(p => now - p.Value > TimeSpan.FromDays(2)).Select(p => p.Key).ToArray()) _lastMemoryTrim.Remove(key);
        var candidates = sample.Distinct().Where(i => MemoryEligible(i, sample))
            .Select(i => new ProcmanMemoryCandidate(new() { Pid = i.Pid, StartTimeUtcTicks = i.StartTimeUtcTicks, ExecutablePath = i.ExecutablePath },
                Path.GetFileName(i.ExecutablePath), MemorySize(i) ?? 0, 0)).OrderByDescending(p => p.WorkingSetBytes);
        return (_backgroundConfig.MemoryMode == ProcmanMemoryModes.DeltaStable ? candidates : candidates.Take(MemoryBatchLimit)).ToArray();
    }
    public async Task<ProcmanMemoryPreview> PreviewMemoryAsync(CancellationToken token = default)
    {
        if (MemorySampleDelayForTests > TimeSpan.Zero) await Task.Delay(MemorySampleDelayForTests, token);
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (MemoryPolicy().BlockReason(BackendCommands.CpuFrameMemoryClean) is { } block) return new("", DateTimeOffset.UtcNow, [], block);
            var mode = _backgroundConfig.MemoryMode;
            var candidates = mode == ProcmanMemoryModes.Aggressive ? [] : MemoryCandidates(ReadMemorySample());
            var note = mode switch
            {
                ProcmanMemoryModes.Aggressive => "激进回收：不统计进程，请求全局工作集及低优先级待备列表回收，忽略逐进程保护与排除名单，可能影响 ACE、游戏和系统。保留其他待备缓存，不强制写回修改页列表；需要管理员权限，仍可能造成换页或卡顿。",
                ProcmanMemoryModes.DeltaStable => "稳洲回收：跳过三角洲主进程、系统进程、ACE 保护进程和有规则的进程（含停用规则）；其余进程包括前台和工具。不受普通模式数量或排除名单限制，不清空全局待备缓存、不强制写回修改页列表。权限不足、身份不明、冷却中的进程跳过。",
                _ => $"普通回收：不设内存大小门槛、不检测 CPU 占用，每次最多 {MemoryBatchLimit} 个后台应用。保护游戏、工具、系统、前台、排除名单及已有设置/规则的进程。",
            };
            var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
            return _memoryPreview = new(Guid.NewGuid().ToString("D"), new DateTimeOffset(now, TimeSpan.Zero).AddMinutes(2), candidates,
                note + $" 最短重复间隔 {MemoryPolicy().MemoryCooldown(_backgroundConfig.MemoryCooldownSeconds) / 60d:0.#} 分钟；预览 2 分钟内有效。") { Mode = mode };
        }
    }
    public ProcmanMemoryTrimReport TrimMemory(ProcmanMemoryTrimArgs args, CancellationToken token = default)
    {
        lock (_gate)
        {
            if (MemoryPolicy().BlockReason(BackendCommands.CpuFrameMemoryClean) is { } block) return new([], block);
            var preview = _memoryPreview; var now = ClockForTests?.Invoke() ?? DateTime.UtcNow; var mode = _backgroundConfig.MemoryMode;
            if (preview is null || args.PreviewId != preview.Id || preview.Mode != mode || args.Processes is null
                || preview.ExpiresUtc <= new DateTimeOffset(now, TimeSpan.Zero)) return new([], "预览失效或模式已变化，请重新预览。");
            if (mode == ProcmanMemoryModes.Aggressive)
            {
                if (args.Processes.Count != 0) return new([], "全局回收不接受进程清单。");
                _memoryPreview = null;
                return TrimGlobalMemory(token, automatic: false);
            }
            var limit = mode == ProcmanMemoryModes.DeltaStable ? preview.Candidates.Count : MemoryBatchLimit;
            if (args.Processes.Count < 1 || args.Processes.Count > limit || args.Processes.Any(p => p is null)
                || args.Processes.Select(p => p.Pid).Distinct().Count() != args.Processes.Count
                || args.Processes.Any(p => !preview.Candidates.Any(c => c.Identity.Pid == p.Pid && c.Identity.StartTimeUtcTicks == p.StartTimeUtcTicks
                    && c.Identity.ExecutablePath.Equals(p.ExecutablePath, StringComparison.OrdinalIgnoreCase)))) return new([], "清单变化或请求超出确认范围，请重新预览。");
            _memoryPreview = null;
            return TrimMemoryBatch(args.Processes, token, automatic: false);
        }
    }
    private bool MemoryOperationAllowed(CancellationToken token, bool automatic) => !token.IsCancellationRequested && !_frozen
        && MemoryPolicy().BlockReason(BackendCommands.CpuFrameMemoryClean) is null
        && (!automatic || (_automaticMemoryActive && FrameAutomaticMemoryActive && !OfflineModeGuard.BlocksNormalAutomation
            && !MemoryPolicy().ManualMemoryCleanOnly && MemoryPressure() is { } pressure && pressure <= 100
            && pressure >= MemoryPolicy().MemoryThreshold(_backgroundConfig.MemoryPressurePercent)));
    private DateTime? _lastGlobalMemoryTrim;
    private ProcmanMemoryTrimReport TrimGlobalMemory(CancellationToken token, bool automatic)
    {
        var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
        if (_lastGlobalMemoryTrim is { } previous && now - previous < TimeSpan.FromSeconds(MemoryPolicy().MemoryCooldown(_backgroundConfig.MemoryCooldownSeconds)))
            return new([], "全局回收仍在用户设置的间隔内，未重复请求。");
        bool Guard() => _backgroundConfig.MemoryMode == ProcmanMemoryModes.Aggressive && MemoryOperationAllowed(token, automatic);
        if (!Guard()) return new([], "全局回收条件变化，未执行。");
        var report = (MemoryGlobalTrimForTests ?? ProcmanGlobalMemoryReclaimer.Trim)(Guard);
        _lastGlobalMemoryTrim = now;
        return report;
    }
    private ProcmanMemoryTrimReport TrimMemoryBatch(List<ProcessIdentityArgs> processes, CancellationToken token, bool automatic)
    {
        var mode = _backgroundConfig.MemoryMode; var stable = mode == ProcmanMemoryModes.DeltaStable;
        var before = ReadMemoryTelemetry(); var watch = Stopwatch.StartNew(); var sample = ReadMemorySample();
        var items = new List<ProcmanMemoryTrimItem>(); var targets = stable ? processes : processes.Take(MemoryBatchLimit);
        foreach (var selected in targets)
        {
            if (!MemoryOperationAllowed(token, automatic) || _backgroundConfig.MemoryMode != mode
                || (!stable && watch.Elapsed > TimeSpan.FromSeconds(2))) break;
            var identity = MemoryIdentity(selected);
            try
            {
                if (!MemoryEligible(identity, sample)) { items.Add(new(identity.Pid, Path.GetFileName(identity.ExecutablePath), "skipped", "当前不再满足回收条件。")); continue; }
                var leases = new List<(string Capability, ProcessPropertyGate.Lease Lease)>();
                try
                {
                    if (!stable) foreach (var capability in KnownCapabilities) leases.Add((capability, _properties.Enter(identity, capability)));
                    bool Owned() => stable || leases.All(l => l.Lease.Owner == "system");
                    if (!Owned()) { items.Add(new(identity.Pid, Path.GetFileName(identity.ExecutablePath), "skipped", "已有其他控制方，未操作。")); continue; }
                    bool Guard() => MemoryOperationAllowed(token, automatic) && _backgroundConfig.MemoryMode == mode && Owned() && MemoryEligible(identity, sample);
                    var result = (MemoryTrimForTests ?? ProcmanMemoryTrimmer.Trim)(identity, Guard);
                    if (result.Status is "completed" or "requested" || automatic) _lastMemoryTrim[MemoryKey(identity)] = ClockForTests?.Invoke() ?? DateTime.UtcNow;
                    items.Add(result);
                }
                finally { for (var i = leases.Count - 1; i >= 0; i--) leases[i].Lease.Dispose(); }
            }
            catch (Exception ex) { items.Add(new(identity.Pid, Path.GetFileName(identity.ExecutablePath), "failed", ex.Message)); }
        }
        return new(items, $"{(stable ? "稳洲回收" : "普通回收")}{(automatic ? "（自动）" : "（手动）")}已处理 {items.Count}/{processes.Count} 个进程。"
            + "工作集可能回涨，不因回涨或无明显效果追加退避；不能按工作集减少量推算实际物理内存释放量。", before, ReadMemoryTelemetry());
    }
}
