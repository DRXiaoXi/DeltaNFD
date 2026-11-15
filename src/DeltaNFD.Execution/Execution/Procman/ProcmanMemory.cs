using System.Diagnostics;
using System.Collections.Concurrent;
using DeltaNFD.Backends;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

public sealed partial class ProcmanEngine
{
    internal Func<ProcmanProcessIdentity, long?>? MemoryReadForTests { get; set; }
    internal Func<IReadOnlyList<ProcmanProcessIdentity>>? MemoryIdentitiesForTests { get; set; }
    internal Func<ProcmanProcessIdentity, bool>? MemorySystemProcessForTests { get; set; }
    internal Func<ProcmanProcessIdentity, Func<bool>, ProcmanMemoryTrimItem>? MemoryTrimForTests { get; set; }
    internal Func<Func<bool>, ProcmanMemoryTrimReport>? MemoryGlobalTrimForTests { get; set; }
    internal TimeSpan MemorySampleDelayForTests { get; set; } = TimeSpan.Zero;
    private ProcmanMemoryPreview? _memoryPreview;
    private readonly ConcurrentDictionary<string, DateTime> _lastMemoryTrim = new(StringComparer.OrdinalIgnoreCase);
    private static string MemoryKey(ProcmanProcessIdentity i) => $"{i.Pid}:{i.StartTimeUtcTicks}:{i.ExecutablePath}";
    private static ProcmanProcessIdentity MemoryIdentity(ProcessIdentityArgs i)
        => new(i.Pid, new DateTime(i.StartTimeUtcTicks, DateTimeKind.Utc), i.ExecutablePath);

    // 内存回收只枚举身份，不访问 TotalProcessorTime，不调用 CPU 采样器。
    private IReadOnlyList<ProcmanProcessIdentity> ReadMemorySample(Func<bool>? current = null)
    {
        if (MemoryIdentitiesForTests is { } identities) return identities();
        if (SampleForTests is { } fixture) return fixture().Processes.Select(p => p.Identity).ToArray();
        var result = new List<ProcmanProcessIdentity>();
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                if (current is not null && !current()) continue;
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
    internal int RuleParseCountForTests => _rules.ParseCount;
    private sealed record MemoryTarget(GameTarget Target, string CanonicalPath);
    private sealed record MemoryScan(ProcmanBackgroundConfig Config, MemoryTarget Target, List<ProcmanRuleEntry> Rules, int Foreground);
    private MemoryTarget CaptureMemoryTarget()
    {
        var target = FrameTarget;
        if (!FrameTargetCurrent(target)) throw new IOException("游戏目标尚未同步，已暂停逐进程回收。");
        return new(target, target.IsCustom ? FrameCanonical(target.ExecutablePath) : "");
    }
    private bool MemoryTargetCurrent(MemoryTarget target)
    {
        try { return FrameTargetCurrent(target.Target) && (!target.Target.IsCustom
            || FrameCanonical(target.Target.ExecutablePath).Equals(target.CanonicalPath, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }
    private bool MemoryTargetProtected(ProcmanProcessIdentity identity, MemoryTarget target)
    {
        if (Path.GetFileNameWithoutExtension(identity.ExecutablePath).Equals(DeltaForceLocator.GameProcessName, StringComparison.OrdinalIgnoreCase)) return true;
        if (!target.Target.IsCustom) return false;
        try { return FrameCanonical(identity.ExecutablePath).Equals(target.CanonicalPath, StringComparison.OrdinalIgnoreCase); }
        catch { return true; } // Unknown canonical identity never grants permission to reclaim.
    }
    // A scan shares one rule snapshot/foreground read. The native guard takes fresh ones.
    private MemoryScan NewScan(ProcmanBackgroundConfig config, MemoryTarget target)
        => new(config, target, _rules.Load(), config.MemoryMode == ProcmanMemoryModes.DeltaStable ? 0 : CurrentForegroundPid());
    private int CurrentForegroundPid() => ForegroundForTests?.Invoke() ?? NativeProcman.ForegroundPid();

    private bool MemoryEligible(ProcmanProcessIdentity identity, MemoryScan scan)
    {
        var config = scan.Config;
        if (Volatile.Read(ref _frozen) || Volatile.Read(ref _backgroundLoadError).Length > 0 || config.MemoryMode == ProcmanMemoryModes.Aggressive) return false;
        if (ProcmanProcessProtection.IsAceProtected(identity.ExecutablePath) || MemoryTargetProtected(identity, scan.Target)) return false;
        if (config.MemoryMode == ProcmanMemoryModes.DeltaStable)
        {
            if (MemorySystemProcessForTests?.Invoke(identity) ?? ProcmanBackgroundFilter.IsSystemProcess(identity)) return false;
            if (scan.Rules.Any(r => r.ExecutablePath.Equals(identity.ExecutablePath, StringComparison.OrdinalIgnoreCase))) return false;
        }
        else
        {
            if (config.ExcludedExecutablePaths.Contains(identity.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                || ProcmanBackgroundFilter.IsToolProcess(identity.ExecutablePath)) return false;
            if (scan.Foreground <= 0 || identity.Pid == scan.Foreground || identity.Pid == Environment.ProcessId) return false;
            if (BackgroundExcludedForTests?.Invoke(identity) ?? ProcmanBackgroundFilter.IsExcluded(identity)) return false;
            if (scan.Rules.Any(r => r.Enabled && r.ExecutablePath.Equals(identity.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                || _changeLog.ForProcess(identity.Pid, identity.StartTimeUtcTicks, identity.ExecutablePath).Any()) return false;
        }
        if (_lastMemoryTrim.TryGetValue(MemoryKey(identity), out var time)
            && (ClockForTests?.Invoke() ?? DateTime.UtcNow) - time < TimeSpan.FromSeconds(MemoryPolicy().MemoryCooldown(config.MemoryCooldownSeconds))) return false;
        return true;
    }

    private ProcmanMemoryCandidate[] MemoryCandidates(IReadOnlyList<ProcmanProcessIdentity> sample,
        ProcmanBackgroundConfig config, MemoryTarget target, Func<bool> current)
    {
        var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
        // A concurrent native completion may refresh the entry after enumeration.
        // Remove only the expired key/value pair, never that process's newer cooldown.
        foreach (var entry in _lastMemoryTrim.Where(p => now - p.Value > TimeSpan.FromDays(2)).ToArray())
            ((ICollection<KeyValuePair<string, DateTime>>)_lastMemoryTrim).Remove(entry);
        var scan = NewScan(config, target);
        var candidates = sample.Distinct().TakeWhile(_ => current()).Where(i => MemoryEligible(i, scan))
            .Select(i => (Identity: i, Size: MemorySize(i))).Where(c => c.Size is >= 0)
            .Select(c => new ProcmanMemoryCandidate(new() { Pid = c.Identity.Pid, StartTimeUtcTicks = c.Identity.StartTimeUtcTicks, ExecutablePath = c.Identity.ExecutablePath },
                Path.GetFileName(c.Identity.ExecutablePath), c.Size ?? 0, 0)).OrderByDescending(p => p.WorkingSetBytes);
        return (config.MemoryMode == ProcmanMemoryModes.DeltaStable ? candidates : candidates.Take(config.MemoryBatchLimit)).ToArray();
    }
    private MemoryTarget? _memoryPreviewTarget;
    public async Task<ProcmanMemoryPreview> PreviewMemoryAsync(CancellationToken token = default)
    {
        if (MemorySampleDelayForTests > TimeSpan.Zero) await Task.Delay(MemorySampleDelayForTests, token);
        ProcmanBackgroundConfig config; long revision;
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (MemoryPolicy().BlockReason(BackendCommands.CpuFrameMemoryClean) is { } block) return new("", DateTimeOffset.UtcNow, [], block);
            config = _backgroundConfig; revision = _memoryRevision;
        }
        using var targetLease = GameTargetService.Default.BeginOperation();
        var mode = config.MemoryMode;
        MemoryTarget? target;
        try { target = mode == ProcmanMemoryModes.Aggressive ? null : CaptureMemoryTarget(); }
        catch (Exception ex) { return new("", DateTimeOffset.UtcNow, [], ex.Message); }
        bool Current() => !token.IsCancellationRequested && revision == Volatile.Read(ref _memoryRevision)
            && (target is null || MemoryTargetCurrent(target));
        var candidates = mode == ProcmanMemoryModes.Aggressive ? [] : MemoryCandidates(ReadMemorySample(Current), config, target!, Current);
        lock (_gate)
        {
            if (!Current() || _memoryStopping > 0 || _memoryDisposed) return new("", DateTimeOffset.UtcNow, [], "回收条件或游戏目标已变化，请重新预览。");
            var note = mode switch
            {
                ProcmanMemoryModes.Aggressive => "激进回收：不统计进程，请求全局工作集及低优先级待备列表回收，忽略逐进程保护与排除名单，可能影响 ACE、游戏和系统。保留其他待备缓存，不强制写回修改页列表；需要管理员权限，仍可能造成换页或卡顿。",
                ProcmanMemoryModes.DeltaStable => "稳洲回收：跳过当前所选游戏、三角洲主进程、系统进程、ACE 保护进程和有规则的进程（含停用规则）；其余进程包括前台和工具。不受普通模式数量或排除名单限制，不清空全局待备缓存、不强制写回修改页列表。权限不足、身份不明、冷却中的进程跳过。",
                _ => $"普通回收：不设内存大小门槛、不检测 CPU 占用，每次最多 {config.MemoryBatchLimit} 个后台应用。保护当前所选游戏、三角洲、工具、系统、前台、排除名单及已有设置/规则的进程。",
            };
            var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
            _memoryPreviewTarget = target;
            return _memoryPreview = new(Guid.NewGuid().ToString("D"), new DateTimeOffset(now, TimeSpan.Zero).AddMinutes(2), candidates,
                note + $" 最短重复间隔 {MemoryPolicy().MemoryCooldown(config.MemoryCooldownSeconds) / 60d:0.#} 分钟；预览 2 分钟内有效。") { Mode = mode };
        }
    }
    public ProcmanMemoryTrimReport TrimMemory(ProcmanMemoryTrimArgs args, CancellationToken token = default)
    {
        using var targetLease = GameTargetService.Default.BeginOperation();
        MemoryWork work; MemoryTarget? target; List<ProcessIdentityArgs> processes;
        lock (_gate)
        {
            if (MemoryPolicy().BlockReason(BackendCommands.CpuFrameMemoryClean) is { } block) return new([], block);
            var preview = _memoryPreview; var now = ClockForTests?.Invoke() ?? DateTime.UtcNow; var mode = _backgroundConfig.MemoryMode;
            target = _memoryPreviewTarget;
            if (preview is null || args.PreviewId != preview.Id || preview.Mode != mode || args.Processes is null
                || preview.ExpiresUtc <= new DateTimeOffset(now, TimeSpan.Zero)
                || (mode != ProcmanMemoryModes.Aggressive && (target is null || !MemoryTargetCurrent(target))))
                return new([], "预览失效、模式或游戏目标已变化，请重新预览。");
            if (mode == ProcmanMemoryModes.Aggressive)
            {
                if (args.Processes.Count != 0) return new([], "全局回收不接受进程清单。");
            }
            else
            {
                var limit = mode == ProcmanMemoryModes.DeltaStable ? preview.Candidates.Count : _backgroundConfig.MemoryBatchLimit;
                if (args.Processes.Count < 1 || args.Processes.Count > limit || args.Processes.Any(p => p is null)
                    || args.Processes.Select(p => p.Pid).Distinct().Count() != args.Processes.Count
                    || args.Processes.Any(p => !preview.Candidates.Any(c => c.Identity.Pid == p.Pid && c.Identity.StartTimeUtcTicks == p.StartTimeUtcTicks
                        && c.Identity.ExecutablePath.Equals(p.ExecutablePath, StringComparison.OrdinalIgnoreCase)))) return new([], "清单变化或请求超出确认范围，请重新预览。");
            }
            processes = args.Processes.Select(p => p with { }).ToList();
            var admitted = BeginMemoryWork(automatic: false, token);
            if (admitted is null) return new([], "已有回收正在执行，或回收已停止/取消；未重复请求。");
            work = admitted;
            _memoryPreview = null;
        }
        try
        {
            return work.Config.MemoryMode == ProcmanMemoryModes.Aggressive ? TrimGlobalMemory(work)
                : TrimMemoryBatch(processes, ReadMemorySample(() => MemoryWorkCurrent(work)), target!, work);
        }
        finally { EndMemoryWork(work); }
    }
    private DateTime? _lastGlobalMemoryTrim;
    private ProcmanMemoryTrimReport TrimGlobalMemory(MemoryWork work)
    {
        var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
        lock (_gate)
        {
            if (_lastGlobalMemoryTrim is { } previous && now - previous < TimeSpan.FromSeconds(MemoryPolicy().MemoryCooldown(work.Config.MemoryCooldownSeconds)))
                return new([], "全局回收仍在用户设置的间隔内，未重复请求。");
            if (!MemoryOperationAllowed(work)) return new([], "全局回收条件变化，未执行。");
            _lastGlobalMemoryTrim = now;
        }
        return (MemoryGlobalTrimForTests ?? ProcmanGlobalMemoryReclaimer.Trim)(() => MemoryOperationAllowed(work));
    }
    private ProcmanMemoryTrimReport TrimMemoryBatch(List<ProcessIdentityArgs> processes,
        IReadOnlyList<ProcmanProcessIdentity> sample, MemoryTarget target, MemoryWork work)
    {
        var config = work.Config; var stable = config.MemoryMode == ProcmanMemoryModes.DeltaStable;
        var before = ReadMemoryTelemetry(); var watch = Stopwatch.StartNew(); var identities = sample.ToHashSet();
        var items = new List<ProcmanMemoryTrimItem>(); var targets = stable ? processes : processes.Take(config.MemoryBatchLimit);
        bool Allowed() => MemoryOperationAllowed(work) && MemoryTargetCurrent(target);
        foreach (var selected in targets)
        {
            if (!Allowed() || (!stable && watch.Elapsed > TimeSpan.FromSeconds(2))) break;
            var identity = MemoryIdentity(selected);
            try
            {
                if (!identities.Contains(identity) || !MemoryEligible(identity, NewScan(config, target)))
                { items.Add(new(identity.Pid, Path.GetFileName(identity.ExecutablePath), "skipped", "当前不再满足回收条件。")); continue; }
                var leases = new List<ProcessPropertyGate.Lease>();
                try
                {
                    if (!stable) foreach (var capability in KnownCapabilities) leases.Add(_properties.Enter(identity, capability));
                    bool Owned() => stable || leases.All(l => l.Owner == "system");
                    if (!Owned()) { items.Add(new(identity.Pid, Path.GetFileName(identity.ExecutablePath), "skipped", "已有其他控制方，未操作。")); continue; }
                    // Native trimmer still reads and verifies the same handle immediately before writing.
                    // Only the duplicate PID lookup/working-set read is removed here.
                    bool Guard() => Allowed() && Owned() && MemoryEligible(identity, NewScan(config, target))
                        && MemoryTargetCurrent(target) && MemoryWorkCurrent(work);
                    var result = (MemoryTrimForTests ?? ProcmanMemoryTrimmer.Trim)(identity, Guard);
                    if (result.Status is "completed" or "requested" || work.Automatic)
                        _lastMemoryTrim[MemoryKey(identity)] = ClockForTests?.Invoke() ?? DateTime.UtcNow;
                    items.Add(result);
                }
                finally { for (var i = leases.Count - 1; i >= 0; i--) leases[i].Dispose(); }
            }
            catch (Exception ex) { items.Add(new(identity.Pid, Path.GetFileName(identity.ExecutablePath), "failed", ex.Message)); }
        }
        return new(items, $"{(stable ? "稳洲回收" : "普通回收")}{(work.Automatic ? "（自动）" : "（手动）")}已处理 {items.Count}/{processes.Count} 个进程。"
            + "工作集可能回涨，不因回涨或无明显效果追加退避；不能按工作集减少量推算实际物理内存释放量。", before, ReadMemoryTelemetry());
    }
}
