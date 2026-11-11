using System.Text.Json;
using DeltaNFD.Services;
using DeltaNFD.Execution.CpuFrame;
using DeltaNFD.Execution.Procman;

namespace BackendSmokeTest;

internal static class FrameGamePriorityChecks
{
    private static int _checks;
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); _checks++; }
    private static readonly ProcmanProcessIdentity Id = new(12345, DateTime.UtcNow, @"C:\Games\DeltaForceClient-Win64-Shipping.exe");
    private sealed class Lab : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "DeltaNFD-frame-priority-" + Guid.NewGuid().ToString("N"));
        internal string Journal => Path.Combine(Root, "frame-priority.json");
        internal readonly ProcessPropertyGate Owners;
        internal readonly Api Api = new();
        internal readonly FrameGamePriority Controller;
        internal Lab()
        {
            Directory.CreateDirectory(Root); Owners = new(Path.Combine(Root, "owners.json"));
            Controller = new(Journal, Owners, Api);
        }
        internal bool Sync(FrameGamePriorityConfig? config = null, Func<ProcmanProcessIdentity, bool>? allow = null) =>
            Controller.Sync([Id], config ?? new() { Cpu = FrameCpuPriority.RealTime, Io = FrameIoPriority.High }, allow ?? (_ => true));
        internal bool Restore() => Controller.Sync([], new(), _ => false);
        public void Dispose() { Directory.Delete(Root, true); }
    }
    private sealed class Api : IFramePriorityApi
    {
        internal readonly Dictionary<string, int> Values = new() { ["priority"] = 32, ["ioPriority"] = 2, ["memoryPriority"] = 3 };
        internal readonly List<(string Property, int Value)> Writes = [];
        internal string? FailWrite;
        internal bool WrongReadback;
        internal string Identity = "same";
        internal Action<string, int>? BeforeWrite;
        public string IdentityState(ProcmanProcessIdentity id) => Identity;
        public IFramePriorityHandle Open(ProcmanProcessIdentity id)
        {
            if (Identity != "same") throw new IOException("fake identity is not current");
            return new Handle(this);
        }
        private sealed class Handle(Api api) : IFramePriorityHandle
        {
            public int Read(string property) => api.Values[property];
            public void Write(string property, int value)
            {
                api.BeforeWrite?.Invoke(property, value); api.Writes.Add((property, value));
                if (api.FailWrite == property) throw new IOException("fake access denied / NTSTATUS 0xC0000061");
                api.Values[property] = api.WrongReadback && property == "priority" ? 128 : value;
            }
            public void Dispose() { }
        }
    }
    internal static async Task RunAsync()
    {
        _checks = 0;
        var defaults = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Check(!defaults.FrameGamePriorityEnabled && defaults.FrameGameCpuPriority == FrameCpuPriority.AboveNormal
            && defaults.FrameGameIoPriority == FrameIoPriority.Normal && defaults.FrameGameMemoryPriority == FrameMemoryPriority.Normal,
            "Old settings must not automatically opt in; configured defaults must match the user request.");
        var configDefaults = JsonSerializer.Deserialize<FrameGamePriorityConfig>("{}")!;
        Check(configDefaults.Cpu == FrameCpuPriority.AboveNormal && configDefaults.Io == FrameIoPriority.Normal
            && configDefaults.Memory == FrameMemoryPriority.Normal && configDefaults.IsValid,
            "Omitted command fields use the same defaults as saved settings.");
        var saved = JsonSerializer.Deserialize<AppSettings>("""{"FrameGameCpuPriority":256,"FrameGameIoPriority":3}""")!;
        Check(saved.FrameGameCpuPriority == FrameCpuPriority.RealTime && saved.FrameGameIoPriority == FrameIoPriority.High,
            "Explicitly saved choices are preserved when defaults change.");
        foreach (var cpu in Enum.GetValues<FrameCpuPriority>())
        foreach (var io in Enum.GetValues<FrameIoPriority>())
        foreach (var memory in Enum.GetValues<FrameMemoryPriority>())
        {
            using var lab = new Lab(); var config = new FrameGamePriorityConfig { Cpu = cpu, Io = io, Memory = memory };
            lab.Api.BeforeWrite = (property, value) => Check(JsonSerializer.Deserialize<List<FrameGamePriority.Backup>>(File.ReadAllText(lab.Journal))!
                .Any(b => b.Identity == Id && b.Property == property && b.Applied == value), "Every native write must have a durable original first.");
            Check(lab.Sync(config), "Every valid combination must apply.");
            Check(lab.Api.Values["priority"] == (int)cpu && lab.Api.Values["ioPriority"] == (int)io && lab.Api.Values["memoryPriority"] == (int)memory,
                "Independent user choices must reach the actual native boundary.");
            var writes = lab.Api.Writes.Count;
            Check(lab.Sync(config) && lab.Api.Writes.Count == writes, "Already-correct values must be queried, not rewritten every tick.");
            // Mutate one property externally; the lock reapplies without replacing its first original.
            lab.Api.Values["priority"] = (int)cpu == 32 ? 128 : 32;
            Check(lab.Sync(config) && lab.Api.Values["priority"] == (int)cpu, "A changed CPU value is maintained.");
            lab.Api.BeforeWrite = null;
            Check(lab.Restore() && lab.Api.Values["priority"] == 32 && lab.Api.Values["ioPriority"] == 2 && lab.Api.Values["memoryPriority"] == 3,
                "Disable must restore first-seen originals, not the most recently observed values.");
            Check(lab.Controller.PendingCount == 0 && !lab.Controller.Snapshot.RestorePending, "Verified restoration clears only completed records.");
        }
        using (var lab = new Lab())
        {
            lab.Api.FailWrite = "ioPriority";
            Check(!lab.Sync() && lab.Controller.Snapshot.HasError, "IO denial must not be hidden by CPU/memory success.");
            Check(lab.Controller.Snapshot.CpuAppliedCount == 1 && lab.Controller.Snapshot.IoAppliedCount == 0 && lab.Controller.Snapshot.MemoryAppliedCount == 1,
                "Status reports each actual readback independently.");
            var attempts = lab.Api.Writes.Count;
            for (var i = 0; i < 4; i++) Check(!lab.Sync(), "Denied native result remains failed.");
            Check(lab.Api.Writes.Count == attempts, "Do not hammer the same denied native write on every tick.");
            lab.Api.FailWrite = null;
            Check(lab.Restore() && lab.Sync(), "An explicit off/on permits retry with preserved originals.");
            Check(lab.Restore(), "Retried successful values are reversible.");
        }
        using (var lab = new Lab())
        {
            lab.Controller.BeforeSaveForTests = () => throw new IOException("fake disk failure");
            Check(!lab.Sync() && lab.Api.Writes.Count == 0, "Backup save failure must prevent all native writes.");
        }
        using (var lab = new Lab())
        {
            Check(lab.Sync(), "Crash fixture applied.");
            var next = new FrameGamePriority(lab.Journal, lab.Owners, lab.Api);
            Check(next.Snapshot.RestorePending && next.PendingCount == 3, "A new backend must expose old durable recovery responsibility.");
            Check(next.Sync([], new(), _ => false) && lab.Api.Values["priority"] == 32 && next.PendingCount == 0,
                "A new backend can restore the previous backend's per-property originals.");
        }
        using (var lab = new Lab())
        {
            Check(lab.Sync(), "Restore failure fixture applied."); lab.Api.FailWrite = "priority";
            Check(!lab.Restore() && lab.Controller.PendingCount == 1 && lab.Controller.Snapshot.RestorePending, "Failed CPU restore retains its record after other properties restore.");
            lab.Api.FailWrite = null; Check(lab.Restore() && lab.Controller.PendingCount == 0, "A later explicit recovery completes only the unfinished property.");
        }
        using (var lab = new Lab())
        {
            Check(lab.Sync(), "Manual takeover fixture applied.");
            using (var owner = lab.Owners.Enter(Id, "priority")) owner.SetOwner("manual");
            lab.Api.Values["priority"] = 128;
            Check(!lab.Sync() && lab.Api.Values["priority"] == 128, "A manual procman override wins over the frame lock.");
            Check(!lab.Restore() && lab.Controller.PendingCount == 1 && lab.Api.Values["priority"] == 128, "Do not overwrite manual selection or discard pending original on disable.");
            using (var owner = lab.Owners.Enter(Id, "priority")) owner.SetOwner(null);
            lab.Api.Values["priority"] = 256;
            Check(lab.Restore() && lab.Api.Values["priority"] == 32, "Recovery resumes after ownership and expected value are restored.");
        }
        using (var lab = new Lab())
        {
            Check(!lab.Sync(allow: _ => false) && lab.Api.Writes.Count == 0, "Latest execution gate is required before any write.");
            var calls = 0;
            Check(!lab.Sync(allow: _ => ++calls % 2 == 1) && lab.Api.Writes.Count == 0, "Recheck after durable backup rejects late stop/config changes.");
        }
        using (var lab = new Lab())
        {
            Check(lab.Sync(), "Unknown identity fixture applied.");lab.Api.Identity = "unknown";
            Check(!lab.Restore() && lab.Controller.PendingCount == 3, "Unknown PID/path/time retains originals without writing.");
            lab.Api.Identity = "missing";
            Check(lab.Restore() && lab.Controller.PendingCount == 0, "A confirmed exited/reused identity never receives restore writes.");
        }
        using (var lab = new Lab())
        {
            Check(!lab.Controller.Sync([Id with { ExecutablePath = @"C:\Games\ACE-Tray.exe" }], new(), _ => true) && lab.Api.Writes.Count == 0,
                "Never target ACE or a custom game through this feature.");
        }
        using (var lab = new Lab())
        {
            File.WriteAllText(lab.Journal, "\0broken"); var bytes = File.ReadAllBytes(lab.Journal);
            Check(!lab.Sync() && lab.Api.Writes.Count == 0 && File.ReadAllBytes(lab.Journal).SequenceEqual(bytes), "Corrupt evidence is retained, never reset to empty.");
        }
        using (var lab = new Lab())
        {
            Check(lab.Sync(), "Missing journal fixture applied.");File.Delete(lab.Journal);
            var calls = lab.Api.Writes.Count;
            Check(!lab.Sync() && lab.Controller.Snapshot.RestorePending && lab.Api.Writes.Count == calls, "Deleted originals must not be replaced by already-modified values.");
        }
        using (var lab = new Lab())
        {
            Check(!lab.Sync(new() { Cpu = (FrameCpuPriority)7 }) && lab.Api.Writes.Count == 0, "Reject arbitrary CPU classes.");
            Check(!lab.Sync(new() { Io = (FrameIoPriority)4 }) && lab.Api.Writes.Count == 0, "Do not expose system-critical IO.");
            Check(!lab.Sync(new() { Memory = (FrameMemoryPriority)6 }) && lab.Api.Writes.Count == 0, "Memory priority is bounded to supported values.");
        }
        using (var lab = new Lab())
        {
            lab.Api.WrongReadback = true;
            Check(!lab.Sync() && lab.Controller.Snapshot.CpuAppliedCount == 0, "A success return without requested readback is not applied.");
            lab.Api.WrongReadback = false;
            Check(!lab.Restore() && lab.Controller.PendingCount == 1, "Unexplained changed value is not overwritten or falsely restored.");
        }
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ProcmanStatusSnapshot { UpdatedUtc = now, RulesEngineRunning = true,
            FramePriority = new() { Maintaining = true, Message = "actual priority fixture", UpdatedUtc = now, BackupCount = 3 } };
        Check(FrameStatusPresentation.Priority(true, true, snapshot, now, "") == "actual priority fixture", "UI uses actual snapshot.");
        Check(FrameStatusPresentation.Priority(true, true, snapshot, now.AddSeconds(20), "").Contains("未确认"), "Stale snapshot cannot claim applied.");
        Check(FrameStatusPresentation.Priority(false, false, snapshot, now, "pipe lost").Contains("恢复仍待核对"), "Off switch/disconnect cannot hide restoration responsibility.");
        Check(!FrameStatusPresentation.Priority(true, false, null, now, "").Contains("生效"), "Selection alone is not execution.");
        Check(typeof(CpuFrameGamePowerArgs).GetProperty("GamePriority") is null, "CPU/Frame command contract does not execute this procman-only feature.");
        await ProcmanIntegrationAsync();
        Console.WriteLine($"Frame game priority passed: {_checks} checks; fake process properties and isolated journals only, no game or host priority writes.");
    }

    private static async Task ProcmanIntegrationAsync()
    {
        var token = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-priority-engine-" + token.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), token.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, token);
        try
        {
            AppSettingsStore.Update(s => { s.FrameModeActive = false; s.FrameAutomaticMemoryEnabled = false; s.FrameGamePriorityEnabled = true; });
            var api = new Api();
            using (var engine = new ProcmanEngine(Path.Combine(root, "changes.json"), Path.Combine(root, "rules.json"), api)
            { BackgroundLoopDisabledForTests = true, FramePriorityTargetsForTests = () => [Id] })
            {
                Check(engine.SyncFrameBackground().Success && api.Writes.Count == 0, "The procman command cannot apply without the frame master switch.");
                AppSettingsStore.Update(s => { s.FrameModeActive = true; s.CustomGameModeEnabled = true; s.CustomGameExecutablePath = @"C:\Other\custom.exe"; });
                Check(engine.SyncFrameBackground().Success && engine.Snapshot().FramePriority.CpuAppliedCount == 1,
                    "The actual procman sync starts priority independently of automatic memory and custom game selection.");
                Check(!engine.BackgroundStatus().AutomaticMemoryActive && engine.Snapshot().HeldChangeCount == 3,
                    "Priority backups enter procman recovery counts without activating memory reclaim.");
                var count = api.Writes.Count;
                Check(engine.FramePriorityOnceForTests() && api.Writes.Count == count, "The existing loop queries already-matching process values.");
                api.Values["priority"] = 128;
                Check(engine.FramePriorityOnceForTests() && api.Values["priority"] == (int)FrameCpuPriority.AboveNormal, "Procman maintains external drift on its own tick.");
                var owners = new ProcessPropertyGate(Path.Combine(root, "procman-owners.json"));
                using (var lease = owners.Enter(Id, "priority"))
                    Check(lease.Owner == "frame-priority", "Procman publishes distinct property ownership for the frame lock.");

                Check((await engine.StopRulesEngineAsync()).Success && api.Values["priority"] == 32
                    && api.Values["ioPriority"] == 2 && api.Values["memoryPriority"] == 3, "Stopping procman monitoring restores all three actual originals.");
                count = api.Writes.Count;
                Check(engine.FramePriorityOnceForTests() && api.Writes.Count == count, "A late tick after monitoring stop cannot reapply even if frame remains selected.");
                Check(engine.SyncFrameBackground().Success && api.Values["priority"] == (int)FrameCpuPriority.AboveNormal, "Explicit sync may restart the stopped feature.");
                engine.FramePriorityTargetsForTests = () => [];
                Check(engine.FramePriorityOnceForTests() && engine.Snapshot().HeldChangeCount == 0 && api.Values["priority"] == 32,
                    "Loss of the target from the confirmed game snapshot restores the surviving instance before waiting.");
                engine.FramePriorityTargetsForTests = () => [Id];
                Check(engine.FramePriorityOnceForTests(), "An explicitly observed new game instance can apply again.");
                AppSettingsStore.Update(s => s.FrameModeActive = false);
                Check(engine.SyncFrameBackground().Success && engine.Snapshot().HeldChangeCount == 0 && api.Values["priority"] == 32,
                    "Turning off frame mode restores through procman, without any CPU/frame command.");

                AppSettingsStore.Update(s => { s.FrameModeActive = true; s.GamePriorityEnabled = true; });
                Check(!engine.SyncFrameBackground().Success && api.Values["priority"] == 32
                    && engine.Snapshot().FramePriority.IoAppliedCount == 1, "Legacy High automation is reported as a CPU conflict instead of fighting frame CPU priority.");
                AppSettingsStore.Update(s => { s.GamePriorityEnabled = false; s.FrameModeActive = false; });
                Check(engine.SyncFrameBackground().Success, "Partial activation is still reversible.");

                Check(engine.SaveRule(new() { ExecutablePath = Id.ExecutablePath, Capability = "ioPriority", Value = "Normal" }).Success,
                    "An existing per-property rule is available to the actual engine.");
                AppSettingsStore.Update(s => s.FrameModeActive = true);
                Check(!engine.SyncFrameBackground().Success && api.Values["ioPriority"] == 2
                    && engine.Snapshot().FramePriority.CpuAppliedCount == 1, "An enabled IO rule wins while independent CPU/memory choices still apply.");
                Check((await engine.RestoreAllAsync()).Success && engine.Snapshot().HeldChangeCount == 0,
                    "Procman's existing all-scope recovery also restores the frame feature.");
                Check((await engine.DeleteRuleAsync(Id.ExecutablePath, "ioPriority")).Success, "Removing the isolated rule succeeds.");
                Check(engine.SyncFrameBackground().Success, "The command resumes all attributes after explicit rule removal.");
                api.FailWrite = "priority";
                Check(!(await engine.StopRulesEngineAsync()).Success && engine.Snapshot().FramePriority.RestorePending
                    && engine.Snapshot().HeldChangeCount == 1, "Procman stop failure preserves the durable original and advertises recovery responsibility.");
                api.FailWrite = null;
                Check((await engine.RestoreAllAsync()).Success && engine.Snapshot().HeldChangeCount == 0, "A later explicit all-scope recovery completes the failed original.");
                Check(engine.SyncFrameBackground().Success, "Fixture for backend disposal.");
            }
            Check(api.Values["priority"] == 32 && api.Values["ioPriority"] == 2 && api.Values["memoryPriority"] == 3,
                "Disposing the procman engine restores each property.");

            // Shared gate rejects the old CPU/frame High writer; no native process setting is changed.
            var self = ProcmanIdentity.TryIdentify(Environment.ProcessId) ?? throw new Exception("Cannot identify own fixture.");
            var shared = ProcessPropertyGate.Default;
            using (var property = shared.Enter(self, "priority")) property.Game(false, "frame-priority");
            using (var oldWriter = ProcessPropertyGate.TryEnterGameLease(self.Pid, "priority"))
                Check(oldWriter is null, "Legacy CPU/frame automatic High cannot overwrite the procman frame-priority owner.");
            using (var property = shared.Enter(self, "priority")) property.Game(true);

            var resetRoot = Path.Combine(root, "factory-reset"); Directory.CreateDirectory(resetRoot);
            var resetJournal = Path.Combine(resetRoot, "frame-game-priority.json");
            File.WriteAllText(resetJournal, "original priority recovery evidence");
            File.WriteAllText(Path.Combine(resetRoot, "settings.json"), """{"FrameModeActive":true,"FrameGamePriorityEnabled":true}""");
            var reset = new FactoryResetConfiguration(resetRoot); reset.Backup(); reset.PauseAutomation();
            Check(!AppSettingsStore.Read(Path.Combine(resetRoot, "settings.json")).FrameGamePriorityEnabled,
                "Factory reset pauses priority automation before restoring configuration.");
            reset.CommitDefaults();
            Check(File.ReadAllText(resetJournal) == File.ReadAllText(Path.Combine(reset.BackupDirectory, "frame-game-priority.json")),
                "Factory reset backs up and preserves the new durable priority originals.");

            var recoveryRoot = Path.Combine(root, "old-session"); Directory.CreateDirectory(recoveryRoot);
            var journal = Path.Combine(recoveryRoot, "frame-game-priority.json");
            var persisted = new FrameGamePriority(journal, new(Path.Combine(recoveryRoot, "procman-owners.json")), api);
            Check(persisted.Sync([Id], new(), _ => true), "Persist a prior backend's durable original.");
            using (var restarted = new ProcmanEngine(Path.Combine(recoveryRoot, "changes.json"), Path.Combine(recoveryRoot, "rules.json"), api)
            { BackgroundLoopDisabledForTests = true, FramePriorityTargetsForTests = () => [Id] })
            {
                Check(restarted.Snapshot().FramePriority.RestorePending && restarted.Snapshot().HeldChangeCount > 0,
                    "A new procman session exposes prior recovery responsibility before activation.");
                Check((await restarted.RestoreAllAsync()).Success && api.Values["priority"] == 32,
                    "The existing procman recovery command restores an earlier backend's original.");
                Check(restarted.SyncFrameBackground().Success, "Restarted procman accepts a fresh explicit sync after recovery.");
                File.WriteAllText(Path.Combine(recoveryRoot, "changes.json.background.json"), "\0broken");
                AppSettingsStore.Update(s => s.FrameModeActive = false);
                Check(!restarted.SyncFrameBackground().Success && api.Values["priority"] == 32
                    && restarted.Snapshot().HeldChangeCount == 0, "Corrupt memory configuration cannot prevent priority restoration when frame stops.");
            }
            AppSettingsStore.Update(s => { s.FrameModeActive = false; s.FrameGamePriorityEnabled = false; });
        }
        finally { Directory.Delete(root, true); }
    }
}
