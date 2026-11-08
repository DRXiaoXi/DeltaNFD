using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Native;

/// <summary>
/// 原生自检（真实测量）：验证「钟摆计划」的全局锁定确实让**不请求的进程**获得更细的等待粒度，
/// 并验证临界窗口的存在（0.50ms 无改善、0.51~0.53ms 明显改善），以及控制器端到端的锁定/释放。
/// 只使用自建临时子进程；结束前释放全部请求。
/// </summary>
internal static class OptimalClockNativeChecks
{
    private static int _checks;
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); _checks++; }

    internal static void Run()
    {
        _checks = 0;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            Console.WriteLine("钟摆计划原生自检跳过：仅 Windows 11 启用。");
            return;
        }
        var executable = Environment.ProcessPath!;
        var api = new NativeOptimalClockMeasurementApi(executable);
        var baseline = api.MeasureRequestlessMs(41);
        Check(baseline is > 0.5, "可实测无请求进程基线：" + baseline);

        // 1) 0.50ms 落在 3 tick 区：锁它不会改善无请求进程。
        // 产品扫描一律在 CPU 负载下测量（空闲时临界点上沿会翻回 3 tick，2026-11-03 实测已记录：
        // 空闲 0.5100 会测出 1.35~1.49ms），所以这里也在标准负载下对照，每值测两次取更优，
        // 避免把空闲 C-state 相位抖动误判成"锁定无效"。
        double m5000, m5100, m5180;
        using (var load = api.StartLoad(OptimalClockScanOptions.DefaultLoadThreads))
        {
            Check(load is not null, "应能启动标准 CPU 负载用于临界点对照");
            Thread.Sleep(300);
            // 本机存在间歇的第三方 0.5ms 请求者：被污染时系统读数会比我们的请求更细（0.51 会被测成 1.5ms）。
            // 所以每次测量前先核验「系统读数 ≈ 请求值」，不符就重试，最多 3 次。
            static double Best(NativeOptimalClockMeasurementApi a, uint value)
            {
                var expected = value / 10000.0;
                var best = double.MaxValue;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var first = a.MeasureGlobal(value, 41, 0);
                    var reading = ProcessPerf.QueryTimerResolutionMs();
                    var second = a.MeasureGlobal(value, 41, 0);
                    if (first is null || second is null) continue;
                    var clean = reading is { } ms && Math.Abs(ms - expected) <= 0.005;
                    var candidate = Math.Min(first.Median, second.Median);
                    // 污染时的读数会明显偏离 2 tick，取更优值也只用来兜底；干净测量优先。
                    best = Math.Min(best, candidate);
                    if (clean) return candidate;
                }
                Check(best < double.MaxValue, $"{value / 10000.0:0.0000}ms 持有并测量应成功");
                return best;
            }
            m5000 = Best(api, 5000);
            m5100 = Best(api, 5100);
            m5180 = Best(api, 5180);
        }
        Check(m5000 > 1.3, $"0.50ms 在负载下应落在 3 tick 区（实测 {m5000:0.###} ms）");
        Check(m5100 < m5000 - 0.2, $"临界点之上应明显优于 0.50ms（0.50 → {m5000:0.###} ms，0.51 → {m5100:0.###} ms）");
        Check(m5180 < m5000 - 0.2, $"0.518ms 同样应明显优于 0.50ms（{m5180:0.###} ms）");
        Check(m5100 <= 1.3 && m5180 <= 1.3, $"临界窗口上沿应落在 2 tick 平台（{m5100:0.###} / {m5180:0.###} ms）");

        // 3) 真实快速扫描（窄区间）：能选出临界点之上的赢家，且候选含 0.001ms 步进。
        var scanner = new OptimalClockScanner(api);
        var scan = scanner.Scan(new OptimalClockScanOptions
        {
            From100ns = 5000, To100ns = 5300, Samples = 25, Repeats = 2, Verify = false,
        });
        Check(scan.Success, "真实扫描应成功：" + scan.Message);
        Check(scan.Mode == OptimalClockMode.Global, "本机已启用全局计时器语义，应判定 Global：" + scan.Mode);
        Check(scan.BestTypical is not null, "扫描应给出典型赢家");
        Check(scan.BestLock is not null, "扫描应给出实际锁定值");
        // 典型赢家按最低中位，可能落在临界边缘（噪声下单次中位会偏低），所以判定要看锁定值：
        // 锁定值必须避开临界边缘（慢样本少），并落在 2 tick 平台。
        Check(scan.BestLock!.Resolution100ns > 5050,
            "锁定值必须在临界点之上（实际 " + scan.BestLock!.Resolution100ns + "）");
        Check(scan.BestLock.Median <= 1.2, "锁定值等待应落在 2 tick 平台（负载下）：" + scan.BestLock.Median);
        Check(scan.BestLock.Over130Pct <= 50, "锁定值应是低慢样本的稳定侧（负载下）：" + scan.BestLock.Over130Pct + "%");
        // 研究结论：负载不会移动最优值，但会让临界点边缘变脆——所以扫描必须在负载下进行。
        Check(scan.LoadThreads > 0, "真实扫描应在 CPU 负载下进行（实际负载线程 " + scan.LoadThreads + "）");
        Check(scan.Message.Contains("负载"), "扫描结论应写明负载条件：" + scan.Message);
        Check(scan.Candidates.Any(c => c.Resolution100ns % 10 == 0 && c.Resolution100ns % 100 != 0), "真实扫描应包含 0.001ms 步进候选");

        // 3b) 真实稳定延迟模式扫描：必须给出「三条件下都稳」的最细值，而不是临界边缘值。
        var controllerStable = new OptimalClockController(new NativeOptimalClockHolder(executable), api);
        var stableScan = scanner.Scan(new OptimalClockScanOptions
        {
            From100ns = 5000, To100ns = 5300, Samples = 25, Repeats = 2, VerifySamples = 40, VerifyRepeats = 1,
        }, OptimalClockScanKind.StableLatency);
        Check(stableScan.Success, "真实稳定延迟扫描应成功：" + stableScan.Message);
        Check(stableScan.ScanKind == OptimalClockScanKind.StableLatency, "结果应标记为稳定延迟模式");
        Check(stableScan.BestLock is not null, "稳定延迟扫描应给出锁定值");
        Check(stableScan.BestLock!.Resolution100ns >= 5100, "稳定延迟锁定值不得低于 0.5100（实际 " + stableScan.BestLock!.Resolution100ns + "）");
        Check(stableScan.BestLock.Median <= 1.20, "稳定延迟锁定值的三条件最差中位应 ≤1.20ms（实际 " + stableScan.BestLock.Median + "）");
        Check(stableScan.StableConditionRows.Count == 3, "三条件都必须有记录（实际 " + stableScan.StableConditionRows.Count + "）");
        var expectedMargin = stableScan.BestLock!.Resolution100ns / 10000.0 * 2 - 1.0;
        Check(Math.Abs(stableScan.SafetyMarginMs - expectedMargin) < 0.002,
            $"安全裕量应等于 2T-1ms（期望 {expectedMargin:0.0000}，实际 {stableScan.SafetyMarginMs:0.0000}）");
        Check(stableScan.Message.Contains("稳定延迟模式"), "结论应写明稳定延迟模式：" + stableScan.Message);
        Check(stableScan.LoadThreads > 0, "稳定延迟扫描必须在 CPU 负载下进行（实际 " + stableScan.LoadThreads + "）");
        // 最强验证：把稳定模式选出的值真锁上，标准负载下**两次**都必须落在 2 tick（不是取更优者）——
        // 「稳定」模式的承诺是任何一次都不翻回 3 tick。
        var stableValue = stableScan.BestLock!.Resolution100ns;
        var stableLocked = controllerStable.Apply(stableValue, supported: true);
        Check(stableLocked, "稳定模式锁定应成功：" + controllerStable.State.Message);
        using (var load = api.StartLoad(OptimalClockScanOptions.DefaultLoadThreads))
        {
            Check(load is not null, "应能启动 CPU 负载用于稳定模式复测");
            Thread.Sleep(300);
            // 用 120 采样：41 采样时单次中位会被调度延迟污染（实测出现过 1.331ms，而 120 采样稳定在 1.10~1.11ms）。
            // 另外本机有间歇的第三方 0.5ms 请求者：被污染时系统读数会比锁定值更细、等待跳到 1.4~1.5ms，
            // 所以每次测量后核验系统读数是否≈锁定值，不符则重试（最多 3 次）。
            double? stableA = null;
            double? stableB = null;
            var expectedMs = stableValue / 10000.0;
            for (var attempt = 0; attempt < 3 && (stableA is null || stableB is null); attempt++)
            {
                var first = api.MeasureRequestlessMs(120);
                var reading = ProcessPerf.QueryTimerResolutionMs();
                var second = api.MeasureRequestlessMs(120);
                var clean = reading is { } ms && Math.Abs(ms - expectedMs) <= 0.005;
                if (first is null || second is null || !clean) continue;
                stableA = first;
                stableB = second;
            }
            Check(stableA is not null && stableB is not null,
                $"稳定模式锁定值应在未被更细请求污染的两次测量中落在 2 tick（实测 {stableA:0.###}/{stableB:0.###} ms，锁定 {expectedMs:0.0000} ms）");
            Check(stableA is <= 1.30 && stableB is <= 1.30,
                $"稳定模式锁定值在负载下两次都应落在 2 tick（实测 {stableA:0.###}/{stableB:0.###} ms）");
        }
        controllerStable.Stop();


        // 4) 控制器端到端：真实持有进程 + 真实测量 → 锁定成功；关闭后释放。
        var controller = new OptimalClockController(new NativeOptimalClockHolder(executable), api);
        // 本机有间歇的第三方 0.5ms 请求者：被污染时系统读数会比锁定值更细、等待跳到 1.4~1.5ms。
        // 只在「系统读数≈锁定值」的干净窗口里取测量，否则重试（最多 4 次），避免把环境干扰当成锁定失效。
        // 容差 0.005ms：量化误差在 0.002ms 内，而第三方 0.5ms 请求与我们的 0.511 差 0.008ms，能区分开。
        double? CleanMeasure(double expectedMs, int samples)
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var measured = api.MeasureRequestlessMs(samples);
                var reading = ProcessPerf.QueryTimerResolutionMs();
                if (measured is not null && reading is { } ms && Math.Abs(ms - expectedMs) <= 0.005) return measured;
            }
            return null;
        }
        try
        {
            // 锁「稳定侧」值（不是中位最低的临界边缘值）。
            var lockValue = (scan.BestLock ?? scan.BestTypical)!.Resolution100ns;
            Check(lockValue > 5050, "锁定值必须在临界点之上（实际 " + lockValue + "）");
            var locked = controller.Apply(lockValue, supported: true);
            Check(locked, "真实锁定应成功：" + controller.State.Message);
            Check(controller.State.Holding && controller.State.VerifiedGlobal, "锁定后应持有且确认全局生效");
            // 判定标准是「系统当前分辨率真的细到请求值」，而不是「无请求进程看起来还行」。
            Check(controller.State.Current100ns > 0 && controller.State.Current100ns <= lockValue + OptimalClockController.ResolutionTolerance100ns,
                $"锁定后系统读数应细到请求值（请求 {lockValue}，读数 {controller.State.Current100ns}）");
            // 独立复测：不请求的进程应落在 2 tick 平台（约 1.0~1.2ms），不能是 3 tick（约 1.5ms）。
            // 临界窗口附近单次中位可能落在双峰之间（研究已记录），所以复测两次取更优者：
            // 真正锁在 3 tick 的值永远不可能测出 ≤1.2ms，这个判据仍能抓出错误锁定。
            // 120 采样 + 干净窗口过滤：41 采样时单次中位会被调度延迟污染（实测出现过 1.35ms，而 120 采样稳定在 1.04~1.11ms）。
            var lockMs = lockValue / 10000.0;
            var idleA = CleanMeasure(lockMs, 120);
            var idleB = CleanMeasure(lockMs, 120);
            var idleBest = Math.Min(idleA ?? double.MaxValue, idleB ?? double.MaxValue);
            Check(idleA is not null && idleB is not null,
                $"应在干净窗口内取到锁定后的两次测量（{idleA:0.###}/{idleB:0.###} ms，锁定 {lockMs:0.0000} ms）");
            // 判据按 tick 归一化：空闲时 Windows 会合并定时器（dynamic tick），0.52 的锁也可能测出 1.2~1.35ms，
            // 只要没到 3 tick（3×0.52≈1.56）就算仍在 2 tick 档。
            Check(idleBest <= 2.6 * lockMs,
                $"锁定稳定侧后无请求进程应仍在 2 tick 档（≤{2.6 * lockMs:0.###} ms；两次实测 {idleA:0.###}/{idleB:0.###} ms）");
            Check(idleBest < m5000,
                $"锁定值应优于 0.50ms 区（0.50 → {m5000:0.###} ms，锁定 → {idleBest:0.###} ms）");
            // 最强的验证：锁定值在**CPU 负载下**仍必须是 2 tick（约 1.04ms），不能掉回 3 tick（约 1.5ms）。
            using (var load = api.StartLoad(OptimalClockScanOptions.DefaultLoadThreads))
            {
                Check(load is not null, "应能启动 CPU 负载用于负载下复测");
                Thread.Sleep(300);
                var loadA = CleanMeasure(lockMs, 120);
                var loadB = CleanMeasure(lockMs, 120);
                var loadBest = Math.Min(loadA ?? double.MaxValue, loadB ?? double.MaxValue);
                Check(loadA is not null && loadB is not null,
                    $"负载下应在干净窗口内取到两次测量（{loadA:0.###}/{loadB:0.###} ms，锁定 {lockMs:0.0000} ms）");
                Check(loadBest <= 2.6 * lockMs,
                    $"锁定值在 {OptimalClockScanOptions.DefaultLoadThreads} 线程负载下应仍在 2 tick 档（≤{2.6 * lockMs:0.###} ms；两次实测 {loadA:0.###}/{loadB:0.###} ms）");
            }
            var statusAfterLock = ProcessPerf.QueryTimerResolutionMs();
            Check(statusAfterLock is { } sysLock && sysLock <= 0.55,
                $"锁定后系统读数应变细：{statusAfterLock}");
            Check(controller.Ensure(enabled: true, resolution100ns: controller.State.Resolution100ns, supported: true), "监督应保持已锁定状态");
        }
        finally
        {
            controller.Stop();
        }
        Check(!controller.State.Holding, "关闭后不得再持有");
        Thread.Sleep(400);
        var afterRelease = ProcessPerf.QueryTimerResolutionMs();
        Check(afterRelease is not null, "释放后仍可读取系统读数");
        var releasedRequestless = api.MeasureRequestlessMs(41);
        Check(releasedRequestless is > 1.3, $"释放后无请求进程应回到较粗粒度（实测 {releasedRequestless:0.###} ms）");

        Console.WriteLine($"钟摆计划原生自检通过（{_checks} 项）：基线 {baseline:0.###} ms → 0.50ms 锁定 {m5000:0.###} ms（无改善）→ 0.51ms {m5100:0.###} ms / 0.518ms {m5180:0.###} ms（临界窗口）→ 锁定值 {scan.BestLock!.Resolution100ns / 10000.0:0.0000} ms（等待 {scan.BestLock.Median:0.###} ms，慢样本 {scan.BestLock.Over130Pct:0.#}%）→ 真实锁定/释放成功。仅自建临时子进程。");
    }
}
