using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FramePacingResearch;

internal sealed record Stats(int Count, double MeanMs, double P50Ms, double P95Ms, double P99Ms,
    double StandardDeviationMs, double MinMs, double MaxMs, double OnePercentLowFps, int LongFrames, double LongFrameThresholdMs)
{
    public static Stats? From(IEnumerable<double> input, double target, bool includeZero = false)
    {
        var data = input.Where(v => double.IsFinite(v) && (includeZero ? v >= 0 : v > 0)).Order().ToArray();
        if (data.Length == 0) return null;
        double mean = data.Average();
        double percentile(double p) {
            var position = (data.Length - 1) * p; var low = (int)position;
            return data[low] + (data[Math.Min(low + 1, data.Length - 1)] - data[low]) * (position - low);
        }
        var worstCount = Math.Max(1, (int)Math.Ceiling(data.Length * .01));
        // Explicit convention: reciprocal of mean duration of worst 1%; not 1000/P99.
        var worstMean = data.Skip(data.Length - worstCount).Average();
        var lowFps = worstMean > 0 ? 1000.0 / worstMean : 0;
        var threshold = Math.Max(2 * 1000.0 / target, 20.0);
        return new(data.Length, mean, percentile(.5), percentile(.95), percentile(.99),
            Math.Sqrt(data.Average(v => Math.Pow(v - mean, 2))), data[0], data[^1], lowFps, data.Count(v => v > threshold), threshold);
    }
}
internal sealed record FrameRow(int Pid, string Swapchain, string Kind, double? SubmitMs, double? DisplayMs,
    double? SubmitInterval, double? DisplayInterval, double? RenderWork, double? Wait, double? Late,
    double? InputLatency, bool? Displayed, bool Complete, bool Occluded, IReadOnlyDictionary<string, double> InputMetrics,
    double? RenderLate, double? WaitOvershoot, double? QueueWait, double? PresentCall);
internal sealed record AnalysisReport
{
    public int SchemaVersion { get; init; } = 1;
    public required string Source { get; init; }
    public required string MetricBasis { get; init; }
    public required string CsvSha256 { get; init; }
    public int? Pid { get; init; }
    public required string Swapchain { get; init; }
    public double TargetDisplayedFps { get; init; }
    public int Rows { get; init; }
    public int NativeFrames { get; init; }
    public int GeneratedFrames { get; init; }
    public int UnknownFrames { get; init; }
    public int RepeatedFrames { get; init; }
    public int TimingAnomalies { get; init; }
    public int FailedRows { get; init; }
    public int OccludedRows { get; init; }
    public double? ObservedNativeFps { get; init; }
    public double? ObservedDisplayedFps { get; init; }
    public double? ObservedDisplayEventFps { get; init; }
    public Stats? NativeIntervals { get; init; }
    public Stats? DisplayIntervals { get; init; }
    public Stats? CpuSubmissionIntervals { get; init; }
    public Stats? RenderWork { get; init; }
    public Stats? PacingWait { get; init; }
    public Stats? DeadlineLateness { get; init; }
    public Stats? RenderLateness { get; init; }
    public Stats? WaitOvershoot { get; init; }
    public Stats? QueueWait { get; init; }
    public Stats? PresentCall { get; init; }
    public Stats? InputLatency { get; init; }
    public IReadOnlyDictionary<string, Stats> InputLatencyMetrics { get; init; } = new Dictionary<string, Stats>();
    public required string Conclusion { get; init; }
    public required string[] Warnings { get; init; }
}

internal static class Analysis
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    internal static string Kind(string text) => text.Trim().ToLowerInvariant() switch
    {
        "application" or "app" or "native" or "synthetic_native" => "native",
        "intel xess-fg" or "amd afmf" or "synthetic_generated" => "generated",
        "repeated" => "repeated",
        _ => "unknown", // No numeric assumptions; unknown/vendor-new enum values must stay unknown.
    };

    public static AnalysisReport Analyze(string path, double target, int? requestedPid = null, string? requestedSwapchain = null)
    {
        if (target is < 30 or > 1000 || !double.IsFinite(target)) throw new ArgumentException("Target FPS must be 30..1000.");
        using var iterator = Csv.Read(path).GetEnumerator();
        if (!iterator.MoveNext()) throw new InvalidDataException("CSV is empty.");
        var header = iterator.Current;
        var columns = new Columns(header);
        bool lab = columns.Has("PresentSubmitMs") && columns.Has("WaitStatus");
        bool presentmon = columns.Has("ProcessID") && columns.Has("SwapChainAddress");
        if (!lab && !presentmon) throw new InvalidDataException("Unsupported CSV schema; no inferred FPS from arbitrary columns.");
        if (presentmon && !columns.Has("FrameType"))
            throw new InvalidDataException("PresentMon CSV requires --track_frame_type; missing frame types cannot be treated as native frames.");
        var rows = new List<FrameRow>();
        int malformed = 0;
        while (iterator.MoveNext())
        {
            var row = iterator.Current;
            if (row.Length == 1 && row[0].Length == 0) continue;
            if (row.Length != header.Length) { malformed++; continue; }
            var pidValue = columns.Number(row, "ProcessID");
            int pid = lab ? 0 : pidValue is >= 1 and <= int.MaxValue && pidValue == Math.Floor(pidValue.Value) ? (int)pidValue : -1;
            if (pid < 0) { malformed++; continue; }
            if (requestedPid is { } pidFilter && pid != pidFilter) continue;
            string chain = lab ? "lab" : columns.Text(row, "SwapChainAddress");
            if (chain.Length == 0) { malformed++; continue; }
            if (requestedSwapchain is not null && !chain.Equals(requestedSwapchain, StringComparison.OrdinalIgnoreCase)) continue;
            var kind = columns.Text(row, lab ? "FrameKind" : "FrameType");
            // CPUStartQPC is raw counter ticks: conversion is forbidden without a declared QPC frequency.
            var submit = lab ? columns.Number(row, "PresentSubmitMs")
                : columns.Number(row, "CPUStartQPCTime", "CPUStartQPCTimeInMs", "CPUStartTime")
                    ?? (columns.Number(row, "TimeInSeconds", "CPUStartTimeInSeconds") is { } sec ? sec * 1000 : null);
            double? display = null;
            if (!lab && submit is { } start && columns.Number(row, "DisplayLatency", "msUntilDisplayed") is { } delta && delta >= 0)
                display = start + delta;
            bool? displayed = null;
            if (!lab && columns.Number(row, "Dropped") is { } dropped) displayed = dropped == 0;
            if (!lab && columns.Has("DisplayedTime")) displayed = columns.Number(row, "DisplayedTime") is >= 0;
            var displayInterval = columns.Number(row, "MsBetweenDisplayChange", "msBetweenDisplayChange");
            var submitInterval = lab ? columns.Number(row, "PresentIntervalMs")
                : columns.Number(row, "MsBetweenPresents", "msBetweenPresents");
            rows.Add(new(pid, chain, kind, submit, display, submitInterval, displayInterval,
                columns.Number(row, "RenderWorkMs"), columns.Number(row, "PacingWaitMs"), columns.Number(row, "LatenessMs"),
                columns.Number(row, "MsClickToPhotonLatency", "ClickToPhotonLatency", "MsAllInputToPhotonLatency", "MsPCLatency"),
                displayed, !lab || columns.Text(row, "WaitStatus") == "completed", columns.Text(row, "Occluded") == "1",
                new[] { "MsClickToPhotonLatency", "ClickToPhotonLatency", "MsAllInputToPhotonLatency", "AllInputToPhotonLatency", "MsPCLatency", "InstrumentedLatency" }
                    .Where(columns.Has).Select(name => (Name: name, Value: columns.Number(row, name)))
                    .Where(pair => pair.Value is > 0).ToDictionary(pair => pair.Name, pair => pair.Value!.Value),
                columns.Number(row, "RenderLateMs"), columns.Number(row, "WaitOvershootMs"),
                columns.Number(row, "QueueWaitMs"), columns.Number(row, "PresentCallMs")));
        }
        if (rows.Count == 0) throw new InvalidDataException("No matching frame rows.");
        var pids = rows.Select(r => r.Pid).Distinct().ToArray();
        if (pids.Length != 1) throw new InvalidDataException("Multiple processes in CSV; select --pid explicitly.");
        var chains = rows.Select(r => r.Swapchain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (chains.Length != 1) throw new InvalidDataException("Multiple swapchains in CSV; select --swapchain explicitly. Never combine streams.");
        var valid = rows.Where(r => r.Complete && !r.Occluded).ToArray();
        bool synthetic = lab && rows.All(r => r.Kind.StartsWith("synthetic_", StringComparison.Ordinal));
        var warnings = new List<string>();
        if (malformed > 0) warnings.Add($"{malformed} malformed rows skipped; capture is unsuitable for a clean comparison.");
        if (rows.Any(r => !r.Complete)) warnings.Add("Failed/cancelled wait rows excluded; run is incomplete.");
        if (rows.Any(r => r.Occluded)) warnings.Add("Occluded presentations excluded; no physical display conclusion.");
        if (synthetic) warnings.Add("Synthetic clock and generated frames only; no real DLSS or Delta Force measurement.");
        else if (lab) warnings.Add("Own DX12 CPU submission timestamps; these are not physical display FPS.");
        var native = valid.Where(r => Kind(r.Kind) == "native").ToArray();
        var generated = valid.Where(r => Kind(r.Kind) == "generated").ToArray();
        var unknown = valid.Count(r => Kind(r.Kind) == "unknown");
        var repeated = valid.Count(r => Kind(r.Kind) == "repeated");
        int timingAnomalies = 0;
        if (unknown > 0 && !lab) warnings.Add($"{unknown} frames have unknown type; native/generated split is incomplete.");
        List<double> Intervals(IEnumerable<FrameRow> data, Func<FrameRow, double?> timestamp)
        {
            var list = new List<double>(); double? previous = null;
            foreach (var frame in data)
            {
                var current = timestamp(frame);
                if (!current.HasValue) { previous = null; continue; }
                if (previous.HasValue)
                {
                    var interval = current.Value - previous.Value;
                    if (interval > 0) list.Add(interval);
                    else { timingAnomalies++; warnings.Add("Duplicate/non-monotonic timestamps; excluded affected interval."); }
                }
                previous = current;
            }
            return list;
        }
        var cpuIntervals = Intervals(valid, r => r.SubmitMs);
        if (cpuIntervals.Count == 0) cpuIntervals = valid.Select(r => r.SubmitInterval).Where(v => v is > 0).Select(v => v!.Value).ToList();
        var nativeIntervals = Intervals(native, r => r.SubmitMs);
        var displayFrames = valid.Where(r => r.Displayed != false && Kind(r.Kind) != "repeated").ToArray();
        var displayIntervals = synthetic ? Intervals(valid, r => r.SubmitMs) : lab ? [] : Intervals(displayFrames, r => r.DisplayMs);
        if (!lab && displayIntervals.Count == 0 && repeated == 0)
            displayIntervals = valid.Where(r => r.Displayed != false && Kind(r.Kind) != "repeated").Select(r => r.DisplayInterval).Where(v => v is > 0).Select(v => v!.Value).ToList();
        // A complete display stream needs known displayedness and timestamp/interval availability.
        if (!lab && (valid.Any(r => r.Displayed is null) || displayIntervals.Count == 0))
        {
            warnings.Add("Display event availability is incomplete; final displayed FPS is unknown.");
            displayIntervals.Clear();
        }
        if (unknown > 0) nativeIntervals.Clear();
        var nativeStats = Stats.From(nativeIntervals, target);
        var displayStats = Stats.From(displayIntervals, target);
        var cpuStats = Stats.From(cpuIntervals, target);
        var elapsed = valid.Length > 1 && valid[^1].SubmitMs is { } last && valid[0].SubmitMs is { } first ? last - first : 0;
        if (elapsed < 10000) warnings.Add("Less than 10 seconds of valid samples; no stable-lock conclusion.");
        var conclusion = synthetic ? "Synthetic scheduler result only; game feasibility is unproven."
            : lab ? "Own DX12 submission timing only; display and game lock are unproven."
            : unknown > 0 ? "Frame types unavailable; native+generated final display FPS is unconfirmed."
            : displayStats is null ? "Final displayed cadence unavailable; do not claim successful lock."
            : elapsed < 10000 || malformed > 0 || timingAnomalies > 0 || rows.Any(r => !r.Complete) ? "Insufficient/invalid samples; do not claim stable lock."
            : 1000 / displayStats.MeanMs < target * .95 ? "Below target: possible insufficient performance or another limiter; cause unconfirmed."
            : Math.Abs(1000 / displayStats.MeanMs - target) > Math.Max(2, target * .02) ? "Measured display cadence differs from target."
            : "Average display cadence is near target; inspect P95/P99, long frames and jitter separately.";
        return new()
        {
            Source = synthetic ? "synthetic" : lab ? "own-dx12" : "presentmon",
            MetricBasis = synthetic ? "simulated display cadence" : lab ? "CPU submission cadence" : "ETW application-start/display events",
            CsvSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            Pid = lab ? null : pids[0], Swapchain = chains[0], TargetDisplayedFps = target, Rows = rows.Count,
            NativeFrames = native.Length, GeneratedFrames = generated.Length, UnknownFrames = unknown,
            RepeatedFrames = repeated, TimingAnomalies = timingAnomalies,
            FailedRows = rows.Count(r => !r.Complete) + malformed, OccludedRows = rows.Count(r => r.Occluded),
            ObservedNativeFps = nativeStats is null ? null : 1000 / nativeStats.MeanMs,
            ObservedDisplayedFps = displayStats is null || unknown > 0 ? null : 1000 / displayStats.MeanMs,
            ObservedDisplayEventFps = displayStats is null ? null : 1000 / displayStats.MeanMs,
            NativeIntervals = nativeStats, DisplayIntervals = displayStats, CpuSubmissionIntervals = cpuStats,
            RenderWork = Stats.From(valid.Where(r => r.RenderWork.HasValue).Select(r => r.RenderWork!.Value), target, includeZero: true),
            PacingWait = Stats.From(valid.Where(r => r.Wait.HasValue).Select(r => r.Wait!.Value), target, includeZero: true),
            DeadlineLateness = Stats.From(valid.Where(r => r.Late.HasValue).Select(r => r.Late!.Value), target, includeZero: true),
            RenderLateness = Stats.From(valid.Where(r => r.RenderLate.HasValue).Select(r => r.RenderLate!.Value), target, includeZero: true),
            WaitOvershoot = Stats.From(valid.Where(r => r.WaitOvershoot.HasValue).Select(r => r.WaitOvershoot!.Value), target, includeZero: true),
            QueueWait = Stats.From(valid.Where(r => r.QueueWait.HasValue).Select(r => r.QueueWait!.Value), target, includeZero: true),
            PresentCall = Stats.From(valid.Where(r => r.PresentCall.HasValue).Select(r => r.PresentCall!.Value), target, includeZero: true),
            InputLatency = Stats.From(valid.Select(r => r.InputLatency ?? 0), target),
            InputLatencyMetrics = valid.SelectMany(r => r.InputMetrics).GroupBy(pair => pair.Key)
                .ToDictionary(group => group.Key, group => Stats.From(group.Select(pair => pair.Value), target)!),
            Conclusion = conclusion, Warnings = warnings.Distinct().ToArray(),
        };
    }

    public static void Save(AnalysisReport report, string output)
    {
        Directory.CreateDirectory(output);
        var json = Path.Combine(output, "analysis.json"); var markdown = Path.Combine(output, "analysis.md");
        if (File.Exists(json) || File.Exists(markdown)) throw new IOException("Analysis output already exists.");
        File.WriteAllText(json, JsonSerializer.Serialize(report, Json), new UTF8Encoding(false));
        var text = new StringBuilder("# Frame pacing analysis\n\n");
        text.AppendLine($"Source: **{report.Source}**. Basis: **{report.MetricBasis}**. Target: {report.TargetDisplayedFps} FPS.");
        text.AppendLine($"\n{report.Conclusion}\n");
        text.AppendLine("| Metric | Samples | Mean ms | P95 ms | P99 ms | Std dev ms | 1% low FPS | Long frames |\n|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var pair in new[] { ("Native", report.NativeIntervals), ("Displayed", report.DisplayIntervals),
            ("CPU submission", report.CpuSubmissionIntervals), ("Render work", report.RenderWork), ("Pacing wait", report.PacingWait), ("Deadline lateness", report.DeadlineLateness),
            ("Render lateness", report.RenderLateness), ("Wait overshoot", report.WaitOvershoot), ("Queue wait", report.QueueWait), ("Present call", report.PresentCall) })
            if (pair.Item2 is { } s) text.AppendLine(FormattableString.Invariant($"| {pair.Item1} | {s.Count} | {s.MeanMs:F4} | {s.P95Ms:F4} | {s.P99Ms:F4} | {s.StandardDeviationMs:F4} | {(pair.Item1 is "Native" or "Displayed" or "CPU submission" ? s.OnePercentLowFps.ToString("F2", CultureInfo.InvariantCulture) : "N/A")} | {s.LongFrames} |"));
        text.AppendLine("\nLong frame: duration > max(2 × target interval, 20 ms). 1% low: 1000 / mean of worst 1% frame durations; not 1000 / P99.");
        text.AppendLine($"\nNative / generated / repeated / unknown frame counts: {report.NativeFrames} / {report.GeneratedFrames} / {report.RepeatedFrames} / {report.UnknownFrames}.");
        if (report.UnknownFrames > 0) text.AppendLine("\nDisplayed-event cadence may be available, but final native+generated FPS remains unconfirmed because frame types are unavailable.");
        if (report.InputLatencyMetrics.Count == 0) text.AppendLine("\nInput latency unavailable; no proxy substituted.");
        foreach (var pair in report.InputLatencyMetrics)
            text.AppendLine($"\n{pair.Key}: P95={pair.Value.P95Ms:F3} ms (original source metric; not automatically end-to-end mouse latency).");
        foreach (var warning in report.Warnings) text.AppendLine("\n- " + warning);
        File.WriteAllText(markdown, text.ToString(), new UTF8Encoding(false));
    }
}
