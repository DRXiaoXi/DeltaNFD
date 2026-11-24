using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

internal static class ClockBurstChecks
{
    private static int _checks;

    internal static void Run()
    {
        CheckSampling();
        CheckSearch();
        CheckFullRange();
        CheckNativeProcessProtocol();
        CheckSavedValue();
        Console.WriteLine($"Clock burst scan: {_checks} checks passed with injected samples, processes and timer state.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _checks++;
    }

    private static void CheckSampling()
    {
        var samples = 0;
        var intervals = new List<int>();
        var fastAndSlow = new[] { 1.03, 1.55, 1.62, 1.51, 1.58 };
        var skewed = ClockBurstProbe.Sample(5000, () => 5000, () =>
        {
            var i = samples++;
            return i < 2 ? 1.0 : fastAndSlow[(i - 2) % fastAndSlow.Length];
        }, intervals.Add)!;
        Check(skewed.JudgedMedians.SequenceEqual([1.55, 1.55]) && samples == 22 && intervals.SequenceEqual(Enumerable.Repeat(1000, 5)),
            "Only two slow judged medians end the candidate, with five actual samples per round and two excluded observation rounds");
        Check(!skewed.Passes(5000), "One unusually fast sample in each round cannot pass the candidate");

        samples = 0;
        intervals.Clear();
        var full = ClockBurstProbe.Sample(5000, () => 5000, () =>
        {
            var i = samples++;
            return i < 12 ? 9 : fastAndSlow[(i - 12) % fastAndSlow.Length];
        }, intervals.Add, ClockBurstProbeMode.Full)!;
        Check(full.IsComplete(5000) && full.HasAllWaits && !full.Passes(5000) && samples == 77
            && intervals.SequenceEqual(Enumerable.Repeat(1000, 16)),
            "Full scan completes all 13 rounds for an obviously bad candidate");
        Check(full.Waits is { } recorded && recorded.Take(5).SequenceEqual(fastAndSlow) && recorded.Count(wait => wait > 1.4) == 52,
            "All unsorted waits are retained, excluding warmup and observation rounds");

        samples = 0;
        intervals.Clear();
        var screening = ClockBurstProbe.Sample(5000, () => 5000, () => { samples++; return 1.5; },
            intervals.Add, ClockBurstProbeMode.Screening)!;
        Check(screening.IsComplete(5000) && screening.HasAllWaits && !screening.Passes(5000) && samples == 77
            && intervals.SequenceEqual(Enumerable.Repeat(200, 16)),
            "Faster cadence completes every judged round and uses 3.2 seconds of scheduled waits");

        samples = 0;
        intervals.Clear();
        var pass = ClockBurstProbe.Sample(5000, () => 4999, () =>
        {
            var i = samples++;
            return i < 12 || i is >= 12 and < 17 ? 1.40004 : 1.4;
        }, intervals.Add)!;
        Check(pass.Passes(5000) && samples == 77 && intervals.SequenceEqual(Enumerable.Repeat(1000, 16)),
            "All 13 judgments, with one failure and two ignored rounds");
        Check(pass.JudgedMedians[0] == 1.40004 && pass.JudgedMedians.Skip(1).All(m => m == 1.4),
            "Threshold uses the full measured value and accepts exactly 1.4 ms");
        Check(!(pass with { After = 5010 }).Passes(5000), "Adjacent/changed readback cannot qualify");
        Check(!(pass with { JudgedMedians = [.. pass.JudgedMedians.Take(12), double.NaN] }).Passes(5000), "Reject invalid durations");
        Check(!(pass with { JudgedMedians = pass.JudgedMedians[..12] }).Passes(5000), "Reject incomplete judgment");
        int queries = 0;
        Check(ClockBurstProbe.Sample(5000, () => ++queries < 3 ? 5000u : 5010u, () => 1.0, _ => { }, ClockBurstProbeMode.Screening) is null,
            "A resolution change during sampling aborts the candidate");
    }

    private static void CheckSearch()
    {
        var sample = new Measurement
        {
            At = value => value switch
            {
                5000 => Good(value) with { After = 5010 },
                5010 => Good(value, 1.41),
                _ => Good(value),
            },
        };
        var result = new OptimalClockScanner(sample).ScanFirstPassing(default);
        Check(result.Success && result.BestLock?.Resolution100ns == 5020 && result.ScanKind == OptimalClockScanKind.FirstPassing,
            "First attributable passing candidate wins");
        Check(sample.Requests.SequenceEqual([5000u, 5010u, 5020u]) && sample.ActiveLoads == 0,
            "Visit candidates in 0.0010 ms steps and release loads");

        sample = new() { At = value => value == 6000 ? Good(value) : null };
        result = new OptimalClockScanner(sample).ScanFirstPassing(default);
        Check(result.BestLock?.Resolution100ns == 6000 && sample.Requests.Count == 101, "Inclusive full grid and 0.6000 ms endpoint");

        sample = new() { At = _ => null };
        Check(!new OptimalClockScanner(sample).ScanFirstPassing(default).Success && sample.ActiveLoads == 0,
            "No candidate yields no result and no load");
        sample = new() { CanLoad = false };
        Check(!new OptimalClockScanner(sample).ScanFirstPassing(default).Success && sample.Requests.Count == 0,
            "Never silently fall back to idle");
        sample = new() { RequestlessTest = 15.625 };
        Check(!new OptimalClockScanner(sample).ScanFirstPassing(default).Success && sample.Requests.Count == 0,
            "Self-only measurements never authorize saving");

        using var cancel = new CancellationTokenSource();
        sample = new() { At = value => { cancel.Cancel(); return Good(value); } };
        try { new OptimalClockScanner(sample).ScanFirstPassing(cancel.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException)
        {
            Check(sample.ActiveLoads == 0 && sample.Requests.Count == 1, "Cancellation releases load without probing another candidate");
        }
    }

    private static void CheckFullRange()
    {
        var counts = new Dictionary<uint, int>();
        var sample = new Measurement
        {
            FullAt = value =>
            {
                var visit = counts.GetValueOrDefault(value) + 1;
                counts[value] = visit;
                return value switch
                {
                    5000 => Good(value, 1.15) with { Waits = [.. Enumerable.Repeat(1.15, 56), .. Enumerable.Repeat(1.395, 9)] },
                    5130 => Good(value, 1.00) with { After = value + 10 },
                    5200 => Good(value, 1.01) with { Waits = [.. Enumerable.Repeat(1.01, 56), .. Enumerable.Repeat(1.39, 9)] },
                    5550 => Good(value, 1.12) with { Waits = [.. Enumerable.Repeat(1.12, 56), .. Enumerable.Repeat(1.18, 9)] },
                    5600 => Good(value, visit == 1 ? 1.01 : 1.45),
                    6000 => Good(value, 1.00) with { Waits = null },
                    _ => Good(value, 1.48),
                };
            },
        };
        var log = new List<string>();
        var result = new OptimalClockScanner(sample, (name, _) => log.Add(name))
            .Scan(new(), OptimalClockScanKind.Cpu2FullRange);
        Check(result.Success && result.ScanKind == OptimalClockScanKind.Cpu2FullRange && result.BestLock?.Resolution100ns == 5550,
            "Tail delay beats a lower median; a lucky initial winner fails confirmation");
        var grid = Enumerable.Range(0, 101).Select(i => 5000u + 10u * (uint)i);
        Check(sample.Requests.Take(101).Order().SequenceEqual(grid) && sample.Requests.Skip(101).SequenceEqual([5200u, 5550u, 5600u, 5600u, 5550u, 5200u]),
            "All 101 candidates receive initial sampling before two opposite-order confirmation passes");
        Check(sample.Modes.Take(101).All(mode => mode == ClockBurstProbeMode.Screening)
            && sample.Modes.Skip(101).SequenceEqual(Enumerable.Repeat(ClockBurstProbeMode.Full, 6))
            && sample.SamplingCpus.Count == 107 && sample.SamplingCpus.All(cpu => cpu == new ClockSamplingMask(0, 4)),
            "Initial sampling and full confirmation bind CPU 2 with their respective cadences");
        Check(result.Candidates.Count == 99 && result.Verified.Select(row => row.Resolution100ns).SequenceEqual([5550u, 5200u])
            && result.Probes == 108 && sample.ActiveLoads == 0, "Exclude clamped and incomplete batches from comparison and release all loads");
        Check(result.Message.Contains("99 档") && result.Message.Contains("0.5550") && result.Message.Contains("P95")
            && log.Count(name => name == "full-range-candidate") == 107 && log.Contains("full-range-ranking"),
            "Display coverage and finalists and log every attempted batch");
        sample = new() { FullAt = value => value == 6000 ? Good(value) : Good(value, 1.5) };
        Check(new OptimalClockScanner(sample).ScanFullRange(default).BestLock?.Resolution100ns == 6000
            && sample.Requests.Count == 103, "The endpoint can win and still requires two confirmation batches");
        sample = new() { FullAt = value => value == 5000 ? Good(value, 1.0) with
            { Waits = [.. Enumerable.Repeat(1.0, 60), .. Enumerable.Repeat(1.41, 5)] }
            : value == 5010 ? Good(value, 1.3) : Good(value, 1.5) };
        Check(new OptimalClockScanner(sample).ScanFullRange(default).BestLock?.Resolution100ns == 5010,
            "Individual slow waits take priority over lower median, even if all round medians passed");
        sample = new() { FullAt = value => Good(value, 1.5) };
        result = new OptimalClockScanner(sample).ScanFullRange(default);
        Check(!result.Success && result.HasFullRangeReport && result.Candidates.Count == 101 && result.Verified.Count == 0
            && sample.Requests.Count == 107 && sample.ActiveLoads == 0 && result.Message.Contains("相对排名"),
            "With no initial pass, retest three for ranking without authorizing a save");
        var visits = 0;
        sample = new() { FullAt = value =>
        {
            var median = ++visits <= 101 ? 1.0 : value == 5990 ? 1.55 : 1.70;
            return Good(value, median);
        } };
        result = new OptimalClockScanner(sample).ScanFullRange(default);
        Check(!result.Success && result.HasFullRangeReport && result.BestLock is null && sample.Requests.Count == 107
            && sample.ActiveLoads == 0 && result.Message.Contains("3/3 档完成两轮复测")
            && result.Message.Contains("1. 0.5990 ms") && result.Message.Contains("P95 1.550 ms"),
            "Completed failing retests are ranked by their worst score and never create a lock value");

        visits = 0;
        sample = new() { FullAt = value => ++visits <= 101 ? Good(value) : null };
        result = new OptimalClockScanner(sample).ScanFullRange(default);
        Check(!result.Success && result.HasFullRangeReport && result.BestLock is null
            && result.Message.Contains("初测排名") && !result.Message.Contains("相对排名"),
            "Missing retest evidence falls back to a distinctly labelled initial ranking");
        sample = new() { FullAt = _ => null };
        result = new OptimalClockScanner(sample).ScanFullRange(default);
        Check(!result.Success && !result.HasFullRangeReport && result.Message.Contains("没有可比较的样本"),
            "No usable measurements remains a failed scan");
        var selected = new ClockSamplingMask(1, 0x28);
        sample = new() { FullAt = value => Good(value, value == 5500 ? 1.0 : 1.5) };
        result = new OptimalClockScanner(sample).Scan(new(), OptimalClockScanKind.ManagedFullRange, selectedCpu: selected);
        var managedGrid = Enumerable.Range(0, 51).Select(i => 5000u + 10u * (uint)i);
        Check(result.Success && result.BestLock?.Resolution100ns == 5500 && result.HasFullRangeReport
            && sample.Requests.Take(51).Order().SequenceEqual(managedGrid)
            && sample.Requests.Skip(51).SequenceEqual([5500u, 5500u])
            && sample.SamplingCpus.All(cpu => cpu == selected), "Managed scan measures exactly 51 grid values and uses the same multi-processor mask in both stages");
        sample = new() { CanLoad = false };
        try { new OptimalClockScanner(sample).ScanFullRange(default); throw new Exception("Expected unavailable load"); }
        catch (InvalidOperationException)
        {
            Check(sample.Requests.Count == 0, "Unavailable load refuses unloaded comparisons");
        }
    }

    private static void CheckNativeProcessProtocol()
    {
        var holder = new Child("HELD:true:5000:4999:4999:");
        var observer = new Child(ClockBurstProbe.Prefix + JsonSerializer.Serialize(Good(5000)));
        var launches = new List<string>();
        var options = new List<string>();
        var api = new NativeOptimalClockMeasurementApi("fixture", info =>
        {
            launches.Add(info.ArgumentList[0]);
            options.AddRange(info.ArgumentList.Skip(2));
            return info.ArgumentList[0] == OptimalClockProbeHost.HoldArgument ? holder : observer;
        });
        Check(api.MeasureBurst(5000, default)?.Passes(5000) == true && launches.SequenceEqual([OptimalClockProbeHost.HoldArgument, ClockBurstProbe.Argument]),
            "Separate no-request observer samples the live holder");
        Check(holder.Disposed && observer.Disposed && !api.HasPendingCleanup, "Both children are gone before accepting the candidate");

        holder = new("HELD:true:6000:5027:6000:");
        observer = new(ClockBurstProbe.Prefix + JsonSerializer.Serialize(Good(6000)));
        launches.Clear();
        Check(api.MeasureBurst(6000, default, ClockBurstProbeMode.Full) is { HasAllWaits: true } full && full.IsComplete(6000)
            && launches.SequenceEqual([OptimalClockProbeHost.HoldArgument, ClockBurstProbe.FullRangeArgument])
            && holder.Disposed && observer.Disposed, "V2 accepts tracked readback with the mismatched set response observed on the host");
        holder = new("HELD:true:6000:5027:6000:");
        observer = new(ClockBurstProbe.Prefix + JsonSerializer.Serialize(Good(6000)));
        launches.Clear();
        Check(api.MeasureBurst(6000, default, ClockBurstProbeMode.Screening) is { HasAllWaits: true } fast && fast.IsComplete(6000)
            && launches.SequenceEqual([OptimalClockProbeHost.HoldArgument, ClockBurstProbe.ScreeningArgument])
            && holder.Disposed && observer.Disposed, "Fast screening launches the short observer and still validates mismatched set readback");

        foreach (var reply in new[] { "HELD:true:6000:6000:5990:", "HELD:true:6000:0:6000:", "HELD:false:6000:5027:6000:" })
        {
            holder = new(reply);
            launches.Clear();
            Check(api.MeasureBurst(6000, default, ClockBurstProbeMode.Full) is null && launches.Count == 1 && holder.Disposed,
                "V2 rejects untracked readback, unreadable output or a failed request: " + reply);
        }

        holder = new("HELD:true:6000:5027:6000:");
        launches.Clear();
        Check(api.MeasureBurst(6000, default) is null && launches.Count == 1 && holder.Disposed,
            "V1 keeps the legacy immediate readout check");
        holder = new("HELD:true:6000:5027:6000:");
        observer = new(null);
        Check(api.MeasureBurst(6000, default, ClockBurstProbeMode.Full) is null && holder.Disposed && observer.Disposed,
            "V2 discards a candidate when independent observation cannot complete");
        Check(OptimalClockProbeHost.TryRun([ClockBurstProbe.FullRangeArgument, "5001"], out var exitCode) && exitCode == 2,
            "V2 probe rejects an off-grid candidate before querying the native timer");

        foreach (var (cpu, mode, argument, option) in new[]
        {
            (new ClockSamplingMask(0, 1), ClockBurstProbeMode.Full, ClockBurstProbe.FullRangeArgument, ClockBurstProbe.Cpu0Option),
            (new ClockSamplingMask(0, 4), ClockBurstProbeMode.Screening, ClockBurstProbe.ScreeningArgument, ClockBurstProbe.Cpu2Option),
            (new ClockSamplingMask(0, 4), ClockBurstProbeMode.Full, ClockBurstProbe.FullRangeArgument, ClockBurstProbe.Cpu2Option),
        })
        {
            holder = new("HELD:true:6000:5027:6000:");
            observer = new(ClockBurstProbe.Prefix + JsonSerializer.Serialize(Good(6000) with { SampledAffinity = cpu }));
            launches.Clear();
            options.Clear();
            Check(api.MeasureBurst(6000, default, mode, cpu) is { HasAllWaits: true } sample && sample.SampledAffinity == cpu
                && launches.SequenceEqual([OptimalClockProbeHost.HoldArgument, argument])
                && options.SequenceEqual([option])
                && holder.Disposed && observer.Disposed, "Observer receives the explicit CPU option and proves the binding");
        }

        var groupedCpu = new ClockSamplingMask(1, 0x28);
        holder = new("HELD:true:5500:5501:5500:");
        observer = new(ClockBurstProbe.Prefix + JsonSerializer.Serialize(Good(5500) with { SampledAffinity = groupedCpu }));
        options.Clear();
        Check(api.MeasureBurst(5500, default, ClockBurstProbeMode.Screening, samplingCpu: groupedCpu)?.SampledAffinity == groupedCpu
            && options.SequenceEqual([ClockBurstProbe.CpuMaskOption, "1", "28"]),
            "The complete multi-processor mask and group are passed to the short observer");

        foreach (var unexpected in new[]
        {
            (ClockBurstProbe.AffinityErrorPrefix + "fixture affinity refused", "fixture affinity refused"),
            (ClockBurstProbe.Prefix + JsonSerializer.Serialize(Good(6000)), "采样进程未确认绑定 CPU 2，已中止扫描。"),
            (ClockBurstProbe.Prefix + JsonSerializer.Serialize(Good(6000) with { SampledAffinity = new(0, 1) }), "采样进程未确认绑定 CPU 2，已中止扫描。"),
        })
        {
            holder = new("HELD:true:6000:5027:6000:");
            observer = new(unexpected.Item1);
            try { api.MeasureBurst(6000, default, ClockBurstProbeMode.Full, samplingCpu: new(0, 4)); throw new Exception("Expected affinity failure"); }
            catch (InvalidOperationException ex) when (ex.Message == unexpected.Item2)
            {
                Check(holder.Disposed && observer.Disposed && !api.HasPendingCleanup, "Binding failure aborts and releases both children");
            }
        }

        Check(OptimalClockProbeHost.TryRun([ClockBurstProbe.ScreeningArgument, "5001", ClockBurstProbe.Cpu2Option], out exitCode) && exitCode == 2,
            "Pinned observer rejects an off-grid request before binding the calling thread");

        holder = new("HELD:true:5000:5010:5010:");
        launches.Clear();
        Check(api.MeasureBurst(5000, default) is null && launches.Count == 1 && holder.Disposed, "Skip rounded-to-another-candidate responses");

        using var cancel = new CancellationTokenSource();
        holder = new("HELD:true:5000:5000:5000:");
        observer = new(null) { OnRead = () => cancel.Cancel() };
        try { api.MeasureBurst(5000, cancel.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException)
        {
            Check(holder.Disposed && observer.Disposed && !api.HasPendingCleanup, "Cancellation of observer closes both child processes");
        }
    }

    private static void CheckSavedValue()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-clock-burst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var marker = Guid.NewGuid();
            File.WriteAllText(Path.Combine(root, "isolated-test.marker"), marker.ToString("D"));
            AppDataPaths.ConfigureIsolatedDataRoot(root, marker);
            var measurement = new Measurement();
            var holder = new Holder();
            var timerPolicy = new ScanTimerPolicy();
            var global = PendulumGlobalInspection.Evaluate(true, new(true, 1), "boot-b",
                new() { WrittenBootId = "boot-a", TargetEnabled = true, Completed = true });
            bool frame = false, game = false;
            using var engine = new ProcmanEngine(Path.Combine(root, "changes.json"), Path.Combine(root, "rules.json"),
                clockHolder: holder, clockMeasurement: measurement, clockCurrentResolution: () => holder.Current100ns,
                timerIgnoreApi: timerPolicy, timerProbe: new FrameTimerLegacyProbe(new ReachedClock()));
            engine.BackgroundLoopDisabledForTests = true;
            engine.FrameModeActiveForTests = () => frame;
            engine.ClockGameRunningForTests = () => game;
            engine.GlobalPrecisionForTests = () => global;

            var result = engine.ScanOptimalClockAsync(false, OptimalClockScanKind.FirstPassing).GetAwaiter().GetResult();
            var saved = AppSettingsStore.ReadRequired();
            Check(result.Success && saved.OptimalClockScanMode == OptimalClockScanMode.FirstPassing && saved.OptimalClockResolution100ns == 5000
                && PendulumFramePolicy.AutomaticOptimumPrepared(global, saved) && !holder.Holding, "Win11 saves 0.5000 ms with matching proof after release: " + result.Message);

            frame = true;
            Check(engine.OptimalClockOnceForTests() && !holder.Holding && engine.Snapshot().OptimalClock.UsesGameDefault
                && result.Message.Contains("沿用三角洲"), "Frame leaves the game's timer alone after a verified 0.5000 ms result");
            Check(!engine.ApplyOptimalClock(5010).Success && !holder.Holding, "An unscanned value cannot replace the saved burst target");
            frame = false;
            Check(engine.OptimalClockOnceForTests() && !holder.Holding, "Disabling frame releases the burst target");

            measurement.Requests.Clear();
            measurement.FullAt = value => Good(value, value == 5000 ? 1.0 : 1.4);
            result = engine.ScanOptimalClockAsync(false, OptimalClockScanKind.Cpu2FullRange).GetAwaiter().GetResult();
            saved = AppSettingsStore.ReadRequired();
            Check(result.Success && saved.OptimalClockScanMode == OptimalClockScanMode.FullRange && saved.OptimalClockResolution100ns == 5000
                && measurement.Requests.Count == 107 && measurement.ActiveLoads == 0 && PendulumFramePolicy.AutomaticOptimumPrepared(global, saved)
                && !holder.Holding, "Full scan saves verified 0.5000 ms after a complete sweep and release: " + result.Message);
            frame = true;
            Check(engine.OptimalClockOnceForTests() && !holder.Holding && engine.Snapshot().OptimalClock.UsesGameDefault,
                "Normal frame entry uses the game default with the full-range proof");
            AppSettingsStore.Update(s => s.OptimalClockResolution100ns = 5010);
            Check(engine.OptimalClockOnceForTests() && holder.Holding && holder.Resolution100ns == 5010,
                "The next verified grid value still starts the holder");
            AppSettingsStore.Update(s => s.OptimalClockResolution100ns = 5000);
            frame = false;
            Check(engine.OptimalClockOnceForTests() && !holder.Holding, "Frame off releases the full-range target");

            var old = JsonSerializer.Serialize(saved);
            var reportVisits = 0;
            measurement.FullAt = value => Good(value, ++reportVisits <= 101 ? 1.0 : 1.5);
            var report = engine.ScanOptimalClockAsync(false, OptimalClockScanKind.Cpu2FullRange).GetAwaiter().GetResult();
            Check(report.Success && report.Message.Contains("扫描完成") && report.Message.Contains("固定在 CPU 2") && report.Message.Contains("未保存") && report.Message.Contains("相对排名")
                && JsonSerializer.Serialize(AppSettingsStore.ReadRequired()) == old && measurement.ActiveLoads == 0 && !holder.Holding,
                "Completed Win11 comparison reaches the UI as success without overwriting previous settings or leaving a holder");

            var group = ProcmanTopology.SystemGroupMasks().First(mask => mask.Mask != 0);
            var selected = new ClockSamplingMask(group.Group, group.Mask);
            var started = DateTime.UtcNow.Ticks;
            engine.ClockTimerSurveyForTests = _ => new([
                new(42421, 5100, "candidate.exe", @"C:\Tools\candidate.exe", started, false, true),
                new(42422, 5500, "boundary.exe", @"C:\Tools\boundary.exe", started, false, true),
                new(42423, 5000, "restricted.exe", @"C:\Tools\restricted.exe", started, false, false),
            ], 5000);
            var observedWhileApplied = false;
            measurement.FullAt = value => { observedWhileApplied |= timerPolicy.State.Forced; return Good(value, 1.5); };
            report = engine.ScanOptimalClockAsync(false, OptimalClockScanKind.ManagedFullRange, selectedCpu: selected).GetAwaiter().GetResult();
            Check(report.Success && report.Message.Contains("临时忽略 1 个") && report.Message.Contains("内核请求")
                && observedWhileApplied && timerPolicy.WrittenPids.SequenceEqual([42421, 42421])
                && timerPolicy.State == new FrameTimerPowerState(0, 0)
                && JsonSerializer.Serialize(AppSettingsStore.ReadRequired()) == old,
                "Managed scan temporarily ignores the one eligible finer request, reports misses, and restores before returning");

            using (var managedCancel = new CancellationTokenSource())
            {
                timerPolicy.WrittenPids.Clear();
                measurement.FullAt = value => { managedCancel.Cancel(); return Good(value); };
                report = engine.ScanOptimalClockAsync(false, OptimalClockScanKind.ManagedFullRange, managedCancel.Token, selectedCpu: selected).GetAwaiter().GetResult();
                Check(!report.Success && timerPolicy.WrittenPids.SequenceEqual([42421, 42421]) && !timerPolicy.State.Forced
                    && JsonSerializer.Serialize(AppSettingsStore.ReadRequired()) == old,
                    "Cancelling a managed scan restores the temporary policy and preserves settings");
            }

            game = true;
            Check(!engine.ScanOptimalClockAsync(false, OptimalClockScanKind.FirstPassing).GetAwaiter().GetResult().Success
                && JsonSerializer.Serialize(AppSettingsStore.ReadRequired()) == old, "Running game rejects new scan before writes");
            game = false;
            using var cancel = new CancellationTokenSource();
            measurement.FullAt = value => { cancel.Cancel(); return Good(value); };
            Check(!engine.ScanOptimalClockAsync(false, OptimalClockScanKind.FullRange, cancel.Token).GetAwaiter().GetResult().Success
                && JsonSerializer.Serialize(AppSettingsStore.ReadRequired()) == old && measurement.ActiveLoads == 0,
                "Full scan cancellation releases its load and preserves previously saved settings");
            var visits = 0;
            measurement.FullAt = value => { if (++visits == 102) game = true; return Good(value); };
            Check(!engine.ScanOptimalClockAsync(false, OptimalClockScanKind.FullRange).GetAwaiter().GetResult().Success
                && JsonSerializer.Serialize(AppSettingsStore.ReadRequired()) == old && measurement.ActiveLoads == 0,
                "A game starting during confirmation aborts and preserves previously saved settings");

            saved.OptimalClockScanMode = OptimalClockScanMode.LowestLatency;
            Check(!PendulumFramePolicy.AutomaticPrepared(global, saved), "An old profile cannot authorize a sub-0.5100 ms target");
            saved.OptimalClockScanMode = OptimalClockScanMode.FirstPassing;
            saved.OptimalClockResolution100ns = 5001;
            Check(!PendulumFramePolicy.AutomaticPrepared(global, saved), "Off-grid proof cannot authorize a frame hold");
            Check(BackendModules.RequiresWriteIntent(BackendModules.Procman, BackendCommands.ProcmanClockBurstScan)
                && BackendModules.TimeoutFor(BackendModules.Procman, BackendCommands.ProcmanClockBurstScan) >= TimeSpan.FromMinutes(30),
                "Scan-and-save command has write intent and accommodates all 101 candidates");
            Check(BackendModules.RequiresWriteIntent(BackendModules.Procman, BackendCommands.ProcmanClockFullRangeScan)
                && BackendModules.TimeoutFor(BackendModules.Procman, BackendCommands.ProcmanClockFullRangeScan) >= TimeSpan.FromMinutes(35),
                "V2 full scan has write intent and a longer timeout");
            Check(BackendModules.RequiresWriteIntent(BackendModules.Procman, BackendCommands.ProcmanClockFastFullRangeScan)
                && BackendModules.TimeoutFor(BackendModules.Procman, BackendCommands.ProcmanClockFastFullRangeScan) >= TimeSpan.FromMinutes(10),
                "V3 fast full scan is versioned separately and still requires a write intent");
            Check(BackendModules.RequiresWriteIntent(BackendModules.Procman, BackendCommands.ProcmanClockCpu0FullRangeScan)
                && BackendModules.TimeoutFor(BackendModules.Procman, BackendCommands.ProcmanClockCpu0FullRangeScan) >= TimeSpan.FromMinutes(10),
                "V4 CPU 0 full scan requires a write intent and accepts the standard scan duration");
            Check(!BackendModules.RequiresWriteIntent(BackendModules.Procman, BackendCommands.ProcmanTimerRequestSurvey)
                && BackendModules.RequiresWriteIntent(BackendModules.Procman, BackendCommands.ProcmanClockManagedScan)
                && BackendModules.RequiresWriteIntent(BackendModules.Procman, BackendCommands.ProcmanClockManagedMaskScan),
                "Reading timer requests is read-only and scoped automatic suppression requires a write intent");
            Check(!engine.ScanOptimalClockAsync(false, OptimalClockScanKind.ManagedFullRange, selectedCpu: new(ushort.MaxValue, 1)).GetAwaiter().GetResult().Success
                && !engine.ScanOptimalClockAsync(false, OptimalClockScanKind.ManagedFullRange, selectedCpu: new(group.Group, 0)).GetAwaiter().GetResult().Success,
                "Unavailable groups and empty masks are rejected before detecting or applying timer policy");
            Check(BackendModules.RequiresWriteIntent(BackendModules.Procman, BackendCommands.ProcmanClockCpu2FullRangeScan)
                && BackendModules.TimeoutFor(BackendModules.Procman, BackendCommands.ProcmanClockCpu2FullRangeScan) >= TimeSpan.FromMinutes(10),
                "V5 CPU 2 command requires a write intent and accepts the standard scan duration");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static ClockBurstMeasurement Good(uint value, double median = 1.05)
        => new(value, value, Enumerable.Repeat(median, ClockBurstProbe.JudgedRounds).ToArray(),
            Enumerable.Repeat(median, ClockBurstProbe.JudgedRounds * ClockBurstProbe.SamplesPerRound).ToArray());

    private sealed class Measurement : IOptimalClockMeasurementApi
    {
        public Func<uint, ClockBurstMeasurement?> At = value => Good(value);
        public Func<uint, ClockBurstMeasurement?> FullAt = value => Good(value);
        public bool CanLoad = true;
        public double RequestlessTest = 2;
        public int ActiveLoads;
        public List<uint> Requests { get; } = [];
        public List<ClockBurstProbeMode> Modes { get; } = [];
        public List<ClockSamplingMask?> SamplingCpus { get; } = [];
        public double? MeasureRequestlessMs(int samples) => 1.04;
        public OptimalClockSample? MeasureSelf(uint value, int samples, int spin) => null;
        public OptimalClockSample MeasureGlobal(uint value, int samples, int spin) => new()
        {
            Requested100ns = value, Granted100ns = value, Current100ns = value,
            ObservedBefore100ns = value, ObservedAfter100ns = value,
            Samples = samples, Median = RequestlessTest, Mean = RequestlessTest, P95 = RequestlessTest,
            Min = RequestlessTest, Max = RequestlessTest, Over130Pct = 0,
        };
        public ClockBurstMeasurement? MeasureBurst(uint value, CancellationToken token,
            ClockBurstProbeMode mode = ClockBurstProbeMode.FirstPassing, ClockSamplingMask? samplingCpu = null)
        {
            if (ActiveLoads != 1) throw new InvalidOperationException("Expected one load per candidate");
            Requests.Add(value);
            Modes.Add(mode);
            SamplingCpus.Add(samplingCpu);
            return mode == ClockBurstProbeMode.FirstPassing ? At(value) : FullAt(value);
        }
        public IOptimalClockLoad? StartBurstLoad()
        {
            if (!CanLoad) return null;
            ActiveLoads++;
            return new Load(this);
        }
        private sealed class Load(Measurement owner) : IOptimalClockLoad
        {
            public int Threads => 2;
            public void Dispose() => owner.ActiveLoads--;
        }
    }

    private sealed class ReachedClock : IFrameTimerReachabilityApi
    {
        public double? MeasureRequestlessGranularityMs() => 1.0;
    }

    private sealed class ScanTimerPolicy : IFrameTimerIgnoreApi
    {
        internal FrameTimerPowerState State = new(0, 0);
        internal List<int> WrittenPids { get; } = [];
        public string IdentityState(ProcmanProcessIdentity identity) => "same";
        public IFrameTimerIgnoreHandle Open(ProcmanProcessIdentity identity) => new Handle(this, identity.Pid);

        private sealed class Handle(ScanTimerPolicy owner, int pid) : IFrameTimerIgnoreHandle
        {
            public FrameTimerPowerState Read() => owner.State;
            public void Write(FrameTimerPowerState state)
            {
                owner.WrittenPids.Add(pid);
                owner.State = state;
            }
            public void Dispose() { }
        }
    }

    private sealed class Holder : IOptimalClockHolderApi
    {
        public bool Holding { get; private set; }
        public uint Resolution100ns { get; private set; }
        public uint Granted100ns => Resolution100ns;
        public uint Current100ns => Holding ? Resolution100ns : 156250;
        public string Error => "";
        public bool Ensure(uint value) { Holding = true; Resolution100ns = value; return true; }
        public void Stop() => Holding = false;
    }

    private sealed class Child(string? output) : IOptimalClockChildProcess
    {
        public bool HasExited { get; private set; }
        public bool Disposed { get; private set; }
        public Action? OnRead;
        public void CloseInput() => HasExited = true;
        public bool WaitForExit(int milliseconds) => HasExited;
        public void Kill() => HasExited = true;
        public string? ReadReadyLine() => output;
        public string? ReadReadyLine(CancellationToken token)
        {
            OnRead?.Invoke();
            token.ThrowIfCancellationRequested();
            return output;
        }
        public void Dispose() => Disposed = true;
    }
}
