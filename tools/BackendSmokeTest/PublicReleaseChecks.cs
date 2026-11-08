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
        Check(!ReleaseFeaturePolicy.PendulumEnabled && !ReleaseFeaturePolicy.FrameTimerIgnoreEnabled, "Public feature policy");
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
                s.FrameModeActive = true; s.FrameGameTimerIgnoreEnabled = true; s.OptimalClockEnabled = true;
                s.OptimalClockScanUtc = DateTimeOffset.UtcNow; s.OptimalClockResolution100ns = 5150;
            });
            var holder = new Holder(); var measure = new Measurements(); var api = new TimerApi(); var probe = new Probe();
            var id = new ProcmanProcessIdentity(12345, DateTime.UtcNow, @"C:\Games\DeltaForceClient-Win64-Shipping.exe");
            using var engine = new ProcmanEngine(Path.Combine(root, "changes.json"), Path.Combine(root, "rules.json"),
                timerIgnoreApi: api, timerProbe: new FrameTimerLegacyProbe(probe), clockHolder: holder,
                clockMeasurement: measure, clockCurrentResolution: () => 5150)
            {
                FrameModeActiveForTests = () => true, BackgroundLoopDisabledForTests = true,
                FrameTimerIgnoreTargetsForTests = () => [id],
                GlobalPrecisionForTests = () => throw new IOException("Hidden feature must not read admission state"),
            };
            var saved = JsonSerializer.Serialize(AppSettingsStore.Read());
            Check(engine.OptimalClockOnceForTests() && holder.Ensures == 0, "Saved frame/scan cannot start hidden holder");
            Check(!engine.ApplyOptimalClock(5150).Success, "Direct apply rejected");
            foreach (var algorithm in new[] { OptimalClockScanKind.LowestLatency, OptimalClockScanKind.StableLatency })
            {
                Check(!(await engine.ScanOptimalClockAsync(true, algorithm)).Success, "Hidden algorithm rejected");
                Check(!(await engine.ScanOptimalClockAsync(true, algorithm, compatibilityOnly: true)).Success, "Hidden compatibility probe rejected");
            }
            Check(engine.FrameTimerIgnoreOnceForTests() && api.Writes == 0 && probe.Calls == 0, "Saved ignore preference cannot apply or measure");
            Check(measure.Calls == 0 && holder.Ensures == 0, "No timer measurements, loads or holder start");
            Check(JsonSerializer.Serialize(AppSettingsStore.Read()) == saved, "Old choices and scan history preserved without execution");
            Check((await PendulumGlobalSettings.ConfigureAsync(new(true, "stale"), default)).Message.Contains("未开放"), "Direct registry enable rejected before native access");

            // Seed an older owned journal with the same fake process API, then let
            // the actual public tick restore it while the frame master stays on.
            if (ProcmanEngine.FrameTimerIgnoreSupported)
            {
                engine.ExperimentalTimerFeaturesForTests = true;
                Check(engine.FrameTimerIgnoreOnceForTests() && api.State.Forced, "Fixture seeds old owned timer bit");
                engine.ExperimentalTimerFeaturesForTests = false;
                Check(engine.FrameTimerIgnoreOnceForTests() && !api.State.Forced, "Public tick restores old timer journal despite active frame");
                Check(!engine.Snapshot().FrameTimerIgnore.RestorePending, "Confirmed restore clears responsibility");
                engine.ExperimentalTimerFeaturesForTests = true;
                Check(engine.FrameTimerIgnoreOnceForTests() && api.State.Forced, "Fixture seeds another old bit");
                engine.ExperimentalTimerFeaturesForTests = false; api.Deny = true;
                Check(!engine.FrameTimerIgnoreOnceForTests() && engine.Snapshot().FrameTimerIgnore.RestorePending, "Failed restoration retains responsibility");
                api.Deny = false;
                Check(engine.FrameTimerIgnoreOnceForTests() && !api.State.Forced, "Restoration can retry after public gate");
            }
            holder.Holding = true;
            Check(engine.OptimalClockOnceForTests() && !holder.Holding, "Public tick releases previously held clock");
            Check(engine.ReleaseOptimalClock().Success, "Release remains available while frame active");

            var repo = new DirectoryInfo(Environment.CurrentDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "DeltaNFD.sln"))) repo = repo.Parent;
            if (repo is null) throw new IOException("Repository not found");
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var main = XDocument.Load(Path.Combine(repo.FullName, "src/DeltaNFD/MainWindow.xaml"));
            var nav = main.Descendants(ns + "NavigationViewItem").Single(x => (string?)x.Attribute("Tag") == "clock");
            Check((string?)nav.Attribute("Visibility") == "Collapsed", "Pendulum navigation hidden");
            var frame = XDocument.Load(Path.Combine(repo.FullName, "src/DeltaNFD/Views/FramePage.xaml"));
            var ignore = frame.Descendants(ns + "ToggleSwitch").Single(x => ((string?)x.Attribute("IsOn"))?.Contains("GameTimerIgnoreSelected") == true);
            Check(ignore.Ancestors().Any(x => (string?)x.Attribute("Visibility") == "Collapsed"), "Timer-ignore tile hidden");
            foreach (var binding in new[] { "FrameAutomaticMemoryToggle", "FrameSrv.DwmRestartOnGameStart", "FrameSrv.PowerPlanLockEnabled", "GamePowerSelected" })
            {
                XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
                var toggle = frame.Descendants(ns + "ToggleSwitch").Single(x => (string?)x.Attribute(xaml + "Name") == binding
                    || ((string?)x.Attribute("IsOn"))?.Contains(binding) == true);
                Check(!toggle.Ancestors().Any(x => (string?)x.Attribute("Visibility") == "Collapsed"), "Unrelated frame tile remains visible: " + binding);
            }
            Check(!frame.Descendants(ns + "TextBlock").Any(x => ((string?)x.Attribute("Text"))?.Contains("精度锁定随帧格启停") == true), "Frame master description matches public scope");
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
        private T Fail<T>() { Calls++; throw new IOException("Unexpected measurement/load"); }
        public OptimalClockSample? MeasureGlobal(uint r, int samples, int spin) => Fail<OptimalClockSample?>();
        public OptimalClockSample? MeasureSelf(uint r, int samples, int spin) => Fail<OptimalClockSample?>();
        public double? MeasureRequestlessMs(int samples) => Fail<double?>();
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
        public string IdentityState(ProcmanProcessIdentity id) => "same";
        public IFrameTimerIgnoreHandle Open(ProcmanProcessIdentity id) => new Handle(this);
        private sealed class Handle(TimerApi api) : IFrameTimerIgnoreHandle
        {
            public FrameTimerPowerState Read() => api.State;
            public void Write(FrameTimerPowerState state)
            {
                if (api.Deny) throw new IOException("fixture access denied");
                api.State = state; api.Writes++;
            }
            public void Dispose() { }
        }
    }
}
