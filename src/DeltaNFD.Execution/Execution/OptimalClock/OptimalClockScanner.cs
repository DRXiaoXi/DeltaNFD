using System.Diagnostics;
using System.Globalization;

namespace DeltaNFD.Execution.OptimalClock;

/// <summary>候选值的聚合结果（多次重复的中位的中位 + 波动 + 慢样本比例）。</summary>
internal sealed record OptimalClockCandidate
{
    public required uint Resolution100ns { get; init; }
    public required double Median { get; init; }
    public required double Mean { get; init; }
    public required double P95 { get; init; }
    public required double Spread { get; init; }
    public required double Over130Pct { get; init; }
    public required int Runs { get; init; }
    public required int Samples { get; init; }

    /// <summary>各次重复的中位（用于判「多数重复是否仍在 2 tick 档」，比极差更抗单次污染）。</summary>
    public IReadOnlyList<double> RunMedians { get; init; } = [];

    /// <summary>落在 2 tick 档（≤2.5 × 自身 T）的重复次数。</summary>
    public int RunsInBand { get; init; }

    public double Milliseconds => Resolution100ns / 10000.0;
}

/// <summary>稳定延迟模式的一个测量条件（空闲/标准负载/重载）在该候选上的汇总。</summary>
internal sealed record OptimalClockConditionRow
{
    public required string Name { get; init; }
    public required int LoadThreads { get; init; }
    public required uint Resolution100ns { get; init; }
    public required double Median { get; init; }
    public required double Spread { get; init; }
    public required double Over130Pct { get; init; }
    public required int Runs { get; init; }

    /// <summary>该条件下落在 2 tick 档的重复次数（多数在档才算这个条件稳）。</summary>
    public int RunsInBand { get; init; }
}

/// <summary>扫描模式：global（全局计时器语义，请求影响全系统）/ self（按进程，仅本进程）。</summary>
internal enum OptimalClockMode { Unknown, Global, Self }

/// <summary>检测模式：最低延迟（原算法）/ 稳定延迟（新算法）。</summary>
public enum OptimalClockScanKind { LowestLatency, StableLatency }

internal sealed record OptimalClockScanOptions
{
    public uint From100ns { get; init; } = 4900;
    public uint To100ns { get; init; } = 6300;
    public int Samples { get; init; } = 50;
    public int Repeats { get; init; } = 3;
    public int VerifySamples { get; init; } = 150;
    public int VerifyRepeats { get; init; } = 2;
    public bool Verify { get; init; } = true;
    public int SpinMs { get; init; }

    /// <summary>测量时同时烧满的 CPU 线程数（0 = 空闲测量）。默认按逻辑核数取四分之一并限幅到 2~8，对齐研究里的 -Stress 8。</summary>
    public int LoadThreads { get; init; } = DefaultLoadThreads;

    /// <summary>默认负载线程数：研究在 32 线程 CPU 上用 8 个满载进程（约 1/4），这里同样取 1/4 并限幅 2~8。</summary>
    public static int DefaultLoadThreads => Math.Clamp(Environment.ProcessorCount / 4, 2, 8);

    public static OptimalClockScanOptions Quick => new() { Samples = 25, Repeats = 2, VerifySamples = 60, VerifyRepeats = 1 };
}

internal sealed record OptimalClockScanResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public OptimalClockMode Mode { get; init; }
    public double? RequestlessBaselineMs { get; init; }
    public IReadOnlyList<OptimalClockCandidate> Candidates { get; init; } = [];
    public OptimalClockCandidate? BestTypical { get; init; }
    public OptimalClockCandidate? BestStable { get; init; }

    /// <summary>实际用于锁定的值：在最优中位附近取「慢样本最少/波动最小」的那个，避开临界点边缘的双峰值。</summary>
    public OptimalClockCandidate? BestLock { get; init; }
    public IReadOnlyList<OptimalClockCandidate> Verified { get; init; } = [];
    public int Probes { get; init; }

    /// <summary>本次扫描实际施加的 CPU 负载线程数（0 = 空闲测量）。</summary>
    public int LoadThreads { get; init; }

    /// <summary>本次使用的检测模式。</summary>
    public OptimalClockScanKind ScanKind { get; init; }

    /// <summary>稳定延迟模式选定的安全裕量（ms）：2 tick 比 1ms 请求多出的余量。0 = 最低延迟模式。</summary>
    public double SafetyMarginMs { get; init; }

    /// <summary>稳定延迟模式的三条件验证说明（空闲/标准/重载的中位）。</summary>
    public string StableConditions { get; init; } = "";

    /// <summary>稳定延迟模式：各条件下的验证明细（条件名、线程数、该条件的最差中位/波动/慢样本）。</summary>
    public IReadOnlyList<OptimalClockConditionRow> StableConditionRows { get; init; } = [];

    public double ElapsedSeconds { get; init; }
    public DateTimeOffset CompletedUtc { get; init; } = DateTimeOffset.UtcNow;

    public static OptimalClockScanResult Failed(string message) => new() { Success = false, Message = message };
}

/// <summary>可注入的测量接口：每个候选都必须来自全新进程（由实现保证）。</summary>
internal interface IOptimalClockMeasurementApi
{
    /// <summary>全局语义路径：全新进程持有该值，另一个**不请求**的全新进程测量；失败返回 null。</summary>
    OptimalClockSample? MeasureGlobal(uint resolution100ns, int samples, int spinMs);

    /// <summary>按进程语义路径：全新进程请求该值并测量自身；失败返回 null。</summary>
    OptimalClockSample? MeasureSelf(uint resolution100ns, int samples, int spinMs);

    /// <summary>在「不请求任何精度」的进程里测量等待粒度（ms）；失败返回 null。</summary>
    double? MeasureRequestlessMs(int samples);

    /// <summary>
    /// 开始 CPU 负载（模拟游戏运行中），返回可停止的句柄；不支持时返回 null（要求负载的扫描必须失败，只有 LoadThreads=0 才允许空闲测量）。
    /// 研究结论：负载不会移动最优值，但会让临界点边缘变脆——所以必须在负载下测量才能选出真正稳的值。
    /// </summary>
    IOptimalClockLoad? StartLoad(int threads) => null;
    // 没有子进程责任的旧假实现无需实现；原生实现必须提供权威清理入口。
    bool HasPendingCleanup => false;
    void CleanupPending() { }
}

/// <summary>可停止的 CPU 负载句柄。</summary>
internal interface IOptimalClockLoad : IDisposable
{
    int Threads { get; }
    // 默认成员保留旧假实现兼容性；原生实现必须核验存活与硬寿命。
    bool EnsureActive() => true;
    bool IsActive => true;
}

/// <summary>
/// 三级逼近扫描（对应研究目录 find-best-resolution.ps1 的方法学）：
/// 0.01 → 0.001 → 0.0001 ms 逐级收窄；每个候选多次重复取「中位的中位」，同时给出
/// 「典型延迟最低」与「最稳」两个赢家，并对头部候选做更多采样复测。
/// </summary>
internal sealed class OptimalClockScanner(IOptimalClockMeasurementApi api, Action<string, object?>? diagnostics = null)
{
    internal const double ReachableThresholdMs = 5.0;

    /// <summary>
    /// 临界点经验下限（100ns；5100 = 0.5100ms）。低于它的值名义上是 2 tick，但负载下会翻回 3 tick：
    /// timer-tuning 研究实测 0.5100 在 8 负载进程下出现双峰（1.258/1.504），本机实测 0.5040 在负载下
    /// 直接变成 1.46~1.51ms。所以锁定值不允许低于这条线，宁可放弃一点点中位去换稳定性。
    /// </summary>
    internal const uint CriticalFloor100ns = 5100;

    internal OptimalClockScanResult Scan(OptimalClockScanOptions options, CancellationToken token = default)
        => Scan(options, OptimalClockScanKind.LowestLatency, token);

    internal OptimalClockScanResult Scan(OptimalClockScanOptions options, OptimalClockScanKind kind, CancellationToken token = default)
    {
        PendulumDiagnosticLog.Emit(diagnostics, "scan-start", new { kind = kind.ToString(), options });
        var result = kind == OptimalClockScanKind.StableLatency ? ScanStable(options, token) : ScanLowestLatency(options, token);
        PendulumDiagnosticLog.Emit(diagnostics, "scan-result", new
        {
            result.Success, result.Message, result.Mode, result.ScanKind, result.RequestlessBaselineMs,
            result.BestLock, result.BestTypical, result.BestStable, result.Probes, result.ElapsedSeconds,
            result.LoadThreads, result.SafetyMarginMs, result.StableConditions, candidates = result.Candidates.Count,
        });
        return result;
    }

    /// <summary>最低延迟模式：原算法（三级逼近 + 双赢家 + 头部复测 + 稳定侧窗口取锁）。</summary>
    private OptimalClockScanResult ScanLowestLatency(OptimalClockScanOptions options, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var probes = 0;
        try
        {
            var baseline = api.MeasureRequestlessMs(21);
            var mode = DetectMode(baseline, ref probes, token);
            if (mode == OptimalClockMode.Unknown)
                return OptimalClockScanResult.Failed("无法判定系统计时器语义（无请求进程基线不可读），未扫描");

            // 研究结论：负载不移动最优值，但会把临界点边缘变脆。所以候选与复测都在负载下进行，
            // 否则空闲测出来的"边缘最优"在游戏运行时就会双峰（表现为有时 1.5ms）。
            var load = options.LoadThreads > 0 ? api.StartLoad(options.LoadThreads) : null;
            try
            {
            if (options.LoadThreads > 0 && load is null)
                return OptimalClockScanResult.Failed("无法启动所需 CPU 负载，未退回空闲扫描");
            var candidates = new List<OptimalClockCandidate>();
            var coarse = MeasureRange(StepList(options.From100ns, options.To100ns, 100), options, mode, candidates, ref probes, token, load);
            var seed = PickTypical(coarse) ?? PickTypical(candidates);
            if (seed is null) return OptimalClockScanResult.Failed("粗扫未得到有效候选，未继续");

            var fineCenter = seed.Resolution100ns;
            var fineFrom = fineCenter >= 100 ? fineCenter - 100 : fineCenter;
            MeasureRange(StepList(fineFrom, fineCenter + 100, 10), options, mode, candidates, ref probes, token, load);
            var fineSeed = PickTypical(candidates) ?? seed;
            var finestFrom = fineSeed.Resolution100ns >= 10 ? fineSeed.Resolution100ns - 10 : fineSeed.Resolution100ns;
            MeasureRange(StepList(finestFrom, fineSeed.Resolution100ns + 10, 1), options, mode, candidates, ref probes, token, load);

            var typical = PickTypical(candidates);
            var stable = PickStable(candidates);
            OptimalClockCandidate? lockCandidate = null;
            var verified = new List<OptimalClockCandidate>();
            if (options.Verify && typical is not null && stable is not null)
            {
                var targets = TopTypical(candidates, 3).Concat(TopStable(candidates, 3))
                    .Concat(TopStable(candidates.Where(IsLockEligible), 3))
                    .Select(c => c.Resolution100ns).Distinct().ToList();
                var verifyOptions = options with { Samples = options.VerifySamples, Repeats = options.VerifyRepeats };
                var verifyRows = new List<OptimalClockCandidate>();
                foreach (var resolution in targets)
                {
                    token.ThrowIfCancellationRequested();
                    var row = MeasureCandidate(resolution, verifyOptions, mode, ref probes, load);
                    if (row is not null) verifyRows.Add(row);
                }
                if (verifyRows.Count > 0)
                {
                    verified.AddRange(verifyRows);
                    typical = PickTypical(verifyRows) ?? typical;
                    stable = PickStable(verifyRows) ?? stable;
                }
            }
            lockCandidate = PickLock(options.Verify ? verified : candidates);
            if (lockCandidate is null)
                return OptimalClockScanResult.Failed("未得到满足 0.5100ms 下限及稳定性要求的" + (options.Verify ? "复测" : "测量") + "，未保存锁定值");

            if (typical is null) return OptimalClockScanResult.Failed("扫描未得到有效候选");
            watch.Stop();
            return new OptimalClockScanResult
            {
                Success = true,
                Message = mode == OptimalClockMode.Global
                    ? $"已找到全局钟摆计划（{LoadText(load)}）：锁定 {lockCandidate?.Milliseconds:0.0000} ms（等待 {lockCandidate?.Median:0.###} ms，慢样本 {lockCandidate?.Over130Pct:0.#}%）；典型 {typical.Milliseconds:0.0000} ms，更稳 {stable?.Milliseconds:0.0000} ms"
                    : $"已找到本进程钟摆计划：锁定 {lockCandidate?.Milliseconds:0.0000} ms（按进程语义，不影响其他进程）",
                Mode = mode,
                RequestlessBaselineMs = baseline,
                Candidates = candidates.OrderBy(c => c.Resolution100ns).ToList(),
                BestTypical = typical,
                BestStable = stable,
                BestLock = lockCandidate,
                Verified = verified,
                Probes = probes,
                LoadThreads = load?.Threads ?? 0,
                ScanKind = OptimalClockScanKind.LowestLatency,
                ElapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
            };
            }
            finally { load?.Dispose(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return OptimalClockScanResult.Failed("扫描失败：" + ex.Message);
        }
    }

    // ---------------- 稳定延迟模式（新算法） ----------------

    /// <summary>
    /// 2 tick 的上限（ms）：0.51~0.53ms 时 3 tick 约 1.53~1.59ms、2 tick 约 1.02~1.06ms，
    /// 1.20ms 足以区分两者，又不会把 0.6ms（1.20ms）误判为 3 tick。
    /// </summary>
    internal const double TwoTickCeilingMs = 1.20;

    /// <summary>某候选是否仍处于 2 tick 档（按它自身的 T 归一化，2.5 tick 以内）。</summary>
    internal static bool IsTwoTickBand(OptimalClockCandidate row)
        => row.Resolution100ns > 0 && double.IsFinite(row.Median) && row.Median > 0
            && row.Median <= TwoTickTicksCeiling * (row.Resolution100ns / 10000.0);

    /// <summary>
    /// 最小安全裕量（ms）：「2 tick 的总时长」必须比 1ms 请求多出这么多，
    /// 否则相位抖动/slack 会把等待推回 3 tick（实测 0.5100~0.5180 在轻载下正是这样翻倍）。
    /// </summary>
    internal const double StableMarginFloorMs = 0.03;

    /// <summary>安全裕量上限（ms）：再大的裕量只是白等，超过它就按它取。</summary>
    internal const double StableMarginCapMs = 0.10;

    /// <summary>
    /// 稳定延迟模式的门限，全部按**tick 归一化**（相对候选自身的 T），不是绝对毫秒：
    /// TicksMax = 每个条件的中位最多允许几个 tick（2 tick 是目标，3 tick 就是翻倍）；
    /// CrossTicksMax = 条件之间（负载引起）的中位差异最多允许几个 tick。
    /// 重负载下唤醒延迟上升是正常的调度现象，只要仍停留在 2 tick 档就不算不稳定。
    /// </summary>
    /// <summary>
    /// 稳定延迟模式的门限，全部按**tick 归一化**（相对候选自身的 T），不是绝对毫秒：
    /// TicksMax = 中位最多允许几个 tick（2 tick 是目标，3 tick 就是翻倍）；
    /// InBandRatioMin = 有多少比例的重复必须落在 2 tick 档（多数在档才算稳，单次污染不会一票否决）；
    /// CrossTicksMax = 条件之间（负载引起）的中位差异最多允许几个 tick。
    /// 重负载下唤醒延迟上升是正常的调度现象，只要仍停留在 2 tick 档就不算不稳定。
    /// </summary>
    internal readonly record struct StableLimits(double TicksMax, double InBandRatioMin, double Over130Max, double CrossTicksMax);

    /// <summary>
    /// 验证门限，逐级放宽；放宽仍不通过就失败（绝不返回 3 tick 或临界值）。
    /// 判据以「中位 + 跨重复/跨条件中位极差」为主：2026-11-03 实测表明 120 采样下中位很稳
    /// （0.55ms 在 16 线程负载 1.1146/1.1094/1.1086，极差 0.005；空闲 1.099/1.189/1.103），
    /// 而「慢样本比例」在负载下混入了调度延迟（25~47% 属正常），只能当宽松的兜底门限。
    /// </summary>
    private static readonly StableLimits[] StableLimitSteps =
    [
        new(2.20, 1.00, 50.0, 0.35),
        new(2.35, 0.67, 55.0, 0.50),
        new(2.50, 0.67, 60.0, 0.60),
    ];

    /// <summary>判定「仍处于 2 tick 档」的 tick 上限：3 tick 就是翻倍，2.5 留出重负载下的调度余量。</summary>
    internal const double TwoTickTicksCeiling = 2.5;

    /// <summary>
    /// 稳定延迟模式：目标是「精度尽可能高」+「测得的 Sleep 稳定且低」。
    /// 1) 标准负载下粗扫，找出仍处于 2 tick 的**最细**候选（悬崖）；
    /// 2) 在悬崖附近细化，用实测抖动估算需要多少安全裕量；
    /// 3) 从「满足裕量的最细值」起取若干候选，在**空闲 / 标准负载 / 重载**三种条件下分别验证；
    /// 4) 取通过验证里最细的那个；全部不通过则逐级放宽门限，仍不通过就如实失败（不保存）。
    /// </summary>
    internal OptimalClockScanResult ScanStable(OptimalClockScanOptions options, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var probes = 0;
        try
        {
            var baseline = api.MeasureRequestlessMs(21);
            var mode = DetectMode(baseline, ref probes, token);
            if (mode == OptimalClockMode.Unknown)
                return OptimalClockScanResult.Failed("无法判定系统计时器语义（无请求进程基线不可读），未扫描");

            var standardThreads = options.LoadThreads > 0 ? options.LoadThreads : OptimalClockScanOptions.DefaultLoadThreads;
            var heavyThreads = Math.Min(Environment.ProcessorCount, Math.Max(standardThreads * 2, standardThreads + 2));
            var maxResolution = (uint)Math.Ceiling(5000 + 5000 * StableMarginCapMs);
            var rows = new List<OptimalClockCandidate>();
            double edgeSpread;

            // 阶段 1+2：标准负载下粗扫找悬崖（仍处于 2 tick 的最细值），再在悬崖附近细化以估抖动。
            using (var load = api.StartLoad(standardThreads))
            {
                if (load is null)
                    return OptimalClockScanResult.Failed("无法启动标准 CPU 负载，未退回空闲扫描（空闲测出的临界值在游戏时会翻倍）");
                var coarseOptions = options with { Repeats = 1 };
                var coarse = new List<OptimalClockCandidate>();
                // 初筛覆盖后续允许的完整区间，不能在 0.5300ms 提前宣告无解。
                foreach (var resolution in StepList(options.From100ns, maxResolution, 10))
                {
                    token.ThrowIfCancellationRequested();
                    var row = MeasureCandidate(resolution, coarseOptions, mode, ref probes, load);
                    if (row is null) continue;
                    coarse.Add(row);
                    rows.Add(row);
                }
                var coarseTwoTick = coarse.Where(IsTwoTickBand).ToList();
                if (coarseTwoTick.Count == 0)
                    return OptimalClockScanResult.Failed("标准负载下没有任何候选处于 2 tick，未保存（系统时钟可能已被其他进程拉细）");
                var approx = coarseTwoTick.OrderBy(r => r.Resolution100ns).First();
                var refineFrom = approx.Resolution100ns >= 25 ? approx.Resolution100ns - 25 : options.From100ns;
                var refineOptions = options with { Repeats = Math.Max(2, options.Repeats) };
                var refine = new List<OptimalClockCandidate>();
                foreach (var resolution in StepList(refineFrom, Math.Min(approx.Resolution100ns + 25, maxResolution), 5))
                {
                    token.ThrowIfCancellationRequested();
                    var row = MeasureCandidate(resolution, refineOptions, mode, ref probes, load);
                    if (row is null) continue;
                    refine.Add(row);
                    rows.Add(row);
                }
                var edge = refine.Where(r => IsTwoTickBand(r) && r.Resolution100ns >= CriticalFloor100ns)
                    .OrderBy(r => r.Resolution100ns).FirstOrDefault()
                    ?? coarseTwoTick.Where(r => r.Resolution100ns >= CriticalFloor100ns).OrderBy(r => r.Resolution100ns).FirstOrDefault()
                    ?? approx;

                // 同一边缘再测两个完整批次，取三批极差的中位来确认裕量。
                // 一次异常不直接剪掉有效区间；多批持续抖动仍提高下界，缺测则拒绝。
                var edgeSpreads = new List<double> { edge.Spread };
                for (var confirmation = 0; confirmation < 2; confirmation++)
                {
                    token.ThrowIfCancellationRequested();
                    var confirmed = MeasureCandidate(edge.Resolution100ns, refineOptions, mode, ref probes, load);
                    if (confirmed is null)
                        return OptimalClockScanResult.Failed("悬崖裕量复测不完整，未保存");
                    rows.Add(confirmed);
                    edgeSpreads.Add(confirmed.Spread);
                }
                edgeSpread = edgeSpreads.OrderBy(spread => spread).ElementAt(1);
            }

            // 阶段 3：搜索区间 = [裕量下限对应的最细值, 裕量上限对应的最粗值]。
            // 2T - 1.0ms ≥ 裕量 → T ≥ (1+裕量)/2；裕量下限由悬崖自身抖动决定（抖动越大退让越多）。
            var requiredMargin = Math.Clamp(edgeSpread + StableMarginFloorMs, StableMarginFloorMs, StableMarginCapMs);
            var minResolution = (uint)Math.Ceiling(5000 + 5000 * requiredMargin);
            if (minResolution < CriticalFloor100ns) minResolution = CriticalFloor100ns;
            var grid = StepList(minResolution, maxResolution, 10).ToList();
            var conditions = new (string Name, int Threads)[] { ("空闲", 0), ("标准负载", standardThreads), ("重载", heavyThreads) };

            // 阶段 4：按需测量并缓存；每个候选在空闲/标准/重载三条件下各测 VerifyRepeats 次，
            // 任何条件不达标即整体不通过（不允许拿单条件冒充稳定）。
            var cache = new Dictionary<uint, List<OptimalClockConditionRow>>();
            List<OptimalClockConditionRow> Validate(uint resolution)
            {
                if (cache.TryGetValue(resolution, out var existing)) return existing;
                var conditionRows = new List<OptimalClockConditionRow>();
                // 至少 3 次重复 + 至少 120 采样：本机存在间歇的 0.5ms 请求者，少采样时单次中位会被瞬时污染
                // （实测 0.55ms 用 40 采样曾单次测出 1.405ms，120 采样后中位稳定在 1.10~1.11ms）。
                var validateOptions = options with
                {
                    Samples = Math.Max(120, options.VerifySamples),
                    Repeats = Math.Max(3, options.VerifyRepeats),
                };
                foreach (var condition in conditions)
                {
                    token.ThrowIfCancellationRequested();
                    using var load = condition.Threads > 0 ? api.StartLoad(condition.Threads) : null;
                    if (condition.Threads > 0 && load is null) return conditionRows; // 负载起不来 → 该候选不通过
                    var row = MeasureCandidate(resolution, validateOptions, mode, ref probes, load);
                    if (row is null) return conditionRows; // 任何一轮无效 → 该候选不通过
                    conditionRows.Add(new OptimalClockConditionRow
                    {
                        Name = condition.Name,
                        LoadThreads = condition.Threads,
                        Resolution100ns = resolution,
                        Median = row.Median,
                        Spread = row.Spread,
                        Over130Pct = row.Over130Pct,
                        Runs = row.Runs,
                        RunsInBand = row.RunsInBand,
                    });
                }
                cache[resolution] = conditionRows;
                return conditionRows;
            }
            bool Evaluate(uint resolution, StableLimits limits)
            {
                var conditionRows = Validate(resolution);
                return PassesAllConditions(conditionRows, limits, conditions.Length);
            }

            // 阶段 5：门限逐级放宽；每级门限内**从最细候选线性向上**找第一个通过者。
            // 不用二分：通过/不通过在噪声下并不单调（细的可能通过、粗的反而失败），二分既可能漏掉更细的
            // 合格区间，也容易把边界算错；也不再用「最粗端点必须通过」当前置条件（端点偶发失败不代表无解）。
            // 命中后再检查比它更细一格（网格步长的一半），有合格的就取更细的，保证「精度尽量高」。
            uint? chosen = null;
            StableLimits used = StableLimitSteps[0];
            List<OptimalClockConditionRow> chosenRows = [];
            foreach (var limits in StableLimitSteps)
            {
                token.ThrowIfCancellationRequested();
                uint? hit = null;
                foreach (var resolution in grid)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Evaluate(resolution, limits)) continue;
                    hit = resolution;
                    break;
                }
                if (hit is null) continue; // 这一级门限下网格内没有通过者 → 放宽门限再试
                // 命中点与下一格之间还有 5 的余地：试一次更细的值，合格就用它。
                var finer = hit.Value >= 10 ? hit.Value - 5 : 0;
                var pick = finer >= minResolution && Evaluate(finer, limits) ? finer : hit.Value;
                chosen = pick;
                used = limits;
                chosenRows = Validate(pick);
                break;
            }
            if (chosen is null)
            {
                var coarsest = Validate(grid[^1]);
                var detail = coarsest.Count > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"，最粗的 {grid[^1] / 10000.0:0.0000} ms 为 中位 {coarsest.Max(r => r.Median):0.###} ms（{coarsest.Max(r => r.Median) / (grid[^1] / 10000.0):0.##} tick）、跨条件极差 {CrossSpreadTicks(coarsest):0.##} tick、在档 {coarsest.Min(r => r.RunsInBand)}/{coarsest.Max(r => r.Runs)} 次、慢样本 {coarsest.Max(r => r.Over130Pct):0.#}%")
                    : "";
                return OptimalClockScanResult.Failed("算法2（更激进）没有候选能通过三条件验证（空闲/标准/重载），未保存" + detail);
            }

            var worstMedian = chosenRows.Max(r => r.Median);
            var worstSpread = chosenRows.Max(r => r.Spread);
            var worstOver = chosenRows.Max(r => r.Over130Pct);
            var lockCandidate = new OptimalClockCandidate
            {
                Resolution100ns = chosen.Value,
                Median = Math.Round(worstMedian, 4),
                Mean = Math.Round(chosenRows.Average(r => r.Median), 4),
                P95 = Math.Round(worstMedian, 4),
                Spread = Math.Round(worstSpread, 4),
                Over130Pct = Math.Round(worstOver, 1),
                Runs = chosenRows.Count,
                Samples = options.VerifySamples,
            };
            var margin = Math.Round(chosen.Value / 10000.0 * 2 - 1.0, 4);
            var conditionText = string.Join(" / ", chosenRows
                .OrderBy(r => r.LoadThreads)
                .Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Name} {r.Median:0.###}ms")));

            // 展示赢家（仅供页面参考，不用于锁定）：三条件都测过的候选里最典型/最稳的。
            var validatedRows = cache
                .Where(pair => pair.Value.Count == conditions.Length)
                .Select(pair => new OptimalClockCandidate
                {
                    Resolution100ns = pair.Key,
                    Median = Math.Round(pair.Value.Max(r => r.Median), 4),
                    Mean = Math.Round(pair.Value.Average(r => r.Median), 4),
                    P95 = Math.Round(pair.Value.Max(r => r.Median), 4),
                    Spread = Math.Round(pair.Value.Max(r => r.Spread), 4),
                    Over130Pct = Math.Round(pair.Value.Max(r => r.Over130Pct), 1),
                    Runs = pair.Value.Count,
                    Samples = options.VerifySamples,
                })
                .OrderBy(r => r.Resolution100ns)
                .ToList();

            watch.Stop();
            return new OptimalClockScanResult
            {
                Success = true,
                Message = mode == OptimalClockMode.Global
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"算法2（更激进）（标准负载 {standardThreads} / 重载 {heavyThreads} 线程）：锁定 {chosen.Value / 10000.0:0.0000} ms（安全裕量 {margin:0.0000} ms，三条件下等待 {conditionText}；判据 ≤{used.TicksMax:0.##} tick/在档 ≥{used.InBandRatioMin:0.##}/跨条件 ≤{used.CrossTicksMax:0.##} tick/慢样本 ≤{used.Over130Max:0.#}%）")
                    : string.Create(CultureInfo.InvariantCulture,
                        $"算法2（更激进）（按进程语义，不影响其他进程）：锁定 {chosen.Value / 10000.0:0.0000} ms（安全裕量 {margin:0.0000} ms，等待 {conditionText}）"),
                Mode = mode,
                RequestlessBaselineMs = baseline,
                Candidates = rows.OrderBy(c => c.Resolution100ns).ToList(),
                BestTypical = validatedRows.OrderBy(r => r.Median).ThenBy(r => r.Spread).FirstOrDefault(),
                BestStable = validatedRows.OrderBy(r => r.Spread).ThenBy(r => r.Over130Pct).FirstOrDefault(),
                BestLock = lockCandidate,
                Verified = validatedRows,
                Probes = probes,
                LoadThreads = standardThreads,
                ScanKind = OptimalClockScanKind.StableLatency,
                SafetyMarginMs = margin,
                StableConditions = conditionText,
                StableConditionRows = chosenRows,
                ElapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return OptimalClockScanResult.Failed("算法2（更激进）扫描失败：" + ex.Message);
        }
    }


    /// <summary>
    /// 某候选在全部条件下都必须达标。判据按**tick 归一化**，因为重负载下唤醒延迟上升是正常的调度现象：
    /// ①每个条件的中位都不许进入 3 tick 档（median ≤ TicksMax × T，TicksMax 最大 2.5，3 tick 是 3×T）；
    /// ②每个条件自身跨重复的波动与慢样本比例达标；
    /// ③条件之间的中位差异（负载引起的上升）不超过 CrossTicksMax × T，允许合理上升但不容许接近翻倍。
    /// 缺任何一个条件都算不通过，不能拿单条件冒充稳定。
    /// </summary>
    internal static bool PassesAllConditions(IReadOnlyList<OptimalClockConditionRow> rows, StableLimits limits, int conditionCount = 3)
    {
        if (rows.Count != conditionCount) return false;
        var resolution = rows[0].Resolution100ns;
        if (resolution == 0) return false;
        var periodMs = resolution / 10000.0;
        return rows.All(r => double.IsFinite(r.Median) && r.Median > 0
                && r.Median <= limits.TicksMax * periodMs
                && r.Runs > 0 && r.RunsInBand >= Math.Max(1, (int)Math.Floor(r.Runs * limits.InBandRatioMin + 1e-9))
                && double.IsFinite(r.Over130Pct) && r.Over130Pct is >= 0 and <= 100 && r.Over130Pct <= limits.Over130Max)
            && double.IsFinite(CrossSpread(rows)) && CrossSpread(rows) <= limits.CrossTicksMax * periodMs;
    }

    /// <summary>跨条件的中位极差（最大中位 - 最小中位），以 tick 计。</summary>
    internal static double CrossSpread(IReadOnlyList<OptimalClockConditionRow> rows)
        => rows.Count == 0 ? 0 : rows.Max(r => r.Median) - rows.Min(r => r.Median);

    /// <summary>跨条件的中位极差折算成 tick 数（便于展示与判断"上升了几个 tick"）。</summary>
    internal static double CrossSpreadTicks(IReadOnlyList<OptimalClockConditionRow> rows)
        => rows.Count == 0 || rows[0].Resolution100ns == 0 ? 0 : CrossSpread(rows) / (rows[0].Resolution100ns / 10000.0);

    private static string LoadText(IOptimalClockLoad? load) => load is null ? "空闲测量" : $"{load.Threads} 线程 CPU 负载下测量";

    /// <summary>判定全局/按进程语义：全新持有进程设 1ms，另一个不请求的进程能否看到变细。</summary>
    private OptimalClockMode DetectMode(double? baseline, ref int probes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // 先按全局语义试：一个全新进程持有 1ms，另一个不请求的进程测量。
        var global = api.MeasureGlobal(OptimalClockNative.OneMillisecond100ns, 25, 0);
        probes++;
        PendulumDiagnosticLog.Emit(diagnostics, "scan-global-probe", new { baseline, global });
        if (global is not null && global.Median <= ReachableThresholdMs) return OptimalClockMode.Global;
        // 全局路径未生效时，用「自请求自测」确认按进程语义仍可测。
        var self = api.MeasureSelf(OptimalClockNative.OneMillisecond100ns, 25, 0);
        probes++;
        PendulumDiagnosticLog.Emit(diagnostics, "scan-self-probe", self);
        return self is null ? OptimalClockMode.Unknown : OptimalClockMode.Self;
    }

    private List<OptimalClockCandidate> MeasureRange(IEnumerable<uint> resolutions, OptimalClockScanOptions options,
        OptimalClockMode mode, List<OptimalClockCandidate> sink, ref int probes, CancellationToken token, IOptimalClockLoad? load)
    {
        var rows = new List<OptimalClockCandidate>();
        foreach (var resolution in resolutions)
        {
            token.ThrowIfCancellationRequested();
            var row = MeasureCandidate(resolution, options, mode, ref probes, load);
            if (row is null) continue;
            rows.Add(row);
            sink.Add(row);
        }
        return rows;
    }

    private OptimalClockCandidate? MeasureCandidate(uint resolution, OptimalClockScanOptions options, OptimalClockMode mode, ref int probes, IOptimalClockLoad? load)
    {
        var medians = new List<double>();
        var means = new List<double>();
        var p95s = new List<double>();
        var overs = new List<double>();
        for (var run = 0; run < Math.Max(1, options.Repeats); run++)
        {
            if (load is not null && !load.EnsureActive())
                throw new InvalidOperationException("CPU 负载已失效，扫描已中止");
            var sample = mode == OptimalClockMode.Self
                ? api.MeasureSelf(resolution, options.Samples, options.SpinMs)
                : api.MeasureGlobal(resolution, options.Samples, options.SpinMs);
            probes++;
            PendulumDiagnosticLog.Emit(diagnostics, "scan-sample", new { resolution, run, mode = mode.ToString(), loadThreads = load?.Threads ?? 0, sample });
            if (load is not null && !load.IsActive)
                throw new InvalidOperationException("测量期间 CPU 负载已失效，结果已丢弃");
            // 缺失或无效的一轮必须使整个候选失效，不能丢弃坏轮后以Runs=1/Spread=0冒充稳定。
            if (!ValidSample(sample, options.Samples)) return null;
            medians.Add(sample!.Median);
            means.Add(sample.Mean);
            p95s.Add(sample.P95);
            overs.Add(sample.Over130Pct);
        }
        if (medians.Count == 0) return null;
        var sorted = medians.OrderBy(v => v).ToList();
        var aggregate = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
        var periodMs = resolution / 10000.0;
        return new OptimalClockCandidate
        {
            Resolution100ns = resolution,
            Median = Math.Round(aggregate, 4),
            Mean = Math.Round(means.Average(), 4),
            P95 = Math.Round(p95s.Average(), 4),
            Spread = Math.Round(sorted[^1] - sorted[0], 4),
            Over130Pct = Math.Round(overs.Average(), 1),
            Runs = sorted.Count,
            Samples = options.Samples,
            RunMedians = medians.Select(m => Math.Round(m, 4)).ToList(),
            // 每次重复是否仍落在 2 tick 档（≤2.5 × 自身 T），比极差更抗单次污染。
            RunsInBand = medians.Count(m => m > 0 && m <= TwoTickTicksCeiling * periodMs),
        };
    }

    internal static bool ValidSample(OptimalClockSample? sample, int requestedSamples)
        => sample is not null && sample.Samples >= Math.Max(5, requestedSamples)
            && double.IsFinite(sample.Median) && sample.Median > 0
            && double.IsFinite(sample.Mean) && sample.Mean > 0
            && double.IsFinite(sample.P95) && sample.P95 > 0
            && double.IsFinite(sample.Min) && sample.Min > 0
            && double.IsFinite(sample.Max) && sample.Max >= sample.Min
            && sample.Median >= sample.Min && sample.Median <= sample.Max
            && sample.Mean >= sample.Min && sample.Mean <= sample.Max
            && sample.P95 >= sample.Min && sample.P95 <= sample.Max
            && double.IsFinite(sample.Over130Pct) && sample.Over130Pct is >= 0 and <= 100;

    internal static IEnumerable<uint> StepList(uint from, uint to, uint step)
    {
        if (step == 0) step = 1;
        if (from > to) (from, to) = (to, from);
        for (var value = from; value <= to; value += step) yield return value;
        if ((to - from) % step != 0 && to >= from) yield return to;
    }

    /// <summary>典型延迟排序：中位最低 → 波动最小 → 均值最低（研究目录 Rank-Typical）。</summary>
    internal static OptimalClockCandidate? PickTypical(IEnumerable<OptimalClockCandidate> rows)
        => rows.OrderBy(r => r.Median).ThenBy(r => r.Spread).ThenBy(r => r.Mean).FirstOrDefault();

    /// <summary>稳定性排序：波动最小 → 慢样本最少 → 中位最低（研究目录 Rank-Stable）。</summary>
    internal static OptimalClockCandidate? PickStable(IEnumerable<OptimalClockCandidate> rows)
        => rows.OrderBy(r => r.Spread).ThenBy(r => r.Over130Pct).ThenBy(r => r.Median).FirstOrDefault();

    /// <summary>
    /// 锁定值选择：先取「中位接近最优」的一小段窗口，再在窗口内选慢样本最少、波动最小的值。
    /// 研究结论：临界点边缘的值中位最低但会双峰（有时 2 tick、有时 3 tick），锁在边缘会表现为"有时 1.5ms"。
    /// </summary>
    internal static OptimalClockCandidate? PickLock(IEnumerable<OptimalClockCandidate> rows)
    {
        var list = rows.Where(IsLockEligible).ToList();
        if (list.Count == 0) return null;
        var bestMedian = list.Min(r => r.Median);
        return list.Where(r => r.Median <= bestMedian + 0.05)
            .OrderBy(r => r.Over130Pct).ThenBy(r => r.Spread).ThenBy(r => r.Median).First();
    }

    /// <summary>
    /// 锁定值资格（最低延迟模式）：下限 0.5100ms + 中位落在 2 tick（≤1.3ms）+ 慢样本/波动不超过实测可达范围。
    /// 慢样本门槛原为 ≤10%，但 2026-11-03 实测（8 线程负载、25 采样）显示本机 2 tick 候选普遍 24~56%，
    /// ≤10% 永远不可达（扫描会一律"无合格结果"），故按实测放宽到 50%、波动放宽到 0.15。
    /// 要更强保证"任何负载都不翻 3 tick"，用稳定延迟模式（空闲/标准/重载三条件验证）。
    /// </summary>
    private static bool IsLockEligible(OptimalClockCandidate row)
        => row.Resolution100ns >= CriticalFloor100ns && row.Runs > 0 && row.Samples > 0
            && double.IsFinite(row.Median) && row.Median > 0 && row.Median <= 1.3
            && double.IsFinite(row.Mean) && row.Mean > 0
            && double.IsFinite(row.P95) && row.P95 > 0
            && double.IsFinite(row.Over130Pct) && row.Over130Pct is >= 0 and <= 50
            && double.IsFinite(row.Spread) && row.Spread is >= 0 and <= 0.15;

    internal static IEnumerable<OptimalClockCandidate> TopTypical(IEnumerable<OptimalClockCandidate> rows, int count)
        => rows.OrderBy(r => r.Median).ThenBy(r => r.Spread).ThenBy(r => r.Mean).Take(count);

    internal static IEnumerable<OptimalClockCandidate> TopStable(IEnumerable<OptimalClockCandidate> rows, int count)
        => rows.OrderBy(r => r.Spread).ThenBy(r => r.Over130Pct).ThenBy(r => r.Median).Take(count);
}
