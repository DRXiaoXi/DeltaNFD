using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

internal static class DpcPendulumChecks
{
    internal static void Run()
    {
        int n = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); n++; }
        bool Throws(Action action) { try { action(); return false; } catch (Exception) { return true; } }
        const string path = @"D:\Private\dpclat.exe";
        var os = new Version(10, 0, 19045, 6093);
        var ready = DpcPendulumInspection.Evaluate(os, new(true, path, "fixture"));
        Check(ready.Ready && ready.Win10Driver is null && ready.PlatformId.StartsWith(DpcPendulumSource.Profile), "Win10 source needs no test-sign/self-owned-driver evidence");
        Check(!DpcPendulumInspection.Evaluate(os, new(false, path, "missing")).Ready, "Unverified source fails closed");
        Check(!DpcPendulumInspection.Evaluate(new(10, 0, 22000), new(true, path, "fixture")).Ready, "Win11 doesn't enter DPC path");
        Check(!DpcPendulumInspection.Evaluate(new(10, 0, 17762), new(true, path, "fixture")).Ready, "Win10 minimum build");
        Check(ready.PlatformId != DpcPendulumInspection.Evaluate(os, new(true, @"D:\Other\dpclat.exe", "fixture")).PlatformId, "Moving file invalidates previous scan identity");
        Check(ready.PlatformId != DpcPendulumInspection.Evaluate(new(10, 0, 19045, 1), new(true, path, "fixture")).PlatformId, "System revision bound");
        Check(JsonSerializer.Deserialize<PendulumGlobalStatus>(JsonSerializer.Serialize(ready))!.Ready, "New source identity survives IPC");
        Check(!new PendulumGlobalStatus { Supported = true, Readable = true, ExperimentalWindows10 = true }.Ready, "Missing old IPC source is not ready");
        const string reportedDriverPath = @"\??\C:\Windows\system32\drivers\dpclat_driver.sys";
        Check(NativeDpcSessionApi.NormalizeServicePath(reportedDriverPath).Equals(@"C:\Windows\system32\drivers\dpclat_driver.sys", StringComparison.OrdinalIgnoreCase),
            "Actual Win10 VM DPC 1.4.0 service path is recognized");
        foreach (var input in new[] { @"C:\Windows\system32\drivers\dpclat_driver.sys", @"\??\C:\Windows\system32\drivers\DPCLAT_DRIVER.SYS",
            @"\SystemRoot\System32\drivers\dpclat_driver.sys", @"System32\drivers\dpclat_driver.sys", @"C:\Windows\system32\drivers\dpclat.sys" })
            Check(NativeDpcSessionApi.NormalizeServicePath(input).Length > 0, "Known local DPC service path form accepted: " + input);
        foreach (var input in new[] { @"C:\Windows\system32\drivers\other.sys", @"C:\dpclat_driver.sys.exe", @"C:\dpclat_driver.sys:stream",
            @"\\server\dpclat_driver.sys", "\"C:\\dpclat_driver.sys\"", @"C:\dpclat_driver.sys --arg", "dpclat_driver.sys" })
            Check(NativeDpcSessionApi.NormalizeServicePath(input).Length == 0, "Foreign/argument/remote/stream driver path rejected: " + input);
        foreach (var invalid in new[] { @"\\server\dpclat.exe", "dpclat.exe", "D:\\a.exe:stream", "\"D:\\a.exe\"", "D:\\a.sys", "D:\\a.exe --arg" })
            Check(Throws(() => DpcPendulumInspection.CanonicalExe(invalid)), "Invalid executable path rejected: " + invalid);
        foreach (var value in new uint[] { 0, 10001, uint.MaxValue }) Check(!DpcProcessHolder.DpcReadbackAccepted(value), "Invalid/coarse timer rejected");
        foreach (var value in new uint[] { 5000, 10000 }) Check(DpcProcessHolder.DpcReadbackAccepted(value), "Other finer request is reported without false exactness");
        foreach (var bytes in new[] { 0, 21233 })
        {
            using var stream = new UnreadableFixture(bytes);
            var identity = NativeDpcSessionApi.ReadDriverIdentity(stream);
            Check(!identity.Verified && identity.Hash.Length == 0 && identity.Bytes == bytes,
                "Wrong driver length is reported without attempting an unbounded read: " + bytes);
        }

        Fake api = new();
        DpcProcessHolder New() => new(api, () => path);
        var holder = New();
        Check(!holder.Ensure(5000) && api.Launches == 0 && api.State is null, "Original EXE cannot claim 0.5ms");
        Check(holder.Ensure(10000) && holder.Holding && api.Launches == 1, "Original 1ms start");
        Check(api.IntentBeforeLaunch && api.State is { Phase: "running", Pid: 123 }, "Intent precedes launch and runtime identity persists");
        Check(api.Process?.WindowMaintenanceStarts == 1 && api.State?.Phase == "running", "Window maintenance begins only after readiness and runtime identity persist");
        Check(holder.Granted100ns == 0, "No invented driver grant returned by EXE");
        Check(holder.Ensure(10000) && api.Launches == 1, "Same request does not spawn again");
        holder.Stop();
        Check(api.Closed == 1 && api.State is null && !holder.Holding, "Normal close and original service cleanup confirmed");
        holder.Stop(); Check(api.Closed == 1, "Repeated stop is idempotent");
        api = new() { RawServicePath = reportedDriverPath }; holder = New();
        Check(holder.Ensure(10000) && holder.Holding && api.State?.ServicePath.EndsWith("dpclat_driver.sys", StringComparison.OrdinalIgnoreCase) == true,
            "Actual VM raw path with matching identity and 1ms now passes holder lifecycle");
        holder.Stop(); Check(api.State is null && api.Closed == 1, "Actual-name path has the same verified cleanup lifecycle");
        api = new() { RawServicePath = reportedDriverPath, Fault = "bad-service" }; holder = New();
        Check(!holder.Ensure(10000) && api.State is null, "Correct driver filename alone cannot bypass content identity rejection");

        foreach (var fault in new[] { "blocked", "input", "service-read", "save-first", "spawn", "timeout", "early-exit", "bad-service" })
        {
            api = new() { Fault = fault }; holder = New();
            Check(!holder.Ensure(10000), "Fault never reports holding: " + fault);
            Check(!string.IsNullOrWhiteSpace(holder.Error), "Original failure survives successful cleanup: " + fault);
            if (fault is "blocked" or "input" or "service-read" or "save-first") Check(api.Launches == 0, "Prelaunch fault has no launch: " + fault);
            if (fault == "save-first") { holder.Stop(); Check(api.State is null, "Failed initial write doesn't invent a running process"); }
            if (fault == "spawn") Check(api.State is { Phase: "intent", Pid: 0 } && Throws(holder.Stop), "Ambiguous spawn preserves intent instead of replaying");
            if (fault is "timeout" or "bad-service") Check(api.Closed == 1 && api.State is null, "Failed readiness normal-closes its own process: " + fault);
        }
        var events = new List<(string Name, object? Data)>();
        api = new() { Fault = "timeout" };
        holder = new(api, () => path, (name, data) => events.Add((name, data)));
        Check(!holder.Ensure(10000) && api.State is null && api.Closed == 1, "Logged timeout still releases own process");
        Check(holder.Error.Contains("15 秒核验超时") && holder.Error.Contains("156250"), "Timeout reports original actual resolution after cleanup");
        Check(events.Count(x => x.Name == "dpc-process-readiness") is > 0 and <= 16, "Readiness logging is bounded during timeout");
        var failureEvent = events.Single(x => x.Name == "dpc-process-start-failed");
        Check(JsonSerializer.Serialize(failureEvent.Data).Contains("156250"), "Failure journal retains pre-cleanup observation");
        events.Clear(); api = new() { Fault = "bad-service" };
        holder = new(api, () => path, (name, data) => events.Add((name, data)));
        Check(!holder.Ensure(10000) && api.CurrentQueries > 0, "Resolution read doesn't short-circuit when service identity fails");
        Check(holder.Error.Contains("驱动身份未通过") && holder.Error.Contains("1.0000ms"), "Service rejection is distinguishable from timer rejection");
        api = new(); holder = new(api, () => path, (_, _) => throw new IOException("logger fault"));
        Check(holder.Ensure(10000), "Logger failure cannot reject a healthy hold");
        holder.Stop(); Check(api.State is null, "Logger failure cannot prevent release");
        api = new() { ExternalService = true }; holder = New();
        Check(!holder.Ensure(10000) && api.Launches == 0 && api.Closed == 0, "External DPC/plugin service is never adopted");
        holder.Stop(); Check(api.Closed == 0 && api.ExternalService, "No journal means no external cleanup");

        api = new(); holder = New(); holder.Ensure(10000); api.CloseFails = true;
        Check(Throws(holder.Stop) && api.State is not null && api.Closed == 0, "Normal-close refusal retains record, no kill fallback");
        api.CloseFails = false; holder.Stop(); Check(api.State is null, "Explicit stop retry resolves record");
        api = new(); holder = New(); holder.Ensure(10000); api.KeepService = true;
        Check(Throws(holder.Stop) && api.State is not null && api.Closed == 1, "Leftover service isn't directly deleted");
        api.KeepService = false; holder.Stop(); Check(api.State is null, "Original cleanup observed on retry");

        api = new(); holder = New(); holder.Ensure(10000);
        var old = api.State!; api.Controller = 901;
        var successor = New();
        Check(!successor.Ensure(10000) && api.Launches == 1, "New backend cannot duplicate an old record");
        successor.Stop(); Check(api.State is null && api.Closed == 1, "Exited old controller's exact process can be recovered");
        api = new(); holder = New(); holder.Ensure(10000); api.Controller = 901; api.OtherControllerAlive = true;
        successor = New(); successor.Stop();
        Check(api.State is not null && api.Closed == 0, "Living other controller isn't cleaned by this backend");
        api.OtherControllerAlive = false; successor.Stop();
        Check(api.State is null, "Recovery after exact old controller exits");

        api = new() { Fault = "spawn" }; holder = New(); holder.Ensure(10000); api.Boot = Guid.NewGuid().ToString();
        holder.Stop(); Check(api.State is null && api.Closed == 0, "Changed boot plus no service safely archives unknown pre-PID intent");
        api = new() { Fault = "spawn" }; holder = New(); holder.Ensure(10000); api.Boot = Guid.NewGuid().ToString(); api.ExternalService = true;
        Check(Throws(holder.Stop) && api.State is not null, "Changed boot alone cannot erase existing service uncertainty");

        api = new(); holder = New();
        var measurement = new ForbiddenMeasurement();
        var controller = new OptimalClockController(holder, measurement, currentResolution: () => api.Current());
        Check(controller.Apply(10000, true), "DPC controller apply");
        Check(controller.State.DpcProcessVerified && !controller.State.KernelResolutionVerified && !controller.State.VerifiedGlobal
            && controller.State.Granted100ns == 0 && controller.State.RequestlessMilliseconds is null && controller.State.ScanLoadThreads == 0,
            "Numeric observation doesn't claim a grant/Sleep/game effect");
        Check(controller.Ensure(true, 10000, true) && api.Launches == 1, "Supervision is query-only while healthy");
        api.Process!.Active = false; api.ActiveService = false;
        Check(!controller.Ensure(true, 10000, true), "Manual close doesn't get overridden by automatic relaunch");
        Check(!controller.Ensure(true, 10000, true) && api.Launches == 1, "Failure latched without spawn loop");
        Check(controller.Stop() && controller.Ensure(true, 10000, true) && api.Launches == 2, "Explicit off/on permits a fresh attempt");
        Check(controller.Stop(), "Controller releases normally");
        Check(controller.Apply(10000, true), "Controller starts before a simulated status failure");
        var closed = api.Closed; api.Fault = "service-read";
        Check(!controller.Stop() && api.Closed == closed + 1 && api.State is not null,
            "Unreadable pre-cleanup service status still attempts normal close and retains uncertainty");
        api.Fault = "";
        Check(controller.Stop() && api.State is null, "Readable service status allows cleanup retry after query failure");
        Check(BackendModules.RequiresWriteIntent(BackendModules.Procman, BackendCommands.ProcmanClockDpcConfigure), "File selection uses mutating backend contract");
        Check(ready.PlatformId != "win10-kernel-resolution-v3/" + os, "Old own-driver identity cannot authorize DPC");

        var isolated = Path.Combine(Path.GetTempPath(), "DeltaNFD-dpc-pendulum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        var marker = Guid.NewGuid(); File.WriteAllText(Path.Combine(isolated, "isolated-test.marker"), marker.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(isolated, marker);
        api = new(); holder = New(); var frame = false; var global = ready;
        using (var engine = new ProcmanEngine(Path.Combine(isolated, "changes.json"), Path.Combine(isolated, "rules.json"),
            clockHolder: holder, clockMeasurement: measurement, clockCurrentResolution: () => api.Current()))
        {
            engine.GlobalPrecisionForTests = () => global;
            engine.FrameModeActiveForTests = () => frame;
            engine.BackgroundLoopDisabledForTests = true;
            var before = JsonSerializer.Serialize(AppSettingsStore.Read());
            Check(engine.ScanOptimalClockAsync(true, OptimalClockScanKind.LowestLatency, compatibilityOnly: true).GetAwaiter().GetResult().Success, "Real engine dispatches DPC probe without Sleep");
            Check(api.State is null && !holder.Holding && JsonSerializer.Serialize(AppSettingsStore.Read()) == before, "Probe closes before returning and doesn't save");
            Check(engine.ScanOptimalClockAsync(false).GetAwaiter().GetResult().Success, "Real engine saves DPC after cleanup");
            var saved = JsonSerializer.Serialize(AppSettingsStore.Read());
            Check(AppSettingsStore.Read().OptimalClockResolution100ns == 10000 && AppSettingsStore.Read().OptimalClockScanPlatform == global.PlatformId
                && api.State is null, "Saved target and source identity after confirmed release");
            Check(engine.Snapshot().OptimalClock is { ScanLoadThreads: 0, RequestlessMilliseconds: null, BestTypicalMedianMs: null }, "Engine IPC has no invented latency statistics");
            frame = true; Check(engine.OptimalClockOnceForTests() && holder.Holding, "Saved source starts with frame");
            Check(engine.Snapshot().OptimalClock is { DpcProcessVerified: true, VerifiedGlobal: false, KernelResolutionVerified: false }, "Engine IPC carries process evidence separately");
            var launches = api.Launches;
            Check(engine.OptimalClockOnceForTests() && api.Launches == launches, "Frame tick doesn't duplicate original EXE");
            global = global with { Win10Dpc = new(false, path, "changed") };
            Check(engine.OptimalClockOnceForTests() && !holder.Holding && api.State is null, "Revoking source readiness releases own process");
            global = ready; frame = false;
            Check(engine.OptimalClockOnceForTests(), "Frame off remains idempotent");
            api.Fault = "timeout";
            var timedOut = engine.ScanOptimalClockAsync(false).GetAwaiter().GetResult();
            Check(!timedOut.Success && timedOut.Message.Contains("15 秒核验超时") && timedOut.Message.Contains("156250"), "Real engine response preserves failed predicate instead of blank error");
            Check(!timedOut.Success && api.State is null
                && JsonSerializer.Serialize(AppSettingsStore.Read()) == saved, "Failed DPC readback leaves earlier selection intact");
            api.Fault = "";
            using (AppSettingsStore.BlockWritesForReset(AppSettingsStore.DefaultPath))
                Check(!engine.ScanOptimalClockAsync(false).GetAwaiter().GetResult().Success && api.State is null
                    && JsonSerializer.Serialize(AppSettingsStore.Read()) == saved, "Settings commit failure releases and never reports saved");
            api.CloseFails = true;
            Check(!engine.ScanOptimalClockAsync(false).GetAwaiter().GetResult().Success
                && api.State is not null && JsonSerializer.Serialize(AppSettingsStore.Read()) == saved, "Unconfirmed original process cleanup cannot save");
            api.CloseFails = false; Check(engine.ReleaseOptimalClock().Success && api.State is null, "Explicit release recovers failed cleanup");
        }

        var original = Environment.GetEnvironmentVariable("DELTANFD_DPC_TEST_ORIGINAL");
        var driver = Environment.GetEnvironmentVariable("DELTANFD_DPC_TEST_DRIVER");
        if (!string.IsNullOrEmpty(driver))
        {
            using var file = File.OpenRead(driver);
            var identity = NativeDpcSessionApi.ReadDriverIdentity(file);
            Check(identity.Verified && identity.Bytes == 21232, "Actual original x64 driver bytes pass pinned identity without execution");
            file.Position = 0; using var changed = new MemoryStream(); file.CopyTo(changed);
            changed.Position = 0; changed.WriteByte((byte)(changed.GetBuffer()[0] ^ 1)); changed.Position = 0;
            Check(!NativeDpcSessionApi.ReadDriverIdentity(changed).Verified, "One-byte changed real driver is rejected regardless of accepted filename");
        }
        if (!string.IsNullOrEmpty(original))
        {
            using (var file = DpcPendulumInspection.OpenVerified(original)) Check(file.Length == DpcPendulumSource.ExeBytes, "Actual player original only read/hashed");
            var dir = Path.Combine(Path.GetTempPath(), "dpc-identity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, "copy.exe");
            try
            {
                File.Copy(original, copy);
                using (DpcPendulumInspection.OpenVerified(copy)) Check(Throws(() => { using var writer = File.Open(copy, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }), "Verified input is locked against replacement/write");
                var bytes = File.ReadAllBytes(copy); bytes[0] ^= 1; File.WriteAllBytes(copy, bytes);
                Check(Throws(() => DpcPendulumInspection.OpenVerified(copy).Dispose()), "One changed byte rejected");
            }
            finally { File.Delete(copy); Directory.Delete(dir); }
        }
        Console.WriteLine($"DPC Win10 pendulum: {n} checks passed. Fake lifecycle plus optional read-only identity fixtures; no DPC/driver/timer/CPU execution.");
    }
    private sealed class ForbiddenMeasurement : IOptimalClockMeasurementApi
    {
        public OptimalClockSample? MeasureGlobal(uint r, int s, int m) => throw new Exception("Unexpected Sleep measurement");
        public OptimalClockSample? MeasureSelf(uint r, int s, int m) => throw new Exception("Unexpected Sleep measurement");
        public double? MeasureRequestlessMs(int samples) => throw new Exception("Unexpected Sleep measurement");
        public IOptimalClockLoad? StartLoad(int n) => throw new Exception("Unexpected CPU load");
    }
    private sealed class Lease : IDisposable { public void Dispose() { } }
    private sealed class UnreadableFixture(int bytes) : MemoryStream
    {
        public override long Length => bytes;
        public override int Read(byte[] buffer, int offset, int count) => throw new Exception("Unexpected mismatched driver read");
        public override int Read(Span<byte> buffer) => throw new Exception("Unexpected mismatched driver read");
    }
    private sealed class Fake : IDpcSessionApi
    {
        public string Boot = Guid.NewGuid().ToString(); public string BootId => Boot;
        public int Controller = 900; public int ControllerPid => Controller; public long ControllerStartTicks => 100;
        public string Fault = "", RawServicePath = ""; public int Launches, Closed, Saves, CurrentQueries; public bool ActiveService, ExternalService, KeepService, CloseFails, OtherControllerAlive, IntentBeforeLaunch;
        public DpcPendulumJournal? State; public FakeProcess? Process;
        public void CheckStartAllowed() { if (Fault == "blocked") throw new IOException("blocked game/platform/permissions"); }
        public IDisposable LockInput(string path) { if (Fault == "input") throw new IOException("invalid original"); return new Lease(); }
        public DpcPendulumJournal? Read() => State is null ? null : Clone(State);
        private static DpcPendulumJournal Clone(DpcPendulumJournal r) => JsonSerializer.Deserialize<DpcPendulumJournal>(JsonSerializer.Serialize(r))!;
        public void Save(DpcPendulumJournal r) { Saves++; if (Fault == "save-first" && Saves == 1) throw new IOException("save"); State = Clone(r); }
        public void Archive(DpcPendulumJournal r) { if (State?.Id != r.Id) throw new IOException("journal changed"); State = null; }
        public IDpcOwnedProcess Launch(string path)
        {
            IntentBeforeLaunch = State is { Phase: "intent", Pid: 0 }; if (!IntentBeforeLaunch) throw new Exception("No durable intent");
            if (Fault == "spawn") throw new IOException("launch uncertain");
            Launches++; ActiveService = Fault != "early-exit"; Process = new(this) { Active = Fault != "early-exit" }; return Process;
        }
        public IDpcOwnedProcess? Find(int p, long t, string path) => Process is { Active: true } && p == Process.Pid && t == Process.StartTicks ? Process : null;
        public bool ControllerAlive(int pid, long ticks) => OtherControllerAlive;
        public DpcServiceObservation Service()
        {
            if (Fault == "service-read") throw new IOException("unknown service state");
            var exists = ExternalService || ActiveService || KeepService;
            var path = RawServicePath.Length == 0 ? @"C:\Windows\System32\drivers\dpclat.sys" : NativeDpcSessionApi.NormalizeServicePath(RawServicePath);
            return new(exists, exists, exists && Fault != "bad-service" && path.Length > 0, exists ? path : "") { RawImagePath = exists ? RawServicePath : "" };
        }
        public uint Current() { CurrentQueries++; return Fault == "timeout" ? 156250u : 10000u; }
        public void Delay(int n) { }
        public sealed class FakeProcess(Fake api) : IDpcOwnedProcess
        {
            public int WindowMaintenanceStarts;
            public void BeginWindowMaintenance() => WindowMaintenanceStarts++;
            public bool Active; public int Pid => 123; public long StartTicks => 200;
            public bool Alive => Active;
            public bool CloseNormally(int timeoutMs) { if (api.CloseFails) return false; api.Closed++; Active = false; api.ActiveService = false; return true; }
            public void Dispose() { }
        }
    }
}
