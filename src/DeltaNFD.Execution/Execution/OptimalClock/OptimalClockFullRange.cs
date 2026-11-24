using System.Diagnostics;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.OptimalClock;

internal sealed partial class OptimalClockScanner
{
    private const int FinalistCount = 3;
    private const int ConfirmationPasses = 2;

    private sealed record BurstScore(OptimalClockCandidate Candidate, int SlowSamples, int SlowRounds)
    {
        internal bool Passes => SlowRounds <= 1;
    }

    internal OptimalClockScanResult ScanFullRange(CancellationToken token, Action? checkEnvironment = null,
        IProgress<string>? progress = null, bool fastInitialSweep = false, OptimalClockScanKind kind = OptimalClockScanKind.FullRange,
        ClockSamplingMask? selectedCpu = null)
    {
        var watch = Stopwatch.StartNew();
        var last = kind == OptimalClockScanKind.ManagedFullRange ? PendulumFramePolicy.ManagedScanLast100ns : PendulumFramePolicy.BurstScanLast100ns;
        var count = (int)((last - PendulumFramePolicy.BurstScanFirst100ns) / PendulumFramePolicy.BurstScanStep100ns) + 1;
        token.ThrowIfCancellationRequested();
        checkEnvironment?.Invoke();
        var baseline = api.MeasureRequestlessMs(21);
        var probes = 0;
        var mode = DetectMode(baseline, ref probes, token);
        if (mode != OptimalClockMode.Global)
            return OptimalClockScanResult.Failed("全范围扫描需要在无请求的独立进程中确认计时器生效。请先完成兼容性检测。");

        var loadThreads = 0;
        ClockSamplingMask? samplingCpu = kind switch
        {
            OptimalClockScanKind.Cpu0FullRange => new(0, 1),
            OptimalClockScanKind.Cpu2FullRange => new(0, 4),
            OptimalClockScanKind.ManagedFullRange => selectedCpu ?? throw new InvalidOperationException("请选择可用的采样 CPU。"),
            _ => null,
        };
        var initialMode = fastInitialSweep || samplingCpu is not null ? ClockBurstProbeMode.Screening : ClockBurstProbeMode.Full;
        var measured = new List<BurstScore>(count);
        for (var position = 0; position < count; position++)
        {
            // Alternate the ends so temperature and background load do not drift in resolution order.
            var offset = position % 2 == 0 ? position / 2 : count - 1 - position / 2;
            var target = PendulumFramePolicy.BurstScanFirst100ns + (uint)offset * PendulumFramePolicy.BurstScanStep100ns;
            var score = Measure(target, $"正在初测 {target / 10000d:0.0000} ms（第 {position + 1}/{count} 档）", 0, initialMode);
            if (score is not null) measured.Add(score);
        }

        var preliminary = Rank(measured).ToArray();
        var finalists = preliminary.Where(score => score.Passes).Take(FinalistCount).ToArray();
        if (finalists.Length == 0) finalists = preliminary.Take(FinalistCount).ToArray();
        var confirmations = finalists.ToDictionary(score => score.Candidate.Resolution100ns,
            score => new List<BurstScore> { score });
        for (var pass = 1; pass <= ConfirmationPasses; pass++)
        {
            var order = pass % 2 == 1 ? finalists.Reverse() : finalists;
            foreach (var finalist in order)
            {
                var target = finalist.Candidate.Resolution100ns;
                var score = Measure(target, $"正在复测 {target / 10000d:0.0000} ms（第 {pass}/{ConfirmationPasses} 轮）", pass, ClockBurstProbeMode.Full);
                if (score is not null) confirmations[target].Add(score);
            }
        }

        var compared = Rank(confirmations.Values.Where(batches => batches.Count == ConfirmationPasses + 1)
            .Select(batches => new BurstScore(batches[0].Candidate with
            {
                Median = batches.Max(score => score.Candidate.Median),
                P95 = batches.Max(score => score.Candidate.P95),
                Spread = batches.Max(score => score.Candidate.Spread),
                Over130Pct = batches.Max(score => score.Candidate.Over130Pct),
                RunMedians = batches.SelectMany(score => score.Candidate.RunMedians).ToArray(),
                Runs = batches.Sum(score => score.Candidate.Runs),
                Mean = batches.Average(score => score.Candidate.Mean),
            }, batches.Max(score => score.SlowSamples), batches.Max(score => score.SlowRounds)))).ToArray();
        // The aggregate records the worst batch, so it passes exactly when every batch passed.
        var verified = compared.Where(score => score.Passes).ToArray();
        var best = verified.FirstOrDefault();
        var coverage = $"共尝试 {count} 档，{measured.Count} 档取得完整初测样本。";
        if (measured.Count > 0)
        {
            coverage = "扫描完成。" + coverage;
            if (samplingCpu is { } cpu) coverage += $"采样线程固定在 {cpu.Display}。";
        }
        string message;
        if (best is not null)
            message = coverage + $"本次推荐 {best.Candidate.Milliseconds:0.0000} ms。达标排名（各项取三批最差值）\n" + FormatRanking(verified);
        else if (compared.Length > 0)
            message = coverage + $"{compared.Length}/{finalists.Length} 档完成两轮复测。自动保存要求每批最多 1 轮超标，本次未保存。相对排名（各项取三批最差值）\n"
                + FormatRanking(compared);
        else if (preliminary.Length > 0)
            message = coverage + "两轮复测的完整样本不足，未保存。初测排名\n" + FormatRanking(preliminary.Take(FinalistCount));
        else
            message = coverage + "没有可比较的样本，未保存新值。";
        PendulumDiagnosticLog.Emit(diagnostics, "full-range-ranking", new { count, preliminary, compared, verified });
        return new OptimalClockScanResult
        {
            Success = best is not null,
            Message = message,
            Mode = mode,
            ScanKind = kind,
            RequestlessBaselineMs = baseline,
            Candidates = preliminary.Select(score => score.Candidate).ToArray(),
            Verified = verified.Select(score => score.Candidate).ToArray(),
            BestLock = best?.Candidate,
            Probes = probes,
            LoadThreads = loadThreads,
            ElapsedSeconds = watch.Elapsed.TotalSeconds,
        };

        BurstScore? Measure(uint target, string status, int pass, ClockBurstProbeMode probeMode)
        {
            token.ThrowIfCancellationRequested();
            checkEnvironment?.Invoke();
            progress?.Report(status);
            using var load = api.StartBurstLoad();
            if (load is null || !load.EnsureActive())
                throw new InvalidOperationException("无法启动全范围扫描的突发 CPU 负载。");
            loadThreads = load.Threads;
            var measurement = api.MeasureBurst(target, token, probeMode, samplingCpu);
            probes++;
            token.ThrowIfCancellationRequested();
            checkEnvironment?.Invoke();
            if (!load.IsActive)
                throw new InvalidOperationException("采样期间突发 CPU 负载已退出。");

            var score = measurement is { HasAllWaits: true, Waits: { } waits } && measurement.IsComplete(target)
                ? Score(target, waits, measurement.JudgedMedians) : null;
            PendulumDiagnosticLog.Emit(diagnostics, "full-range-candidate", new { target, pass, probeMode = probeMode.ToString(), samplingCpu, measurement, score });
            return score;
        }
    }

    private static string FormatRanking(IEnumerable<BurstScore> scores)
        => string.Join("\n", scores.Select((score, index) =>
            $"{index + 1}. {score.Candidate.Milliseconds:0.0000} ms · 超过 1.4 ms {score.SlowSamples}/{ClockBurstProbe.JudgedRounds * ClockBurstProbe.SamplesPerRound} 次"
            + $" · 超标轮数 {score.SlowRounds}/{ClockBurstProbe.JudgedRounds} · P95 {score.Candidate.P95:0.000} ms"));

    private static IOrderedEnumerable<BurstScore> Rank(IEnumerable<BurstScore> scores)
        => scores.OrderBy(score => score.SlowSamples)
            .ThenBy(score => score.Candidate.P95)
            .ThenBy(score => score.Candidate.Median)
            .ThenByDescending(score => score.Candidate.Resolution100ns);

    private static BurstScore Score(uint target, double[] waits, double[] medians)
    {
        var ordered = waits.Order().ToArray();
        var candidate = new OptimalClockCandidate
        {
            Resolution100ns = target,
            Median = ordered[ordered.Length / 2],
            Mean = ordered.Average(),
            P95 = ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1],
            Spread = ordered[^1] - ordered[0],
            Over130Pct = 100.0 * ordered.Count(wait => wait >= 1.3) / ordered.Length,
            Runs = medians.Length,
            Samples = ClockBurstProbe.SamplesPerRound,
            RunMedians = medians,
        };
        return new BurstScore(candidate, ordered.Count(ClockBurstProbe.OverLimit), medians.Count(ClockBurstProbe.OverLimit));
    }
}
