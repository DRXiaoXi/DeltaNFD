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
            FrameMemorySelectedForTests = () => true,
            ForegroundForTests = () => Environment.ProcessId, MemoryPolicyForTests = () => new([]) };
        public void Dispose()
        {
            foreach (var child in _children)
            { try { if (!child.HasExited) { child.StandardInput.WriteLine("quit"); if (!child.WaitForExit(3000)) child.Kill(); } } finally { child.Dispose(); } }
        }
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
        engine.BackgroundExcludedForTests = _ => false;
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
}
