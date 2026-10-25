using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DeltaNFD.Inspection;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.ViewModels;

internal static class DashboardMemoryHealthChecks
{
    private const ulong GB = DashboardMemoryHealth.GB;
    private static int _count;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); _count++; }
    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        internal void Advance(double seconds) => Ticks += (long)(seconds * TimeSpan.TicksPerSecond);
    }
    internal static void Run()
    {
        _count = 0;
        foreach (var state in Enum.GetValues<DashboardHealthState>())
            Check(DashboardMemoryHealth.SuggestRecovery(state) == (state is DashboardHealthState.Warning or DashboardHealthState.Abnormal),
                "only actual low-memory yellow/red states offer recovery guidance: " + state);
        DashboardMemoryReading Evaluate(ulong available, bool? game) => DashboardMemoryHealth.Evaluate(new(new(32 * GB, available)), game);
        foreach (var (available, expected) in new (ulong, DashboardHealthState)[]
        {
            (9 * GB, DashboardHealthState.Normal), (8 * GB, DashboardHealthState.Normal),
            (8 * GB - 1, DashboardHealthState.Warning), (2 * GB, DashboardHealthState.Warning),
            (GB, DashboardHealthState.Warning), (GB - 1, DashboardHealthState.Warning), (0, DashboardHealthState.Warning),
        }) Check(Evaluate(available, false).State == expected, "idle strict 8 GB boundary: " + available);
        foreach (var (available, expected) in new (ulong, DashboardHealthState)[]
        {
            (3 * GB, DashboardHealthState.Normal), (2 * GB, DashboardHealthState.Normal),
            (2 * GB - 1, DashboardHealthState.Warning), (GB, DashboardHealthState.Warning),
            (GB - 1, DashboardHealthState.Abnormal), (0, DashboardHealthState.Abnormal),
        }) Check(Evaluate(available, true).State == expected, "game strict 2/1 GB boundaries: " + available);
        Check(Evaluate(3 * GB, false).State == DashboardHealthState.Warning && Evaluate(3 * GB, true).State == DashboardHealthState.Normal, "same reading changes policy with game state");
        Check(Evaluate(8 * GB - 1, false).Text.Contains("7.99 GB"), "rounded display cannot show threshold as met");
        Check(Evaluate(0, null).State == DashboardHealthState.Information && !Evaluate(0, null).Text.Contains("未运行"), "unknown game cannot pretend stopped or healthy");
        Check(DashboardMemoryHealth.Evaluate(new(null, "fixture error"), true).State == DashboardHealthState.Information, "failed read is unconfirmed, not low memory");
        Check(DashboardMemoryHealth.Evaluate(new(new(0, 0)), false).State == DashboardHealthState.Information, "zero total is invalid");
        Check(DashboardMemoryHealth.Evaluate(new(new(GB, 2 * GB)), true).State == DashboardHealthState.Information, "impossible available memory is invalid");
        Check(DashboardMemoryHealth.PausedSummary(DashboardHealthState.Abnormal, "体检暂停").Contains("严重不足"), "critical memory remains visible while components paused");
        Check(DashboardMemoryHealth.PausedSummary(DashboardHealthState.Warning, "体检暂停").Contains("提醒"), "memory warning remains visible while components paused");
        Check(DashboardHealthPresentation.Summary(false, DashboardHealthState.Normal, DashboardHealthState.Normal, DashboardHealthState.Normal, DashboardHealthState.Warning) == "1 项提醒", "memory contributes to homepage summary");

        var clock = new Clock(); var reads = 0; var fail = false;
        var sampler = new DashboardMemorySampler(() => { reads++; if (fail) throw new IOException("fixture"); return new(new(32 * GB, 3 * GB)); }, clock);
        Check(sampler.Refresh(false).State == DashboardHealthState.Warning && reads == 1, "first memory read");
        for (var i = 0; i < 100; i++) sampler.Refresh(false);
        Check(reads == 1, "heartbeat burst reuses reading");
        clock.Advance(1.999); sampler.Refresh(false); Check(reads == 1, "no extra sample before two seconds");
        clock.Advance(0.001); sampler.Refresh(false); Check(reads == 2, "refresh at two seconds");
        Check(sampler.Refresh(true).State == DashboardHealthState.Normal && reads == 3, "game transition immediately re-evaluates and reads");
        sampler.Refresh(true, true); Check(reads == 4, "manual refresh bypasses throttle");
        fail = true; Check(sampler.Refresh(true, true).State == DashboardHealthState.Information, "exception cannot preserve misleading healthy state");
        fail = false; Check(sampler.Refresh(true, true).State == DashboardHealthState.Normal, "read recovers without starting executor");

        var queued = new Queue<Action>(); var observerReads = 0; var updates = 0; bool? game = false;
        var observer = new DashboardMemoryObserver(new(() => { observerReads++; return new(new(32 * GB, 3 * GB)); }, clock), () => game,
            action => { queued.Enqueue(action); return true; });
        DashboardMemoryReading? latest = null;
        observer.Updated += reading => { updates++; latest = reading; };
        observer.Start(); for (var i = 0; i < 100; i++) observer.Pulse();
        Check(queued.Count == 1, "coalesce queued heartbeat notifications");
        observer.Stop(); queued.Dequeue()(); Check(observerReads == 0 && updates == 0, "leaving page cancels late queued read");
        observer.Start(); var old = queued.Dequeue(); observer.Stop(); observer.Start(); old();
        Check(observerReads == 0 && queued.Count == 1, "late old page cannot invalidate new generation");
        queued.Dequeue()(); Check(observerReads == 1 && latest?.State == DashboardHealthState.Warning, "reentry takes fresh reading");
        game = true; observer.Pulse(); queued.Dequeue()(); Check(latest?.State == DashboardHealthState.Normal && observerReads == 2, "existing pulse switches to in-game policy");
        game = null; observer.Pulse(); queued.Dequeue()(); Check(latest?.State == DashboardHealthState.Information, "disconnect invalidates memory grade");
        game = false; observer.Pulse(); queued.Dequeue()(); Check(latest?.State == DashboardHealthState.Warning, "reconnect applies idle policy");
        observer.Pulse(); observer.Pulse(true); queued.Dequeue()(); Check(observerReads == 5, "manual force survives notification coalescing");
        observer.Stop(); var before = observerReads; for (var i = 0; i < 100; i++) observer.Pulse(true);
        Check(queued.Count == 0 && observerReads == before, "unloaded homepage has no memory work");
        var accepted = false;
        var rejected = new DashboardMemoryObserver(new(() => new(new(32 * GB, 3 * GB)), clock), () => false,
            action => { if (!accepted) return false; queued.Enqueue(action); return true; });
        rejected.Start(); accepted = true; rejected.Pulse(); Check(queued.Count == 1, "dispatcher refusal does not wedge future updates");
        queued.Dequeue()(); rejected.Stop();

        var imports = typeof(PhysicalMemoryInspection).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<DllImportAttribute>()).Where(a => a is not null).ToArray();
        Check(imports.Length == 1 && imports[0]!.EntryPoint == "GlobalMemoryStatusEx", "memory inspection has only the read-only native entry point");
        var sessions = BackendSessions.Snapshot().Count;
        var timer = Stopwatch.StartNew(); var live = PhysicalMemoryInspection.Read(); timer.Stop();
        Check(live.Reading is { IsValid: true }, "native physical-memory read is valid");
        Check(BackendSessions.Snapshot().Count == sessions, "memory inspection starts no backend");
        Console.WriteLine($"主页内存检测检查通过：{_count} 项；实际只读查询 {timer.Elapsed.TotalMilliseconds:0.###} ms；未启动栏目后端或回收内存。");
    }
}
