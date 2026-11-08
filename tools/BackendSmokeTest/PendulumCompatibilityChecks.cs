using System.Text.Json;
using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

/// <summary>模拟 Win10/Win11 路径，不是 Win10 实机验收；所有 timer、负载及持有进程均为替身。</summary>
internal static class PendulumCompatibilityChecks
{
    private static int _count;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); _count++; }

    private sealed class Measurements : IOptimalClockMeasurementApi
    {
        internal double? Wait = 1.05;
        internal Func<uint, int, OptimalClockSample?>? Probe;
        internal int RequestlessCalls, Loads, SelfCalls;
        internal bool FailWait;
        internal readonly List<uint> Resolutions = [];
        public OptimalClockSample? MeasureGlobal(uint r, int samples, int spin)
        {
            Resolutions.Add(r);
            return Probe is null ? Sample(r, r == 40000 ? 4.5 : r == 10000 ? 1.8 : r / 10000.0 * 2, samples) : Probe(r, samples);
        }
        public OptimalClockSample? MeasureSelf(uint r, int samples, int spin) { SelfCalls++; return Sample(r, r / 10000.0 * 2, samples); }
        public double? MeasureRequestlessMs(int samples)
        { RequestlessCalls++; if (FailWait) throw new IOException("injected wait read failure"); return Wait; }
        public IOptimalClockLoad? StartLoad(int threads) { Loads++; return new Load(threads); }
        private sealed class Load(int threads) : IOptimalClockLoad
        { public int Threads => threads; public void Dispose() { } }
    }

    private sealed class Holder : IOptimalClockHolderApi
    {
        internal int Ensures, Stops;
        internal bool FailStop;
        public bool Holding { get; private set; }
        public uint Resolution100ns { get; private set; }
        public uint Granted100ns => Resolution100ns;
        public uint Current100ns => Resolution100ns;
        public string Error => "";
        public bool Ensure(uint r) { Ensures++; Resolution100ns = r; Holding = true; return true; }
        public void Stop() { Stops++; if (FailStop) throw new IOException("injected release failure"); Holding = false; }
    }
    private sealed class Policy : IOptimalClockTimerPolicyApi
    {
        internal int Calls;
        public OptimalClockPowerState Read() { Calls++; throw new System.ComponentModel.Win32Exception(5); }
        public void Write(OptimalClockPowerState state) { Calls++; throw new Exception("unexpected write"); }
    }
    private sealed class Child(string line) : IOptimalClockChildProcess
    {
        public bool HasExited { get; private set; }
        public void CloseInput() => HasExited = true;
        public bool WaitForExit(int milliseconds) => HasExited;
        public void Kill() => HasExited = true;
        public string? ReadReadyLine() => line;
        public void Dispose() { }
    }
    private sealed class Registry : IPendulumGlobalEnvironment
    {
        internal int Writes;
        public string? Blocker() => null;
        public PendulumGlobalStatus Read() => PendulumGlobalInspection.EvaluateWindows10(new Version(10, 0, 19045, 3570));
        public PendulumRegistryValue ReadValue() => throw new Exception("Win10 must not read registry for configuration");
        public PendulumGlobalRecord? ReadRecord() => throw new Exception("Win10 must not read Win11 journal");
        public void Backup(PendulumRegistryValue value) => throw new Exception("Win10 must not back up registry for configuration");
        public void Save(PendulumGlobalRecord record) => throw new Exception("Win10 must not save Win11 journal");
        public void Write(bool enabled) { Writes++; }
    }
    private static OptimalClockSample Sample(uint r, double median, int samples) => new()
    {
        Requested100ns = r, Granted100ns = r, Current100ns = r, Samples = samples,
        Median = median, Mean = median, P95 = median + .002, Min = median - .002, Max = median + .002, Over130Pct = 0,
        ObservedBefore100ns = r, ObservedAfter100ns = r,
    };

    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-pendulum-compatibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        try
        {
            Platforms();
            Probes();
            Protocol();
            Controllers();
            Logs(root);
            Engine(root);
            Console.WriteLine($"Pendulum experimental compatibility: {_count} isolated checks passed.");
            Console.WriteLine("Evidence: simulated Windows 10/11 branches only; no real timer requests or CPU load. NOT Windows 10 hardware acceptance.");
        }
        finally
        {
            // Only this exact, generated test folder is removed.
            if (Path.GetFileName(root).StartsWith("DeltaNFD-pendulum-compatibility-", StringComparison.Ordinal))
                try { Directory.Delete(root, true); } catch { }
        }
    }

    internal static void ReadOnly()
    {
        var version = PendulumGlobalInspection.ReadSystemVersion();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            evidence = "actual-host-readonly", os = version.ToString(),
            profile = PendulumGlobalInspection.ExperimentalWindows10 ? "Windows10-experimental" : "Windows11",
            timerRequests = false, processPolicyWrites = false, cpuLoad = false,
            windows10HardwareAcceptance = false,
        }));
        if (version.Build < 22000)
            Console.WriteLine("This entry only reads OS identity; it does not test compatibility.");
    }

    private static void Protocol()
    {
        var events = new List<string>();
        var children = new Queue<IOptimalClockChildProcess>([
            new Child("HELD:true:10000:10000:10000:"),
            new Child("MEAS:1.8:1.8:1.8:1.8:1.8:0:61:5000:5000"),
        ]);
        var api = new NativeOptimalClockMeasurementApi("fake-only", _ => children.Dequeue(), (name, _) => events.Add(name));
        var sample = api.MeasureGlobal(10000, 61, 0);
        Check(sample is { Current100ns: 5000, ObservedBefore100ns: 5000, ObservedAfter100ns: 5000 },
            "Native parser uses measurement-time readout rather than earlier holder readout");
        Check(children.Count == 0 && events.Count(e => e == "probe-child-output") == 2 && events.Count(e => e == "probe-child-exited") == 2,
            "Both measurement and holder output and cleanup logged");
        api = new NativeOptimalClockMeasurementApi("fake-only", _ => new Child("MEAS:1.05:1.05:1.05:1.05:1.05:0:5"));
        Check(api.MeasureRequestlessMs(61) is null, "Insufficient protocol sample count cannot confirm effect");
        api = new NativeOptimalClockMeasurementApi("fake-only", _ => new Child("SELF:true:5200:5200:5200:1.05:1.05:1.05:1.05:1.05:0"));
        Check(api.MeasureSelf(5200, 61, 0) is null, "Truncated self protocol rejected without indexing past end");
    }

    private static void Platforms()
    {
        foreach (var build in new[] { 17763, 18363, 19041, 19045 })
        {
            var status = PendulumGlobalInspection.EvaluateWindows10(new Version(10, 0, build, 3570));
            Check(status is { Supported: true, Readable: true, Ready: true, ExperimentalWindows10: true }, "Win10 test path admission " + build);
            Check(!status.Configured && !status.BootConfirmed && !status.RequiresRestart, "Win10 must not fabricate registry/restart proof");
            Check(status.PlatformId.Contains(build.ToString()), "Win10 scan provenance contains build");
        }
        foreach (var version in new[] { new Version(6, 3, 9600), new Version(10, 0, 17134), new Version(10, 0, 22000) })
            Check(!PendulumGlobalInspection.EvaluateWindows10(version).Ready, "Other OS cannot enter Win10 path");
        var one = PendulumGlobalInspection.EvaluateWindows10(new Version(10, 0, 19045, 3570));
        var two = PendulumGlobalInspection.EvaluateWindows10(new Version(10, 0, 19045, 3571));
        Check(one.PlatformId != two.PlatformId, "System revision change invalidates experimental provenance");
        var win11 = PendulumGlobalInspection.Evaluate(true, new(true, 1), "boot-a",
            new PendulumGlobalRecord { WrittenBootId = "boot-a", Completed = true, TargetEnabled = true });
        Check(!win11.Ready && !win11.ExperimentalWindows10 && win11.RequiresRestart, "Win11 same-boot gate remains");
        var registry = new Registry();
        Check(!PendulumGlobalSettingsEngine.Configure(registry, new(true, registry.Read().Fingerprint)).Success && registry.Writes == 0,
            "Win10 config command cannot write Win11 key");
        var policy = new Policy();
        Check(OptimalClockTimerPolicy.TryHonorRequests(out _, policy, supportsPolicy: false) && policy.Calls == 0,
            "Win10 does not write unsupported ignore-timer policy");
        Check(!OptimalClockTimerPolicy.TryHonorRequests(out var error, policy, supportsPolicy: true) && error.Contains("Win32 5"),
            "Win11 access denied still blocks holder");
    }

    private static void Probes()
    {
        var api = new Measurements();
        var events = new List<string>();
        Check(PendulumGlobalProbe.Verify(api, default, (name, _) => events.Add(name)).Success, "A/B/A responsive requestless process accepted");
        Check(api.Resolutions.SequenceEqual(new uint[] { 10000, 40000, 10000 }) && api.Loads == 0 && api.SelfCalls == 0,
            "Capability probe is bounded, requestless and has no CPU load");
        Check(events.Count(e => e == "compatibility-probe") == 3 && events.Contains("compatibility-result"), "All capability results logged");
        foreach (var wait in new[] { 15.6, 1.05, double.NaN })
        {
            api = new Measurements { Probe = (r, n) => Sample(r, wait, n) };
            Check(!PendulumGlobalProbe.Verify(api, default).Success, "Readout alone or fixed unrelated timer cannot pass: " + wait);
            Check(api.SelfCalls == 0 && api.Loads == 0, "Failed capability probe cannot fall back to self or load");
        }
        foreach (var mode in new[] { "missing", "short", "overridden", "changed-after", "missing-endpoints", "missing-before", "missing-after", "wrong-request", "no-grant" })
        {
            api = new Measurements { Probe = (r, n) => mode switch
            {
                "missing" => null, "short" => Sample(r, 1.05, 5),
                "overridden" => Sample(r, 1.05, n) with { Current100ns = 5000 },
                "changed-after" => Sample(r, 1.05, n) with { ObservedAfter100ns = 5000 },
                "missing-endpoints" => Sample(r, 1.05, n) with { ObservedBefore100ns = null, ObservedAfter100ns = null },
                "missing-before" => Sample(r, 1.05, n) with { ObservedBefore100ns = null },
                "missing-after" => Sample(r, 1.05, n) with { ObservedAfter100ns = null },
                "wrong-request" => Sample(r, 1.05, n) with { Requested100ns = 0 },
                _ => Sample(r, 1.05, n) with { Granted100ns = 0 },
            }};
            Check(!PendulumGlobalProbe.Verify(api, default).Success, "Incomplete or polluted capability denied: " + mode);
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        api = new Measurements(); var observed = false;
        try { PendulumGlobalProbe.Verify(api, cancelled.Token); } catch (OperationCanceledException) { observed = true; }
        Check(observed && api.Resolutions.Count == 0, "Cancelled capability does not start probes");
    }

    private static void Controllers()
    {
        foreach (var wait in new double?[] { null, 15.6, double.NaN, double.PositiveInfinity, 0, -1 })
        {
            var holder = new Holder();
            var controller = new OptimalClockController(holder, new Measurements { Wait = wait }, () => 5200);
            Check(!controller.Apply(5200, true), "System readout must not hide failed requestless effect: " + wait);
            Check(!holder.Holding && !controller.State.VerifiedGlobal && controller.State.HasError, "Rejected effect releases holder");
        }
        var now = DateTimeOffset.UtcNow;
        var api = new Measurements(); var healthy = new Holder(); var events = new List<string>();
        var subject = new OptimalClockController(healthy, api, () => 5200, diagnostics: (name, _) => events.Add(name), clock: () => now);
        Check(subject.Apply(5200, true) && api.RequestlessCalls == 3, "Three fresh requestless batches confirm initial effect");
        var verified = subject.State.GlobalVerifiedUtc;
        Check(verified == now && events.Contains("lock-confirmed"), "Confirmation timestamp and log recorded");
        for (var tick = 0; tick < 5; tick++) Check(subject.Ensure(true, 5200, true), "Healthy supervision");
        Check(api.RequestlessCalls == 3 && healthy.Ensures == 1, "Two-second supervision does not spawn frequent measurement or duplicate holders");
        now += TimeSpan.FromMinutes(1);
        Check(subject.Ensure(true, 5200, true) && api.RequestlessCalls == 6 && subject.State.GlobalVerifiedUtc > verified,
            "Periodic real-effect verification does not rely only on readout");
        now += TimeSpan.FromMinutes(1); api.Wait = 15.6;
        Check(!subject.Ensure(true, 5200, true) && !healthy.Holding && !subject.State.VerifiedGlobal,
            "Correct system readout with lost effect releases and reports failure");
        var starts = healthy.Ensures;
        now += TimeSpan.FromSeconds(2);
        Check(!subject.Ensure(true, 5200, true) && healthy.Ensures == starts, "Lost effect uses bounded retry interval");
        now += TimeSpan.FromMinutes(1); api.Wait = 1.05;
        Check(subject.Ensure(true, 5200, true) && subject.State.VerifiedGlobal, "Effect can recover after retry interval");
        Check(subject.Stop() && events.Contains("release-confirmed"), "Release logged");
        healthy = new Holder { FailStop = true };
        subject = new OptimalClockController(healthy, new Measurements { Wait = 15.6 }, () => 5200);
        Check(!subject.Apply(5200, true) && subject.State.Holding && subject.State.HasError && !subject.State.VerifiedGlobal,
            "Failed release after bad effect retains responsibility");
        healthy.FailStop = false; Check(subject.Stop(), "Retained release can be retried");
        healthy = new Holder { FailStop = true };
        subject = new OptimalClockController(healthy, new Measurements { FailWait = true }, () => 5200);
        Check(!subject.Apply(5200, true) && subject.State.Holding && subject.State.HasError && !subject.State.VerifiedGlobal,
            "Exception plus failed release retains responsibility");
        healthy.FailStop = false; Check(subject.Stop(), "Exception release can be retried");
        healthy = new Holder(); subject = new OptimalClockController(healthy, new Measurements(), () => 5200,
            diagnostics: (_, _) => throw new IOException("log sink failed"));
        Check(subject.Apply(5200, true) && subject.Stop(), "Logging callback cannot break lock or release");
    }

    private static void Logs(string root)
    {
        var directory = Path.Combine(root, "diagnostic-tests");
        var log = new PendulumDiagnosticLog(directory, 1024);
        log.Write("algorithm-result", new { algorithm = 1, requested100ns = 5200, medianMs = 1.05 });
        var lines = File.ReadAllLines(log.CurrentPath);
        using (var json = JsonDocument.Parse(lines[^1]))
        {
            Check(json.RootElement.GetProperty("name").GetString() == "algorithm-result", "Structured event readable");
            Check(json.RootElement.GetProperty("data").GetProperty("requested100ns").GetUInt32() == 5200, "Exact numeric values preserved");
            Check(json.RootElement.GetProperty("utc").GetDateTimeOffset().Offset == TimeSpan.Zero, "UTC timestamp");
            Check(json.RootElement.GetProperty("os").GetString()!.Length > 0 && json.RootElement.GetProperty("profile").GetString()!.Length > 0,
                "Every rotated event carries actual host OS/profile");
        }
        log.Write("invalid-sample", new { medianMs = double.NaN });
        using (var json = JsonDocument.Parse(File.ReadAllLines(log.CurrentPath)[^1]))
            Check(log.Error.Length == 0 && json.RootElement.GetProperty("data").GetProperty("medianMs").GetString() == "NaN",
                "Invalid numeric measurements remain diagnosable in valid JSON");
        for (var index = 0; index < 40; index++) log.Write("rotation", new { text = new string('x', 700), index });
        Check(Directory.GetFiles(directory, "pendulum_*.jsonl").Length <= 12 && log.Error.Length == 0, "Log retention bounded");
        foreach (var path in Directory.GetFiles(directory, "pendulum_*.jsonl"))
            foreach (var line in File.ReadAllLines(path)) { using var json = JsonDocument.Parse(line); Check(json.RootElement.GetProperty("schema").GetInt32() == 1, "Rotated log remains complete JSON"); }
        var other = Path.Combine(directory, "keep.txt"); File.WriteAllText(other, "keep");
        log.Write("keep", new { }); Check(File.ReadAllText(other) == "keep", "Retention never touches unrelated files");
        var broken = Path.Combine(root, "blocked-log-directory"); File.WriteAllText(broken, "existing bytes");
        var failing = new PendulumDiagnosticLog(broken);
        failing.Write("result", new { success = true });
        Check(failing.Error.Contains("日志保存失败") && File.ReadAllText(broken) == "existing bytes", "Logging I/O failure reported without corrupting blocking file");
    }

    private static void Engine(string root)
    {
        var status = PendulumGlobalInspection.EvaluateWindows10(new Version(10, 0, 19045, 3570));
        var holder = new Holder(); var api = new Measurements(); var frame = false;
        using var engine = new ProcmanEngine(Path.Combine(root, "changes.json"), Path.Combine(root, "rules.json"),
            clockHolder: holder, clockMeasurement: api, clockCurrentResolution: () => AppSettingsStore.Read().OptimalClockResolution100ns);
        engine.ExperimentalTimerFeaturesForTests = true;
                engine.FrameModeActiveForTests = () => frame;
        engine.GlobalPrecisionForTests = () => status;
        engine.BackgroundLoopDisabledForTests = true;
        AppSettingsStore.Update(s => { s.OptimalClockScanUtc = DateTimeOffset.UtcNow; s.OptimalClockScanPlatform = ""; });
        frame = true;
        Check(engine.OptimalClockOnceForTests() && holder.Ensures == 0, "Old or Win11 scan does not authorize experimental Win10 lock");
        Check(!engine.ApplyOptimalClock(null).Success && holder.Ensures == 0, "Direct apply cannot bypass experimental provenance");
        frame = false;
        var saved = JsonSerializer.Serialize(AppSettingsStore.Read());
        Check(engine.ScanOptimalClockAsync(true, OptimalClockScanKind.LowestLatency, compatibilityOnly: true).GetAwaiter().GetResult().Success,
            "Compatibility-only authority route");
        Check(api.Loads == 0 && holder.Ensures == 0 && JsonSerializer.Serialize(AppSettingsStore.Read()) == saved,
            "Compatibility-only probe cannot save results, load CPU or authorize lock");
        Check(engine.ScanOptimalClockAsync(true).GetAwaiter().GetResult().Success, "Win10 simulated global-capable scanner saves result");
        Check(AppSettingsStore.Read().OptimalClockScanPlatform == status.PlatformId, "Saved result bound to tested OS revision/profile");
        Check(File.Exists(engine.Snapshot().OptimalClock.DiagnosticLogPath), "Authority exposes actual diagnostic path");
        frame = true;
        Check(engine.OptimalClockOnceForTests() && holder.Holding && engine.Snapshot().OptimalClock.GlobalVerifiedUtc is not null,
            "Simulated Win10 frame starts only after verified scan and effect");
        status = PendulumGlobalInspection.EvaluateWindows10(new Version(10, 0, 19045, 3571));
        Check(engine.OptimalClockOnceForTests() && !holder.Holding, "Changed Windows revision releases incompatible scan");
        frame = false;
        saved = JsonSerializer.Serialize(AppSettingsStore.Read());
        api.Probe = (r, n) => Sample(r, 15.6, n); var loads = api.Loads;
        Check(!engine.ScanOptimalClockAsync(true).GetAwaiter().GetResult().Success && api.Loads == loads,
            "Win10 per-process-only behavior rejected before CPU load");
        Check(JsonSerializer.Serialize(AppSettingsStore.Read()) == saved, "Failed compatibility cannot overwrite previous scan");
        api.Probe = (r, n) => Sample(r, 15.6, n);
        status = new() { Supported = true, Readable = true, Configured = true, BootConfirmed = true };
        frame = true;
        Check(engine.OptimalClockOnceForTests() && !holder.Holding, "Experimental Win10 scan cannot be reused on Win11");
        Check(!engine.ApplyOptimalClock(null).Success, "Win11 direct apply also rejects experimental scan provenance");
        frame = false;
        Check(!engine.ScanOptimalClockAsync(true).GetAwaiter().GetResult().Success,
            "Win11 self-only scan is also never saved as global");
        Check(JsonSerializer.Serialize(AppSettingsStore.Read()) == saved, "Win11 rejected self-only scan preserves settings");
    }
}
