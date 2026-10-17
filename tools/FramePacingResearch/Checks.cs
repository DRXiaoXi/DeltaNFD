using System.Text;

namespace FramePacingResearch;

internal static class Checks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-frame-analysis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); int count = 0;
        void check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        void reject(Action action, string name) {
            try { action(); } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException)
            { check(true, name); return; } throw new Exception("Expected rejection: " + name);
        }
        string file(string name, string text) { var p = Path.Combine(root, name); File.WriteAllText(p, text, new UTF8Encoding(false)); return p; }
        var quoted = file("quoted.csv", "\uFEFFa,b,c\r\n\"with,comma\",\"multi\nline\",\"escaped\"\"quote\"\r\n");
        var parsed = Csv.Read(quoted).ToArray();
        check(parsed.Length == 2 && parsed[1].SequenceEqual(new[] {"with,comma", "multi\nline", "escaped\"quote"}), "CSV BOM / quoted comma / newline / escaped quote");
        reject(() => Csv.Read(file("broken.csv", "a,b\n\"unclosed,b")).ToArray(), "malformed CSV quote rejected");
        reject(() => new Columns(["ProcessID", "processid"]), "duplicate headers rejected");
        check(Csv.Number("NA") is null && Csv.Number("NaN") is null && Csv.Number("Infinity") is null, "missing/nonfinite values remain unavailable");
        var known = Stats.From(new[] {1.0, 2, 3, 4, 100}, 144)!;
        check(known.Count == 5 && Math.Abs(known.P95Ms - 80.8) < .0001 && known.P99Ms > known.P95Ms, "P95/P99 use interpolated quantiles");
        check(Math.Abs(known.OnePercentLowFps - 10) < .001, "1% low is reciprocal worst-duration mean");
        check(known.LongFrames == 1 && known.LongFrameThresholdMs == 20, "long frame threshold is explicit");
        check(Stats.From([0.0, 0], 144, true)!.MeanMs == 0, "zero lateness retained");
        check(Stats.From([], 144) is null, "empty stream stays unavailable");
        check(Analysis.Kind("Application") == "native" && Analysis.Kind("Intel XeSS-FG") == "generated"
            && Analysis.Kind("AMD AFMF") == "generated", "pinned PresentMon type labels mapped");
        check(Analysis.Kind("NotSet") == "unknown" && Analysis.Kind("Repeated") == "repeated"
            && Analysis.Kind("0") == "unknown" && Analysis.Kind("NvidiaGenerated") == "unknown", "unknown/repeated/numeric/unverified vendor types not invented as native");
        const string header = "Application,ProcessID,SwapChainAddress,FrameType,CPUStartQPCTime,DisplayLatency,DisplayedTime,MsClickToPhotonLatency\n";
        string pm(string name, Func<int, string> row) {
            var text = new StringBuilder(header); for (int i = 0; i < 2000; i++) text.AppendLine(row(i)); return file(name, text.ToString());
        }
        string f(double value) => value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        var regularPath = pm("regular.csv", i => $"game.exe,42,0x1,Application,{f(i * 1000.0 / 144)},2,{f(1000.0 / 144)},NA");
        var regular = Analysis.Analyze(regularPath, 144);
        check(regular.NativeFrames == 2000 && regular.GeneratedFrames == 0, "native frame count");
        check(Math.Abs(regular.ObservedDisplayedFps!.Value - 144) < .001, "display FPS from actual displayed-event timeline");
        check(regular.InputLatency is null, "missing input latency not substituted");
        var inputPath = file("input.csv", "ProcessID,SwapChainAddress,FrameType,CPUStartQPCTime,DisplayLatency,DisplayedTime,ClickToPhotonLatency,AllInputToPhotonLatency\n42,0x1,Application,0,2,7,NA,12\n42,0x1,Application,7,2,7,NA,14\n");
        var input = Analysis.Analyze(inputPath, 144);
        check(input.InputLatencyMetrics.ContainsKey("AllInputToPhotonLatency") && !input.InputLatencyMetrics.ContainsKey("ClickToPhotonLatency"),
            "available input metric retained without replacing unavailable click metric");
        check(regular.Conclusion.Contains("inspect P95/P99"), "near-average FPS does not imply stable-lock claim");
        var fgPath = pm("fg.csv", i => $"game.exe,42,0x1,{(i % 2 == 0 ? "Application" : "Intel XeSS-FG")},{f(i * 1000.0 / 144)},2,{f(1000.0 / 144)},NA");
        var fg = Analysis.Analyze(fgPath, 144);
        check(fg.NativeFrames == 1000 && fg.GeneratedFrames == 1000, "native / generated counts separated");
        check(Math.Abs(fg.ObservedNativeFps!.Value - 72) < .001 && Math.Abs(fg.ObservedDisplayedFps!.Value - 144) < .001, "synthetic ETW fixture keeps native72 / displayed144 distinct");
        var unknown = Analysis.Analyze(pm("unknown.csv", i => $"game.exe,42,0x1,NotSet,{f(i * 7.0)},2,7,NA"), 144);
        check(unknown.NativeIntervals is null && unknown.UnknownFrames == 2000 && unknown.ObservedDisplayedFps is null,
            "uninstrumented frame types are unavailable, not assumed native/final FPS");
        var repeated = Analysis.Analyze(pm("repeated.csv", i => $"game.exe,42,0x1,{(i % 2 == 0 ? "Application" : "Repeated")},{i * 7},2,7,NA"), 144);
        check(repeated.RepeatedFrames == 1000 && repeated.GeneratedFrames == 0 && repeated.DisplayIntervals!.MeanMs == 14,
            "repeated scanout does not inflate native+generated display FPS");
        var corruptTime = Analysis.Analyze(pm("time.csv", i => $"game.exe,42,0x1,Application,{(i == 100 ? 0 : i * 7)},2,7,NA"), 144);
        check(corruptTime.TimingAnomalies > 0 && corruptTime.Conclusion.Contains("invalid"), "non-monotonic time cannot produce stable-lock conclusion");
        reject(() => Analysis.Analyze(file("no-type.csv", "ProcessID,SwapChainAddress,CPUStartTime\n42,0x1,1\n"), 144), "missing FrameType rejected");
        reject(() => Analysis.Analyze(pm("pids.csv", i => $"game.exe,{(i % 2 == 0 ? 42 : 43)},0x1,Application,{i * 7},2,7,NA"), 144), "multiple PIDs require explicit selection");
        check(Analysis.Analyze(Path.Combine(root, "pids.csv"), 144, 42).Pid == 42, "PID filter selects one stream");
        reject(() => Analysis.Analyze(pm("chains.csv", i => $"game.exe,42,{(i % 2 == 0 ? "0x1" : "0x2")},Application,{i * 7},2,7,NA"), 144), "multiple swapchains never mixed");
        check(Analysis.Analyze(Path.Combine(root, "chains.csv"), 144, 42, "0x2").Swapchain == "0x2", "swapchain filter explicit");
        var absent = Analysis.Analyze(pm("absent-display.csv", i => $"game.exe,42,0x1,Application,{i * 7},NA,NA,NA"), 144);
        check(absent.DisplayIntervals is null && absent.ObservedDisplayedFps is null, "no display telemetry means no final displayed FPS");
        var raw = file("raw-qpc.csv", "ProcessID,SwapChainAddress,FrameType,CPUStartQPC,DisplayLatency,DisplayedTime\n42,0x1,Application,10000000,2,7\n42,0x1,Application,10070000,2,7\n");
        check(Analysis.Analyze(raw, 144).CpuSubmissionIntervals is null, "raw QPC never interpreted as milliseconds");
        var shortCsv = file("short.csv", header + "game.exe,42,0x1,Application,0,2,7,NA\ngame.exe,42,0x1,Application,7,2,7,NA\n");
        check(Analysis.Analyze(shortCsv, 144).Conclusion.Contains("Insufficient"), "short sample cannot claim stable lock");
        const string labHeader = "FrameIndex,FrameKind,PresentSubmitMs,PresentIntervalMs,RenderWorkMs,PacingWaitMs,LatenessMs,WaitStatus,Occluded\n";
        var lab = Analysis.Analyze(file("lab.csv", labHeader + "1,dx12_cpu_submit,7,0,1,6,0,completed,0\n2,dx12_cpu_submit,14,7,1,6,0,completed,0\n"), 144);
        check(lab.Source == "own-dx12" && lab.ObservedDisplayedFps is null, "DX12 CPU submission is not physical display FPS");
        var synthetic = Analysis.Analyze(file("synthetic.csv", labHeader + "1,synthetic_native,7,0,1,6,0,completed,0\n1,synthetic_generated,14,7,0,0,0,completed,0\n"), 144);
        check(synthetic.Source == "synthetic" && synthetic.Conclusion.Contains("unproven"), "simulated generation is never real game/DLSS evidence");
        var failed = Analysis.Analyze(file("failed.csv", labHeader + "1,synthetic_native,7,0,1,6,0,completed,0\n2,synthetic_native,2007,2000,1,1999,2000,timeout,0\n"), 144);
        check(failed.FailedRows == 1 && failed.CpuSubmissionIntervals is null, "timeout row excluded from successful frames");
        var occluded = Analysis.Analyze(file("occluded.csv", labHeader + "1,dx12_cpu_submit,7,0,1,6,0,completed,1\n"), 144);
        check(occluded.OccludedRows == 1 && occluded.CpuSubmissionIntervals is null, "occluded presentations excluded");
        reject(() => Capture.ValidateTargetName(@"C:\game\other.exe", false), "capture cannot silently target another application");
        reject(() => Capture.ValidateTargetName("DeltaForceClient-Win64-Shipping.exe", false), "capture requires full process path");
        Capture.ValidateTargetName(@"C:\game\DeltaForceClient-Win64-Shipping.exe", false); check(true, "explicit Delta Force executable accepted");
        var m = new CaptureManifest {
            Condition = "uncapped", Scene = "training-route-A", FrameGeneration = "off", TargetDisplayedFps = 144,
            RequestedSeconds = 60, GameExe = "game", GameExeSha256 = "hash", Pid = 42, ProcessStartUtcTicks = 1,
            OsBuild = "os", DriverFingerprint = "driver", PresentMonSha256 = Capture.ExpectedHash, StartedUtc = DateTimeOffset.UtcNow, Status = "complete",
        };
        var groups = new[] {"uncapped", "native", "nvidia"}.SelectMany(condition => Enumerable.Range(0, 3).Select(i => m with {Condition = condition, SessionName = condition + i})).ToArray();
        Comparison.ValidateGroups(groups); check(true, "three conditions with three runs accepted");
        reject(() => Comparison.ValidateGroups(groups[..8]), "missing comparison repetition rejected");
        reject(() => Comparison.ValidateGroups(groups.Append(m with {SessionName = "extra", Scene = "other-map"}).ToArray()), "different scene cannot be compared");
        reject(() => Comparison.ValidateGroups(groups.Append(m with {SessionName = "extra", DriverFingerprint = "new-driver"}).ToArray()), "different driver cannot be compared");
        reject(() => Comparison.ValidateGroups(groups.Append(m with {SessionName = "extra", FrameGeneration = "on"}).ToArray()), "FG groups must each include complete baseline set");
        reject(() => Comparison.ValidateGroups(groups.Append(groups[0]).ToArray()), "copied session cannot inflate repetitions");
        reject(() => Options.Parse(["--target", "144", "--target", "120"]), "duplicate CLI options rejected");
        reject(() => Options.Parse(["--target", "NaN"]).Integer("target", 30, 1000), "nonfinite CLI target rejected");
        var saved = Path.Combine(root, "report"); Analysis.Save(regular, saved);
        check(File.Exists(Path.Combine(saved, "analysis.json")) && File.Exists(Path.Combine(saved, "analysis.md")), "standalone analysis artifacts written");
        reject(() => Analysis.Save(regular, saved), "analysis does not overwrite existing results");
        Console.WriteLine($"Frame analysis checks: {count} passed (isolated fixtures; no game/process/settings changes).");
    }
}
