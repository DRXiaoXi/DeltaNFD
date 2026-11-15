using System.Text.Json;
using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

internal static class DpcAlgorithmChecks
{
    internal static void Run()
    {
        int n = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); n++; }
        bool Throws(Action action) { try { action(); return false; } catch { return true; } }
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-dpc-algorithm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var marker = Guid.NewGuid(); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), marker.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, marker);
        Check(!JsonSerializer.Deserialize<OptimalClockArgs>("{}")!.DpcAlgorithmScan, "Old clients retain basic DPC behavior by default");
        Check(JsonSerializer.Deserialize<OptimalClockArgs>(JsonSerializer.Serialize(new OptimalClockArgs { DpcAlgorithmScan = true }))!.DpcAlgorithmScan,
            "Algorithm opt-in survives IPC serialization");

        var world = new World(); var dpc = new Holder(world, true); var fine = new Holder(world, false);
        var combined = new DpcCombinedHolder(dpc, fine);
        Check(combined.Ensure(10000) && combined.DpcProcess && !combined.DpcAssisted && fine.Starts == 0, "Basic mode has only original DPC");
        combined.Stop(); combined.AlgorithmMode = true;
        Check(combined.Ensure(5200) && dpc.Resolution100ns == 10000 && fine.Resolution100ns == 5200 && combined.Holding, "Two independent request values are kept distinct");
        Check(combined.Granted100ns == 5200 && !combined.DpcProcess && combined.DpcAssisted, "Grant belongs to extra request, not DPC");
        var starts = (dpc.Starts, fine.Starts);
        Check(combined.Ensure(5200) && starts == (dpc.Starts, fine.Starts), "Same value is idempotent");
        fine.FailStop = true;
        Check(Throws(combined.Stop) && !dpc.Holding && fine.Holding, "DPC cleanup still attempted when extra request cleanup fails");
        fine.FailStop = false; combined.Stop();
        Check(!combined.HasCleanupResponsibility, "Pending extra cleanup can be retried");
        combined.Ensure(5200); dpc.FailStop = true;
        Check(Throws(combined.Stop) && !fine.Holding && dpc.Holding, "Extra cleanup still happens when DPC close fails");
        dpc.FailStop = false; combined.Stop();
        foreach (var value in new uint[] { 0, 5099, 10001, uint.MaxValue })
            Check(!combined.Ensure(value) && !combined.HasCleanupResponsibility, "Joint hold rejects unsupported target: " + value);
        fine.FailStart = true;
        Check(!combined.Ensure(5200) && !dpc.Holding && !fine.Holding, "Extra request failure releases DPC too"); fine.FailStart = false;

        var measurement = new Measurement(world);
        var sample = Measurement.Sample(5200, 1.041, 50);
        Check(DpcAssistedMeasurement.MatchesCandidate(sample, 5200), "Exact candidate accepted");
        foreach (var bad in new[] { sample with { Granted100ns = 10000 }, sample with { Current100ns = 10000 },
            sample with { ObservedBefore100ns = null }, sample with { ObservedAfter100ns = 10000 }, sample with { Requested100ns = 5100 } })
            Check(!DpcAssistedMeasurement.MatchesCandidate(bad, 5200), "Base/clamped/contaminated candidate rejected");
        combined.AlgorithmMode = false; combined.Ensure(10000);
        var wrapped = new DpcAssistedMeasurement(measurement, () => combined.DpcHolding, default);
        Check(wrapped.MeasureGlobal(5200, 50, 0) is not null, "Independent candidate measurement while DPC alive");
        Check(wrapped.MeasureSelf(5200, 50, 0) is null && measurement.SelfCalls == 0, "No self-only fallback can masquerade as global");
        measurement.Clamp = true; Check(wrapped.MeasureGlobal(5200, 50, 0) is null, "Readback stays at 1ms cannot score as 0.52ms"); measurement.Clamp = false;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Check(Throws(() => new DpcAssistedMeasurement(measurement, () => true, cancelled.Token).MeasureGlobal(5200, 50, 0)), "Cancellation before candidate request");
        }
        dpc.Stop(); Check(Throws(() => wrapped.MeasureGlobal(5200, 50, 0)), "DPC identity/liveness loss aborts scan");

        combined.AlgorithmMode = true;
        var clock = new OptimalClockController(combined, measurement, () => world.Current);
        Check(clock.Apply(5200, true) && clock.State.DpcProcessVerified && clock.State.VerifiedGlobal && clock.State.Resolution100ns == 5200,
            "Controller verifies extra target and requestless wait with DPC");
        dpc.Stop();
        Check(!clock.Ensure(true, 5200, true) && !fine.Holding, "Manual DPC exit releases fine request");
        starts = (dpc.Starts, fine.Starts);
        Check(!clock.Ensure(true, 5200, true) && starts == (dpc.Starts, fine.Starts), "No repeated automatic DPC relaunch");
        Check(clock.Stop() && clock.Ensure(true, 5200, true), "Explicit off/on allows new joint hold"); clock.Stop();

        foreach (var kind in new[] { OptimalClockScanKind.LowestLatency, OptimalClockScanKind.StableLatency })
        {
            world = new(); dpc = new(world, true); fine = new(world, false); combined = new(dpc, fine); measurement = new(world);
            var global = DpcPendulumInspection.Evaluate(new(10, 0, 19045, 6093), new(true, @"D:\fixture\dpclat.exe", "fixture"));
            var frame = false;
            using var engine = new ProcmanEngine(Path.Combine(root, kind + "-changes.json"), Path.Combine(root, kind + "-rules.json"),
                clockHolder: combined, clockMeasurement: measurement, clockCurrentResolution: () => world.Current);
            engine.GlobalPrecisionForTests = () => global; engine.FrameModeActiveForTests = () => frame; engine.BackgroundLoopDisabledForTests = true;
            Check(engine.ScanOptimalClockAsync(false, kind).GetAwaiter().GetResult().Success && AppSettingsStore.Read().OptimalClockScanPlatform == global.PlatformId,
                "Existing basic 1ms save remains supported: " + kind);
            var before = JsonSerializer.Serialize(AppSettingsStore.Read()); measurement.Clamp = true;
            Check(!engine.ScanOptimalClockAsync(true, kind, dpcAlgorithmScan: true).GetAwaiter().GetResult().Success
                && JsonSerializer.Serialize(AppSettingsStore.Read()) == before && !combined.HasCleanupResponsibility,
                "Bad candidate keeps saved basic route and releases owners: " + kind); measurement.Clamp = false;
            var result = engine.ScanOptimalClockAsync(true, kind, dpcAlgorithmScan: true).GetAwaiter().GetResult();
            Check(result.Success, "Real engine dispatches original scanner kind " + kind + ": " + result.Message);
            var saved = AppSettingsStore.Read();
            Check(saved.OptimalClockScanPlatform == global.PlatformId + DpcPendulumSource.AlgorithmProfileSuffix
                && saved.OptimalClockResolution100ns is >= 5100 and <= 6300 && saved.OptimalClockScanLoadThreads > 0
                && saved.OptimalClockScanMode == (kind == OptimalClockScanKind.StableLatency ? OptimalClockScanMode.StableLatency : OptimalClockScanMode.LowestLatency),
                "Scanned target/mode/source/statistics actually persist: " + kind);
            Check(!combined.HasCleanupResponsibility && measurement.ActiveLoads == 0, "Save happens after clean release: " + kind);
            if (kind == OptimalClockScanKind.StableLatency)
                Check(measurement.Loads.Distinct().Count() >= 2 && measurement.IdleCandidateSamples > 0, "Algorithm 2 validates idle/standard/heavy loads");
            frame = true; Check(engine.OptimalClockOnceForTests() && combined.AlgorithmMode && dpc.Holding && fine.Holding, "Frame restores saved pair: " + kind);
            Check(engine.Snapshot().OptimalClock is { DpcProcessVerified: true, VerifiedGlobal: true } && fine.Resolution100ns == saved.OptimalClockResolution100ns,
                "IPC reports actual scanned hold rather than fixed 1ms: " + kind);
            Check(!engine.ApplyOptimalClock(saved.OptimalClockResolution100ns + 1).Success && fine.Resolution100ns == saved.OptimalClockResolution100ns,
                "Direct apply cannot substitute an unscanned joint target");
            starts = (dpc.Starts, fine.Starts);
            Check(!engine.ScanOptimalClockAsync(true, kind, dpcAlgorithmScan: true).GetAwaiter().GetResult().Success && starts == (dpc.Starts, fine.Starts),
                "Active frame must be explicitly closed before new scan");
            frame = false; Check(engine.OptimalClockOnceForTests() && !combined.HasCleanupResponsibility, "Frame off stops both requests: " + kind);
            var prior = JsonSerializer.Serialize(AppSettingsStore.Read());
            var admitted = global; measurement.AfterMeasure = () => global = global with { PlatformId = "changed-source" };
            Check(!engine.ScanOptimalClockAsync(true, kind, dpcAlgorithmScan: true).GetAwaiter().GetResult().Success
                && JsonSerializer.Serialize(AppSettingsStore.Read()) == prior && !combined.HasCleanupResponsibility,
                "Source identity change during scan cannot replace result: " + kind);
            measurement.AfterMeasure = null; global = admitted;
            using (AppSettingsStore.BlockWritesForReset(AppSettingsStore.DefaultPath))
                Check(!engine.ScanOptimalClockAsync(true, kind, dpcAlgorithmScan: true).GetAwaiter().GetResult().Success
                    && JsonSerializer.Serialize(AppSettingsStore.Read()) == prior && !combined.HasCleanupResponsibility, "Commit failure never saves or leaks: " + kind);
            using var cancel = new CancellationTokenSource(); measurement.AfterMeasure = () => cancel.Cancel();
            Check(!engine.ScanOptimalClockAsync(true, kind, cancel.Token, dpcAlgorithmScan: true).GetAwaiter().GetResult().Success
                && JsonSerializer.Serialize(AppSettingsStore.Read()) == prior && !combined.HasCleanupResponsibility && measurement.ActiveLoads == 0,
                "Cancelled scan retains old result and closes both: " + kind); measurement.AfterMeasure = null;
            measurement.AfterMeasure = () => fine.FailStop = true;
            Check(!engine.ScanOptimalClockAsync(true, kind, dpcAlgorithmScan: true).GetAwaiter().GetResult().Success
                && JsonSerializer.Serialize(AppSettingsStore.Read()) == prior && engine.DpcCleanupPending,
                "Failed extra cleanup retains recovery scope and does not save: " + kind);
            measurement.AfterMeasure = null; fine.FailStop = false;
            Check(engine.ReleaseOptimalClock().Success && !engine.DpcCleanupPending, "Explicit recovery closes leftover extra request: " + kind);
        }
        Console.WriteLine($"DPC assisted algorithms: {n} checks passed; real scanner/engine with fake requests, measurements, loads and DPC only. No native timer/CPU/DPC execution.");
    }
    private sealed class World { public uint Current = 156250; public bool Dpc, Fine; public uint FineValue; }
    private sealed class Holder(World world, bool isDpc) : IOptimalClockHolderApi
    {
        public bool DpcProcess => isDpc;
        public bool Holding => isDpc ? world.Dpc : world.Fine;
        public uint Resolution100ns { get; private set; }
        public uint Granted100ns => isDpc ? 0 : Resolution100ns;
        public uint Current100ns => world.Current;
        public string Error => "injected request failure";
        public bool FailStart, FailStop; public int Starts;
        public bool Ensure(uint value)
        {
            if (FailStart) return false;
            if (!Holding) Starts++;
            Resolution100ns = value;
            if (isDpc) world.Dpc = true; else { world.Fine = true; world.FineValue = value; }
            world.Current = world.Fine ? world.FineValue : world.Dpc ? 10000u : 156250u; return true;
        }
        public void Stop()
        {
            if (FailStop) throw new IOException("injected cleanup failure");
            if (isDpc) world.Dpc = false; else world.Fine = false;
            world.Current = world.Fine ? world.FineValue : world.Dpc ? 10000u : 156250u;
        }
    }
    private sealed class Measurement(World world) : IOptimalClockMeasurementApi
    {
        public bool Clamp; public int SelfCalls, ActiveLoads, IdleSamples, IdleCandidateSamples; public List<int> Loads = [];
        public Action? AfterMeasure;
        public OptimalClockSample? MeasureGlobal(uint value, int samples, int spin)
        {
            if (!world.Dpc) throw new Exception("DPC wasn't held during measurement");
            if (ActiveLoads == 0) { IdleSamples++; if (value < 10000) IdleCandidateSamples++; }
            var median = (value < 5100 ? 3.0 : 2.0) * value / 10000.0 + 0.001;
            var sample = Sample(value, median, samples);
            if (Clamp && value != 10000) sample = sample with { Granted100ns = 10000, Current100ns = 10000, ObservedBefore100ns = 10000, ObservedAfter100ns = 10000 };
            AfterMeasure?.Invoke(); return sample;
        }
        public OptimalClockSample? MeasureSelf(uint r, int n, int spin) { SelfCalls++; throw new Exception("Unexpected self-only fallback"); }
        public double? MeasureRequestlessMs(int samples) => world.Dpc ? world.Fine ? 2.0 * world.FineValue / 10000.0 + 0.001 : 2.001 : 15.625;
        public IOptimalClockLoad? StartLoad(int threads) { Loads.Add(threads); ActiveLoads++; return new Load(this, threads); }
        private sealed class Load(Measurement owner, int threads) : IOptimalClockLoad { public int Threads => threads; public void Dispose() { owner.ActiveLoads--; } }
        public static OptimalClockSample Sample(uint value, double median, int samples) => new() {
            Requested100ns = value, Granted100ns = value, Current100ns = value, ObservedBefore100ns = value, ObservedAfter100ns = value,
            Median = median, Mean = median, P95 = median + 0.002, Min = median - 0.002, Max = median + 0.002, Over130Pct = 0, Samples = samples };
    }
}
