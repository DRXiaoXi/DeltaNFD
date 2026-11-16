using System.Runtime.InteropServices;
using System.Text.Json;
using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

internal static class PendulumKernelChecks
{
    private static int _checks;
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); _checks++; }
    private sealed class NoSleep : IOptimalClockMeasurementApi
    {
        public OptimalClockSample? MeasureGlobal(uint r, int n, int s) => throw new Exception("Kernel path called Sleep measurement");
        public OptimalClockSample? MeasureSelf(uint r, int n, int s) => throw new Exception("Kernel path called self measurement");
        public double? MeasureRequestlessMs(int n) => throw new Exception("Kernel path called requestless Sleep");
        public IOptimalClockLoad? StartLoad(int n) => throw new Exception("Kernel path started CPU load");
    }
    private sealed class Holder : IOptimalClockHolderApi
    {
        public bool KernelRequest => true;
        public bool Holding { get; private set; }
        public uint Resolution100ns { get; private set; }
        public uint Granted100ns => ClampToOneMs ? 10000 : Resolution100ns;
        public uint Current100ns => Holding ? Granted100ns : 156250;
        public string Error => "injected driver unavailable";
        internal bool Missing, ClampToOneMs, FailStop;
        internal int Requests;
        public bool Ensure(uint r) { Requests++; if (Missing) return false; Resolution100ns = r; Holding = true; return true; }
        public void Stop() { if (FailStop) throw new IOException("release incomplete"); Holding = false; }
    }
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-kernel-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = Guid.NewGuid(); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), marker.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, marker);
        var h = new Holder(); uint? overrideReadback = null;
        var c = new OptimalClockController(h, new NoSleep(), () => overrideReadback ?? h.Current100ns);
        Check(Marshal.SizeOf<PendulumKernelHolder.Packet>() == 24, "Fixed pointer-free protocol size");
        Check(c.Apply(5000, true), "0.5ms accepted with slow/unused Sleep");
        Check(c.State is { Holding: true, KernelResolutionVerified: true, VerifiedGlobal: false,
            RequestlessMilliseconds: null, Mode: OptimalClockMode.KernelResolution }, "Numerical proof is separate from Sleep proof");
        Check(c.Ensure(true, 5000, true), "Kernel supervision never measures Sleep");
        overrideReadback = 0;
        Check(!c.Ensure(true, 5000, true) && !h.Holding && c.State.HasError, "Unreadable endpoint releases request");
        overrideReadback = null;
        Check(c.Apply(5000, true), "Can recover after numerical readback failure");
        overrideReadback = 20000;
        Check(!c.Ensure(true, 5000, true) && !h.Holding, "Coarser readback fails");
        overrideReadback = 4990;
        Check(c.Apply(5000, true) && c.State.Current100ns == 4990, "Finer external request remains observable");
        Check(c.Stop() && !h.Holding && !c.State.KernelResolutionVerified, "Release clears numerical proof");
        overrideReadback = null;
        foreach (var r in new uint[] { 0, 4999, 10001, uint.MaxValue })
            Check(!c.Apply(r, true) && !h.Holding, "Reject request outside narrow driver range " + r);
        Check(!c.Apply(5000, false), "Unsupported platform cannot request");
        h.Missing = true; Check(!c.Apply(5000, true) && !c.State.Holding, "Missing driver never succeeds"); h.Missing = false;
        h.ClampToOneMs = true;
        Check(!c.Apply(5000, true) && !h.Holding, "Do not label 1ms grant as 0.5ms");
        Check(c.Apply(10000, true), "1ms fallback is a truthful separate request");
        h.FailStop = true;
        Check(!c.Stop() && c.State.Holding && c.State.HasError, "Failed release retains responsibility");
        h.FailStop = false; Check(c.Stop() && !h.Holding, "Release can be retried");
        for (uint r = 5000; r <= 10000; r += 1000)
        {
            Check(OptimalClockController.KernelReadbackAccepted(r, r, r), "Exact endpoint " + r);
            Check(!OptimalClockController.KernelReadbackAccepted(r, 0, r), "Zero grant rejected " + r);
            Check(!OptimalClockController.KernelReadbackAccepted(r, r, 0), "Zero readback rejected " + r);
            Check(!OptimalClockController.KernelReadbackAccepted(r, r, r + 51), "Coarse endpoint outside tolerance " + r);
        }
        var global = PendulumGlobalInspection.EvaluateWindows10(new Version(10, 0, 19045, 6093), new PendulumWin10DriverStatus { Readable = true, TestSigningEnabled = true, DriverRegistered = true, DriverRunning = true, DriverIdentityVerified = true });
        Check(global.PlatformId.StartsWith("win10-kernel-resolution-v3/"), "Old Sleep-based provenance cannot authorize this path");
        global = global with { ClockEnvironmentRevision = "fixture-environment" };
        h = new Holder(); var frame = false;
        using var engine = new ProcmanEngine(Path.Combine(root, "changes.json"), Path.Combine(root, "rules.json"),
            clockHolder: h, clockMeasurement: new NoSleep(), clockCurrentResolution: () => h.Current100ns);
        engine.GlobalPrecisionForTests = () => global;
        engine.FrameModeActiveForTests = () => frame;
        engine.BackgroundLoopDisabledForTests = true;
        foreach (var preparation in new[] {
            global.Win10Driver! with { TestSigningEnabled = false },
            global.Win10Driver! with { DriverRegistered = false },
            global.Win10Driver! with { DriverRunning = false },
            global.Win10Driver! with { DriverIdentityVerified = false },
            global.Win10Driver! with { Readable = false } })
        {
            var admitted = global; global = global with { Win10Driver = preparation };
            var priorRequests = h.Requests;
            Check(!engine.ScanOptimalClockAsync(true, OptimalClockScanKind.LowestLatency, compatibilityOnly: true).GetAwaiter().GetResult().Success
                && h.Requests == priorRequests, "Unprepared backend cannot run compatibility probe");
            Check(!engine.ScanOptimalClockAsync(false).GetAwaiter().GetResult().Success && h.Requests == priorRequests, "Unprepared backend cannot save selection");
            frame = true;
            Check(!engine.ApplyOptimalClock(5000).Success && h.Requests == priorRequests, "Direct apply cannot bypass preparation");
            Check(engine.OptimalClockOnceForTests() && !h.Holding && h.Requests == priorRequests, "Frame remains usable while clock waits");
            global = admitted; frame = false;
        }
        var before = JsonSerializer.Serialize(AppSettingsStore.Read());
        Check(engine.ScanOptimalClockAsync(true, OptimalClockScanKind.LowestLatency, compatibilityOnly: true).GetAwaiter().GetResult().Success,
            "Compatibility-only driver route works without Sleep");
        Check(!h.Holding && JsonSerializer.Serialize(AppSettingsStore.Read()) == before, "Probe releases and leaves selection unchanged");
        Check(engine.ScanOptimalClockAsync(false).GetAwaiter().GetResult().Success, "Selection saves without latency scanner");
        Check(AppSettingsStore.Read().OptimalClockScanEnvironment == global.ClockEnvironmentRevision, "Numerical proof saves environment identity");
        Check(!h.Holding && AppSettingsStore.Read().OptimalClockResolution100ns == 5000
            && AppSettingsStore.Read().OptimalClockScanPlatform == global.PlatformId,
            $"Saves platform-bound 0.5ms after release: holding={h.Holding}, saved={AppSettingsStore.Read().OptimalClockResolution100ns}, platform={AppSettingsStore.Read().OptimalClockScanPlatform}, expected={global.PlatformId}");
        Check(engine.Snapshot().OptimalClock is { BestLock100ns: 5000, ScanLoadThreads: 0,
            RequestlessMilliseconds: null, BestTypicalMedianMs: null }, "No invented latency statistics");
        frame = true; Check(engine.OptimalClockOnceForTests() && h.Holding, "Frame starts driver hold");
        Check(engine.Snapshot().OptimalClock is { KernelResolutionVerified: true, VerifiedGlobal: false }, "IPC truthfully describes numerical proof");
        var readyGlobal = global; global = global with { Win10Driver = global.Win10Driver! with { DriverRunning = false } };
        Check(engine.OptimalClockOnceForTests() && !h.Holding, "Revoked readiness releases active hold");
        global = readyGlobal; Check(engine.OptimalClockOnceForTests() && h.Holding, "Restored preparation rechecks saved platform before holding");
        frame = false; Check(engine.OptimalClockOnceForTests() && !h.Holding, "Frame off releases driver hold");
        h.ClampToOneMs = true;
        Check(engine.ScanOptimalClockAsync(true).GetAwaiter().GetResult().Success
            && AppSettingsStore.Read().OptimalClockResolution100ns == 10000, "Engine selects separately confirmed 1ms fallback");
        var saved = JsonSerializer.Serialize(AppSettingsStore.Read()); h.Missing = true;
        Check(!engine.ScanOptimalClockAsync(true).GetAwaiter().GetResult().Success
            && JsonSerializer.Serialize(AppSettingsStore.Read()) == saved, "Missing driver cannot overwrite earlier selection");
        h.Missing = false;
        using (AppSettingsStore.BlockWritesForReset(AppSettingsStore.DefaultPath))
            Check(!engine.ScanOptimalClockAsync(true).GetAwaiter().GetResult().Success && !h.Holding
                && JsonSerializer.Serialize(AppSettingsStore.Read()) == saved, "Failed settings commit cannot report a saved selection or leave a request");
        Console.WriteLine($"Pendulum kernel numerical path: {_checks} checks passed. Fakes only; no native timer, driver, service, Sleep measurement or CPU load.");
    }
}
