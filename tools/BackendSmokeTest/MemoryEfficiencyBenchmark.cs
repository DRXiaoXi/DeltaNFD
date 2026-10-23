using System.Diagnostics;
using System.Text.Json;
using System.Numerics;
using System.Runtime.InteropServices;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

internal static class MemoryEfficiencyBenchmark
{
    private sealed record Round(int Limit, int Repeat, int Candidates, int Completed, double PreviewMs, double TrimMs,
        double ControllerCpuMs, long WorkingSetBefore, long WorkingSetImmediate, long WorkingSetAfter5s,
        long PrivateBytesBefore, long PrivateBytesAfter5s, long WorkingSetAfterRetouch,
        double WarmTouchMeanMs, double RetouchMeanMs, ulong? SystemAvailableBefore, ulong? SystemAvailableAfter,
        double? ConcurrentSystemCpuPercent);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    internal static async Task RunAsync(bool allLevels = false)
    {
        var childCount = allLevels ? 10 : 5;
        var logicalProcessors = Math.Max(1, ProcmanTopology.SystemGroupMasks().Sum(g => BitOperations.PopCount(g.Mask)));
        var processorName = ProcmanCpuTopologyReader.Read().ProcessorName;
        const long allocated = 320L * 1024 * 1024;
        var telemetry = ProcmanMemoryTelemetryReader.Read();
        if (telemetry is null || telemetry.AvailablePhysicalBytes < (ulong)(childCount * allocated + (2L << 30)))
            throw new InvalidOperationException($"可用物理内存不足以在保留 2GiB 余量的情况下创建 {childCount} 个测试进程，未开始分配。");
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-memory-efficiency-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var images = Path.Combine(root, "fixture-image"); Directory.CreateDirectory(images);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(images, Path.GetFileName(file)));
        var executable = Path.Combine(images, "BackendSmokeTest.exe");
        var children = new List<Process>();
        var rounds = new List<Round>();
        long WorkingSet(IEnumerable<Process> selected) => selected.Sum(p => { p.Refresh(); return p.WorkingSet64; });
        long PrivateBytes(IEnumerable<Process> selected) => selected.Sum(p => { p.Refresh(); return p.PrivateMemorySize64; });
        async Task<double> Touch(Process child)
        {
            var watch = Stopwatch.StartNew();
            await child.StandardInput.WriteLineAsync("touch");
            if (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) != "ok") throw new Exception("fixture touch not acknowledged");
            return watch.Elapsed.TotalMilliseconds;
        }
        using var controller = Process.GetCurrentProcess();
        try
        {
            for (var index = 0; index < childCount; index++)
            {
                var child = Process.Start(new ProcessStartInfo(executable, "--procman-soak-child --allocate-memory")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })!;
                children.Add(child);
                if (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) != "ready") throw new Exception("fixture allocation not ready");
            }
            var identities = children.Select(p => ProcmanIdentity.TryIdentify(p.Id) ?? throw new Exception("fixture identity unknown")).ToArray();
            var iteration = 0;
            // 全档测试先预热 10 进程批次（不计入结果），再用固定种子的乱序跑每档三次。
            var order = new[] { 3, 5, 5, 3, 3, 5 }.ToList();
            if (allLevels)
            {
                var random = new Random(20261013);
                order = Enumerable.Range(0, 3).SelectMany(_ => Enumerable.Range(1, 10).OrderBy(_ => random.Next())).ToList();
                order.Insert(0, 10);
            }
            foreach (var limit in order)
            {
                var touch = new Dictionary<int, double>();
                foreach (var child in children) touch[child.Id] = await Touch(child);
                await Task.Delay(250);
                using var engine = new ProcmanEngine(Path.Combine(root, "round-" + iteration++ + ".json"))
                {
                    BackgroundLoopDisabledForTests = true,
                    FrameModeActiveForTests = () => false,
                    ForegroundForTests = () => Environment.ProcessId,
                    // 唯一候选分类注入是严格限定到自建进程；CPU采样、身份、工作集、锁和回收均走实际代码。
                    BackgroundExcludedForTests = candidate => !identities.Contains(candidate),
                    MemoryPolicyForTests = () => new([]),
                };
                engine.SaveBackgroundConfig(new() { MemoryBatchLimit = limit });
                var previewWatch = Stopwatch.StartNew();
                var preview = await engine.PreviewMemoryAsync();
                var previewMs = previewWatch.Elapsed.TotalMilliseconds;
                if (preview.Candidates.Count != limit) throw new Exception($"expected {limit} owned candidates, got {preview.Candidates.Count}: {preview.Message}");
                var selected = preview.Candidates.Select(c => children.Single(p => p.Id == c.Identity.Pid)).ToArray();
                var privateBefore = PrivateBytes(selected);
                var warmMean = selected.Average(p => touch[p.Id]);
                controller.Refresh(); var cpuBefore = controller.TotalProcessorTime;
                var systemBeforeKnown = GetSystemTimes(out var idleBefore, out var kernelBefore, out var userBefore);
                var trimWatch = Stopwatch.StartNew();
                var report = engine.TrimMemory(new() { PreviewId = preview.Id, Processes = preview.Candidates.Select(c => c.Identity).ToList() });
                trimWatch.Stop();
                var trimMs = trimWatch.Elapsed.TotalMilliseconds;
                controller.Refresh(); var controllerCpu = (controller.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                double? systemPercent = null;
                if (GetSystemTimes(out var idleAfter, out var kernelAfter, out var userAfter) && systemBeforeKnown
                    && idleAfter >= idleBefore && kernelAfter >= kernelBefore && userAfter >= userBefore)
                {
                    var total = kernelAfter - kernelBefore + userAfter - userBefore;
                    if (total > 0) systemPercent = Math.Clamp(100d * (total - Math.Min(total, idleAfter - idleBefore)) / total, 0, 100);
                }
                var before = report.Items.Sum(i => i.BeforeBytes ?? 0);
                var immediate = report.Items.Sum(i => i.AfterBytes ?? 0);
                if (report.Items.Count != limit || report.Items.Any(i => i.Status != "completed"))
                    throw new Exception("batch did not complete: " + JsonSerializer.Serialize(report));
                await Task.Delay(5000);
                var afterIdle = WorkingSet(selected);
                var privateAfter = PrivateBytes(selected);
                var afterAvailable = ProcmanMemoryTelemetryReader.Read()?.AvailablePhysicalBytes;
                var retouch = new List<double>();
                foreach (var child in selected) retouch.Add(await Touch(child));
                var afterTouch = WorkingSet(selected);
                if (allLevels && iteration == 1)
                {
                    Console.WriteLine($"WARMUP excluded: {limit}/{limit}, trim={trimMs:0.0}ms");
                    continue;
                }
                var repeat = rounds.Count(r => r.Limit == limit) + 1;
                rounds.Add(new(limit, repeat, preview.Candidates.Count, report.Items.Count(i => i.Status == "completed"),
                    previewMs, trimMs, controllerCpu, before, immediate, afterIdle, privateBefore, privateAfter, afterTouch,
                    warmMean, retouch.Average(), report.Before?.AvailablePhysicalBytes, afterAvailable, systemPercent));
                Console.WriteLine($"limit={limit} repeat={repeat}: {report.Items.Count}/{limit} completed, preview={previewMs:0.0}ms, trim={trimMs:0.0}ms, controller CPU={controllerCpu:0.0}ms ({100 * controllerCpu / trimMs / logicalProcessors:0.00}% whole-machine equivalent); WS={before / 1048576d:0.0}->{immediate / 1048576d:0.0}->{afterIdle / 1048576d:0.0}MiB (5s), retouch={retouch.Average():0.0}ms/process, WS retouched={afterTouch / 1048576d:0.0}MiB");
            }
            var destination = Path.GetFullPath(Path.Combine("_buildcheck", allLevels ? "memory-efficiency-levels-1-10-20261013.json" : "memory-efficiency-20261013.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, JsonSerializer.Serialize(new
            {
                ReportedUtc = DateTimeOffset.UtcNow, LogicalProcessors = logicalProcessors, ProcessorName = processorName,
                CpuDefinition = "Recycler process CPU time / execution wall time; whole-machine equivalent divides by active logical processor count. Not an incremental system-wide CPU attribution. ConcurrentSystemCpuPercent includes all applications. Short windows have accounting granularity error.",
                FixtureCount = childCount, BytesAllocatedPerFixture = allocated, Engine = "production memory path; own-fixture eligibility filter only",
                Caveats = "Synthetic idle working sets, real API; aggregate working set decrease is not physical memory freed; system available memory changes are concurrent and not attributable. Fresh isolated engine per round; no user process or settings changed.",
                Rounds = rounds,
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var group in rounds.GroupBy(r => r.Limit).OrderBy(g => g.Key))
            {
                double Median(IEnumerable<double> values) => values.Order().ElementAt(group.Count() / 2);
                Console.WriteLine($"SUMMARY {group.Key}: trim mean={group.Average(r => r.TrimMs):0.0}ms median={Median(group.Select(r => r.TrimMs)):0.0}ms, CPU time mean={group.Average(r => r.ControllerCpuMs):0.0}ms, CPU equivalent={100 * group.Sum(r => r.ControllerCpuMs) / group.Sum(r => r.TrimMs) / logicalProcessors:0.00}% whole-machine / {100 * group.Sum(r => r.ControllerCpuMs) / group.Sum(r => r.TrimMs):0.0}% single-logical-core, WS decrease at5s mean={group.Average(r => (r.WorkingSetBefore - r.WorkingSetAfter5s) / 1048576d):0.0}MiB");
            }
            Console.WriteLine("Report: " + destination);
        }
        finally
        {
            foreach (var child in children)
            {
                try { if (!child.HasExited) { child.StandardInput.WriteLine("quit"); if (!child.WaitForExit(3000)) child.Kill(); } }
                finally { child.Dispose(); }
            }
        }
    }
}
