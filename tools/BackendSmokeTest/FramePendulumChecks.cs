using System.Text.Json;
using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

internal static class FramePendulumChecks
{
    private static int _count;
    private static void Check(bool value, string name) { if (!value) throw new IOException(name); _count++; }
    private static PendulumGlobalStatus Global(bool win10, bool ready = true) => win10
        ? new() { Supported = true, Readable = true, ExperimentalWindows10 = true, PlatformId = "win10-dpc-exe-v1/fixture",
            Win10Dpc = new(ready, @"C:\fixture\dpc.exe", "fixture") }
        : new() { Supported = true, Readable = true, Configured = true, BootConfirmed = ready };

    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-frame-pendulum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var token = Guid.NewGuid(); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), token.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, token);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var settings = new AppSettings { OptimalClockResolution100ns = 5150 };
            Check(!PendulumFramePolicy.Prepared(Global(false), settings), "No saved scan means unprepared");
            settings.OptimalClockScanUtc = now;
            Check(PendulumFramePolicy.Prepared(Global(false), settings), "Win11 boot-confirmed saved scan prepared");
            Check(!PendulumFramePolicy.Prepared(Global(false, false), settings), "Win11 restart remains necessary");
            Check(!PendulumFramePolicy.Prepared(Global(true), settings), "Win10 cannot reuse Win11 scan");
            settings.OptimalClockScanPlatform = Global(true).PlatformId;
            Check(!PendulumFramePolicy.Prepared(Global(true), settings), "Basic DPC profile cannot authorize a different target");
            settings.OptimalClockResolution100ns = 10000;
            Check(PendulumFramePolicy.Prepared(Global(true), settings), "Win10 original source and saved basic 1ms prepared");
            Check(!PendulumFramePolicy.Prepared(Global(true, false), settings), "Missing DPC identity revokes preparation");
            settings.OptimalClockScanPlatform += DpcPendulumSource.AlgorithmProfileSuffix;
            foreach (var target in new uint[] { 5100, 5150, 10000, 5000, 10001 })
            {
                settings.OptimalClockResolution100ns = target;
                Check(PendulumFramePolicy.Prepared(Global(true), settings) == (target is >= 5100 and <= 10000), "Algorithm target range " + target);
            }
            var oldClock = JsonSerializer.Deserialize<OptimalClockSnapshot>("{}")!;
            Check(!oldClock.Prepared, "Old backend cannot claim preparation");
            ProcmanStatusSnapshot Snapshot(bool prepared = true, bool win10 = false) => new()
            {
                Module = DeltaNFD.Backends.BackendModules.Procman, UpdatedUtc = now,
                OptimalClock = new() { Prepared = prepared, SavedResolution100ns = 5150, ExperimentalWindows10 = win10 },
            };
            Check(FrameStatusPresentation.PendulumPrepared(Snapshot(), now, ""), "Fresh prepared backend shows tile");
            Check(!FrameStatusPresentation.PendulumPrepared(Snapshot(false), now, ""), "Unprepared backend hides tile");
            Check(!FrameStatusPresentation.PendulumPrepared(Snapshot(), now.AddSeconds(11), ""), "Stale backend hides tile");
            Check(!FrameStatusPresentation.PendulumPrepared(Snapshot(), now.AddSeconds(-3), ""), "Future backend rejected");
            Check(!FrameStatusPresentation.PendulumPrepared(Snapshot(), now, "disconnected"), "Disconnected backend hides tile");
            Check(!FrameStatusPresentation.PendulumPrepared(null, now, ""), "Unknown backend hides tile");
            Check(FrameStatusPresentation.Pendulum(false, Snapshot(), now, "").Contains("等待开启帧格"), "Prepared off is not an applied claim");
            Check(FrameStatusPresentation.Pendulum(true, Snapshot(), now, "").Contains("尚未生效"), "No holder is not applied");
            Check(!FrameStatusPresentation.PendulumDescription(false).Contains("DPC"), "Win11 explanation has no DPC requirement");
            Check(FrameStatusPresentation.PendulumDescription(true).Contains("保持 DPC 在后台"), "Win10 explanation states DPC lifetime");
            foreach (var mode in new[] { "win11", "win10-basic", "win10-algorithm1", "win10-algorithm2" }) Lifecycle(Path.Combine(root, mode), mode);
            Console.WriteLine($"Frame pendulum: {_count} checks passed; real controllers and frame tick with fake process/policy/timer APIs only. No native timer, driver, CPU load or game writes.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Lifecycle(string directory, string mode)
    {
        Directory.CreateDirectory(directory);
        bool win10 = mode != "win11", algorithm = mode.Contains("algorithm"), active = false;
        var global = Global(win10);
        var events = new List<string>();
        var dpc = new Holder(events, "dpc", true);
        var candidate = new Holder(events, "clock", false);
        IOptimalClockHolderApi holder = win10 ? new DpcCombinedHolder(dpc, candidate) : candidate;
        var target = win10 && !algorithm ? 10000u : 5150u;
        var policy = new TimerApi(events);
        var reach = new Reachability();
        var identity = new ProcmanProcessIdentity(12345, DateTime.UtcNow, @"C:\Games\DeltaForceClient-Win64-Shipping.exe");
        AppSettingsStore.Update(s =>
        {
            s.FrameModeActive = false; s.FrameGameTimerIgnoreEnabled = false;
            s.OptimalClockScanUtc = DateTimeOffset.UtcNow; s.OptimalClockResolution100ns = target;
            s.OptimalClockScanPlatform = win10 ? global.PlatformId + (algorithm ? DpcPendulumSource.AlgorithmProfileSuffix : "") : "";
            s.OptimalClockScanMode = mode.EndsWith("2") ? OptimalClockScanMode.StableLatency : OptimalClockScanMode.LowestLatency;
        });
        using var engine = new ProcmanEngine(Path.Combine(directory, "changes.json"), Path.Combine(directory, "rules.json"),
            timerIgnoreApi: policy, timerProbe: new FrameTimerLegacyProbe(reach), clockHolder: holder,
            clockMeasurement: new Measurements(), clockCurrentResolution: () => holder.Current100ns)
        {
            BackgroundLoopDisabledForTests = true, FrameModeActiveForTests = () => active, GlobalPrecisionForTests = () => global,
            FrameTimerIgnoreTargetsForTests = () => [identity], FrameTimerIgnoreWindows10ForTests = () => win10,
            FrameTimerPolicyProbeForTests = () => new(true, ""),
        };
        Check(engine.Snapshot().OptimalClock.Prepared, mode + ": prepared even with frame off");
        Check(engine.FramePendulumOnceForTests() && !holder.Holding && policy.Writes == 0, mode + ": preparation does not execute while frame off");
        global = Global(win10, false); active = true;
        AppSettingsStore.Update(s => s.FrameGameTimerIgnoreEnabled = true);
        Check(engine.FramePendulumOnceForTests() && !holder.Holding && policy.Writes == 0, mode + ": legacy independent preference cannot bypass preparation");
        AppSettingsStore.Update(s => s.FrameGameTimerIgnoreEnabled = false);
        global = Global(win10); events.Clear();
        Check(engine.SyncFrameBackground().Success && holder.Holding && policy.State.Forced, mode + ": initial frame sync holds then automatically ignores without old preference");
        var start = events.FindIndex(x => x.EndsWith("-start"));
        Check(start >= 0 && events.IndexOf("ignore-apply") > start, mode + ": holder starts before game write");
        Check(!win10 || dpc.Holding, mode + ": Win10 DPC remains alive");
        Check(!algorithm || candidate.Holding && holder.Current100ns == target, mode + ": saved algorithm target held with DPC");
        var snapshot = engine.Snapshot();
        Check(snapshot.OptimalClock.Prepared && snapshot.FrameTimerIgnore.IgnoreApplied, mode + ": authoritative snapshot exposes both components");
        var runningSnapshot = new ProcmanStatusSnapshot { UpdatedUtc = snapshot.UpdatedUtc, RulesEngineRunning = true,
            OptimalClock = snapshot.OptimalClock, FrameTimerIgnore = snapshot.FrameTimerIgnore };
        Check(FrameStatusPresentation.Pendulum(true, runningSnapshot, DateTimeOffset.UtcNow, "").Contains("已忽略"), mode + ": applied text requires actual target readback");
        Check(!AppSettingsStore.Read().FrameGameTimerIgnoreEnabled, mode + ": no hidden preference mutation");
        events.Clear(); active = false;
        Check(engine.FramePendulumOnceForTests() && !holder.Holding && !policy.State.Forced, mode + ": frame off releases both");
        Check(events.IndexOf("ignore-restore") >= 0 && events.IndexOf("ignore-restore") < events.FindIndex(x => x.EndsWith("-stop")), mode + ": game restored before clock released");
        active = true;
        Check(engine.FramePendulumOnceForTests() && policy.State.Forced, mode + ": explicit frame restart can apply again");
        global = Global(win10, false);
        Check(engine.FramePendulumOnceForTests() && !holder.Holding && !policy.State.Forced && !engine.Snapshot().OptimalClock.Prepared, mode + ": lost preparation revokes and restores both");
        global = Global(win10); reach.Milliseconds = 15.625;
        Check(engine.FramePendulumOnceForTests() && holder.Holding && !policy.State.Forced, mode + ": coarse requestless clock keeps ignore bit off");
        active = false; engine.FramePendulumOnceForTests();
        reach.Milliseconds = 1; active = true;
        Check(engine.FramePendulumOnceForTests() && policy.State.Forced, mode + ": fine clock required again after reset");
        if (win10)
        {
            dpc.Holding = false;
            engine.FramePendulumOnceForTests();
            Check(!policy.State.Forced && !engine.Snapshot().FrameTimerIgnore.IgnoreApplied, mode + ": DPC exit restores game ignore");
            active = false; engine.FramePendulumOnceForTests(); active = true;
            engine.FramePendulumOnceForTests();
        }
        policy.DenyRestore = true; active = false;
        Check(!engine.FramePendulumOnceForTests() && !holder.Holding && engine.Snapshot().FrameTimerIgnore.RestorePending, mode + ": failed restore retains responsibility while clock cleanup still attempted");
        Check(engine.Snapshot().OptimalClock.HasError && engine.Snapshot().OptimalClock.Message.Contains("恢复未确认"), mode + ": clock release cannot erase game restoration failure");
        policy.DenyRestore = false;
        Check(engine.FramePendulumOnceForTests() && !policy.State.Forced && !engine.Snapshot().FrameTimerIgnore.RestorePending, mode + ": restoration remains retryable");
    }

    private sealed class Holder(List<string> events, string name, bool dpc) : IOptimalClockHolderApi
    {
        public bool DpcProcess => dpc;
        public bool Holding { get; set; }
        public uint Resolution100ns { get; private set; }
        public uint Granted100ns => Resolution100ns;
        public uint Current100ns => Holding ? Resolution100ns : 156250;
        public string Error => "";
        public bool Ensure(uint target) { events.Add(name + "-start"); Resolution100ns = target; Holding = true; return true; }
        public void Stop() { events.Add(name + "-stop"); Holding = false; }
    }
    private sealed class Measurements : IOptimalClockMeasurementApi
    {
        public double? MeasureRequestlessMs(int samples) => 1;
        public OptimalClockSample? MeasureGlobal(uint r, int samples, int spin) => throw new IOException("Unexpected scanner in frame fixture");
        public OptimalClockSample? MeasureSelf(uint r, int samples, int spin) => throw new IOException("Unexpected self probe");
        public IOptimalClockLoad? StartLoad(int threads) => throw new IOException("Unexpected CPU load");
    }
    private sealed class Reachability : IFrameTimerReachabilityApi
    {
        internal double Milliseconds = 1;
        public double? MeasureRequestlessGranularityMs() => Milliseconds;
    }
    private sealed class TimerApi(List<string> events) : IFrameTimerIgnoreApi
    {
        internal FrameTimerPowerState State = new(1, 1);
        internal bool DenyRestore;
        internal int Writes;
        public string IdentityState(ProcmanProcessIdentity identity) => "same";
        public IFrameTimerIgnoreHandle Open(ProcmanProcessIdentity identity) => new Handle(this, events);
        private sealed class Handle(TimerApi api, List<string> events) : IFrameTimerIgnoreHandle
        {
            public FrameTimerPowerState Read() => api.State;
            public void Write(FrameTimerPowerState state)
            {
                if (api.DenyRestore && !state.Forced) throw new IOException("fixture restore denied");
                events.Add(state.Forced ? "ignore-apply" : "ignore-restore"); api.Writes++; api.State = state;
            }
            public void Dispose() { }
        }
    }
}
