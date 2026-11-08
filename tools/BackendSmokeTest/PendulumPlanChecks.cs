using System.Text.Json;
using DeltaNFD.Inspection;
using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

internal static class PendulumPlanChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        var value = new PendulumRegistryValue(true, 1);
        var record = new PendulumGlobalRecord { WrittenBootId = "boot-a", TargetEnabled = true, Completed = true };
        Check(PendulumGlobalInspection.NormalizeBootId("20261102120000.000000+480") == PendulumGlobalInspection.NormalizeBootId("20261102040000.000000+000"),
            "Timezone representation changes do not fabricate a restart or clear legacy recovery restart evidence");
        Check(!PendulumGlobalInspection.Evaluate(false, value, "boot-b", record).Ready, "Win10 denied even with ready registry and restart evidence");
        Check(!PendulumGlobalInspection.Evaluate(true, value, "boot-a", record).Ready, "Same boot cannot become ready by reopening app");
        Check(PendulumGlobalInspection.Evaluate(true, value, "boot-b", record).Ready, "Later verified boot admits plan");
        Check(!PendulumGlobalInspection.Evaluate(true, value, "", record).Ready, "Unknown boot identity denied");
        Check(PendulumGlobalInspection.Evaluate(true, value, "boot-b", null) is { Ready: false, NeedsRegistration: true }, "External registry 1 alone does not prove restart");
        Check(!PendulumGlobalInspection.Evaluate(true, new(false, 0), "boot-b", record).Ready, "Missing value denied");
        Check(!PendulumGlobalInspection.Evaluate(true, value, "boot-b", record with { Completed = false }).Ready, "Incomplete responsibility denied");
        Check(PendulumGlobalInspection.Evaluate(true, new(true, 0), "boot-b", record).NeedsRegistration, "External config change requires new confirmation");
        foreach (var failure in new[] { "backup", "save-before", "permission", "fingerprint", "external" })
        {
            var env = new GlobalFake { Failure = failure };
            var args = new PendulumGlobalArgs(true, failure == "fingerprint" ? "stale" : env.Read().Fingerprint);
            var result = PendulumGlobalSettingsEngine.Configure(env, args);
            Check(!result.Success && env.Writes == 0, failure + " prevents registry writes");
        }
        var setup = new GlobalFake();
        var on = PendulumGlobalSettingsEngine.Configure(setup, new(true, setup.Read().Fingerprint));
        Check(on.Success && on.RequiresReboot && setup.Value.Value == 1 && setup.Record?.Completed == true, "Enable saves, writes, reads back, requires restart");
        Check(setup.Order.IndexOf("backup") < setup.Order.IndexOf("write") && setup.Order.IndexOf("save") < setup.Order.IndexOf("write"), "Backup and journal precede write");
        Check(!setup.Read().Ready, "Successful configuration is not same-boot readiness");
        setup.BootId = "boot-b";
        Check(setup.Read().Ready, "Restart admits after successful write");
        var off = PendulumGlobalSettingsEngine.Configure(setup, new(false, setup.Read().Fingerprint));
        Check(off.Success && off.RequiresReboot && !setup.Read().Ready && setup.Value.Value == 0, "Disable revokes admission immediately");
        Check(setup.Record!.Original == new PendulumRegistryValue(false, 0), "First original survives repeated toggles");
        foreach (var failure in new[] { "write", "readback", "save-after" })
        {
            var env = new GlobalFake { Failure = failure };
            var result = PendulumGlobalSettingsEngine.Configure(env, new(true, env.Read().Fingerprint));
            Check(!result.Success && result.OutcomeUnknown && env.Record is not null, failure + " preserves unknown responsibility");
        }
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-pendulum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var token = Guid.NewGuid(); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), token.ToString("D"));
            AppDataPaths.ConfigureIsolatedDataRoot(root, token);
            var now = DateTimeOffset.UtcNow;
            var path = Path.Combine(root, "history.json");
            var history = new PendulumClockHistory(path, () => now);
            history.Observe(5150, 5000);
            Check(history.Snapshot.Count == 1 && history.Snapshot[0].Kind == "FinerRequest" && File.Exists(path), "Finer request recorded durably");
            for (var i = 0; i < 10; i++) { now = now.AddSeconds(2); history.Observe(5150, 5000); }
            Check(history.Snapshot.Count == 1 && history.Snapshot[0].LastSeenUtc == now, "Existing supervision deduplicates continued override");
            history.Observe(5150, 5150);
            Check(history.Snapshot[0].RecoveredUtc == now, "Observed recovery recorded");
            history.Observe(5150, 10000);
            Check(history.Snapshot[0].Kind == "LockLost", "Coarser drift records failed lock");
            history.Observe(5150, 0);
            Check(history.Snapshot[0].Kind == "Unreadable", "Unknown current precision recorded honestly");
            for (var i = 0; i < 40; i++) { history.Observe(5150, 5150); history.Observe(5150, 5000); }
            Check(history.Snapshot.Count == 32, "History bounded to 32 episodes");
            Check(new PendulumClockHistory(path).Snapshot.Count == 32, "Saved notices survive process/page lifetime");
            var broken = Path.Combine(root, "broken.json"); File.WriteAllText(broken, "broken-record");
            var failedHistory = new PendulumClockHistory(broken); failedHistory.Observe(5150, 5000);
            Check(failedHistory.Error.Length > 0 && File.ReadAllText(broken) == "broken-record", "Corrupt history preserved, error surfaced");

            uint current = 5150; bool frame = false;
            var global = PendulumGlobalInspection.Evaluate(true, value, "boot-b", record);
            var holder = new Holder(); var measurement = new Measurement();
            using var engine = new ProcmanEngine(Path.Combine(root, "changes.json"), Path.Combine(root, "rules.json"),
                clockHolder: holder, clockMeasurement: measurement, clockCurrentResolution: () => current);
            engine.BackgroundLoopDisabledForTests = true;
            engine.ExperimentalTimerFeaturesForTests = true;
                engine.FrameModeActiveForTests = () => frame;
            engine.GlobalPrecisionForTests = () => global;
            AppSettingsStore.Update(s => { s.OptimalClockEnabled = true; s.OptimalClockResolution100ns = 5150; s.OptimalClockScanUtc = DateTimeOffset.UtcNow; });
            Check(engine.OptimalClockOnceForTests() && !holder.Holding, "Retired independent enable flag cannot hold clock with frame off");
            Check(!engine.ApplyOptimalClock(5150).Success && holder.Ensures == 0, "Legacy apply command cannot bypass frame master");
            frame = true; global = global with { BootConfirmed = false };
            Check(engine.OptimalClockOnceForTests() && holder.Ensures == 0, "Frame waits for confirmed restart");
            Check(!engine.ScanOptimalClockAsync(true).GetAwaiter().GetResult().Success && measurement.Samples == 0, "Scan also obeys global restart gate");
            global = PendulumGlobalInspection.Evaluate(true, value, "boot-b", record);
            AppSettingsStore.Update(s => s.OptimalClockScanUtc = null);
            Check(engine.OptimalClockOnceForTests() && holder.Ensures == 0, "No scan evidence cannot use default lock");
            AppSettingsStore.Update(s => s.OptimalClockScanUtc = DateTimeOffset.UtcNow);
            Check(engine.OptimalClockOnceForTests() && holder.Holding && holder.Ensures == 1, "Ready frame starts holder without independent switch");
            Check(engine.OptimalClockOnceForTests() && holder.Ensures == 1, "Healthy supervision does not stack requests");
            current = 5000;
            Check(engine.OptimalClockOnceForTests() && engine.Snapshot().OptimalClock.Notices.Any(n => n.Kind == "FinerRequest"), "Override retained in authority snapshot");
            current = 5150; engine.OptimalClockOnceForTests();
            Check(engine.Snapshot().OptimalClock.Notices.Any(n => n.RecoveredUtc is not null), "Recovery retained in authority snapshot");
            global = global with { Configured = false };
            Check(engine.OptimalClockOnceForTests() && !holder.Holding, "Revoked global setup releases frame clock");
            frame = false;
            Check(engine.ReleaseOptimalClock().Success, "Clock release is available with frame off");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        Console.WriteLine($"Pendulum plan: {count} checks passed; fake registry, timer and measurement APIs only.");
    }
    private sealed class GlobalFake : IPendulumGlobalEnvironment
    {
        public string BootId = "boot-a", Failure = "";
        public PendulumRegistryValue Value = new(false, 0);
        public PendulumGlobalRecord? Record;
        public int Writes;
        public List<string> Order = [];
        public string? Blocker() => Failure == "permission" ? "permission denied" : null;
        public PendulumGlobalStatus Read() => PendulumGlobalInspection.Evaluate(true, Value, BootId, Record);
        public PendulumRegistryValue ReadValue() => Failure == "readback" && Writes > 0 ? new(true, 0) : Value;
        public PendulumGlobalRecord? ReadRecord() => Record;
        public void Backup(PendulumRegistryValue original) { if (Failure == "backup") throw new IOException("backup"); Order.Add("backup"); }
        public void Save(PendulumGlobalRecord record)
        {
            if (Failure == "save-before" && Writes == 0 || Failure == "save-after" && Writes > 0) throw new IOException("save");
            Record = JsonSerializer.Deserialize<PendulumGlobalRecord>(JsonSerializer.Serialize(record)); Order.Add("save");
            if (Failure == "external") Value = new(true, 1);
        }
        public void Write(bool enabled) { Writes++; Order.Add("write"); if (Failure == "write") throw new IOException("write"); Value = new(true, enabled ? 1 : 0); }
    }
    private sealed class Holder : IOptimalClockHolderApi
    {
        public int Ensures;
        public bool Holding { get; private set; }
        public uint Resolution100ns { get; private set; }
        public uint Granted100ns => Resolution100ns;
        public uint Current100ns => Resolution100ns;
        public string Error => "";
        public bool Ensure(uint resolution100ns) { Ensures++; Holding = true; Resolution100ns = resolution100ns; return true; }
        public void Stop() { Holding = false; }
    }
    private sealed class Measurement : IOptimalClockMeasurementApi
    {
        public int Samples;
        public double? MeasureRequestlessMs(int samples) { Samples++; return 1.05; }
        public OptimalClockSample? MeasureGlobal(uint r, int samples, int spinMs) { Samples++; return null; }
        public OptimalClockSample? MeasureSelf(uint r, int samples, int spinMs) { Samples++; return null; }
    }
}
