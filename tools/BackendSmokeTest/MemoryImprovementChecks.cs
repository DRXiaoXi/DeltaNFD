using System.Reflection;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

internal static class MemoryImprovementChecks
{
    private static int _count;
    private static void Check(bool ok, string name) { if (!ok) throw new IOException(name); _count++; Console.WriteLine("PASS: " + name); }
    private static async Task Within(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(8));
    private sealed class Fixture : IDisposable
    {
        internal readonly ProcmanEngine Engine;
        internal DateTime Now = DateTime.UtcNow;
        internal GameTarget Target = new(false, "", "DeltaForceClient-Win64-Shipping", "三角洲", 1);
        internal ProcmanProcessIdentity[] Ids = [];
        internal readonly List<int> Writes = [];
        internal int Enumerations, SizeReads, PressureReads, TelemetryReads;
        internal int Foreground = 90000;
        internal uint? Pressure = 95;
        internal readonly object Gate;
        internal Fixture(string root, string mode = ProcmanMemoryModes.Normal)
        {
            AppSettingsStore.Update(s => { s.CustomGameModeEnabled = false; s.CustomGameExecutablePath = ""; });
            Engine = new(Path.Combine(root, Guid.NewGuid().ToString("N") + ".json"))
            {
                BackgroundLoopDisabledForTests = true, FrameModeActiveForTests = () => true, FrameMemorySelectedForTests = () => true,
                ClockForTests = () => Now, ForegroundForTests = () => Foreground, MemoryPolicyForTests = () => new([]),
                FrameTargetForTests = () => Target, FrameCanonicalPathForTests = Path.GetFullPath,
                MemoryPressureForTests = () => { PressureReads++; return Pressure; },
                MemoryTelemetryForTests = () => { TelemetryReads++; return new(8UL << 30, 1UL << 30, 88); },
                MemoryIdentitiesForTests = () => { Enumerations++; return Ids; },
                MemoryReadForTests = _ => { SizeReads++; return 1024; },
                MemorySystemProcessForTests = _ => false, BackgroundExcludedForTests = _ => false,
                MemoryPressureWindowForTests = TimeSpan.Zero,
                MemoryGlobalTrimForTests = _ => throw new Exception("Unexpected native/global path")
            };
            Engine.MemoryTrimForTests = (id, guard) => { if (!guard()) return new(id.Pid, "fake", "skipped", "guard"); Writes.Add(id.Pid); return new(id.Pid, "fake", "completed", "simulated"); };
            Gate = typeof(ProcmanEngine).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Engine)!;
            Check(Engine.SaveBackgroundConfig(new() { MemoryMode = mode }).Success, "fixture config");
        }
        internal void Custom(string path)
        {
            Target = new(true, path, Path.GetFileNameWithoutExtension(path), "selected", Target.Generation + 1);
            AppSettingsStore.Update(s => { s.CustomGameModeEnabled = true; s.CustomGameExecutablePath = path; });
        }
        internal ProcmanProcessIdentity Id(int pid, string name = "ordinary.exe") => new(pid, Now, @"C:\Apps\" + name);
        internal async Task<ProcmanMemoryTrimArgs> Confirm()
        { var preview = await Engine.PreviewMemoryAsync(); return new() { PreviewId = preview.Id, Processes = preview.Candidates.Select(c => c.Identity).ToList() }; }
        public void Dispose() => Engine.Dispose();
    }
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-memory-improvements-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var token = Guid.NewGuid(); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), token.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, token);
        foreach (var mode in new[] { ProcmanMemoryModes.Normal, ProcmanMemoryModes.DeltaStable })
        {
            using var f = new Fixture(root, mode);
            var selected = f.Id(20001, "Selected.exe"); f.Custom(selected.ExecutablePath);
            f.Ids = [selected, selected with { Pid = 20002 }, selected with { Pid = 20003, ExecutablePath = @"D:\Other\Selected.exe" }, f.Id(20004, "Child.exe"), f.Id(20005, "DeltaForceClient-Win64-Shipping.exe")];
            f.Foreground = selected.Pid;
            var args = await f.Confirm();
            Check(args.Processes.Select(p => p.Pid).Order().SequenceEqual(new[] { 20003, 20004 }), mode + ": selected path/all instances protected, same-name other path and child remain independent");
            var report = f.Engine.TrimMemory(args);
            Check(f.Writes.Order().SequenceEqual(new[] { 20003, 20004 }), mode + ": final dispatch protects selected and legacy game");
            f.Now = f.Now.AddMinutes(11); f.Writes.Clear(); f.Enumerations = f.SizeReads = f.TelemetryReads = 0;
            f.Engine.AutomaticMemoryOnceForTests();
            Check(f.Writes.Order().SequenceEqual(new[] { 20003, 20004 }), mode + ": automatic shares game protection");
            Check(f.Enumerations == 1 && f.SizeReads == 2 && f.TelemetryReads == 2, mode + ": one enumeration, one size per candidate, before/after full telemetry only");
            f.Now = f.Now.AddMinutes(11); args = await f.Confirm();
            f.Target = f.Target with { Generation = f.Target.Generation + 1 };
            Check(f.Engine.TrimMemory(args).Items.Count == 0, mode + ": changed target generation invalidates preview");
            f.Engine.FrameCanonicalPathForTests = _ => throw new IOException("unresolved");
            Check((await f.Confirm()).Processes.Count == 0, mode + ": unknown canonical target fails closed");
        }
        using (var f = new Fixture(root, ProcmanMemoryModes.DeltaStable))
        {
            f.Ids = [f.Id(20010), f.Id(20011)]; var args = await f.Confirm();
            var calls = 0;
            f.Engine.MemoryTrimForTests = (id, guard) =>
            {
                calls++; f.Custom(id.ExecutablePath);
                Check(!guard(), "target/settings changed after native entry: final guard rejects");
                return new(id.Pid, "fake", "skipped", "changed");
            };
            f.Engine.TrimMemory(args); Check(calls == 1, "target change stops remainder of old batch");
        }
        foreach (var automatic in new[] { false, true })
        foreach (var mode in new[] { ProcmanMemoryModes.Normal, ProcmanMemoryModes.DeltaStable, ProcmanMemoryModes.Aggressive })
        {
            using var f = new Fixture(root, mode); f.Ids = [f.Id(21001), f.Id(21002)];
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var calls = 0;
            ProcmanMemoryTrimItem Native(int pid, Func<bool> guard)
            {
                calls++; Check(!Monitor.IsEntered(f.Gate), mode + ": native outside engine gate");
                entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("fixture release");
                Check(!guard(), mode + ": stop rejects remaining native guard");
                return new(pid, "fake", "skipped", "stopped");
            }
            f.Engine.MemoryTrimForTests = (id, guard) => Native(id.Pid, guard);
            f.Engine.MemoryGlobalTrimForTests = guard => new([Native(0, guard)], "fake");
            var args = await f.Confirm();
            var run = automatic ? f.Engine.AutomaticMemoryOnceAsyncForTests() : Task.Run(() => f.Engine.TrimMemory(args));
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(8)), mode + ": native entered");
                await Within(Task.Run(() => f.Engine.BackgroundStatus()));
                Check(true, mode + ": status stays responsive during native call");
                var another = await f.Confirm();
                Check(f.Engine.TrimMemory(another).Items.Count == 0 && calls == 1, mode + ": overlapping manual request rejected");
                var stop = f.Engine.StopRulesEngineAsync();
                Check(!stop.IsCompleted && !f.Engine.BackgroundStatus().AutomaticMemoryActive, mode + ": stop cancels promptly but waits for active call");
                Check(!f.Engine.StartRulesEngine().Success, mode + ": restart cannot race pending stop");
                Check(!f.Engine.SaveBackgroundConfig(new()).Success, mode + ": config save cannot reactivate pending stop");
                release.Set(); await Within(run); await Within(stop);
                Check(calls == 1, mode + ": stop prevents subsequent target/global step");
            }
            finally { release.Set(); await Within(run); }
        }
        using (var f = new Fixture(root))
        {
            f.Ids = [f.Id(22501)]; var args = await f.Confirm();
            f.Engine.MemoryIdentitiesForTests = () => throw new IOException("enumeration unavailable");
            try { f.Engine.TrimMemory(args); Check(false, "manual enumeration must fail"); }
            catch (IOException) { Check(true, "manual exception remains a failure"); }
            f.Engine.MemoryIdentitiesForTests = () => f.Ids;
            Check(f.Engine.TrimMemory(await f.Confirm()).Items.Single().Status == "completed", "manual failure releases execution slot");
            f.Now = f.Now.AddMinutes(11);
            f.Engine.MemoryIdentitiesForTests = () => throw new IOException("worker unavailable");
            f.Engine.AutomaticMemoryOnceForTests();
            Check(f.Engine.BackgroundStatus().Feedback.Contains("worker unavailable"), "worker exception reported without restoring unrelated frame features");
            f.Engine.MemoryIdentitiesForTests = () => f.Ids;
            f.Now = f.Now.AddMinutes(11); f.Engine.AutomaticMemoryOnceForTests();
            Check(f.Writes.Count == 2, "worker failure releases slot for a later round");
        }
        using (var f = new Fixture(root))
        {
            f.Ids = [f.Id(22601)]; var args = await f.Confirm();
            using var cancel = new CancellationTokenSource();
            f.Engine.MemoryTrimForTests = (id, guard) => { cancel.Cancel(); Check(!guard(), "caller cancellation reaches final native guard"); return new(id.Pid, "fake", "skipped", "cancelled"); };
            Check(f.Engine.TrimMemory(args, cancel.Token).Items.Single().Status == "skipped", "cancelled manual request has no fake success");
        }
        using (var f = new Fixture(root, ProcmanMemoryModes.DeltaStable))
        {
            var game = f.Id(22701, "Game.exe"); f.Custom(game.ExecutablePath);
            f.Ids = [game, game with { Pid = 22702, ExecutablePath = @"D:\Alias\game.exe" }, f.Id(22703)];
            f.Engine.FrameCanonicalPathForTests = p => p.StartsWith(@"D:\Alias", StringComparison.OrdinalIgnoreCase) ? game.ExecutablePath.ToUpperInvariant() : Path.GetFullPath(p);
            Check((await f.Confirm()).Processes.Select(p => p.Pid).SequenceEqual(new[] { 22703 }), "canonical alias and case variant protect same selected game");
            AppSettingsStore.Update(s => s.CustomGameExecutablePath = @"D:\New\Other.exe");
            Check((await f.Confirm()).Processes.Count == 0, "settings/backend target disagreement blocks admission");
        }
        using (var f = new Fixture(root))
        {
            f.Engine.MemoryPressureWindowForTests = TimeSpan.FromSeconds(30);
            f.Engine.AutomaticMemoryOnceForTests();
            for (var i = 0; i < 3; i++) { f.Now = f.Now.AddSeconds(30); f.Engine.AutomaticMemoryOnceForTests(); }
            Check(f.Enumerations == 1 && f.PressureReads == 4, "empty candidates keep 30s pressure checks but one scan per configured interval");
            f.Now = f.Now.AddSeconds(540); f.Engine.AutomaticMemoryOnceForTests();
            Check(f.Enumerations == 2, "empty scan retries at exact 600s interval without extra backoff");
            f.Ids = [f.Id(23000)]; f.Engine.SaveBackgroundConfig(new() { MemoryCooldownSeconds = 60 });
            f.Now = f.Now.AddSeconds(30); f.Engine.AutomaticMemoryOnceForTests();
            f.Now = f.Now.AddSeconds(30); f.Engine.AutomaticMemoryOnceForTests();
            Check(f.Writes.Count == 1, "saved config resets candidate scheduling and preserves pressure confirmation");
        }
        using (var f = new Fixture(root, ProcmanMemoryModes.DeltaStable))
        {
            f.Ids = [f.Id(24001), f.Id(24002)]; var calls = 0; var args = await f.Confirm();
            f.Engine.MemoryTrimForTests = (id, guard) =>
            {
                calls++;
                f.Engine.SaveBackgroundConfig(new() { MemoryMode = ProcmanMemoryModes.Normal });
                f.Engine.SaveBackgroundConfig(new() { MemoryMode = ProcmanMemoryModes.DeltaStable });
                Check(!guard(), "switch away and back cannot revive old work");
                return new(id.Pid, "fake", "skipped", "revision");
            };
            f.Engine.TrimMemory(args); Check(calls == 1, "revision invalidates remaining batch");
        }
        using (var f = new Fixture(root))
        {
            f.Ids = [f.Id(25001)]; var args = await f.Confirm();
            var properties = (ProcessPropertyGate)typeof(ProcmanEngine).GetField("_properties", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Engine)!;
            Task? competitor = null;
            f.Engine.MemoryTrimForTests = (id, guard) =>
            {
                using var gateHeld = new ManualResetEventSlim();
                competitor = Task.Run(() => { lock (f.Gate) { gateHeld.Set(); using var lease = properties.Enter(id, "priority"); } });
                if (!gateHeld.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("competitor gate");
                Check(guard(), "final guard never waits for engine gate while holding property mutex");
                return new(id.Pid, "fake", "completed", "simulated");
            };
            await Within(Task.Run(() => f.Engine.TrimMemory(args))); await Within(competitor!);
            Check(true, "opposite ownership ordering completes without deadlock");
        }
        using (var f = new Fixture(root))
        {
            // Actual production loop: frame checks must continue while the memory worker is blocked.
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var observed = new ManualResetEventSlim(); var nativeActive = 0;
            f.Ids = [f.Id(26001)];
            f.Engine.FrameModeActiveForTests = () => { if (Volatile.Read(ref nativeActive) == 1) observed.Set(); return true; };
            f.Engine.MemoryTrimForTests = (id, guard) => { Volatile.Write(ref nativeActive, 1); entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException(); return new(id.Pid, "fake", "skipped", "simulated"); };
            f.Engine.BackgroundLoopDisabledForTests = false;
            f.Engine.StartRulesEngine();
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(5)), "real rules loop dispatched memory worker");
                Check(observed.Wait(TimeSpan.FromSeconds(5)), "real frame supervision continues while memory native call is blocked");
            }
            finally { release.Set(); await Within(f.Engine.StopRulesEngineAsync()); }
        }
        Console.WriteLine($"Memory improvements: {_count} checks passed. All process/global writes simulated; no real memory cleanup.");
    }
}
