using System.Text.Json;
using System.Xml.Linq;
using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

internal static class PublicReleaseChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new IOException(name); count++; }
        Check(ReleaseFeaturePolicy.PendulumEnabled && ReleaseFeaturePolicy.FrameTimerIgnoreEnabled, "Pendulum and frame timer-ignore restored");
        Check(!new AppSettings().FrameGameTimerIgnoreEnabled, "Restored option is off for new configurations");
        Check(!new AppSettings().FrameGamePriorityEnabled, "New configuration leaves game priority off");
        Check(!JsonSerializer.Deserialize<AppSettings>("{}")!.FrameGamePriorityEnabled, "Missing legacy field leaves priority off");
        Check(JsonSerializer.Deserialize<AppSettings>("{\"FrameGamePriorityEnabled\":true}")!.FrameGamePriorityEnabled, "Explicit priority choice survives upgrade");

        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-public-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var token = Guid.NewGuid();
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), token.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, token);
        try
        {
            AppSettingsStore.Update(s =>
            {
                s.FrameModeActive = true; s.FrameGameTimerIgnoreEnabled = false; s.OptimalClockEnabled = true;
                s.OptimalClockScanUtc = DateTimeOffset.UtcNow; s.OptimalClockResolution100ns = 5150;
            });
            var holder = new Holder(); var measure = new Measurements(); var api = new TimerApi(); var probe = new Probe();
            var frameActive = true;
            var global = new PendulumGlobalStatus { Supported = true, Readable = true, Configured = true,
                BootConfirmed = false, Message = "fixture: restart not confirmed" };
            var id = new ProcmanProcessIdentity(12345, DateTime.UtcNow, @"C:\Games\DeltaForceClient-Win64-Shipping.exe");
            using var engine = new ProcmanEngine(Path.Combine(root, "changes.json"), Path.Combine(root, "rules.json"),
                timerIgnoreApi: api, timerProbe: new FrameTimerLegacyProbe(probe), clockHolder: holder,
                clockMeasurement: measure, clockCurrentResolution: () => 5150)
            {
                FrameModeActiveForTests = () => frameActive, BackgroundLoopDisabledForTests = true,
                FrameTimerIgnoreTargetsForTests = () => [id],
                GlobalPrecisionForTests = () => global,
            };
            var saved = JsonSerializer.Serialize(AppSettingsStore.Read());
            Check(engine.OptimalClockOnceForTests() && holder.Ensures == 0, "Saved frame/scan cannot bypass restart admission");
            Check(engine.ApplyOptimalClock(5150).Message == global.Message, "Direct apply respects current admission");
            foreach (var algorithm in new[] { OptimalClockScanKind.LowestLatency, OptimalClockScanKind.StableLatency })
            {
                Check((await engine.ScanOptimalClockAsync(true, algorithm)).Message == global.Message, "Algorithm obeys restart admission");
                Check((await engine.ScanOptimalClockAsync(true, algorithm, compatibilityOnly: true)).Message == global.Message, "Compatibility probe obeys restart admission");
            }
            Check(engine.FrameTimerIgnoreOnceForTests() && api.Writes == 0 && probe.Calls == 0, "Unselected ignore option cannot apply or measure");
            Check(measure.Calls == 0 && holder.Ensures == 0, "No timer measurements, loads or holder start");
            Check(JsonSerializer.Serialize(AppSettingsStore.Read()) == saved, "Old choices and scan history preserved without execution");
            global = new PendulumGlobalStatus { Supported = true, Readable = true, ExperimentalWindows10 = true, Win10Driver = new PendulumWin10DriverStatus { Readable = true, TestSigningEnabled = true, DriverRegistered = true, DriverRunning = true, DriverIdentityVerified = true }, PlatformId = "fixture-win10" };
            Check(engine.OptimalClockOnceForTests() && holder.Ensures == 0, "Win10 cannot reuse an unbound legacy scan");
            Check(!engine.ApplyOptimalClock(5150).Success && measure.Calls == 0, "Win10 requires matching local scan before holding");

            // Exercise the actual restored release policy, without the test-only
            // feature override. Turning the option or frame off must still restore.
            if (ProcmanEngine.FrameTimerIgnoreSupported)
            {
                global = new() { Supported = true, Readable = true, Configured = true, BootConfirmed = true };
                measure.AllowRequestless = true;
                engine.OptimalClockOnceForTests();
                Check(engine.FrameTimerIgnoreOnceForTests() && api.State.Forced, "Prepared verified clock automatically applies ignore under release policy");
                frameActive = false;
                Check(engine.FrameTimerIgnoreOnceForTests() && !api.State.Forced, "Frame off restores the linked option");
                Check(!engine.Snapshot().FrameTimerIgnore.RestorePending, "Confirmed restore clears responsibility");
                frameActive = true;
                Check(engine.FrameTimerIgnoreOnceForTests() && api.State.Forced, "Fixture seeds another old bit");
                frameActive = false; api.Deny = true;
                Check(!engine.FrameTimerIgnoreOnceForTests() && engine.Snapshot().FrameTimerIgnore.RestorePending, "Failed restoration retains responsibility");
                api.Deny = false;
                Check(engine.FrameTimerIgnoreOnceForTests() && !api.State.Forced, "Restoration can retry after frame off");
                frameActive = true;

                // Simulated Win10 runs the real admission branch with a fake worker,
                // never a native policy probe or a game process.
                engine.FrameTimerIgnoreWindows10ForTests = () => true;
                var capabilityCalls = 0;
                engine.FrameTimerPolicyProbeForTests = () => { capabilityCalls++; return new(false, "fixture Win32 87"); };
                var writes = api.Writes;
                Check(engine.FrameTimerIgnoreOnceForTests() && api.Writes == writes, "Win10 rejected worker policy cannot modify target");
                Check(engine.Snapshot().FrameTimerIgnore is { ExperimentalWindows10: true, PolicyAccepted: false,
                    Supported: false, IgnoreApplied: false, BackupCount: 0 }, "Rejected Win10 trial reports actual capability without backups");
                Check(engine.FrameTimerIgnoreOnceForTests() && capabilityCalls == 1, "Failed capability is not retried every tick");
                frameActive = false;
                Check(engine.FrameTimerIgnoreOnceForTests(), "Win10 trial may be turned off after capability rejection");
                engine.FrameTimerPolicyProbeForTests = () => { capabilityCalls++; return new(true, ""); };
                frameActive = true;
                Check(engine.FrameTimerIgnoreOnceForTests() && api.State.Forced && capabilityCalls == 2, "Explicit re-enable retries Win10 capability and applies after confirmation");
                Check(engine.Snapshot().FrameTimerIgnore is { ExperimentalWindows10: true, PolicyAccepted: true,
                    Supported: true, IgnoreApplied: true }, "Accepted Win10 trial still requires actual target readback");
                frameActive = false;
                Check(engine.FrameTimerIgnoreOnceForTests() && !api.State.Forced, "Win10 trial restores target when disabled");
                api.IgnoreWrites = true;
                frameActive = true;
                Check(!engine.FrameTimerIgnoreOnceForTests() && !engine.Snapshot().FrameTimerIgnore.IgnoreApplied,
                    "Accepted worker does not hide failed target readback");
                api.IgnoreWrites = false;
                frameActive = false;
                Check(engine.FrameTimerIgnoreOnceForTests() && engine.Snapshot().FrameTimerIgnore.BackupCount == 0,
                    "Failed target write can reconcile unchanged original");
                engine.FrameTimerIgnoreWindows10ForTests = null;
                engine.FrameTimerPolicyProbeForTests = null;
            }
            frameActive = true;
            global = new() { Supported = true, Readable = true, Configured = true, BootConfirmed = false };
            holder.Holding = true;
            Check(engine.OptimalClockOnceForTests() && !holder.Holding, "Unmet scan admission releases a previous holder");
            Check(!engine.ReleaseOptimalClock().Success, "Active frame remains the owner of clock release");
            frameActive = false;
            Check(engine.ReleaseOptimalClock().Success, "Release remains available with frame off");

            var repo = new DirectoryInfo(Environment.CurrentDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "DeltaNFD.sln"))) repo = repo.Parent;
            if (repo is null) throw new IOException("Repository not found");
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var main = XDocument.Load(Path.Combine(repo.FullName, "src/DeltaNFD/MainWindow.xaml"));
            var nav = main.Descendants(ns + "NavigationViewItem").Single(x => (string?)x.Attribute("Tag") == "clock");
            Check((string?)nav.Attribute("Visibility") != "Collapsed", "Pendulum navigation restored");
            var frame = XDocument.Load(Path.Combine(repo.FullName, "src/DeltaNFD/Views/FramePage.xaml"));
            var ignore = frame.Descendants(ns + "Grid").Single(x => (string?)x.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "PendulumFramePanel");
            Check(((string?)ignore.Attribute("Visibility"))?.Contains("FrameSrv.PendulumPrepared") == true, "Linked tile visible only after current preparation");
            foreach (var binding in new[] { "FrameAutomaticMemoryToggle", "FrameSrv.DwmRestartOnGameStart", "FrameSrv.PowerPlanLockEnabled", "GamePowerSelected" })
            {
                XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
                var toggle = frame.Descendants(ns + "ToggleSwitch").Single(x => (string?)x.Attribute(xaml + "Name") == binding
                    || ((string?)x.Attribute("IsOn"))?.Contains(binding) == true);
                Check(!toggle.Ancestors().Any(x => (string?)x.Attribute("Visibility") == "Collapsed"), "Unrelated frame tile remains visible: " + binding);
            }
            Check(!frame.Descendants(ns + "TextBlock").Any(x => ((string?)x.Attribute("Text"))?.Contains("请先在「钟摆计划」") == true), "Unprepared frame page has no unconditional clock preparation prompt");
            Console.WriteLine($"Public release: {count} passed; isolated process APIs and journals; no native timer, registry or game writes.");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Holder : IOptimalClockHolderApi
    {
        public bool Holding { get; set; }
        public uint Resolution100ns => 5150;
        public uint Granted100ns => 5150;
        public uint Current100ns => 5150;
        public string Error => "";
        internal int Ensures;
        public bool Ensure(uint value) { Ensures++; Holding = true; return true; }
        public void Stop() => Holding = false;
    }
    private sealed class Measurements : IOptimalClockMeasurementApi
    {
        internal int Calls;
        internal bool AllowRequestless;
        private T Fail<T>() { Calls++; throw new IOException("Unexpected measurement/load"); }
        public OptimalClockSample? MeasureGlobal(uint r, int samples, int spin) => Fail<OptimalClockSample?>();
        public OptimalClockSample? MeasureSelf(uint r, int samples, int spin) => Fail<OptimalClockSample?>();
        public double? MeasureRequestlessMs(int samples) => AllowRequestless ? 1 : Fail<double?>();
        public IOptimalClockLoad? StartLoad(int threads) => Fail<IOptimalClockLoad?>();
    }
    private sealed class Probe : IFrameTimerReachabilityApi
    {
        internal int Calls;
        public double? MeasureRequestlessGranularityMs() { Calls++; return 1; }
    }
    private sealed class TimerApi : IFrameTimerIgnoreApi
    {
        internal FrameTimerPowerState State = new(0, 0);
        internal int Writes;
        internal bool Deny;
        internal bool IgnoreWrites;
        public string IdentityState(ProcmanProcessIdentity id) => "same";
        public IFrameTimerIgnoreHandle Open(ProcmanProcessIdentity id) => new Handle(this);
        private sealed class Handle(TimerApi api) : IFrameTimerIgnoreHandle
        {
            public FrameTimerPowerState Read() => api.State;
            public void Write(FrameTimerPowerState state)
            {
                if (api.Deny) throw new IOException("fixture access denied");
                if (!api.IgnoreWrites) api.State = state;
                api.Writes++;
            }
            public void Dispose() { }
        }
    }
}
