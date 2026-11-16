using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

internal static class ClockValueChecks
{
    private static int _checks;
    private static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); _checks++; }
    private sealed class Holder(bool dpc = false) : IOptimalClockHolderApi
    {
        public bool DpcProcess => dpc;
        public bool Holding { get; private set; }
        public uint Resolution100ns { get; private set; }
        public uint Granted100ns => dpc ? 0 : Resolution100ns;
        public Func<uint>? Current;
        public uint Current100ns => Current?.Invoke() ?? (Holding ? Resolution100ns : 156250u);
        public string Error => "fixture holder failure";
        public bool FailEnsure, FailStop;
        public int Ensures, Stops;
        public Action? AfterEnsure;
        public bool Ensure(uint value) { Ensures++; if (FailEnsure) return false; Resolution100ns = value; Holding = true; AfterEnsure?.Invoke(); return true; }
        public void Stop() { Stops++; if (FailStop && Holding) throw new IOException("fixture release failure"); Holding = false; }
    }
    private sealed class Api : IOptimalClockMeasurementApi
    {
        public int GlobalCalls, SelfCalls, LoadCalls;
        public uint? Actual;
        public bool Invalid, Pending, CleanupDenied, PendingAfterMeasurement;
        public Action? AfterMeasurement;
        public TaskCompletionSource? Entered;
        public ManualResetEventSlim? Continue;
        public bool HasPendingCleanup => Pending;
        public void CleanupPending() { if (CleanupDenied) throw new IOException("fixture cleanup denied"); Pending = false; }
        public OptimalClockSample? MeasureGlobal(uint target, int samples, int spin)
        {
            GlobalCalls++; Entered?.TrySetResult();
            if (Continue is not null && !Continue.Wait(TimeSpan.FromSeconds(5))) throw new IOException("fixture wait timeout");
            var actual = Actual ?? target;
            var value = new OptimalClockSample { Requested100ns = target, Granted100ns = actual, Current100ns = actual,
                ObservedBefore100ns = actual, ObservedAfter100ns = actual, Samples = samples,
                Median = Invalid ? double.NaN : 1.02, Mean = 1.03, P95 = 1.1, Min = 0.9, Max = 1.2, Over130Pct = 0 };
            Pending = PendingAfterMeasurement; AfterMeasurement?.Invoke(); return value;
        }
        public OptimalClockSample? MeasureSelf(uint target, int samples, int spin) { SelfCalls++; throw new Exception("Self fallback must never run"); }
        public double? MeasureRequestlessMs(int samples) => 1.02;
        public IOptimalClockLoad? StartLoad(int threads) { LoadCalls++; return new Load(threads); }
        private sealed class Load(int threads) : IOptimalClockLoad { public int Threads => threads; public void Dispose() { } }
    }
    private static PendulumGlobalStatus Global(bool win10) => new()
    {
        Supported = true, Readable = true, Configured = true, BootConfirmed = true, ExperimentalWindows10 = win10,
        PlatformId = win10 ? "win10-dpc-exe-v1/owned-fixture" : "", Fingerprint = "fixture-ready", ClockEnvironmentRevision = "fixture-environment",
        Win10Dpc = win10 ? new(true, @"C:\fixture\dpc.exe", "fixture") : null,
    };
    internal static async Task RunAsync()
    {
        _checks = 0;
        foreach (var pair in new[] { ("0.5000", 5000u), ("0.5130", 5130u), ("1.0000", 10000u), ("15.6250", 156250u) })
            Check(ClockValueInput.TryParse(pair.Item1, false, out var value) && value == pair.Item2, "decimal converts exactly to 100ns: " + pair.Item1);
        foreach (var text in new[] { "", " ", "NaN", "Infinity", "1e-3", "0,5130", "-0.5", "0.4999", "15.6251", "0.51301", "0.50000", "999999999999999999999999999" })
            Check(!ClockValueInput.TryParse(text, false, out _), "invalid value refused: " + text);
        Check(!ClockValueInput.TryParse("1.0001", true, out _) && ClockValueInput.TryParse("1", true, out _), "DPC 1ms ceiling enforced");
        var api = new Api();
        var row = new ClockValueTester(api).Test(5130, true, default);
        Check(row.TargetVerified && row.Samples == 200 && row.MedianMs == 1.02 && row.P95Ms == 1.1 && row.Custom, "single-value stats preserved");
        Check(api.SelfCalls == 0 && api.LoadCalls == 0 && api.GlobalCalls == 1, "independent idle measurement only; no self fallback or load");
        api.Actual = 5000;
        row = new ClockValueTester(api).Test(6000, false, default);
        Check(!row.TargetVerified && row.Granted100ns == 5000 && row.Samples == 200, "external finer/clamped timer does not masquerade as requested target");
        api.Actual = null; api.Invalid = true;
        row = new ClockValueTester(api).Test(5000, true, default);
        Check(!row.TargetVerified && row.MedianMs is null && row.Samples == 0, "nonfinite measurements are not serialized as success");
        Check(ClockValuePresentation.Sleep(row).Contains("未确认") && !ClockValuePresentation.Sleep(row).Contains("核对通过"), "failed UI does not claim target matched");
        var ready = new BackendReady(Convert.ToBase64String(new byte[32]), BackendModules.Procman, "0.935.0", 0, 1, 1, @"C:\fixture.exe", new string('A', 64));
        Check(BackendJson.Deserialize<BackendReady>(JsonSerializer.SerializeToElement(ready, BackendJson.SerializerOptions)).ClockValueVersion == 0, "old ready defaults to unsupported");
        Check(BackendJson.Deserialize<BackendReady>(JsonSerializer.SerializeToElement(ready with { ClockValueVersion = 1 }, BackendJson.SerializerOptions)).ClockValueVersion == 1, "new ready advertises value protocol");
        Check(BackendMessages.ParseReady(JsonSerializer.SerializeToElement(ready with { ClockValueVersion = 1 }, BackendJson.SerializerOptions)).ClockValueVersion == 1,
            "real strict ready parser preserves advertised value protocol");
        var legacyReady = JsonSerializer.SerializeToNode(ready, BackendJson.SerializerOptions)!.AsObject(); legacyReady.Remove("clockValueVersion");
        Check(BackendMessages.ParseReady(JsonSerializer.SerializeToElement(legacyReady)).ClockValueVersion == 0, "strict old ready defaults to unsupported");
        try { BackendMessages.ParseReady(JsonSerializer.SerializeToElement(ready with { ClockValueVersion = 2 }, BackendJson.SerializerOptions)); throw new Exception("future version accepted"); }
        catch (BackendContractException) { Check(true, "future value protocol remains rejected"); }
        Check(BackendModules.RequiresWriteIntent(BackendModules.Procman, BackendCommands.ProcmanClockValue), "temporary timer/DPC actions retain mutation intent");
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-clock-values-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var id = Guid.NewGuid(); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        foreach (var win10 in new[] { false, true }) await Engine(root, win10);
        var source = new DirectoryInfo(Environment.CurrentDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "DeltaNFD.sln"))) source = source.Parent;
        if (source is null) throw new Exception("source not found");
        var client = File.ReadAllText(Path.Combine(source.FullName, "src/DeltaNFD/Services/Backends/BackendClient.cs"));
        Check(client.IndexOf("command == BackendCommands.ProcmanClockValue && _clockValueVersion != 1", StringComparison.Ordinal)
            < client.IndexOf("BackendUnconfirmedStore.BeginIntent", StringComparison.Ordinal), "old backend refused before write intent and send");
        var page = System.Xml.Linq.XDocument.Load(Path.Combine(source.FullName, "src/DeltaNFD/Views/OptimalClockPage.xaml"));
        var input = page.Descendants().Single(x => x.Name.LocalName == "TextBox" && ((string?)x.Attribute("Text"))?.Contains("CustomResolutionText") == true);
        Check(((string?)input.Attribute("Header"))?.Contains("ms") == true && input.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.Name"), "manual input has visible units and an accessible label");
        Check(page.Descendants().Any(x => x.Name.LocalName == "TextBlock" && ((string?)x.Attribute("Text"))?.Contains("SleepTestResultText") == true
            && (string?)x.Attribute("IsTextSelectionEnabled") == "True"), "Sleep diagnostics remain selectable for feedback");
        Console.WriteLine($"Clock custom value and Sleep checks: {_checks} passed. Real controllers/settings/lifecycle with fake clocks/processes; no native timer, DPC, CPU load or game writes. Fixture: {root}");
    }
    private static async Task Engine(string root, bool win10)
    {
        var mode = win10 ? "win10" : "win11"; var folder = Path.Combine(root, mode); Directory.CreateDirectory(folder);
        var global = Global(win10); var active = false; var game = false;
        var candidate = new Holder(); var dpc = new Holder(true);
        dpc.Current = () => candidate.Holding ? candidate.Resolution100ns : dpc.Holding ? 10000u : 156250u;
        IOptimalClockHolderApi holder = win10 ? new DpcCombinedHolder(dpc, candidate) : candidate;
        var api = new Api();
        AppSettingsStore.Update(s => { s.FrameModeActive = false; s.FrameAutomaticMemoryEnabled = false; s.CustomGameModeEnabled = false;
            s.OptimalClockUseCustomResolution = false; s.OptimalClockCustomVerifiedUtc = null; s.OptimalClockCustomPlatform = "";
            s.OptimalClockScanUtc = DateTimeOffset.UtcNow; s.OptimalClockScanPlatform = win10 ? global.PlatformId + DpcPendulumSource.AlgorithmProfileSuffix : "";
            s.OptimalClockScanEnvironment = global.ClockEnvironmentRevision;
            s.OptimalClockResolution100ns = 6000; s.OptimalClockBestTypical100ns = 6100; s.OptimalClockBestStable100ns = 6200; });
        string Auto() { var s = AppSettingsStore.ReadRequired(); return $"{s.OptimalClockResolution100ns}/{s.OptimalClockScanUtc:O}/{s.OptimalClockScanPlatform}/{s.OptimalClockBestTypical100ns}/{s.OptimalClockBestStable100ns}"; }
        var originalAuto = Auto();
        var basic = AppSettingsStore.ReadRequired(); basic.OptimalClockScanPlatform = win10 ? global.PlatformId : "";
        basic.OptimalClockResolution100ns = 10000; basic.OptimalClockBestTypical100ns = basic.OptimalClockBestStable100ns = 0;
        Check(!PendulumFramePolicy.AutomaticOptimumPrepared(global, basic), mode + ": unscanned base value is not an automatic optimum");
        if (win10) { basic.OptimalClockBestTypical100ns = 6000; Check(!PendulumFramePolicy.AutomaticOptimumPrepared(global, basic), "Win10 stale best fields do not turn a base profile into an optimum"); }
        using var engine = new ProcmanEngine(Path.Combine(folder, "changes.json"), Path.Combine(folder, "rules.json"), clockHolder: holder,
            clockMeasurement: api, clockCurrentResolution: () => holder.Current100ns)
        { BackgroundLoopDisabledForTests = true, FrameModeActiveForTests = () => active, GlobalPrecisionForTests = () => global,
            ClockGameRunningForTests = () => game, FrameTargetForTests = () => new(false, "", "fixture", "DeltaForceClient-Win64-Shipping.exe", 1) };
        Task<OperationResult> Run(ClockValueAction action, uint? value = null, CancellationToken ct = default) => engine.ClockValueAsync(new() { Action = action, Resolution100ns = value }, ct);
        Check(!(await Run((ClockValueAction)99, 5000)).Success && candidate.Ensures == 0, mode + ": unknown action no request");
        Check(!(await Run(ClockValueAction.TestCustomSleep, 4999)).Success && candidate.Ensures == 0, mode + ": out-of-range no request");
        Check(!(await Run(ClockValueAction.TestAutomaticSleep, 6000)).Success, mode + ": auto source cannot accept forged input value");
        game = true; Check(!(await Run(ClockValueAction.SaveCustom, 5000)).Success, mode + ": running selected game blocks new request"); game = false;
        active = true; Check(!(await Run(ClockValueAction.TestCustomSleep, 5000)).Success, mode + ": active frame blocks test"); active = false;
        candidate.FailEnsure = true;
        Check(!(await Run(ClockValueAction.SaveCustom, 5000)).Success && !AppSettingsStore.ReadRequired().OptimalClockUseCustomResolution,
            mode + ": failed candidate request cannot authorize custom mode"); candidate.FailEnsure = false;
        candidate.Current = () => 5000;
        Check(!(await Run(ClockValueAction.SaveCustom, 6000)).Success && !AppSettingsStore.ReadRequired().OptimalClockUseCustomResolution,
            mode + ": finer external timer cannot authorize an unconfirmed custom target"); candidate.Current = null;
        var saved = await Run(ClockValueAction.SaveCustom, 5000);
        Check(saved.Success, mode + ": verified custom 0.5ms saved: " + saved.Message);
        var settings = AppSettingsStore.ReadRequired();
        Check(settings.OptimalClockCustomEnvironment == global.ClockEnvironmentRevision, mode + ": custom proof saves current environment identity");
        Check(settings.OptimalClockUseCustomResolution && settings.OptimalClockCustomResolution100ns == 5000 && Auto() == originalAuto,
            mode + ": custom storage is independent from automatic results");
        Check(PendulumFramePolicy.Prepared(global, settings) && !holder.Holding && !candidate.Holding && !dpc.Holding,
            mode + ": prepared only after verified hold and release");
        Check(!PendulumFramePolicy.Prepared(global, withCloneCustomSource("wrong")), mode + ": custom record binds current source");
        AppSettings withCloneCustomSource(string source) { var read = AppSettingsStore.ReadRequired(); read.OptimalClockCustomPlatform = source; return read; }
        active = true;
        Check(engine.OptimalClockOnceForTests() && holder.Holding && holder.Resolution100ns == 5000
            && engine.Snapshot().OptimalClock is { Prepared: true, CustomResolutionActive: true, SavedResolution100ns: 5000 }, mode + ": actual frame tick holds saved custom value");
        var admittedEnvironment = global;
        global = global with { ClockEnvironmentRevision = "changed-environment" };
        Check(engine.OptimalClockOnceForTests() && !holder.Holding && !engine.Snapshot().OptimalClock.Prepared,
            mode + ": environment change releases actual custom hold and invalidates readiness");
        global = admittedEnvironment;
        engine.ClockEnvironmentLeaseForTests = () => { global = global with { ClockEnvironmentRevision = "changed-before-lease" }; return null; };
        Check(!engine.ApplyOptimalClock(null).Success && !holder.Holding, mode + ": direct hold reads readiness after acquiring environment lease");
        global = admittedEnvironment;
        Check(engine.OptimalClockOnceForTests() && !holder.Holding, mode + ": background hold reads readiness after acquiring environment lease");
        engine.ClockEnvironmentLeaseForTests = null; global = admittedEnvironment;
        active = false; Check(engine.OptimalClockOnceForTests() && !holder.Holding, mode + ": frame off releases custom hold");
        var before = JsonSerializer.Serialize(AppSettingsStore.ReadRequired());
        var result = await Run(ClockValueAction.TestCustomSleep, 5500);
        Check(result.Success && engine.Snapshot().OptimalClock.SleepTest is { Requested100ns: 5500, TargetVerified: true, ReleaseConfirmed: true, Samples: 200 }, mode + ": typed manual Sleep result and release");
        Check(JsonSerializer.Serialize(AppSettingsStore.ReadRequired()) == before && !candidate.Holding && !dpc.Holding, mode + ": test does not switch selection or alter saved results");
        Check((await Run(ClockValueAction.TestAutomaticSleep)).Success && engine.Snapshot().OptimalClock.SleepTest is { Requested100ns: 6000, Custom: false }, mode + ": saved automatic optimum can be tested while custom remains selected");
        Check(AppSettingsStore.ReadRequired().OptimalClockUseCustomResolution && api.SelfCalls == 0 && api.LoadCalls == 0, mode + ": tests do not switch modes or start CPU stress");
        api.Actual = 5000;
        Check(!(await Run(ClockValueAction.TestCustomSleep, 7000)).Success && engine.Snapshot().OptimalClock.SleepTest is { TargetVerified: false, Granted100ns: 5000, ReleaseConfirmed: true }, mode + ": contaminated samples not attributed to requested value"); api.Actual = null;
        Check((await Run(ClockValueAction.UseAutomatic)).Success && !AppSettingsStore.ReadRequired().OptimalClockUseCustomResolution
            && AppSettingsStore.ReadRequired().OptimalClockCustomVerifiedUtc is not null && Auto() == originalAuto, mode + ": switch to auto preserves custom and scan records");
        api.PendingAfterMeasurement = true; api.AfterMeasurement = () => api.CleanupDenied = true;
        Check(!(await Run(ClockValueAction.TestCustomSleep, 5000)).Success && engine.Snapshot().OptimalClock.SleepTest is { ReleaseConfirmed: false }, mode + ": unresolved cleanup cannot report test success");
        api.CleanupDenied = api.PendingAfterMeasurement = false; api.AfterMeasurement = null;
        Check(engine.ReleaseOptimalClock().Success, mode + ": release responsibility can be retried");
        candidate.AfterEnsure = () => candidate.FailStop = true;
        Check(!(await Run(ClockValueAction.SaveCustom, 5600)).Success && !AppSettingsStore.ReadRequired().OptimalClockUseCustomResolution,
            mode + ": release failure prevents custom persistence");
        candidate.FailStop = false; candidate.AfterEnsure = null; Check(engine.ReleaseOptimalClock().Success, mode + ": failed candidate release retried");
        api.AfterMeasurement = () => global = withFingerprintChanged();
        PendulumGlobalStatus withFingerprintChanged() => new() { Supported = true, Readable = true, Configured = true, BootConfirmed = true,
            ExperimentalWindows10 = win10, PlatformId = global.PlatformId, Fingerprint = "changed", Win10Dpc = global.Win10Dpc };
        Check(!(await Run(ClockValueAction.TestCustomSleep, 5000)).Success && engine.Snapshot().OptimalClock.SleepTest?.TargetVerified == false,
            mode + ": preparation identity change invalidates result"); api.AfterMeasurement = null; global = Global(win10);
        candidate.AfterEnsure = () => global = global with { ClockEnvironmentRevision = "changed-environment" };
        before = JsonSerializer.Serialize(AppSettingsStore.ReadRequired());
        Check(!(await Run(ClockValueAction.SaveCustom, 5500)).Success && JsonSerializer.Serialize(AppSettingsStore.ReadRequired()) == before,
            mode + ": environment change during validation cannot replace saved custom proof");
        candidate.AfterEnsure = null; global = Global(win10);
        api.AfterMeasurement = () => game = true;
        Check(!(await Run(ClockValueAction.TestCustomSleep, 5000)).Success && !holder.Holding && !candidate.Holding && !dpc.Holding,
            mode + ": game starting during measurement invalidates result and releases requests"); api.AfterMeasurement = null; game = false;
        api.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); api.Continue = new(false);
        var pending = Run(ClockValueAction.TestCustomSleep, 5000); await api.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!(await Run(ClockValueAction.TestCustomSleep, 6000)).Success, mode + ": concurrent experiments refused");
        Check(!engine.ReleaseOptimalClock().Success && !pending.IsCompleted, mode + ": public release cannot prematurely report an active experiment released");
        var stopping = engine.StopRulesEngineAsync(); await Task.Delay(25);
        Check(!stopping.IsCompleted, mode + ": stop waits for in-flight measurement cleanup");
        api.Continue.Set(); Check(!(await pending.WaitAsync(TimeSpan.FromSeconds(5))).Success && (await stopping.WaitAsync(TimeSpan.FromSeconds(5))).Success,
            mode + ": shutdown cancellation releases both request owners");
        Check(!holder.Holding && !candidate.Holding && !dpc.Holding && Auto() == originalAuto, mode + ": no saved-result or hold leak on cancellation");
        api.Continue.Dispose(); api.Continue = null;
    }
}
