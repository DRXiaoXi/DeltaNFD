using System.Text;
using System.Text.Json;

namespace FramePacingResearch;

internal static class Comparison
{
    public static void Run(string manifestPath, string output)
    {
        if (new FileInfo(manifestPath).Length > 64 * 1024) throw new InvalidDataException("Comparison manifest too large.");
        var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifestPath)) ?? throw new InvalidDataException("Expected JSON array of capture directories.");
        if (paths.Length is < 9 or > 100) throw new InvalidDataException("Require >=3 captures for each of uncapped/native/nvidia (9..100 total).");
        var runs = new List<(CaptureManifest Meta, AnalysisReport Report)>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var basis = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        foreach (var value in paths)
        {
            var dir = Path.GetFullPath(value, basis);
            if (!directories.Add(dir)) throw new InvalidDataException("A capture directory cannot count as multiple repetitions.");
            var meta = JsonSerializer.Deserialize<CaptureManifest>(File.ReadAllText(Path.Combine(dir, "capture.json")), Analysis.Json)
                ?? throw new InvalidDataException("Missing capture manifest.");
            var csv = Path.Combine(dir, "frames.csv");
            if (meta.Source != "presentmon" || meta.Status != "complete" || meta.CollectorExitCode != 0 || meta.RequestedSeconds < 60
                || (meta.FinishedUtc - meta.StartedUtc).TotalSeconds < 59 || meta.FrameGeneration == "unknown"
                || meta.CollectorWarnings.Length != 0 || meta.PresentMonSha256 != Capture.ExpectedHash || !Capture.Hash(csv).Equals(meta.CsvSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Incomplete/synthetic/unverified capture cannot enter a game comparison.");
            var report = Analysis.Analyze(csv, meta.TargetDisplayedFps, meta.Pid);
            if (report.DisplayIntervals is null || report.FailedRows != 0 || report.TimingAnomalies != 0 || report.UnknownFrames != 0 || report.DisplayIntervals.Count < 100)
                throw new InvalidDataException("Frame types/display timing incomplete; choose verified stream before comparing.");
            if ((meta.FrameGeneration == "on" && report.GeneratedFrames == 0) || (meta.FrameGeneration == "off" && report.GeneratedFrames != 0))
                throw new InvalidDataException("Declared FG mode does not have matching observed generated-frame evidence.");
            runs.Add((meta, report));
        }
        ValidateGroups(runs.Select(r => r.Meta).ToArray());
        Directory.CreateDirectory(output);
        if (File.Exists(Path.Combine(output, "comparison.md"))) throw new IOException("Comparison output exists.");
        var text = new StringBuilder("# Delta Force frame pacing comparison\n\n");
        text.AppendLine("User-labelled conditions; this tool did not change any game/driver setting. Results are observed, not proof of causality.\n");
        text.AppendLine("| Condition | FG | Runs | Median mean FPS | Median P95 ms | Median P99 ms | Median stddev ms | Total long frames |\n|---|---|---:|---:|---:|---:|---:|---:|");
        double median(IEnumerable<double> values) { var data = values.Order().ToArray(); return data.Length % 2 == 1 ? data[data.Length / 2] : (data[data.Length / 2 - 1] + data[data.Length / 2]) / 2; }
        foreach (var group in runs.GroupBy(r => (r.Meta.Condition, r.Meta.FrameGeneration)))
        {
            var stats = group.Select(r => r.Report.DisplayIntervals!).ToArray();
            text.AppendLine(FormattableString.Invariant($"| {group.Key.Condition} | {group.Key.FrameGeneration} | {stats.Length} | {median(stats.Select(s => 1000 / s.MeanMs)):F2} | {median(stats.Select(s => s.P95Ms)):F3} | {median(stats.Select(s => s.P99Ms)):F3} | {median(stats.Select(s => s.StandardDeviationMs)):F3} | {stats.Sum(s => s.LongFrames)} |"));
        }
        text.AppendLine("\nNo automatic best/worst verdict: account for input latency availability, GPU load, scene variance and sample quality. Reported total long frames depend on sample duration; compare equal-duration runs.\n");
        File.WriteAllText(Path.Combine(output, "comparison.md"), text.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "comparison.json"), JsonSerializer.Serialize(runs.Select(r => new { capture = r.Meta, analysis = r.Report }), Analysis.Json), new UTF8Encoding(false));
        Console.WriteLine("Comparison saved: " + output);
    }
    internal static void ValidateGroups(IReadOnlyList<CaptureManifest> runs)
    {
        if (runs.Count == 0) throw new InvalidDataException("No runs.");
        var first = runs[0];
        if (runs.Any(r => string.IsNullOrEmpty(r.SessionName)) || runs.Select(r => r.SessionName).Distinct().Count() != runs.Count)
            throw new InvalidDataException("Capture sessions must be distinct; copied runs cannot count as repetitions.");
        if (runs.Any(r => r.Scene != first.Scene || r.TargetDisplayedFps != first.TargetDisplayedFps
            || r.GameExeSha256 != first.GameExeSha256 || r.DriverFingerprint != first.DriverFingerprint
            || r.OsBuild != first.OsBuild || r.RequestedSeconds != first.RequestedSeconds))
            throw new InvalidDataException("Scene, target, game build, driver, OS and duration must match.");
        foreach (var group in runs.GroupBy(r => r.FrameGeneration))
            foreach (var condition in new[] { "uncapped", "native", "nvidia" })
                if (group.Count(r => r.Condition == condition) < 3)
                    throw new InvalidDataException("Each FG group requires >=3 runs for each condition.");
        if (runs.Any(r => r.Condition is not ("uncapped" or "native" or "nvidia"))) throw new InvalidDataException("Unknown comparison condition.");
    }
}
