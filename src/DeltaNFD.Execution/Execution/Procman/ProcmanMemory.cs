using System.Diagnostics;
using DeltaNFD.Backends;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

public sealed partial class ProcmanEngine
{
    internal const int MemoryBatchLimit = 3;
    internal const long MinimumTrimWorkingSet = 256L * 1024 * 1024;
    internal Func<ProcmanProcessIdentity, long?>? MemoryReadForTests { get; set; }
    internal Func<ProcmanProcessIdentity, Func<bool>, ProcmanMemoryTrimItem>? MemoryTrimForTests { get; set; }
    internal TimeSpan MemorySampleDelayForTests { get; set; } = TimeSpan.FromSeconds(1);
    private ProcmanMemoryPreview? _memoryPreview;
    private readonly Dictionary<string, DateTime> _lastMemoryTrim = new(StringComparer.OrdinalIgnoreCase);

    private static string MemoryKey(ProcmanProcessIdentity identity) => $"{identity.Pid}:{identity.StartTimeUtcTicks}:{identity.ExecutablePath}";
    private static ProcmanProcessIdentity MemoryIdentity(ProcessIdentityArgs identity) =>
        new(identity.Pid, new DateTime(identity.StartTimeUtcTicks, DateTimeKind.Utc), identity.ExecutablePath);
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

    private bool MemoryEligible(ProcmanProcessIdentity identity, ProcmanCpuSample sample)
    {
        if (_frozen || IsBackgroundExcluded(identity.ExecutablePath) || ProcmanResponsivenessFilter.IsToolProcess(identity.ExecutablePath)
            || Path.GetFileNameWithoutExtension(identity.ExecutablePath).Equals(DeltaForceLocator.GameProcessName, StringComparison.OrdinalIgnoreCase)) return false;
        var foreground = ForegroundForTests?.Invoke() ?? NativeProcman.ForegroundPid();
        if (foreground <= 0 || identity.Pid == foreground || identity.Pid == Environment.ProcessId) return false;
        if (ResponsivenessExcludedForTests?.Invoke(identity) ?? ProcmanResponsivenessFilter.IsExcluded(identity)) return false;
        if (_rules.EnabledRules().Any(r => r.ExecutablePath.Equals(identity.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            || _changeLog.ForProcess(identity.Pid, identity.StartTimeUtcTicks, identity.ExecutablePath)
                .Any(r => r.Owner != ProcmanControlCoordinator.OwnerResponsiveness || r.Parent is not null)) return false;
        if (_lastMemoryTrim.TryGetValue(MemoryKey(identity), out var time) && (ClockForTests?.Invoke() ?? DateTime.UtcNow) - time < TimeSpan.FromSeconds(Math.Max(600, MemoryPolicy().MemoryCooldownSeconds))) return false;
        if (_memoryReboundUntil.TryGetValue(MemoryKey(identity), out var until) && (ClockForTests?.Invoke() ?? DateTime.UtcNow) < until) return false;
        return sample.Processes.Any(p => p.Identity == identity) && MemorySize(identity) >= MinimumTrimWorkingSet;
    }

    private ProcmanMemoryCandidate[] MemoryCandidates(ProcmanCpuSample sample)
    {
        var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
        foreach (var key in _lastMemoryTrim.Where(p => now - p.Value > TimeSpan.FromDays(2)).Select(p => p.Key).ToArray()) _lastMemoryTrim.Remove(key);
        foreach (var key in _memoryReboundUntil.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) _memoryReboundUntil.Remove(key);
        return sample.Processes.Select(p => p.Identity).Distinct().Where(p => MemoryEligible(p, sample))
            .Select(p => new ProcmanMemoryCandidate(new() { Pid = p.Pid, StartTimeUtcTicks = p.StartTimeUtcTicks, ExecutablePath = p.ExecutablePath },
                Path.GetFileName(p.ExecutablePath), MemorySize(p) ?? 0, sample.Processes.First(s => s.Identity == p).Cpu))
            .OrderByDescending(p => p.WorkingSetBytes).Take(MemoryBatchLimit).ToArray();
    }

    public async Task<ProcmanMemoryPreview> PreviewMemoryAsync(CancellationToken token = default)
    {
        lock (_gate) _ = SampleForTests?.Invoke() ?? _sampler.Sample();
        await Task.Delay(MemorySampleDelayForTests, token);
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (MemoryPolicy().BlockReason(BackendCommands.CpuFrameMemoryClean) is { } block)
                return new("", DateTimeOffset.UtcNow, [], block);
            var sample = SampleForTests?.Invoke() ?? _sampler.Sample();
            var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
            var candidates = MemoryCandidates(sample);
            _memoryPreview = new(Guid.NewGuid().ToString("D"), new DateTimeOffset(now, TimeSpan.Zero).AddMinutes(2), candidates,
                "列出工作集至少 256 MiB 的后台应用，不限制 CPU 占用，每次最多 3 个。保护游戏、工具、系统、前台、排除名单及已有设置/规则的进程；预览 2 分钟内有效。");
            return _memoryPreview;
        }
    }

    public ProcmanMemoryTrimReport TrimMemory(ProcmanMemoryTrimArgs args, CancellationToken token = default)
    {
        lock (_gate)
        {
            if (MemoryPolicy().BlockReason(BackendCommands.CpuFrameMemoryClean) is { } block) return new([], block);
            var preview = _memoryPreview;
            var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
            if (preview is null || args.PreviewId != preview.Id || preview.ExpiresUtc <= new DateTimeOffset(now, TimeSpan.Zero)
                || args.Processes is null || args.Processes.Count is < 1 or > MemoryBatchLimit || args.Processes.Any(p => p is null)
                || args.Processes.Select(p => p.Pid).Distinct().Count() != args.Processes.Count
                || args.Processes.Any(p => !preview.Candidates.Any(c => c.Identity.Pid == p.Pid && c.Identity.StartTimeUtcTicks == p.StartTimeUtcTicks
                    && c.Identity.ExecutablePath.Equals(p.ExecutablePath, StringComparison.OrdinalIgnoreCase))))
                return new([], "预览失效、清单变化或请求超出确认范围，请重新预览。");
            _memoryPreview = null; // Single use: do not replay after a timeout or repeated click.
            return TrimMemoryBatch(args.Processes, token, automatic: false);
        }
    }

    // Caller holds _gate. Automatic batches never consume/replace a user's manual preview.
    private ProcmanMemoryTrimReport TrimMemoryBatch(List<ProcessIdentityArgs> processes, CancellationToken token, bool automatic)
    {
        var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
        var beforeMemory = ReadMemoryTelemetry();
        var budget = Stopwatch.StartNew();
        var items = new List<ProcmanMemoryTrimItem>();
        foreach (var selected in processes)
        {
            if (token.IsCancellationRequested || budget.Elapsed > TimeSpan.FromSeconds(2)) break;
            var identity = MemoryIdentity(selected);
            try
            {
                var sample = SampleForTests?.Invoke() ?? _sampler.Sample();
                if (!MemoryEligible(identity, sample)) { items.Add(new(identity.Pid, Path.GetFileName(identity.ExecutablePath), "skipped", "当前不再满足后台回收条件。")); continue; }
                var leases = new List<(string Capability, ProcessPropertyGate.Lease Lease)>();
                try
                {
                    // Do not race a manual/rule/game writer holding any property of this identity.
                    foreach (var capability in KnownCapabilities) leases.Add((capability, _properties.Enter(identity, capability)));
                    bool OwnedByUs() => leases.All(l => l.Lease.Owner == "system"
                        || (l.Capability == CapabilityPriority && l.Lease.Owner == ProcmanControlCoordinator.OwnerResponsiveness));
                    if (!OwnedByUs()) { items.Add(new(identity.Pid, Path.GetFileName(identity.ExecutablePath), "skipped", "已有其他控制方，未操作。")); continue; }
                    bool Guard() => !token.IsCancellationRequested && MemoryPolicy().BlockReason(BackendCommands.CpuFrameMemoryClean) is null
                        && (!automatic || (_automaticMemoryActive && !OfflineModeGuard.BlocksNormalAutomation && !MemoryPolicy().ManualMemoryCleanOnly
                            && MemoryPressure() is { } pressure && pressure <= 100 && pressure >= MemoryPolicy().MemoryThreshold(_backgroundConfig.MemoryPressurePercent)))
                        && OwnedByUs() && MemoryEligible(identity, SampleForTests?.Invoke() ?? _sampler.Sample());
                    var result = (MemoryTrimForTests ?? ProcmanMemoryTrimmer.Trim)(identity, Guard);
                    if (result.Status is "completed" or "requested")
                    {
                        _lastMemoryTrim[MemoryKey(identity)] = now;
                        if (automatic && result.BeforeBytes is > 0 && result.AfterBytes is { } after && after < result.BeforeBytes * .95)
                            _memoryObservations.Add((identity, result.BeforeBytes.Value, now));
                    }
                    else if (automatic) _lastMemoryTrim[MemoryKey(identity)] = now; // Denied targets are not retried each round.
                    items.Add(result);
                }
                finally { for (var i = leases.Count - 1; i >= 0; i--) leases[i].Lease.Dispose(); }
            }
            catch (Exception ex) { items.Add(new(identity.Pid, Path.GetFileName(identity.ExecutablePath), "failed", ex.Message)); }
        }
        return new(items, $"{(automatic ? "自动回收" : "手动回收")}已处理 {items.Count}/{processes.Count} 个进程。仅回收工作集，不结束进程、不清空系统缓存；内存可能回涨。不能按工作集减少量推算实际物理内存释放量。", beforeMemory, ReadMemoryTelemetry());
    }
}
