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
            var defaultWinner = new AppSettings
            {
                OptimalClockResolution100ns = 5000, OptimalClockScanUtc = now, OptimalClockScanMode = OptimalClockScanMode.FullRange,
            };
            Check(PendulumFramePolicy.Prepared(Global(false), defaultWinner) && PendulumFramePolicy.UsesDeltaForceDefault(defaultWinner),
                "verified automatic 0.5000 ms uses Delta Force's timer");
            defaultWinner.CustomGameModeEnabled = true;
            Check(!PendulumFramePolicy.UsesDeltaForceDefault(defaultWinner), "custom games still use their saved automatic value");
            defaultWinner.CustomGameModeEnabled = false;
            defaultWinner.OptimalClockUseCustomResolution = true;
            Check(!PendulumFramePolicy.UsesDeltaForceDefault(defaultWinner), "explicit custom timer selection still holds the verified value");
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
            Check(JsonSerializer.Deserialize<AppSettings>("{}")!.FramePendulumEnabled, "legacy JSON preserves automatic linkage");
            var disabled = new AppSettings { FramePendulumEnabled = false };
            Check(!JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(disabled))!.FramePendulumEnabled, "explicit off persists across reload");
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
            Check(FrameStatusPresentation.Pendulum(true, Snapshot(), now, "", enabled: false).Contains("未启用"), "off is distinct from waiting for a holder");
            Check(FrameStatusPresentation.Pendulum(true, Snapshot(), now.AddSeconds(11), "", enabled: false).Contains("待核对"), "stale off snapshot does not claim clean restoration");
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
        var target = win10 ? (algorithm ? 5150u : 10000u) : 5180u;
        if (!win10) candidate.ReportedCurrent = 5000; // Host: saved 0.5180 ms, verified live 0.5000 ms.
        var policy = new TimerApi(events);
        var reach = new Reachability();
        var testUtc = DateTime.UtcNow;
        var identity = new ProcmanProcessIdentity(12345, DateTime.UtcNow, @"C:\Games\DeltaForceClient-Win64-Shipping.exe");
        AppSettingsStore.Update(s =>
        {
            s.FrameModeActive = false; s.FrameGameTimerIgnoreEnabled = false; s.FramePendulumEnabled = true;
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
            FrameTimerPolicyProbeForTests = () => new(true, ""), ClockForTests = () => testUtc,
        };
        Check(engine.Snapshot().OptimalClock.Prepared, mode + ": prepared even with frame off");
        Check(engine.FramePendulumOnceForTests() && !holder.Holding && policy.Writes == 0, mode + ": preparation does not execute while frame off");
        global = Global(win10, false); active = true;
        AppSettingsStore.Update(s => s.FrameGameTimerIgnoreEnabled = true);
        Check(engine.FramePendulumOnceForTests() && !holder.Holding && policy.Writes == 0, mode + ": legacy independent preference cannot bypass preparation");
        AppSettingsStore.Update(s => s.FrameGameTimerIgnoreEnabled = false);
        global = Global(win10); events.Clear();
        AppSettingsStore.Update(s => { s.FramePendulumEnabled = false; s.OptimalClockEnabled = true; });
        Check(engine.SyncFrameBackground().Success && !holder.Holding && policy.Writes == 0, mode + ": off prevents holder and game write while frame stays active");
        AppSettingsStore.Update(s => s.FramePendulumEnabled = true);
        Check(engine.SyncFrameBackground().Success && holder.Holding && policy.State.Forced, mode + ": enabling during frame holds then ignores, including verified finer Win11 clock");
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
        if (!win10)
        {
            candidate.ReportedCurrent = 7000;
            Check(!engine.FramePendulumOnceForTests() && !policy.State.Forced, mode + ": drift coarser than allowed revokes ignore");
            candidate.ReportedCurrent = 5000;
            Check(engine.FramePendulumOnceForTests() && policy.State.Forced, mode + ": reverified finer clock reapplies");
            string Status(ProcmanStatusSnapshot state) => FrameStatusPresentation.Pendulum(true,
                new ProcmanStatusSnapshot { UpdatedUtc = state.UpdatedUtc, RulesEngineRunning = true,
                    OptimalClock = state.OptimalClock, FrameTimerIgnore = state.FrameTimerIgnore }, DateTimeOffset.UtcNow, "");
            Check(Status(engine.Snapshot()).Contains("仍有更细读数，未达到目标，来源未确认"), "persistent 0.5000 does not claim exact lock or identify the requester");
            Check(engine.Snapshot().FrameTimerIgnore is { PostApplyReachabilityConfirmed: true, AfterPolicyClock100ns: 5000 }, "policy is confirmed even when clock mismatch remains");

            void Reset()
            {
                active = false;
                engine.FramePendulumOnceForTests();
                candidate.ReportedCurrent = 5000;
                active = true;
            }
            Reset();
            policy.OnWrite = s => candidate.ReportedCurrent = s.Forced ? target : 5000u;
            var probes = reach.Count;
            Check(engine.FramePendulumOnceForTests() && reach.Count >= probes + 2 && policy.State.Forced, "before/after independent probes run around the write");
            var recovered = engine.Snapshot();
            Check(recovered.FrameTimerIgnore is { BeforePolicyClock100ns: 5000, AfterPolicyClock100ns: 5180, PostApplyReachabilityConfirmed: true }, "game request removal 0.5000 -> 0.5180 is observed in same tick");
            Check(Status(recovered).Contains("忽略前 0.5000 ms，复核读数已接近目标"), "UI uses after sample rather than pre-policy clock snapshot");
            Check(engine.FramePendulumOnceForTests() && Status(engine.Snapshot()).Contains("忽略前 0.5000 ms"), "subsequent stable checks retain the observed transition");

            Reset();
            policy.OnWrite = s => candidate.ReportedCurrent = s.Forced ? 156250u : 5000u;
            Check(engine.FramePendulumOnceForTests() && !policy.State.Forced
                && !engine.Snapshot().FrameTimerIgnore.PostApplyReachabilityConfirmed,
                "coarse post-apply clock rolls back to game preference even when the pre-check passed");
            Check(Status(engine.Snapshot()).Contains("保留游戏原精度"), "coarse fallback is visible rather than marked applied");
            var blockedWrites = policy.Writes;
            Check(engine.FramePendulumOnceForTests() && policy.Writes == blockedWrites, "cooldown prevents repeatedly toggling the game policy");
            testUtc = testUtc.AddSeconds(31);
            policy.OnWrite = s => candidate.ReportedCurrent = s.Forced ? target : 5000u;
            reach.Measure = () => policy.State.Forced ? null : 1;
            Check(engine.FramePendulumOnceForTests() && policy.Writes > blockedWrites && !policy.State.Forced,
                "after cooldown a missing post-apply measurement also rolls back even if the numerical value matches");
            testUtc = testUtc.AddSeconds(31);
            reach.Measure = null;
            Check(engine.FramePendulumOnceForTests() && policy.State.Forced && engine.Snapshot().FrameTimerIgnore.AfterPolicyClock100ns == target,
                "later retry is allowed once measurements confirm safe clock again");
            policy.OnWrite = null;
        }
        events.Clear();
        AppSettingsStore.Update(s => s.FramePendulumEnabled = false);
        Check(engine.SyncFrameBackground().Success && active && !holder.Holding && !dpc.Holding && !policy.State.Forced,
            mode + ": switching off releases both without stopping frame");
        Check(events.IndexOf("ignore-restore") >= 0 && events.IndexOf("ignore-restore") < events.FindIndex(x => x.EndsWith("-stop")), mode + ": live switch restores game before clock release");
        Check(FrameStatusPresentation.Pendulum(true, runningSnapshot, DateTimeOffset.UtcNow, "", enabled: false).Contains("尚未确认"), mode + ": do not report released from old running snapshot");
        AppSettingsStore.Update(s => s.FramePendulumEnabled = true);
        Check(engine.SyncFrameBackground().Success && holder.Holding && policy.State.Forced, mode + ": live switch can re-enable both");
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
        policy.DenyRestore = true; AppSettingsStore.Update(s => s.FramePendulumEnabled = false);
        Check(!engine.FramePendulumOnceForTests() && !holder.Holding && engine.Snapshot().FrameTimerIgnore.RestorePending, mode + ": failed restore retains responsibility while clock cleanup still attempted");
        Check(engine.Snapshot().OptimalClock.HasError && engine.Snapshot().OptimalClock.Message.Contains("恢复未确认"), mode + ": clock release cannot erase game restoration failure");
        Check(FrameStatusPresentation.Pendulum(true, engine.Snapshot(), DateTimeOffset.UtcNow, "", enabled: false).Contains("恢复仍待核对"), mode + ": failed restore remains visible after switch off");
        policy.DenyRestore = false;
        Check(engine.FramePendulumOnceForTests() && !policy.State.Forced && !engine.Snapshot().FrameTimerIgnore.RestorePending, mode + ": restoration remains retryable");
        if (!win10 || algorithm)
        {
            AppSettingsStore.Update(s => s.FramePendulumEnabled = true);
            Check(engine.FramePendulumOnceForTests() && holder.Holding && policy.State.Forced, mode + ": non-default timer and game override active before default transition");
            events.Clear();
            AppSettingsStore.Update(s => { s.OptimalClockScanMode = OptimalClockScanMode.FullRange; s.OptimalClockResolution100ns = 5000; });
            Check(engine.SyncFrameBackground().Success && active && !holder.Holding && !policy.State.Forced,
                mode + ": automatic 0.5000 ms leaves frame active and restores the game's own timer");
            Check(events.IndexOf("ignore-restore") >= 0 && events.IndexOf("ignore-restore") < events.FindIndex(x => x.EndsWith("-stop")),
                mode + ": switching to the game default restores the game before releasing the holder");
            events.Clear();
            Check(engine.FramePendulumOnceForTests() && engine.ApplyOptimalClock(5000).Success
                && !events.Any(e => e.EndsWith("-start")) && !policy.State.Forced,
                mode + ": neither maintenance nor explicit application starts a holder or ignores the game timer");
            var defaultState = engine.Snapshot();
            Check(defaultState.OptimalClock is { Prepared: true, UsesGameDefault: true, SavedResolution100ns: 5000, Holding: false }
                && FrameStatusPresentation.Pendulum(true, defaultState, DateTimeOffset.UtcNow, "").Contains("沿用三角洲"),
                mode + ": saved scan and runtime status explain why the game supplies its own timer");
        }
    }

    private sealed class Holder(List<string> events, string name, bool dpc) : IOptimalClockHolderApi
    {
        public bool DpcProcess => dpc;
        public bool Holding { get; set; }
        public uint Resolution100ns { get; private set; }
        public uint Granted100ns => Resolution100ns;
        internal uint? ReportedCurrent;
        public uint Current100ns => Holding ? (ReportedCurrent ?? Resolution100ns) : 156250;
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
        internal Func<double?>? Measure;
        internal int Count;
        public double? MeasureRequestlessGranularityMs() { Count++; return Measure is null ? Milliseconds : Measure(); }
    }
    private sealed class TimerApi(List<string> events) : IFrameTimerIgnoreApi
    {
        internal FrameTimerPowerState State = new(1, 1);
        internal bool DenyRestore;
        internal Action<FrameTimerPowerState>? OnWrite;
        internal int Writes;
        public string IdentityState(ProcmanProcessIdentity identity) => "same";
        public IFrameTimerIgnoreHandle Open(ProcmanProcessIdentity identity) => new Handle(this, events);
        private sealed class Handle(TimerApi api, List<string> events) : IFrameTimerIgnoreHandle
        {
            public FrameTimerPowerState Read() => api.State;
            public void Write(FrameTimerPowerState state)
            {
                if (api.DenyRestore && !state.Forced) throw new IOException("fixture restore denied");
                events.Add(state.Forced ? "ignore-apply" : "ignore-restore"); api.Writes++; api.State = state; api.OnWrite?.Invoke(state);
            }
            public void Dispose() { }
        }
    }
}
