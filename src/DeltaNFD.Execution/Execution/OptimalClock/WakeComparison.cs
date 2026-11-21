namespace DeltaNFD.Execution.OptimalClock;

// One independent observer, one bounded measurement block. Times are milliseconds.
internal sealed record WakeBlock(int Samples, uint Before, uint After, double SleepMedian, double SleepP95,
    double LateMedian, double LateP95, int Skipped, uint ApiReading = 0)
{
    public double MissRate => Skipped / ((double)Samples + Skipped);
    internal bool Valid(int n) => Samples == n && Skipped >= 0 && Before > 0 && After > 0 &&
        double.IsFinite(SleepMedian) && SleepMedian > 0 && double.IsFinite(SleepP95) && SleepP95 >= SleepMedian &&
        double.IsFinite(LateMedian) && LateMedian >= 0 && double.IsFinite(LateP95) && LateP95 >= LateMedian;
    internal bool Tracks(uint? target) => Math.Abs((long)Before - After) <= 50 &&
        (target is not uint t || (ApiReading > 0 && Math.Abs((long)t - Before) <= 50 && Math.Abs((long)t - After) <= 50));
}

internal enum WakeVerdict { Shortlist, Recommend, Keep, Inconclusive }
internal sealed record WakeReport(WakeVerdict Verdict, string Message, uint? Candidate = null);

internal static class WakeComparison
{
    private static readonly uint[] Grid = [5000, 5100, 5300, 5600, 6000, 6300, 7500, 10000];
    // Practical minimum effect, not a statistical confidence interval. Never treat individual wakes as replicates.
    private const double MinimumGain = 0.05, MaxNoise = 0.35;
    private static double Range(IEnumerable<double> values) => values.Max() - values.Min();
    private static string Ms(uint v) => $"{v / 10000d:0.0000} ms";

    internal static WakeReport Run(uint? reference, string referenceName, bool quick, Func<uint?, int, WakeBlock?> measure,
        CancellationToken token, Action<string, object?>? log = null)
    {
        WakeBlock? Take(uint? target, int count, string phase)
        {
            token.ThrowIfCancellationRequested();
            var block = measure(target, count);
            token.ThrowIfCancellationRequested();
            log?.Invoke("comparison-block", new { phase, target, count, block });
            return block is not null && block.Valid(count) && block.Tracks(target) ? block : null;
        }
        WakeReport Result(WakeVerdict verdict, string text, uint? value = null) => new(verdict,
            $"{text}。\n对照设置：{referenceName.Trim()}（144 Hz 唤醒测试）。未更改保存值。", value);
        var controls = new List<WakeBlock>();
        bool ReferenceDrift(IEnumerable<WakeBlock> rows) => rows.Max(x => Math.Max(x.Before,x.After)) - rows.Min(x => Math.Min(x.Before,x.After)) > 50;
        // Retry latency noise once in full mode, with three longer independent blocks. Never
        // retry a missing/invalid reading or a change in the observed reference setting.
        for (int pass = 0; ; ++pass)
        {
            var window = new List<WakeBlock>();
            for (int i = 0; i < 3; ++i)
            {
                if (Take(reference, pass == 0 ? 40 : 96, pass == 0 ? "baseline" : "baseline-confirm") is not {} a)
                    return Result(WakeVerdict.Inconclusive, "这次没测准：未取得有效的对照读数，还没有比较其他设置");
                window.Add(a);
            }
            if (ReferenceDrift(controls.Concat(window)))
                return Result(WakeVerdict.Inconclusive, "对照设置的实际精度发生变化，还没有比较其他设置");
            controls = window;
            double spread = Range(window.Select(x => x.LateP95));
            if (spread <= MaxNoise) break;
            if (quick || pass > 0)
                return Result(WakeVerdict.Inconclusive, $"{(quick ? "初筛时" : "延长采样后")}，现有设置的晚醒时间仍相差 {spread:0.000} ms（上限 {MaxNoise:0.00} ms）。尚未比较其他设置，先不用改");
        }
        var ranked = new List<(uint Target, WakeBlock Row)>();
        // A candidate is attributable only if the independent observer distinguishes it from baseline.
        bool Distinct(WakeBlock a, WakeBlock b) => Math.Min(Math.Abs((long)a.Before-b.Before), Math.Abs((long)a.After-b.After)) > 50;
        foreach (var t in Grid.Where(t => t != reference))
            if (Take(t, 40, "screen") is {} b && controls.All(a => Distinct(a,b))) ranked.Add((t,b));
        if (ranked.Count == 0) return Result(WakeVerdict.Inconclusive, "这次无法判断：测不出其他设置与现有设置的差别，先不用改");
        var first = ranked.OrderBy(x => x.Row.MissRate).ThenBy(x => x.Row.LateP95).ThenBy(x => x.Row.LateMedian).First();
        // Refine only around an observable coarse band and only within the eligible domain.
        foreach (var t in new[]{first.Target - 100u, first.Target + 100u}.Where(t => t >= Grid[0] && t <= Grid[^1] && t != reference && !Grid.Contains(t)))
            if (Take(t, 40, "refine") is {} b && controls.All(a => Distinct(a,b))) ranked.Add((t,b));
        var finalists = ranked.OrderBy(x => x.Row.MissRate).ThenBy(x => x.Row.LateP95).ThenBy(x => x.Row.LateMedian).Take(2).ToArray();
        if (quick) return Result(WakeVerdict.Shortlist, "值得复测的值：" + string.Join("、",finalists.Select(x=>Ms(x.Target))) + "；这只是初筛，想知道是否更准时，请运行完整比较");
        var winners = new List<(uint Target, double Gain, double A, double B)>();
        var details = new List<string>();
        int uncertain = 0;
        void Unresolved(uint target, string reason)
        {
            ++uncertain;
            details.Add($"{Ms(target)}：{reason}，无法判断");
        }
        foreach (var finalist in finalists)
        {
            var gains = new List<double>(); var aRows = new List<WakeBlock>(); var bRows = new List<WakeBlock>(); bool valid = true, safe = true;
            foreach (var order in new[]{"ABBA", "BAAB"})
            {
                var aa = new List<WakeBlock>(); var bb = new List<WakeBlock>();
                foreach (char arm in order)
                {
                    var row = Take(arm == 'A' ? reference : finalist.Target, 96, $"final/{finalist.Target}/{order}/{arm}");
                    if (row is null) { valid = false; break; }
                    (arm == 'A' ? aa : bb).Add(row);
                }
                if (!valid) break;
                aRows.AddRange(aa); bRows.AddRange(bb);
                // Require gains in both orders, including the worst B versus the best A in each block.
                gains.Add(aa.Min(x=>x.LateP95) - bb.Max(x=>x.LateP95));
                safe &= bb.Max(x=>x.LateMedian) <= aa.Min(x=>x.LateMedian) + MinimumGain &&
                    bb.Max(x=>x.MissRate) <= aa.Min(x=>x.MissRate) &&
                    bb.Max(x=>x.SleepMedian) <= aa.Min(x=>x.SleepMedian) + MinimumGain;
            }
            if (!valid) { Unresolved(finalist.Target, "未取得有效读数"); continue; }
            if (ReferenceDrift(controls.Concat(aRows)) || !aRows.All(a=>bRows.All(b=>Distinct(a,b))))
            { Unresolved(finalist.Target, "实际计时精度变化，或与对照无法区分"); continue; }
            double noise = Math.Max(Range(controls.Select(x=>x.LateP95)),Range(aRows.Select(x=>x.LateP95)));
            if (noise > MaxNoise) { Unresolved(finalist.Target, $"对照的晚醒时间相差 {noise:0.000} ms（上限 {MaxNoise:0.00} ms）"); continue; }
            if (gains.Min() < -Math.Max(MinimumGain,noise) && gains.Max() > Math.Max(MinimumGain,noise))
            { Unresolved(finalist.Target, "交换测试顺序后结果相反"); continue; }
            if (safe && gains.Min() > Math.Max(MinimumGain, noise))
                winners.Add((finalist.Target, gains.Min(), aRows.Average(x=>x.LateP95), bRows.Average(x=>x.LateP95)));
            else details.Add($"{Ms(finalist.Target)}：复测未见稳定优势");
        }
        if (winners.Count == 0)
        {
            string summary = uncertain == finalists.Length ? "暂时无法判断，先不用改设置" : "建议保留现有设置：本轮没有证据支持换值";
            return Result(uncertain > 0 ? WakeVerdict.Inconclusive : WakeVerdict.Keep, summary + "。复测结果：\n" + string.Join("\n", details));
        }
        var win = winners.OrderByDescending(x=>x.Gain).ThenByDescending(x=>x.Target).First();
        return Result(WakeVerdict.Recommend, $"{Ms(win.Target)} 值得试用：交换测试顺序后，程序仍能更准时地醒来。这不代表游戏帧数会提高。晚醒时间（P95 均值） {win.A:0.000} → {win.B:0.000} ms", win.Target);
    }
}
