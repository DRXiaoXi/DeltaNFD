using System.Diagnostics;
using System.Xml.Linq;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

internal static class ProcmanMemoryModeChecks
{
    internal static async Task RunAsync()
    {
        var id = Guid.NewGuid(); var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-memory-modes-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var now = DateTime.UtcNow; var count = 0; var writes = new List<int>(); var globals = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        var ordinary = new ProcmanProcessIdentity(10001, now, @"C:\Apps\ordinary.exe");
        var foreground = ordinary with { Pid = 10002, ExecutablePath = @"C:\Apps\foreground.exe" };
        var game = ordinary with { Pid = 10003, ExecutablePath = @"C:\Games\DeltaForceClient-Win64-Shipping.exe" };
        var system = ordinary with { Pid = 10004, ExecutablePath = @"C:\Windows\System32\services.exe" };
        var controlled = ordinary with { Pid = 10005, ExecutablePath = @"C:\Apps\controlled.exe" };
        var excluded = ordinary with { Pid = 10006, ExecutablePath = @"C:\Apps\excluded.exe" };
        var tool = ordinary with { Pid = 10007, ExecutablePath = @"C:\Apps\DeltaNFD.exe" };
        var identities = new[] { ordinary, foreground, game, system, controlled, excluded, tool };
        using var engine = new ProcmanEngine(Path.Combine(root, "changes.json"))
        {
            BackgroundLoopDisabledForTests = true, FrameModeActiveForTests = () => true,
            FrameMemorySelectedForTests = () => true, FrameSuppressionSelectedForTests = () => false,
            ForegroundForTests = () => foreground.Pid, ClockForTests = () => now,
            MemoryIdentitiesForTests = () => identities,
            SampleForTests = () => throw new Exception("CPU sampler must never be called by memory modes"),
            MemoryReadForTests = _ => 1024,
            ResponsivenessExcludedForTests = i => i == system,
            MemorySystemProcessForTests = i => i == system,
            MemoryPolicyForTests = () => new([]),
            MemoryTrimForTests = (i, guard) =>
            {
                if (!guard()) return new(i.Pid, "fixture", "skipped", "guard stopped");
                writes.Add(i.Pid); return new(i.Pid, "fixture", "completed", "no shrink", 1024, 1024);
            },
            MemoryGlobalTrimForTests = guard =>
            {
                if (!guard()) return new([], "global guard stopped");
                globals++; return new([new(0, "global fixture", "completed", "simulated global call")], "simulated");
            },
        };
        engine.SaveRule(new() { ExecutablePath = controlled.ExecutablePath, Capability = "priority", Value = "Normal" });
        Check(engine.SaveBackgroundConfig(new() { MemoryMode = ProcmanMemoryModes.Normal, MemoryBatchLimit = 1,
            MemoryCooldownSeconds = 60, ExcludedExecutablePaths = [excluded.ExecutablePath] }).Success, "save normal mode");
        var preview = await engine.PreviewMemoryAsync();
        Check(preview.Mode == "normal" && preview.Candidates.Count == 1 && preview.Candidates[0].Identity.Pid == ordinary.Pid,
            "normal keeps previous exclusions/count and admits 1KiB working set without CPU sampling");
        Check(engine.TrimMemory(new() { PreviewId = preview.Id, Processes = preview.Candidates.Select(c => c.Identity).ToList() }).Items.Count == 1,
            "normal small working set actual dispatch");
        now = now.AddMinutes(2); preview = await engine.PreviewMemoryAsync();
        engine.SaveBackgroundConfig(engine.BackgroundStatus().Config with { MemoryMode = ProcmanMemoryModes.DeltaStable });
        Check(engine.TrimMemory(new() { PreviewId = preview.Id, Processes = preview.Candidates.Select(c => c.Identity).ToList() }).Items.Count == 0,
            "changing mode invalidates old preview before writing");
        preview = await engine.PreviewMemoryAsync();
        Check(preview.Mode == "delta-stable" && preview.Candidates.Count == 4, "stable removes count limit and protects game/system/rule identities");
        Check(preview.Candidates.All(c => c.Identity.Pid != game.Pid && c.Identity.Pid != system.Pid)
            && preview.Candidates.Any(c => c.Identity.Pid == foreground.Pid) && preview.Candidates.All(c => c.Identity.Pid != controlled.Pid)
            && preview.Candidates.Any(c => c.Identity.Pid == excluded.Pid) && preview.Candidates.Any(c => c.Identity.Pid == tool.Pid),
            "stable includes foreground/custom exclusion/tool but excludes saved-rule processes");
        writes.Clear();
        var result = engine.TrimMemory(new() { PreviewId = preview.Id, Processes = preview.Candidates.Select(c => c.Identity).ToList() });
        Check(result.Items.Count == 4 && writes.Count == 4 && !writes.Contains(game.Pid) && !writes.Contains(system.Pid)
            && !writes.Contains(controlled.Pid), "stable dispatch never trims a rule-controlled process");
        Check(engine.ListRules().Rules.Any(r => r.ExecutablePath == controlled.ExecutablePath), "stable reclaim does not delete user rules");
        now = now.AddMinutes(2); preview = await engine.PreviewMemoryAsync();
        engine.SaveRule(new() { ExecutablePath = foreground.ExecutablePath.ToUpperInvariant(), Capability = "priority", Value = "Normal", Enabled = false });
        writes.Clear();
        result = engine.TrimMemory(new() { PreviewId = preview.Id, Processes = preview.Candidates.Select(c => c.Identity).ToList() });
        Check(!writes.Contains(foreground.Pid) && result.Items.Any(i => i.Pid == foreground.Pid && i.Status == "skipped"),
            "rule added after preview is rechecked before writing, including disabled rules and case-insensitive paths");
        now = now.AddMinutes(2); preview = await engine.PreviewMemoryAsync();
        Check(preview.Candidates.All(c => c.Identity.Pid != foreground.Pid && c.Identity.Pid != controlled.Pid),
            "disabled saved rules are also excluded from stable preview");
        engine.MemoryPressureForTests = () => 95; engine.MemoryPressureWindowForTests = TimeSpan.Zero;
        writes.Clear(); engine.AutomaticMemoryOnceForTests();
        Check(writes.Count > 0 && !writes.Contains(controlled.Pid) && !writes.Contains(foreground.Pid),
            "automatic stable cleanup protects enabled and disabled rules using the same eligibility gate");
        engine.SaveBackgroundConfig(engine.BackgroundStatus().Config with { MemoryMode = ProcmanMemoryModes.Aggressive });
        engine.MemoryIdentitiesForTests = () => throw new Exception("global mode must not enumerate processes");
        engine.MemoryReadForTests = _ => throw new Exception("global mode must not read process working sets");
        preview = await engine.PreviewMemoryAsync();
        Check(preview.Mode == "aggressive" && preview.Candidates.Count == 0, "aggressive preview has no process statistics");
        Check(engine.TrimMemory(new() { PreviewId = preview.Id, Processes = [new() { Pid = ordinary.Pid, StartTimeUtcTicks = ordinary.StartTimeUtcTicks, ExecutablePath = ordinary.ExecutablePath }] }).Items.Count == 0,
            "global mode rejects arbitrary process list");
        Check(engine.TrimMemory(new() { PreviewId = preview.Id, Processes = [] }).Items.Count == 1 && globals == 1,
            "global mode empty confirmed list dispatches directly");
        Check(engine.TrimMemory(new() { PreviewId = preview.Id, Processes = [] }).Items.Count == 0 && globals == 1,
            "global preview single-use");
        preview = await engine.PreviewMemoryAsync();
        Check(engine.TrimMemory(new() { PreviewId = preview.Id, Processes = [] }).Items.Count == 0 && globals == 1,
            "global mode respects user interval");
        now = now.AddMinutes(2); engine.MemoryPressureForTests = () => 95;
        engine.MemoryPressureWindowForTests = TimeSpan.Zero;
        engine.AutomaticMemoryOnceForTests();
        Check(globals == 2, "automatic aggressive dispatch never enumerates CPU/processes");
        now = now.AddSeconds(60); engine.AutomaticMemoryOnceForTests();
        Check(globals == 3, "aggressive repeat uses configured interval with no effectiveness backoff");
        engine.FrameModeActiveForTests = () => false; now = now.AddMinutes(2); engine.AutomaticMemoryOnceForTests();
        Check(globals == 3, "automatic global mode stops with frame master");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        preview = await engine.PreviewMemoryAsync();
        Check(engine.TrimMemory(new() { PreviewId = preview.Id }, cancel.Token).Items.Count == 0 && globals == 3, "cancel blocks global call");
        Check(ProcmanGlobalMemoryReclaimer.Trim(() => false).Items.Count == 0, "native global entry guard false never adjusts privileges or calls system purge");
        var commands = new List<int>();
        var nativePlan = ProcmanGlobalMemoryReclaimer.ExecuteCommands(() => true, command => { commands.Add(command); return 0; });
        Check(commands.SequenceEqual(new[] { 2, 4 }) && nativePlan.All(i => i.Status == "completed"), "native global command dispatch uses system working sets then standby list without process enumeration");
        commands.Clear();
        var nativeFailed = ProcmanGlobalMemoryReclaimer.ExecuteCommands(() => true, command => { commands.Add(command); return unchecked((int)0xC0000061); });
        Check(commands.Count == 2 && nativeFailed.All(i => i.Status == "failed" && i.Message.Contains("C0000061")), "native NTSTATUS privilege failure not reported as success");
        var keepGoing = true; commands.Clear();
        var nativeCancel = ProcmanGlobalMemoryReclaimer.ExecuteCommands(() => keepGoing, command => { commands.Add(command); keepGoing = false; return 0; });
        Check(commands.SequenceEqual(new[] { 2 }) && nativeCancel.Last().Status == "skipped", "cancel/disable before second global command prevents standby purge");
        try { engine.SaveBackgroundConfig(new() { MemoryMode = "invalid" }); Check(false, "invalid mode accepted"); }
        catch (ArgumentException) { Check(engine.BackgroundStatus().Config.MemoryMode == "aggressive", "invalid mode retains existing configuration"); }
        using (var reload = new ProcmanEngine(Path.Combine(root, "changes.json")))
            Check(reload.BackgroundStatus().Config.MemoryMode == "aggressive", "mode persists across engine restart");

        var views = Path.Combine(Directory.GetCurrentDirectory(), "src", "DeltaNFD", "Views");
        var xaml = XDocument.Load(Path.Combine(views, "ProcessPage.xaml")); XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var combo = xaml.Descendants().Single(n => (string?)n.Attribute(x + "Name") == "MemoryModeCombo");
        Check(combo.Elements().Select(n => (string?)n.Attribute("Content")).SequenceEqual(new[] { "普通回收模式", "激进回收模式", "稳洲回收模式" }), "all three mode choices visible");
        var source = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src/DeltaNFD.Execution/Execution/Procman/ProcmanMemory.cs"));
        Check(!source.Contains("_sampler.Sample()") && !source.Contains("MinimumTrimWorkingSet"), "production memory path has no CPU sampler or old size threshold");
        var background = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src/DeltaNFD.Execution/Execution/Procman/ProcmanBackground.cs"));
        Check(!background.Contains("_memoryBackoffUntil") && !background.Contains("_memoryReboundUntil"), "removed effectiveness/rebound backoff implementation");
        Console.WriteLine($"Memory modes: {count} checks passed; all global/all-process effects simulated, native global tested only with false guard. No real global cleanup.");
    }
}
