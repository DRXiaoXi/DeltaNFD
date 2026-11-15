using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

internal static class CustomFrameTargetChecks
{
    private static int _checks;
    private static void Check(bool ok, string why) { if (!ok) throw new IOException(why); _checks++; }
    private static string Isolate()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-custom-frame-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var token = Guid.NewGuid(); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), token.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, token); return root;
    }
    private static readonly string Selected = @"C:\Games\A\OtherGame.exe";
    private static readonly GameTarget Custom = new(true, Selected, "OtherGame", "OtherGame.exe", 1);
    private static readonly GameTarget Delta = new(false, "", "DeltaForceClient-Win64-Shipping", "三角洲", 2);
    private static ProcmanProcessIdentity Id(int pid, string path) => new(pid, DateTime.UtcNow, path);

    internal static void Run()
    {
        var root = Isolate();
        try
        {
            var a = Id(12345, Selected); var b = Id(12346, Selected);
            var delta = Id(12347, @"C:\Games\DeltaForceClient-Win64-Shipping.exe");
            var wrong = Id(12348, @"C:\Games\B\OtherGame.exe"); var child = Id(12349, @"C:\Games\A\Child.exe");
            using (var f = new Fixture(Path.Combine(root, "selection"), Custom))
            {
                f.Targets = [a, b, delta, wrong, child];
                Check(f.Sync(), "Custom frame starts with actual shared tick");
                Check(f.Priority.WrittenPids.SetEquals([a.Pid, b.Pid]) && f.Priority.Writes == 6, "Priority only selected path and multiple instances");
                Check(f.Timer.WrittenPids.SetEquals([a.Pid, b.Pid]) && f.Timer.Writes == 2, "Timer ignore excludes simultaneous Delta, same-name other directory and child EXE");
                Check(f.Engine.Snapshot().FramePriority.InstanceCount == 2 && f.Engine.Snapshot().FrameTimerIgnore.AppliedCount == 2, "Actual counts follow selected instances");
                Check(JsonSerializer.Deserialize<List<FrameGamePriority.Backup>>(File.ReadAllText(f.PriorityPath))!.All(x => x.CustomTargetPath == Selected), "Custom priority originals bound to admitted image");
                Check(JsonSerializer.Deserialize<List<FrameGameTimerIgnore.Backup>>(File.ReadAllText(f.TimerPath))!.All(x => x.CustomTargetPath == Selected), "Custom ignore originals bound to admitted image");
                Check(f.Engine.Snapshot().OptimalClock.Holding, "Prepared clock still held while custom game ignores requests");
                f.Active = false;
                Check(f.Sync() && f.Engine.Snapshot().FramePriority.BackupCount == 0 && f.Engine.Snapshot().FrameTimerIgnore.BackupCount == 0, "Frame off restores selected games and clears completed records");
                f.Active = true; Check(f.Sync(), "Custom frame can explicitly restart");
                f.Priority.Deny = f.Timer.Deny = true; f.Active = false;
                Check(!f.Sync() && f.Engine.Snapshot().FrameTimerIgnore.RestorePending && File.Exists(f.PriorityPath), "Denied restore preserves responsibility");
                f.Priority.Deny = f.Timer.Deny = false;
                Check(f.Sync() && !f.Engine.Snapshot().FrameTimerIgnore.RestorePending, "Custom originals can be retried");
            }
            using (var f = new Fixture(Path.Combine(root, "generation"), Custom))
            {
                f.Targets = [a];
                f.Priority.BeforeRead = () => f.Target = f.Target with { Generation = 2 };
                Check(!f.Engine.FramePriorityOnceForTests() && f.Priority.Writes == 0, "Generation change after open refuses priority write");
                AppSettingsStore.Update(s => s.FrameGamePriorityEnabled = false);
                Check(f.Engine.OptimalClockOnceForTests(), "Prepare fake clock for generation gate");
                f.Timer.BeforeRead = () => f.Target = f.Target with { Generation = 3 };
                Check(!f.Engine.FrameTimerIgnoreOnceForTests() && f.Timer.Writes == 0, "Generation change after open refuses ignore write");
                f.Active = false; Check(f.Sync(), "Generation failures leave no unowned modification");
            }
            foreach (var algorithm in new[] { false, true })
            {
                using var f = new Fixture(Path.Combine(root, algorithm ? "win10-algorithm" : "win10-basic"), Custom, win10: true, algorithm: algorithm);
                f.Targets = [a, delta, wrong];
                Check(f.Sync() && f.Engine.Snapshot().OptimalClock.DpcProcessVerified, "Custom Win10 keeps verified DPC base: algorithm=" + algorithm);
                Check(f.Timer.WrittenPids.SetEquals([a.Pid]) && f.Engine.Snapshot().FrameTimerIgnore.PolicyAccepted == true,
                    "Custom Win10 policy still self-checks and only accepts selected path");
                f.Active = false; Check(f.Sync(), "Custom Win10 releases and restores both modes");
            }
            var priority = new PriorityApi(); var timer = new TimerApi();
            var recovery = Path.Combine(root, "restart");
            using (var f = new Fixture(recovery, Custom, priority, timer))
            {
                f.Targets = [a]; Check(f.Sync(), "Seed custom scope for restart");
                f.PreserveForRestart = true;
            }
            using (var f = new Fixture(recovery, Delta, priority, timer))
            {
                f.Active = false;
                priority.DenyCustomRecovery = timer.DenyCustomRecovery = true;
                Check(!f.Sync() && f.Engine.Snapshot().FrameTimerIgnore.RestorePending, "Unknown custom recovery admission preserves originals in the new mode");
                priority.DenyCustomRecovery = timer.DenyCustomRecovery = false;
                Check(f.Sync() && f.Engine.Snapshot().FramePriority.BackupCount == 0 && f.Engine.Snapshot().FrameTimerIgnore.BackupCount == 0,
                    "Restart in another mode restores prior custom records without requiring the old file");
            }
            var legacy = Path.Combine(root, "legacy"); Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "frame-game-priority.json"), JsonSerializer.Serialize(new[] { new FrameGamePriority.Backup(delta, "priority", 32, 128) }));
            File.WriteAllText(Path.Combine(legacy, "frame-game-timer-ignore.json"), JsonSerializer.Serialize(new[] { new FrameGameTimerIgnore.Backup(delta, 1, 1, 5, 5) }));
            priority = new(); priority.Values(delta.Pid)["priority"] = 128;
            timer = new(); timer.States[delta.Pid] = new(5, 5);
            using (var f = new Fixture(legacy, Custom, priority, timer))
            {
                f.Active = false;
                priority.DenyCustomRecovery = timer.DenyCustomRecovery = true;
                Check(f.Sync() && priority.Values(delta.Pid)["priority"] == 32 && !timer.States[delta.Pid].Forced,
                    "Legacy unscoped Delta records restore while custom mode is selected");
            }
            foreach (var marker in new string?[] { null, @"C:\Games\B\OtherGame.exe", @"C:\Windows\System32\svchost.exe" })
            {
                var dir = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "frame-game-priority.json"), JsonSerializer.Serialize(new[] { new FrameGamePriority.Backup(a, "priority", 32, 128, marker) }));
                File.WriteAllText(Path.Combine(dir, "frame-game-timer-ignore.json"), JsonSerializer.Serialize(new[] { new FrameGameTimerIgnore.Backup(a, 1, 1, 5, 5, marker) }));
                using var f = new Fixture(dir, Custom); f.Active = false;
                Check(!f.Sync() && f.Priority.Writes == 0 && f.Timer.Writes == 0, "Unscoped, mismatched or protected custom record cannot authorize recovery: " + marker);
                f.PreserveForRestart = true;
            }
            using (var f = new Fixture(Path.Combine(root, "mismatch"), Custom))
            {
                f.Targets = [a]; Check(f.Sync(), "Seed current custom target");
                AppSettingsStore.Update(s => s.CustomGameModeEnabled = false);
                Check(!f.Sync() && f.Engine.Snapshot().FrameTimerIgnore.BackupCount == 0 && f.Engine.Snapshot().FramePriority.BackupCount == 0,
                    "Backend/setting disagreement restores existing state rather than applying to a stale target");
                f.Target = Delta; f.Active = false;
            }
            Check(FrameStatusPresentation.PendulumDescription(false, "OtherGame.exe").Contains("OtherGame.exe")
                && !FrameStatusPresentation.PendulumDescription(false, "OtherGame.exe").Contains("三角洲"), "Custom presentation names selected EXE");
            var now = DateTimeOffset.UtcNow; var message = "等待 中文三角洲.exe 启动";
            var snapshot = new ProcmanStatusSnapshot { UpdatedUtc = now, RulesEngineRunning = true,
                FramePriority = new() { UpdatedUtc = now, Message = message }, FrameTimerIgnore = new() { Supported = true, UpdatedUtc = now, Message = message } };
            Check(FrameStatusPresentation.Priority(true, true, snapshot, now, "", "中文三角洲.exe") == message
                && FrameStatusPresentation.TimerIgnore(true, true, snapshot, now, "", "中文三角洲.exe").StartsWith(message, StringComparison.Ordinal),
                "Backend messages and filenames containing 三角洲 are not rewritten");
            Console.WriteLine($"Custom frame target: {_checks} checks passed; real engine/controllers with fake targets, process-property APIs and clock only.");
        }
        finally { Cleanup(root); }
    }

    internal static async Task RunLiveAsync()
    {
        var root = Isolate(); var processes = new List<Process>();
        try
        {
            string Copy(string name)
            {
                var directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
                foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
                    if (file.EndsWith(".dll") || file.EndsWith(".json")) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
                var exe = Path.Combine(directory, "FrameTargetFixture.exe"); File.Copy(Environment.ProcessPath!, exe); return exe;
            }
            var a = Copy("A"); var b = Copy("B");
            Check((await GameTargetService.Default.ChangeAsync(true, a)).Success, "Select actual inert EXE through real mode service");
            foreach (var exe in new[] { a, a, b }) processes.Add(Process.Start(new ProcessStartInfo(exe, "--game-target-test-child") { UseShellExecute = false, CreateNoWindow = true })!);
            await Task.Delay(300);
            using var f = new Fixture(Path.Combine(root, "state"), GameTargetService.Default.Current, live: true);
            Check(f.Sync(), "Actual target enumeration reaches fake writers");
            var expected = processes.Take(2).Select(p => p.Id).ToHashSet();
            Check(f.Priority.WrittenPids.SetEquals(expected) && f.Priority.Writes == 6, "Real PID/path selection priority: two selected instances only");
            Check(f.Timer.WrittenPids.SetEquals(expected) && f.Timer.Writes == 2, "Real PID/path selection timer ignore: same-name other directory excluded");
            f.Active = false; Check(f.Sync(), "Own fixture originals reconcile");
            Console.WriteLine($"Custom frame live identity: {_checks} checks passed; only own inert child processes; all property/clock APIs fake, no native writes or timer requests.");
        }
        finally
        {
            foreach (var p in processes) { try { if (!p.HasExited) { p.Kill(); await p.WaitForExitAsync(); } } finally { p.Dispose(); } }
            Cleanup(root);
        }
    }
    private static void Cleanup(string root)
    {
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(root);
        if (!full.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("DeltaNFD-custom-frame-", StringComparison.Ordinal)
            || !File.Exists(Path.Combine(full, "isolated-test.marker"))) throw new IOException("Fixture cleanup target not verified");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                foreach (var file in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected fixture reparse file");
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(full, true); return;
            }
            catch (IOException) when (attempt < 3) { Thread.Sleep(150); }
            catch (UnauthorizedAccessException) when (attempt < 3) { Thread.Sleep(150); }
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal GameTarget Target; internal bool Active = true, PreserveForRestart;
        internal ProcmanProcessIdentity[] Targets = [];
        internal readonly PriorityApi Priority; internal readonly TimerApi Timer; internal readonly ProcmanEngine Engine;
        internal readonly string PriorityPath, TimerPath;
        internal Fixture(string root, GameTarget target, PriorityApi? priority = null, TimerApi? timer = null, bool live = false,
            bool win10 = false, bool algorithm = false)
        {
            Directory.CreateDirectory(root); Target = target; Priority = priority ?? new(); Timer = timer ?? new();
            PriorityPath = Path.Combine(root, "frame-game-priority.json"); TimerPath = Path.Combine(root, "frame-game-timer-ignore.json");
            AppSettingsStore.Update(s => { s.FrameModeActive = false; s.CustomGameModeEnabled = target.IsCustom; s.CustomGameExecutablePath = target.ExecutablePath;
                s.FrameGamePriorityEnabled = true; s.FrameGameCpuPriority = FrameCpuPriority.High; s.FrameGameIoPriority = FrameIoPriority.High; s.FrameGameMemoryPriority = FrameMemoryPriority.Normal;
                s.OptimalClockScanUtc = DateTimeOffset.UtcNow; s.OptimalClockScanPlatform = win10 ? "fixture-custom-win10" + (algorithm ? DpcPendulumSource.AlgorithmProfileSuffix : "") : "";
                s.OptimalClockResolution100ns = win10 && !algorithm ? 10000u : 5150u; });
            IOptimalClockHolderApi clock = win10 ? new DpcCombinedHolder(new Clock(true), new Clock()) : new Clock();
            Engine = new(Path.Combine(root, "changes.json"), Path.Combine(root, "rules.json"), priorityApi: Priority, timerIgnoreApi: Timer,
                timerProbe: new FrameTimerLegacyProbe(new Reachability()), clockHolder: clock, clockMeasurement: new Measurements(), clockCurrentResolution: () => clock.Current100ns)
            { BackgroundLoopDisabledForTests = true, FrameModeActiveForTests = () => Active,
                GlobalPrecisionForTests = () => win10 ? new() { Supported = true, Readable = true, ExperimentalWindows10 = true,
                    PlatformId = "fixture-custom-win10", Win10Dpc = new(true, @"C:\fixture\dpc.exe", "fixture") }
                    : new() { Supported = true, Readable = true, Configured = true, BootConfirmed = true },
                FrameTimerIgnoreWindows10ForTests = () => win10, FrameTimerPolicyProbeForTests = () => new(true, "") };
            if (!live) { Engine.FrameTargetForTests = () => Target; Engine.FrameCanonicalPathForTests = Path.GetFullPath;
                Engine.FramePriorityTargetsForTests = () => Targets; Engine.FrameTimerIgnoreTargetsForTests = () => Targets; }
        }
        internal bool Sync() => Engine.SyncFrameBackground().Success;
        public void Dispose() { if (PreserveForRestart) return; Active = false; Engine.SyncFrameBackground(); Engine.Dispose(); }
    }
    private sealed class Clock(bool dpc = false) : IOptimalClockHolderApi
    {
        private uint _resolution = 5150;
        public bool DpcProcess => dpc;
        public bool Holding { get; private set; } public uint Resolution100ns => _resolution; public uint Granted100ns => _resolution;
        public uint Current100ns => Holding ? _resolution : 156250u; public string Error => "";
        public bool Ensure(uint target) { _resolution = target; Holding = true; return true; } public void Stop() => Holding = false;
    }
    private sealed class Measurements : IOptimalClockMeasurementApi
    {
        public double? MeasureRequestlessMs(int samples) => 1;
        public OptimalClockSample? MeasureGlobal(uint r, int samples, int spin) => throw new IOException("No scanner allowed");
        public OptimalClockSample? MeasureSelf(uint r, int samples, int spin) => throw new IOException("No self probe allowed");
        public IOptimalClockLoad? StartLoad(int threads) => throw new IOException("No CPU load allowed");
    }
    private sealed class Reachability : IFrameTimerReachabilityApi { public double? MeasureRequestlessGranularityMs() => 1; }
    private sealed class TimerApi : IFrameTimerIgnoreApi
    {
        internal int Writes; internal bool Deny, DenyCustomRecovery; internal Action? BeforeRead; internal readonly HashSet<int> WrittenPids = [];
        internal readonly Dictionary<int, FrameTimerPowerState> States = [];
        public string IdentityState(ProcmanProcessIdentity id) => "same";
        public IFrameTimerIgnoreHandle Open(ProcmanProcessIdentity id) { States.TryAdd(id.Pid, new(1, 1)); return new Handle(this, id.Pid); }
        public IFrameTimerIgnoreHandle OpenForRestore(ProcmanProcessIdentity id, bool customTarget)
        { if (customTarget && DenyCustomRecovery) throw new UnauthorizedAccessException("fixture custom admission unknown"); return Open(id); }
        private sealed class Handle(TimerApi api, int pid) : IFrameTimerIgnoreHandle
        {
            public FrameTimerPowerState Read() { var hook = api.BeforeRead; api.BeforeRead = null; hook?.Invoke(); return api.States[pid]; }
            public void Write(FrameTimerPowerState state) { if (api.Deny) throw new IOException("fixture denied"); api.Writes++; api.WrittenPids.Add(pid); api.States[pid] = state; }
            public void Dispose() { }
        }
    }
    private sealed class PriorityApi : IFramePriorityApi
    {
        internal int Writes; internal bool Deny, DenyCustomRecovery; internal Action? BeforeRead; internal readonly HashSet<int> WrittenPids = [];
        private readonly Dictionary<int, Dictionary<string, int>> _values = [];
        internal Dictionary<string, int> Values(int pid) { if (!_values.TryGetValue(pid, out var v)) _values[pid] = v = new() { ["priority"] = 32, ["ioPriority"] = 2, ["memoryPriority"] = 1 }; return v; }
        public string IdentityState(ProcmanProcessIdentity id) => "same";
        public IFramePriorityHandle Open(ProcmanProcessIdentity id) => new Handle(this, id.Pid);
        public IFramePriorityHandle OpenForRestore(ProcmanProcessIdentity id, bool customTarget)
        { if (customTarget && DenyCustomRecovery) throw new UnauthorizedAccessException("fixture custom admission unknown"); return Open(id); }
        private sealed class Handle(PriorityApi api, int pid) : IFramePriorityHandle
        {
            public int Read(string property) { var hook = api.BeforeRead; api.BeforeRead = null; hook?.Invoke(); return api.Values(pid)[property]; }
            public void Write(string property, int value) { if (api.Deny) throw new IOException("fixture denied"); api.Writes++; api.WrittenPids.Add(pid); api.Values(pid)[property] = value; }
            public void Dispose() { }
        }
    }
}
