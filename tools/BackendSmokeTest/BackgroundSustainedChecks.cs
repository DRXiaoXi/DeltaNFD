using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

internal static class BackgroundSustainedChecks
{
    internal static void RunChild(bool allocate)
    {
        var bytes = allocate ? new byte[320 * 1024 * 1024] : new byte[1];
        void Touch() { for (var i = 0; i < bytes.Length; i += 4096) bytes[i]++; }
        Touch();
        Console.WriteLine("ready");
        for (var command = Console.ReadLine(); command is not null && command != "quit"; command = Console.ReadLine())
        { if (command == "touch") Touch(); Console.WriteLine("ok"); }
        GC.KeepAlive(bytes);
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root;
        private readonly string _exe;
        private readonly List<Process> _children = [];
        internal Fixture(string name)
        {
            var id = Guid.NewGuid(); Root = Path.Combine(Path.GetTempPath(), "DeltaNFD-" + name + "-" + id.ToString("N"));
            Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, "isolated-test.marker"), id.ToString("D"));
            AppDataPaths.ConfigureIsolatedDataRoot(Root, id);
            var images = Path.Combine(Root, "fixture-image"); Directory.CreateDirectory(images);
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(images, Path.GetFileName(file)));
            _exe = Path.Combine(images, "BackendSmokeTest.exe");
        }
        internal async Task<Process> Child(bool allocate = false)
        {
            var child = Process.Start(new ProcessStartInfo(_exe, "--procman-soak-child" + (allocate ? " --allocate-memory" : ""))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })!;
            _children.Add(child); child.PriorityClass = ProcessPriorityClass.Normal;
            if (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) != "ready") throw new Exception("fixture not ready");
            return child;
        }
        internal ProcmanEngine Engine(string name) => new(Path.Combine(Root, name + ".json"))
        { BackgroundLoopDisabledForTests = true, FrameModeActiveForTests = () => true,
            FrameSuppressionSelectedForTests = () => true, FrameMemorySelectedForTests = () => true,
            ForegroundForTests = () => Environment.ProcessId, MemoryPolicyForTests = () => new([]) };
        public void Dispose()
        {
            foreach (var child in _children)
            { try { if (!child.HasExited) { child.StandardInput.WriteLine("quit"); if (!child.WaitForExit(3000)) child.Kill(); } } finally { child.Dispose(); } }
        }
    }
    internal static async Task SuppressionAsync()
    {
        using var fixture = new Fixture("suppression-sustained");
        var child = await fixture.Child();
        var identity = ProcmanIdentity.TryIdentify(child.Id)!;
        ProcessPriorityClass Priority() { child.Refresh(); return child.PriorityClass; }
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        var options = new ProcmanSuppressionOptions();
        Check(options is { CpuBusyThreshold: 65, CpuBusySeconds: 3, CpuIdleThreshold: 50, CpuIdleSeconds: 6,
            CooldownSeconds: 15, MaxAdjustPerRound: 1, CandidateCpuFloorPercent: 0 }, "new defaults");
        foreach (var (threads, expected) in new[] { (4,5), (8,5), (12,4), (16,3), (24,2), (32,2), (48,1), (64,1), (128,1) })
            Check(ProcmanEngine.AdaptiveCandidateThreshold(threads) == expected, "adaptive threshold: " + threads);
        var old = JsonSerializer.Serialize(new ProcmanBackgroundConfig());
        using (var document = JsonDocument.Parse(old))
        {
            var legacy = JsonSerializer.Serialize(new { SchemaVersion = 1, MemoryPressurePercent = 88, ExcludedExecutablePaths = new[] { @"C:\Apps\excluded.exe" },
                Suppression = new { CpuBusyThreshold = 80, CpuBusySeconds = 6, CpuIdleThreshold = 60, CpuIdleSeconds = 8,
                    CooldownSeconds = 10, CandidateCpuFloorPercent = 10, MaxAdjustPerRound = 2 } });
            var legacyPath = Path.Combine(fixture.Root, "legacy.json"); File.WriteAllText(legacyPath, legacy);
            var loaded = new ProcmanBackgroundStore(legacyPath).Load();
            Check(loaded.Suppression == options && loaded.MemoryPressurePercent == 88 && loaded.ExcludedExecutablePaths.Count == 1, "legacy exact-default migration preserves memory/exclusions");
            File.WriteAllText(legacyPath, legacy.Replace("\"CpuBusyThreshold\":80", "\"CpuBusyThreshold\":79"));
            Check(new ProcmanBackgroundStore(legacyPath).Load().Suppression.CpuBusyThreshold == 79, "custom legacy values retained");
            new ProcmanBackgroundStore(legacyPath).Save(new() { Suppression = options with { CpuBusyThreshold = 80, CpuBusySeconds = 6,
                CpuIdleThreshold = 60, CpuIdleSeconds = 8, CooldownSeconds = 10, CandidateCpuFloorPercent = 10, MaxAdjustPerRound = 2 } });
            Check(new ProcmanBackgroundStore(legacyPath).Load().Suppression.CpuBusyThreshold == 80, "versioned intentional old values retained");
        }
        using var engine = fixture.Engine("suppression");
        var now = DateTime.UtcNow; double? total = 64; double candidate = 6;
        var foreground = Environment.ProcessId; var frame = true;
        engine.FrameModeActiveForTests = () => frame; engine.ForegroundForTests = () => foreground;
        engine.ClockForTests = () => now; engine.LogicalProcessorCountForTests = () => 8;
        engine.SampleForTests = () => new(total, [(identity, candidate)]);
        engine.ResponsivenessExcludedForTests = current => current != identity;
        Check(engine.SaveBackgroundConfig(new()).Success, "save defaults with simulated 12100F topology");
        Check(engine.Snapshot().ResponsivenessConfig!.CandidateCpuFloorPercent == 5, "8-thread effective candidate threshold");
        for (var second = 0; second < 15; second++) { await engine.ResponsivenessOnceForTests(); now = now.AddSeconds(1); }
        Check(Priority() == ProcessPriorityClass.Normal, "below trigger cannot demote");
        total = 90;
        await engine.ResponsivenessOnceForTests(); now = now.AddSeconds(2); await engine.ResponsivenessOnceForTests();
        Check(Priority() == ProcessPriorityClass.Normal, "short spike below 3 seconds ignored");
        now = now.AddSeconds(1); await engine.ResponsivenessOnceForTests();
        Check(Priority() == ProcessPriorityClass.BelowNormal, "3 seconds sustained pressure writes real child priority");
        foreground = child.Id; now = now.AddSeconds(1); await engine.ResponsivenessOnceForTests();
        Check(Priority() == ProcessPriorityClass.Normal, "foreground transition restores despite high CPU");
        foreground = Environment.ProcessId;
        total = 20;
        for (var second = 0; second < 40; second++) { now = now.AddSeconds(1); await engine.ResponsivenessOnceForTests(); }
        total = 90; now = now.AddSeconds(1); await engine.ResponsivenessOnceForTests();
        now = now.AddSeconds(3); await engine.ResponsivenessOnceForTests();
        Check(Priority() == ProcessPriorityClass.BelowNormal, "empty low-load restoration must not extend cooldown forever");
        total = 20; now = now.AddSeconds(1); await engine.ResponsivenessOnceForTests(); now = now.AddSeconds(6); await engine.ResponsivenessOnceForTests();
        for (var cycle = 0; cycle < 400; cycle++)
        {
            total = 90; now = now.AddSeconds(16); await engine.ResponsivenessOnceForTests();
            now = now.AddSeconds(3); await engine.ResponsivenessOnceForTests();
            Check(Priority() == ProcessPriorityClass.BelowNormal, "cycle demoted: " + cycle);
            total = 49; now = now.AddSeconds(1); await engine.ResponsivenessOnceForTests();
            now = now.AddSeconds(6); await engine.ResponsivenessOnceForTests();
            Check(Priority() == ProcessPriorityClass.Normal && engine.Snapshot().HeldChangeCount == 0, "cycle restored without held records: " + cycle);
            Check(engine.ResponsivenessEvents().Count <= 200, "event ring remains bounded: " + cycle);
        }
        total = 90; now = now.AddSeconds(16); await engine.ResponsivenessOnceForTests(); now = now.AddSeconds(3); await engine.ResponsivenessOnceForTests();
        frame = false; total = null; await engine.ResponsivenessOnceForTests();
        Check(Priority() == ProcessPriorityClass.Normal && engine.Snapshot().HeldChangeCount == 0, "stop restores even when sampling unavailable");
        Console.WriteLine($"Suppression sustained: {checks} checks; 400 real child demote/restore cycles over accelerated time; no user process touched.");
        Console.WriteLine("Isolated evidence: " + fixture.Root);
    }
    internal static async Task MemoryAsync()
    {
        using var fixture = new Fixture("memory-sustained");
        using var engine = fixture.Engine("memory");
        var origin = DateTime.UtcNow; var now = origin; uint? pressure = 90; var frame = true;
        var targets = Enumerable.Range(1, 8).Select(i => new ProcmanProcessIdentity(10000 + i, origin, $@"C:\Fixture\target{i}.exe")).ToArray();
        var writes = new List<(DateTime Time, int Pid)>();
        engine.ClockForTests = () => now; engine.FrameModeActiveForTests = () => frame;
        engine.MemoryPressureForTests = () => pressure;
        engine.SampleForTests = () => new(20, targets.Select(i => (i, .1)).ToList());
        engine.ResponsivenessExcludedForTests = _ => false;
        var sizes = targets.ToDictionary(i => i.Pid, _ => 600L * 1024 * 1024);
        engine.MemoryReadForTests = i => sizes.GetValueOrDefault(i.Pid);
        engine.MemoryTrimForTests = (i, guard) =>
        {
            if (!guard()) return new(i.Pid, "fixture", "skipped", "eligibility changed");
            var before = sizes[i.Pid]; sizes[i.Pid] = before / 2; writes.Add((now, i.Pid));
            return new(i.Pid, "fixture", "completed", "simulated", before, before / 2);
        };
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        Check(engine.SaveBackgroundConfig(new()).Success, "memory defaults saved");
        engine.AutomaticMemoryOnceForTests();
        Check(writes.Count == 0, "30 second sustained window");
        now = now.AddSeconds(29); engine.AutomaticMemoryOnceForTests();
        Check(writes.Count == 0, "cannot poll faster than 30 seconds");
        now = origin.AddSeconds(30); engine.AutomaticMemoryOnceForTests();
        Check(writes.Count == 5, "first round capped at default five");
        for (var second = 60; second <= 24 * 3600; second += 30)
        {
            now = origin.AddSeconds(second);
            // 冷数据不立即回涨，仍能在下轮重新积累工作集。
            if (second % 600 == 0) foreach (var target in targets) sizes[target.Pid] = 600L * 1024 * 1024;
            engine.AutomaticMemoryOnceForTests();
            Check(engine.Snapshot().LastError.Length == 0, "24h tick error-free: " + second);
        }
        var rounds = writes.GroupBy(w => w.Time).OrderBy(g => g.Key).ToList();
        Check(rounds.Count > 60 && rounds.All(g => g.Count() <= 5), "many independent rounds remain batch bounded at five");
        for (var i = 1; i < rounds.Count; i++) Check(rounds[i].Key - rounds[i - 1].Key >= TimeSpan.FromMinutes(10), "round cooldown maintained");
        foreach (var target in writes.GroupBy(w => w.Pid))
        {
            var times = target.Select(w => w.Time).ToList();
            for (var i = 1; i < times.Count; i++) Check(times[i] - times[i - 1] >= TimeSpan.FromMinutes(10), "identity cooldown maintained");
        }
        var beforeStop = writes.Count; frame = false; now = now.AddMinutes(40); engine.AutomaticMemoryOnceForTests();
        Check(writes.Count == beforeStop, "stopped frame cannot trim later");
        frame = true; pressure = null; now = now.AddMinutes(40); engine.AutomaticMemoryOnceForTests();
        Check(writes.Count == beforeStop, "unavailable pressure cannot trim");
        pressure = 90; now = now.AddSeconds(30); engine.AutomaticMemoryOnceForTests();
        pressure = 40; now = now.AddSeconds(30); engine.AutomaticMemoryOnceForTests();
        Check(writes.Count == beforeStop, "pressure falling resets pending sustained window");
        Console.WriteLine($"Memory sustained: {checks} checks; accelerated 24h, {rounds.Count} rounds, {writes.Count} simulated trims; no real system pressure created.");
        Console.WriteLine("Isolated evidence: " + fixture.Root);
        await Task.CompletedTask;
    }
    internal static async Task NativeSoakAsync()
    {
        using var fixture = new Fixture("background-native-soak");
        var priorityChild = await fixture.Child(); var memoryChild = await fixture.Child(allocate: true);
        var priorityIdentity = ProcmanIdentity.TryIdentify(priorityChild.Id)!; var memoryIdentity = ProcmanIdentity.TryIdentify(memoryChild.Id)!;
        using var suppression = fixture.Engine("priority-soak"); using var memory = fixture.Engine("memory-soak");
        double total = 90;
        suppression.ResponsivenessExcludedForTests = i => i != priorityIdentity;
        suppression.SampleForTests = () => new(total, [(priorityIdentity, 20)]);
        suppression.SaveBackgroundConfig(new());
        memory.FrameSuppressionSelectedForTests = () => false;
        memory.MemoryPressureForTests = () => 90; // 不制造真实全机内存高压。
        memory.ResponsivenessExcludedForTests = i => i != memoryIdentity;
        memory.SampleForTests = () => new(20, [(memoryIdentity, .1)]);
        memory.SaveBackgroundConfig(new());
        var beforeBytes = memoryChild.WorkingSet64;
        var stopwatch = Stopwatch.StartNew(); var lastStage = -1; var demoted = 0; var restored = 0; var prior = ProcessPriorityClass.Normal;
        var retouched = false;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(65))
        {
            var stage = (int)(stopwatch.Elapsed.TotalSeconds / 10);
            // 6 秒繁忙 + 20 秒低负载，覆盖 3 秒触发、6 秒恢复、15 秒恢复后冷却。
            total = stopwatch.Elapsed.TotalSeconds % 26 < 6 ? 90 : 20;
            await suppression.ResponsivenessOnceForTests(); memory.AutomaticMemoryOnceForTests();
            if (!retouched && stopwatch.Elapsed.TotalSeconds >= 40)
            {
                memoryChild.StandardInput.WriteLine("touch");
                if (await memoryChild.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "ok") throw new Exception("memory fixture retouch failed");
                retouched = true;
            }
            priorityChild.Refresh(); var current = priorityChild.PriorityClass;
            if (current != prior) { if (current == ProcessPriorityClass.BelowNormal) demoted++; else if (current == ProcessPriorityClass.Normal) restored++; prior = current; }
            if (stage != lastStage) { Console.WriteLine($"Native soak {stopwatch.Elapsed.TotalSeconds:0}s: priority={current}, memory-round={(memory.BackgroundStatus().LastReport is null ? "pending" : "completed")}"); lastStage = stage; }
            await Task.Delay(1000);
        }
        suppression.FrameModeActiveForTests = () => false; await suppression.ResponsivenessOnceForTests();
        memory.FrameModeActiveForTests = () => false; memory.AutomaticMemoryOnceForTests();
        priorityChild.Refresh(); memoryChild.Refresh();
        if (demoted < 3 || restored < 3 || priorityChild.PriorityClass != ProcessPriorityClass.Normal || suppression.Snapshot().HeldChangeCount != 0)
            throw new Exception("native sustained priority cycles/restoration failed");
        var report = memory.BackgroundStatus().LastReport;
        if (report is null || report.Items.Count != 1 || report.Items[0].Pid != memoryChild.Id || report.Items[0].Status != "completed")
            throw new Exception("native automatic memory trim not verified: " + JsonSerializer.Serialize(report));
        if (report.Items[0].BeforeBytes is not { } before || report.Items[0].AfterBytes is not { } after || after >= before * .95
            || !memory.BackgroundStatus().Feedback.Contains("不因无明显效果或快速回涨额外延长"))
            throw new Exception("native working-set reduction/no-additional-backoff not verified");
        Console.WriteLine($"Native 65s soak passed: {demoted} demotions/{restored} low-load restorations; final Normal; own memory child working set {beforeBytes / 1048576} -> {memoryChild.WorkingSet64 / 1048576} MiB.");
        Console.WriteLine(JsonSerializer.Serialize(report));
        Console.WriteLine("Simulated CPU/pressure signals; real priority API and working-set API only on two owned children. Evidence: " + fixture.Root);
    }
}
