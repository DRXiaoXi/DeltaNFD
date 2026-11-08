using System.Text.Json;
using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

/// <summary>
/// 隔离检查：钟摆计划的扫描方法学（三级逼近/重复/双赢家/复测）、全局生效门禁、持有进程生命周期。
/// 全部使用假测量与假持有进程，不发出任何真实定时器请求、不启动真实子进程。
/// </summary>
internal static class OptimalClockChecks
{
    private static int _checks;
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); _checks++; }

    private sealed class FakeMeasurement : IOptimalClockMeasurementApi
    {
        internal Func<uint, int, int, OptimalClockSample?>? Global;
        internal Func<uint, int, int, OptimalClockSample?>? Self;
        internal double? Requestless = 1.9;
        internal bool FailRequestless;
        internal int RequestlessCalls;
        internal readonly List<uint> Measured = [];

        /// <summary>按「当前活跃负载线程数」给值的测量钩子（用于稳定延迟模式的三条件验证）。</summary>
        internal Func<uint, int, OptimalClockSample?>? Conditioned;

        /// <summary>当前活跃负载线程数（0 = 空闲）。</summary>
        internal int ActiveLoadThreads;

        public OptimalClockSample? MeasureGlobal(uint resolution100ns, int samples, int spinMs)
        {
            Measured.Add(resolution100ns);
            if (Conditioned is not null) return Conditioned(resolution100ns, ActiveLoadThreads);
            return Global is not null ? Global(resolution100ns, samples, spinMs) : Sample(resolution100ns, 1.5);
        }

        public OptimalClockSample? MeasureSelf(uint resolution100ns, int samples, int spinMs)
        {
            Measured.Add(resolution100ns);
            return Self is not null ? Self(resolution100ns, samples, spinMs) : Sample(resolution100ns, 1.5);
        }

        public double? MeasureRequestlessMs(int samples)
        {
            RequestlessCalls++;
            if (FailRequestless) throw new IOException("fake measurement failed");
            return Requestless;
        }

        internal int? LoadThreadsSeen;
        internal int LoadStartCalls;
        internal int LoadDisposeCalls;
        internal int ActiveChecks;
        internal int FailLoadAfter = int.MaxValue;
        internal bool NullLoad;
        internal readonly List<int> LoadRequests = [];

        public IOptimalClockLoad? StartLoad(int threads)
        {
            LoadStartCalls++;
            LoadThreadsSeen = threads;
            LoadRequests.Add(threads);
            if (NullLoad) return null;
            ActiveLoadThreads = Math.Max(ActiveLoadThreads, threads);
            return new FakeLoad(this, threads);
        }

        private sealed class FakeLoad(FakeMeasurement owner, int threads) : IOptimalClockLoad
        {
            public int Threads { get; } = threads;
            public bool EnsureActive() => ++owner.ActiveChecks <= owner.FailLoadAfter;
            public bool IsActive => ++owner.ActiveChecks <= owner.FailLoadAfter;
            public void Dispose()
            {
                owner.LoadDisposeCalls++;
                if (owner.ActiveLoadThreads == threads) owner.ActiveLoadThreads = 0;
            }
        }

        internal static OptimalClockSample Sample(uint resolution, double median, double spread = 0.002, double over = 0, int samples = 50)
            => new()
            {
                Requested100ns = resolution,
                Granted100ns = resolution,
                Current100ns = resolution,
                Median = median,
                Mean = median,
                P95 = median + spread,
                Min = median - spread,
                Max = median + spread,
                Over130Pct = over,
                Samples = samples,
            };
    }

    private sealed class FakeHolder : IOptimalClockHolderApi
    {
        internal bool HoldingValue;
        internal uint Resolution;
        internal uint Granted;
        internal uint Current;
        internal string ErrorValue = "";
        internal bool FailEnsure;
        internal bool FailStop;
        internal int EnsureCalls;
        internal int StopCalls;
        internal List<uint> Ensured = [];
        public bool Holding => HoldingValue;
        public uint Resolution100ns => Resolution;
        public uint Granted100ns => Granted;
        public uint Current100ns => Current;
        public string Error => ErrorValue;
        public bool Ensure(uint resolution100ns)
        {
            EnsureCalls++;
            Ensured.Add(resolution100ns);
            if (FailEnsure) { ErrorValue = "fake holder failure"; return false; }
            HoldingValue = true;
            Resolution = resolution100ns;
            Granted = resolution100ns;
            Current = resolution100ns;
            ErrorValue = "";
            return true;
        }
        public void Stop() { StopCalls++; if (FailStop) throw new IOException("fake stop denied"); HoldingValue = false; }
    }

    /// <summary>按研究结论构造的假测量：0.50 及以下=3 个 tick（1.49ms），临界窗口起=2 个 tick（1.03ms）。</summary>
    private static OptimalClockSample? Plateau(uint resolution, int samples, int spinMs)
    {
        if (resolution <= 5050) return FakeMeasurement.Sample(resolution, 1.49, 0.004, 60, samples);
        if (resolution <= 5150) return FakeMeasurement.Sample(resolution, 1.033, 0.002, 5, samples);
        if (resolution <= 5300) return FakeMeasurement.Sample(resolution, 1.04, 0.003, 8, samples);
        return FakeMeasurement.Sample(resolution, resolution / 10000.0 + 0.5, 0.01, 20, samples);
    }

    internal static void Run()
    {
        _checks = 0;
        RunTimerPolicyChecks();
        var defaults = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Check(!defaults.OptimalClockEnabled, "钟摆计划默认关闭，升级不自动开启");
        Check(defaults.OptimalClockResolution100ns == 5130, "默认锁定值取研究结论的临界窗口上沿 0.5130ms");

        // 1) 三级逼近：粗扫 0.01 → 细扫 0.001 → 精扫 0.0001，候选数符合预期。
        var fake = new FakeMeasurement { Global = Plateau };
        var scanner = new OptimalClockScanner(fake);
        var result = scanner.Scan(new OptimalClockScanOptions { From100ns = 4900, To100ns = 5500, Samples = 20, Repeats = 1, Verify = false });
        Check(result.Success, "扫描应成功：" + result.Message);
        Check(result.Mode == OptimalClockMode.Global, "全局生效时应判定为 Global 模式");
        Check(result.Candidates.Count >= 8, "应产生足够候选，实际 " + result.Candidates.Count);
        Check(result.Candidates.Any(c => c.Resolution100ns % 100 == 0), "粗扫包含 0.01ms 步进候选");
        Check(result.Candidates.Any(c => c.Resolution100ns % 10 == 0 && c.Resolution100ns % 100 != 0), "细扫包含 0.001ms 步进候选");
        Check(result.Candidates.Any(c => c.Resolution100ns % 10 != 0), "精扫包含 0.0001ms 步进候选");
        Check(result.BestTypical is not null && result.BestTypical.Median <= 1.1,
            "典型赢家应落在 2 tick 平台（实际 " + result.BestTypical?.Median + "）");
        Check(result.BestTypical!.Resolution100ns > 5050, "典型赢家必须在临界点之上（不能选 3 tick 区）");

        // 2) 双赢家：典型按中位、稳定按波动，允许不是同一个值。
        var stable = scanner.Scan(new OptimalClockScanOptions { From100ns = 4900, To100ns = 5500, Samples = 20, Repeats = 1, Verify = false });
        Check(stable.BestStable is not null, "应给出稳定赢家");
        Check(stable.BestStable!.Spread <= stable.BestTypical!.Spread + 0.0001, "稳定赢家的波动不应大于典型赢家");
        Check(stable.BestTypical!.Median <= stable.BestStable!.Median + 0.05, "典型赢家的中位应最低（允许并列）");

        // 3) 复测：对头部候选追加采样，并据此重排。
        var verify = scanner.Scan(new OptimalClockScanOptions { From100ns = 5000, To100ns = 5300, Samples = 20, Repeats = 1, Verify = true, VerifySamples = 40, VerifyRepeats = 1 });
        Check(verify.Success && verify.Verified.Count is > 0 and <= 9, "复测候选应去重且不超过 9 个，实际 " + verify.Verified.Count);
        Check(verify.Verified.Select(v => v.Resolution100ns).Distinct().Count() == verify.Verified.Count, "复测候选必须去重");

        // 4) 按进程语义：全局路径不可用时退回 Self 模式。
        var selfFake = new FakeMeasurement
        {
            Global = (_, _, _) => FakeMeasurement.Sample(10000, 15.6, 0.5, 90),
            Self = Plateau,
            Requestless = 15.6,
        };
        var selfResult = new OptimalClockScanner(selfFake).Scan(new OptimalClockScanOptions { From100ns = 5000, To100ns = 5300, Samples = 20, Repeats = 1, Verify = false });
        Check(selfResult.Success && selfResult.Mode == OptimalClockMode.Self, "全局路径不可用时应判定为 Self 模式");
        Check(selfResult.Message.Contains("按进程"), "按进程语义必须在说明中写明只影响本进程");

        // 5) 全部测量失败：如实失败，不编造结论。
        var deadFake = new FakeMeasurement { Global = (_, _, _) => null, Self = (_, _, _) => null, Requestless = null };
        var dead = new OptimalClockScanner(deadFake).Scan(new OptimalClockScanOptions { Samples = 10, Repeats = 1, Verify = false });
        Check(!dead.Success && dead.BestTypical is null, "测量不可用时必须失败而不是给出候选");
        Check(dead.Message.Contains("无法判定") || dead.Message.Contains("未得到"), "失败原因应如实说明：" + dead.Message);

        // 6) StepList：边界与步进正确。
        Check(OptimalClockScanner.StepList(4900, 5500, 100).Count() == 7, "0.01ms 步进候选数应为 7");
        Check(OptimalClockScanner.StepList(5100, 5300, 10).Count() == 21, "0.001ms 步进候选数应为 21");
        Check(OptimalClockScanner.StepList(5120, 5140, 1).Count() == 21, "0.0001ms 步进候选数应为 21");
        Check(OptimalClockScanner.StepList(5500, 4900, 100).First() == 4900, "反向范围应自动纠正");

        // 6a) 扫描必须在 CPU 负载下测量，并在结束后释放负载（研究结论：负载下才能选出真正稳定的值）。
        var loadedFake = new FakeMeasurement { Global = Plateau };
        var loadedScan = new OptimalClockScanner(loadedFake).Scan(new OptimalClockScanOptions
        {
            From100ns = 5000, To100ns = 5300, Samples = 5, Repeats = 1, Verify = false, LoadThreads = 6,
        });
        Check(loadedScan.Success, "负载下扫描应成功：" + loadedScan.Message);
        Check(loadedScan.LoadThreads == 6, "扫描结果应记录负载线程数，实际 " + loadedScan.LoadThreads);
        Check(loadedFake.LoadStartCalls == 1 && loadedFake.LoadThreadsSeen == 6,
            "扫描应启动一次 6 线程负载，实际 " + loadedFake.LoadStartCalls + "/" + loadedFake.LoadThreadsSeen);
        Check(loadedFake.LoadDisposeCalls == 1, "扫描结束必须释放负载，实际释放 " + loadedFake.LoadDisposeCalls);
        Check(loadedScan.Message.Contains("负载"), "扫描结论应写明负载条件：" + loadedScan.Message);

        var idleFake = new FakeMeasurement { Global = Plateau };
        var idleScan = new OptimalClockScanner(idleFake).Scan(new OptimalClockScanOptions
        {
            From100ns = 5000, To100ns = 5300, Samples = 5, Repeats = 1, Verify = false, LoadThreads = 0,
        });
        Check(idleScan.Success && idleScan.LoadThreads == 0, "空闲扫描不应记录负载线程");
        Check(idleFake.LoadStartCalls == 0, "空闲扫描不得启动负载进程");
        Check(OptimalClockScanOptions.DefaultLoadThreads is >= 2 and <= 8, "默认负载线程数应在 2~8 之间：" + OptimalClockScanOptions.DefaultLoadThreads);

        // 6b) 锁定值选择：在最优中位附近取「慢样本最少」的稳定侧，避开临界边缘的双峰值。
        OptimalClockCandidate Cand(uint resolution, double median, double over, double spread) => new()
        {
            Resolution100ns = resolution, Median = median, Mean = median + 0.15, P95 = median + 0.4,
            Spread = spread, Over130Pct = over, Runs = 3, Samples = 50,
        };
        var edge = Cand(5040, 1.02, 40, 0.02);      // 中位最低但双峰（边缘）
        var mid = Cand(5130, 1.03, 6, 0.002);
        var safe = Cand(5180, 1.035, 2, 0.001);
        Check(OptimalClockScanner.PickLock([edge, mid, safe])?.Resolution100ns == 5180,
            "锁定值应选稳定侧（慢样本最少），而不是中位最低的边缘值");
        Check(OptimalClockScanner.PickLock([edge]) is null, "低于硬下限的单候选必须拒绝");
        Check(OptimalClockScanner.PickLock([]) is null, "没有候选时不得给出锁定值");
        Check(OptimalClockScanner.PickLock([Cand(5200, 1.20, 5, 0.002), Cand(5400, 1.10, 1, 0.001)])?.Resolution100ns == 5400,
            "中位明显更差的候选不得进入锁定窗口");
        // 临界点以下即使中位最低、慢样本最少也不得锁定（负载下会翻回 3 tick）。
        Check(OptimalClockScanner.PickLock([Cand(5035, 1.02, 0, 0.001), Cand(5130, 1.03, 4, 0.002)])?.Resolution100ns == 5130,
            "临界点以下的值不得被锁定");
        var fallback = OptimalClockScanner.PickLock([Cand(5035, 1.02, 0, 0.001), Cand(5040, 1.03, 1, 0.002)]);
        Check(fallback is null, "全部候选低于硬下限必须失败");
        Check(OptimalClockScanner.PickLock([Cand(5200, 1.04, 65, .01)]) is null, "全部慢样本超标必须拒绝");
        Check(OptimalClockScanner.PickLock([Cand(5200, 1.04, 0, .20)]) is null, "全部波动超标必须拒绝");
        Check(OptimalClockScanner.PickLock([Cand(5200, double.NaN, 0, 0)]) is null, "NaN不得锁定");
        Check(OptimalClockScanner.PickLock([Cand(4900, 1.01, 0, 0), Cand(5200, 1.10, 0, 0)])?.Resolution100ns == 5200,
            "先执行硬门槛再计算窗口，安全候选不得被不安全最低值挤出");

        var edgeApi = new FakeMeasurement { Global = (r, n, _) => FakeMeasurement.Sample(r, r < 5100 ? (n >= 150 ? 1.48 : 1.02) : 1.06, over: n >= 150 && r < 5100 ? 65 : 0, samples: n) };
        var edgeScan = new OptimalClockScanner(edgeApi).Scan(new OptimalClockScanOptions());
        Check(edgeScan.Success && edgeScan.BestLock?.Resolution100ns >= 5100, "复测必须包含安全候选且不得退回边缘");
        Check(edgeScan.Verified.Any(c => c.Resolution100ns >= 5100), "复测集合必须包含安全侧候选");
        var badVerify = new FakeMeasurement { Global = (r,n,_) => FakeMeasurement.Sample(r, n >= 150 ? 1.48 : 1.04, over: n >= 150 ? 65 : 0, samples: n) };
        Check(!new OptimalClockScanner(badVerify).Scan(new OptimalClockScanOptions()).Success, "全部复测不稳定时不得回退初扫并成功");
        var absentLoad = new FakeMeasurement { Global = Plateau, NullLoad = true };
        Check(!new OptimalClockScanner(absentLoad).Scan(new OptimalClockScanOptions()).Success, "默认负载不可用时不得静默空闲扫描");
        foreach (var expireAt in new[] { 0, 1, 20, 121 })
        {
            var expired = new FakeMeasurement { Global = Plateau, FailLoadAfter = expireAt };
            var failedLoad = new OptimalClockScanner(expired).Scan(new OptimalClockScanOptions());
            Check(!failedLoad.Success && failedLoad.Message.Contains("负载"), "负载在测量前/中/后失效必须失败：" + expireAt);
            Check(expired.LoadDisposeCalls == 1, "负载失效后仍须回收句柄");
        }
        RunCompleteRepeatChecks();
        RunNativeLifecycleChecks();

        // 7) 控制器：Windows 10（不支持）不执行任何锁定。
        var holder = new FakeHolder();
        var controller = new OptimalClockController(holder, new FakeMeasurement(), () => 10000);
        Check(!controller.Apply(5130, supported: false), "不支持系统必须拒绝锁定");
        Check(holder.EnsureCalls == 0 && holder.StopCalls >= 1, "不支持系统不得启动持有进程");
        Check(controller.State.Message.Contains("Windows 11"), "不支持原因应写明仅 Windows 11");

        // 8) 控制器：分辨率越界拒绝。
        holder = new FakeHolder();
        controller = new OptimalClockController(holder, new FakeMeasurement(), () => 10000);
        Check(!controller.Apply(100, supported: true), "过细分辨率必须拒绝");
        Check(!controller.Apply(200_000, supported: true), "过粗分辨率必须拒绝");
        Check(holder.EnsureCalls == 0, "越界时不得启动持有进程");

        // 9) 控制器：持有进程启动失败如实失败。
        holder = new FakeHolder { FailEnsure = true };
        controller = new OptimalClockController(holder, new FakeMeasurement(), () => 10000);
        Check(!controller.Apply(5130, supported: true), "持有进程失败必须如实失败");
        Check(controller.State.HasError && controller.State.Message.Contains("持有"), "失败原因应说明持有进程：" + controller.State.Message);

        // 10) 控制器：系统读数仍停在默认 15.625ms（全局语义未生效/请求被忽略）→ 释放并拒绝。
        holder = new FakeHolder();
        controller = new OptimalClockController(holder, new FakeMeasurement(), () => 156_250);
        Check(!controller.Apply(5130, supported: true), "系统读数未变细必须拒绝");
        Check(holder.Holding == false && holder.StopCalls >= 1, "未生效时必须释放持有进程");
        Check(!controller.State.Enabled && controller.State.HasError, "未生效时不得显示已启用");
        Check(controller.State.Message.Contains("15.6250") || controller.State.Message.Contains("未生效") || controller.State.Message.Contains("未锁定"),
            "说明应指出系统读数未变细：" + controller.State.Message);
        Check(controller.State.Current100ns == 156_250, "快照应记录真实系统读数");

        // 11) 控制器：系统读数真的细到请求值 → 成功，并记录真实读数。
        holder = new FakeHolder();
        controller = new OptimalClockController(holder, new FakeMeasurement(), () => 5130);
        Check(controller.Apply(5130, supported: true), "系统读数变细后应成功锁定");
        Check(controller.State.Holding && controller.State.VerifiedGlobal && controller.State.Enabled, "成功时快照应记录持有与已验证");
        Check(controller.State.Resolution100ns == 5130 && controller.State.Granted100ns == 5130, "快照应记录请求值与授予值");
        Check(controller.State.Current100ns == 5130, "快照应记录真实系统读数");
        Check(controller.State.Message.Contains("0.5130"), "状态文字应写明锁定值：" + controller.State.Message);

        // 11b) 首次读数偏粗、重启持有进程后变细 → 应重试成功（避免与残留请求竞争时误判失败）。
        holder = new FakeHolder();
        var sequence = new Queue<uint>([156_250, 5130]);
        controller = new OptimalClockController(holder, new FakeMeasurement(), () => sequence.Count > 0 ? sequence.Dequeue() : 5130);
        Check(controller.Apply(5130, supported: true), "首次读数偏粗后应重启持有进程并重试成功");
        Check(holder.EnsureCalls >= 2, "重试时应重新启动持有进程，实际 " + holder.EnsureCalls);

        // 12) 监督：读数正常时不重复启动；持有进程消失或读数漂粗时重新拉起。
        var before = holder.EnsureCalls;
        Check(controller.Ensure(enabled: true, resolution100ns: 5130, supported: true), "已持有时监督应返回成功");
        Check(holder.EnsureCalls == before, "已持有时不得重复启动持有进程");
        holder.HoldingValue = false; // 模拟持有进程意外退出
        Check(controller.Ensure(enabled: true, resolution100ns: 5130, supported: true), "持有进程消失时应重新拉起");
        Check(holder.EnsureCalls == before + 1, "重新拉起应只调用一次");

        // 12b) 持有进程还活着、但系统读数被拉粗（请求失效/被忽略）→ 必须重新施加而不是继续显示已锁定。
        holder = new FakeHolder();
        var reads = 0;
        // Apply 的初次核对和三批实际等待复验均正常；随后监督读数漂粗，再施加后恢复。
        controller = new OptimalClockController(holder, new FakeMeasurement(), () => reads++ switch
        {
            <= 3 => 5130u,
            4 => 10000u,
            _ => 5130u,
        });
        Check(controller.Apply(5130, supported: true), "先锁定成功");
        var beforeDrift = holder.EnsureCalls;
        Check(controller.Ensure(enabled: true, resolution100ns: 5130, supported: true), "漂移后监督应重新施加并成功");
        Check(holder.EnsureCalls >= beforeDrift + 1, "漂移时应重启持有进程，实际 " + holder.EnsureCalls);
        Check(controller.State.Current100ns == 5130, "重新施加后快照应记录变细后的读数");

        // 13) 监督：未启用/不支持时不保持。
        Check(!controller.Ensure(enabled: false, resolution100ns: 5130, supported: true), "未启用时监督不得保持");
        Check(!controller.Ensure(enabled: true, resolution100ns: 5130, supported: false), "不支持时监督不得保持");

        // 14) 关闭：释放并清空状态。
        Check(controller.Stop(), "关闭应成功");
        Check(!controller.State.Holding && !controller.State.Enabled, "关闭后不得显示持有或启用");
        Check(holder.Holding == false, "关闭后持有进程必须已停止");

        // 15) 扫描结论记录：写入快照但不改变当前持有值。
        holder = new FakeHolder();
        controller = new OptimalClockController(holder, new FakeMeasurement(), () => 5130);
        Check(controller.Apply(5130, supported: true), "先锁定成功");
        controller.RecordScan(new OptimalClockScanResult
        {
            Success = true,
            Message = "m",
            Mode = OptimalClockMode.Global,
            BestTypical = new OptimalClockCandidate { Resolution100ns = 5100, Median = 1.033, Mean = 1.2, P95 = 1.6, Spread = 0.002, Over130Pct = 5, Runs = 3, Samples = 50 },
            BestStable = new OptimalClockCandidate { Resolution100ns = 5180, Median = 1.035, Mean = 1.2, P95 = 1.6, Spread = 0.001, Over130Pct = 3, Runs = 3, Samples = 50 },
            RequestlessBaselineMs = 1.9,
            LoadThreads = 8,
        });
        Check(controller.State.BestTypical100ns == 5100 && controller.State.BestStable100ns == 5180, "扫描结论应写入快照");
        Check(controller.State.Resolution100ns == 5130, "记录扫描结论不得改变当前持有值");

        var scanMetadata = controller.State;
        Check(controller.Apply(5180, true), "记录扫描后再次Apply应成功");
        Check(controller.State.ScanLoadThreads == 8 && controller.State.ScanUtc == scanMetadata.ScanUtc && controller.State.BestTypical100ns == 5100,
            "Apply必须保留扫描赢家与时间");
        Check(controller.State.Message.Contains("1.9 ms"), "成功文字应显示当次实测，首次也不得为空");
        Check(controller.Stop(), "元数据测试释放应成功");
        Check(controller.State.ScanUtc == scanMetadata.ScanUtc && controller.State.BestStable100ns == 5180,
            "Stop必须保留扫描元数据");
        holder.FailStop = true;
        Check(controller.Apply(5180, true), "故障注入前先持有");
        Check(!controller.Stop() && controller.State.Holding && controller.State.HasError,
            "释放失败必须保留Holding与HasError并返回false");
        Check(!controller.Apply(100, true) && controller.State.Holding && controller.State.HasError,
            "Apply拒绝路径释放失败仍须保留责任");
        Check(!controller.Apply(5180, false) && controller.State.Holding && controller.State.HasError,
            "不支持系统路径释放失败不得假称已释放");
        Check(!controller.Ensure(true, 5180, true) && controller.State.HasError && !controller.State.VerifiedGlobal,
            "监督不得将释放未确认的责任重新标成验证成功");
        holder.FailStop = false;
        Check(controller.Stop() && !controller.State.Holding, "释放失败后必须能够重试");
        var currentSampleApi = new FakeMeasurement { Requestless = 1.04 };
        var sampleController = new OptimalClockController(new FakeHolder(), currentSampleApi, () => 5180);
        Check(sampleController.Apply(5180, true) && sampleController.State.Message.Contains("1.04 ms"),
            "首次Apply成功文字必须使用首次实测");
        currentSampleApi.Requestless = 1.12;
        Check(sampleController.Apply(5180, true) && sampleController.State.Message.Contains("1.12 ms"),
            "再次Apply成功文字必须使用当次而非旧实测");
        Check(sampleController.Stop(), "当次测量测试释放");
        var releaseFailureHolder = new FakeHolder { FailStop = true };
        var failedVerification = new OptimalClockController(releaseFailureHolder, new FakeMeasurement(), () => throw new IOException("fake query failed"));
        Check(!failedVerification.Apply(5180, true) && failedVerification.State.Holding && failedVerification.State.HasError,
            "Apply验证异常且释放失败时必须报告Holding/HasError");
        releaseFailureHolder.FailStop = false;
        Check(failedVerification.Stop(), "Apply失败释放责任可重试");

        // 16) 契约：状态可序列化并包含本功能字段。
        var snapshot = new OptimalClockSnapshot { Supported = true, Holding = true, Resolution100ns = 5130, VerifiedGlobal = true, Message = "m" };
        var json = JsonSerializer.Serialize(new ProcmanStatusSnapshot { OptimalClock = snapshot });
        Check(json.Contains("OptimalClock") && json.Contains("VerifiedGlobal") && json.Contains("5130"), "进程管理状态快照应包含钟摆计划字段");

        // 17) 命令白名单：三条命令已注册且扫描允许长超时。
        Check(DeltaNFD.Backends.BackendModules.IsAllowed(DeltaNFD.Backends.BackendModules.Procman, DeltaNFD.Backends.BackendCommands.ProcmanClockApply), "apply 命令应在白名单");
        Check(DeltaNFD.Backends.BackendModules.IsAllowed(DeltaNFD.Backends.BackendModules.Procman, DeltaNFD.Backends.BackendCommands.ProcmanClockRelease), "release 命令应在白名单");
        Check(DeltaNFD.Backends.BackendModules.IsAllowed(DeltaNFD.Backends.BackendModules.Procman, DeltaNFD.Backends.BackendCommands.ProcmanClockScan), "scan 命令应在白名单");
        Check(!DeltaNFD.Backends.BackendModules.IsAllowed(DeltaNFD.Backends.BackendModules.Procman, "procman.clock.unknown"), "未注册命令不得放行");
        Check(DeltaNFD.Backends.BackendModules.IsMutating(DeltaNFD.Backends.BackendModules.Procman, DeltaNFD.Backends.BackendCommands.ProcmanClockApply), "apply 必须视为修改类");
        Check(!DeltaNFD.Backends.BackendModules.IsMutating(DeltaNFD.Backends.BackendModules.Procman, DeltaNFD.Backends.BackendCommands.ProcmanClockScan), "scan 只是测量，不产生持久修改");

        // 18) 引擎接线：真实 ProcmanEngine + 假持有/假测量，验证 apply/release/监督与状态快照、设置持久化。
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-optimal-clock-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var token = Guid.NewGuid();
            File.WriteAllText(Path.Combine(root, "isolated-test.marker"), token.ToString("D"));
            AppDataPaths.ConfigureIsolatedDataRoot(root, token);
            var engineHolder = new FakeHolder();
            var engineApi = new FakeMeasurement { Requestless = 1.9, Global = Plateau };
            using (var engine = new ProcmanEngine(Path.Combine(root, "changes.json"), Path.Combine(root, "rules.json"),
                clockHolder: engineHolder, clockMeasurement: engineApi, clockCurrentResolution: () => 5130))
            {
                engine.ExperimentalTimerFeaturesForTests = true;
                engine.FrameModeActiveForTests = () => AppSettingsStore.Read().FrameModeActive;
                engine.GlobalPrecisionForTests = () => new() { Supported = true, Readable = true, Configured = true, BootConfirmed = true };
                engine.BackgroundLoopDisabledForTests = true;
                AppSettingsStore.Update(s => { s.FrameModeActive = true; s.OptimalClockScanUtc = DateTimeOffset.UtcNow; s.OptimalClockResolution100ns = 5130; });
                var apply = engine.ApplyOptimalClock(5130);
                Check(apply.Success, "引擎锁定应成功：" + apply.Message);
                var locked = engine.Snapshot().OptimalClock;
                Check(locked.Holding && locked.VerifiedGlobal && locked.Resolution100ns == 5130,
                    "引擎快照应记录已锁定/已验证/锁定值");
                Check(engine.OptimalClockOnceForTests(), "启用中监督 tick 应保持锁定");
                Check(engineHolder.EnsureCalls == 1, "已持有时监督不得重复启动持有进程");
                engineHolder.FailStop = true;
                var savedBeforeScan = AppSettingsStore.Read();
                var measuredBeforeScan = engineApi.Measured.Count;
                var requestlessBeforeScan = engineApi.RequestlessCalls;
                var rejectedScan = engine.ScanOptimalClockAsync(quick: true).GetAwaiter().GetResult();
                Check(!rejectedScan.Success, "扫描前旧请求无法释放必须拒绝");
                Check(engineApi.Measured.Count == measuredBeforeScan && engineApi.RequestlessCalls == requestlessBeforeScan,
                    "释放未确认不得开始任何候选或基线测量");
                var savedAfterScan = AppSettingsStore.Read();
                Check(savedAfterScan.OptimalClockEnabled == savedBeforeScan.OptimalClockEnabled
                    && savedAfterScan.OptimalClockResolution100ns == savedBeforeScan.OptimalClockResolution100ns,
                    "扫描前释放失败不得改变原设置");
                Check(engine.Snapshot().OptimalClock.Holding && engine.Snapshot().OptimalClock.HasError,
                    "引擎扫描拒绝后必须保留持有责任与错误");
                Check(GameTargetService.Default.GetSwitchBlocker().Contains("钟摆计划还原未完成"),
                    "引擎扫描拒绝后必须保留恢复失败门禁");
                AppSettingsStore.Update(s => s.FrameModeActive = false);
                var failedRelease = engine.ReleaseOptimalClock();
                Check(!failedRelease.Success && engine.Snapshot().OptimalClock.Holding
                    && GameTargetService.Default.GetSwitchBlocker().Contains("钟摆计划还原未完成"),
                    "显式Release失败不得清除持有责任或恢复门禁");
                engineHolder.FailStop = false;
                var release = engine.ReleaseOptimalClock();
                Check(release.Success, "引擎释放应成功：" + release.Message);
                Check(!engine.Snapshot().OptimalClock.Holding, "释放后快照不得再显示持有");
                Check(!AppSettingsStore.Read().FrameModeActive, "释放遵循帧格关闭意图");
                Check(!GameTargetService.Default.GetSwitchBlocker().Contains("钟摆计划还原未完成"),
                    "故障解除并确认释放后必须清除本功能恢复失败门禁");
                var recoveredScan = engine.ScanOptimalClockAsync(quick: true).GetAwaiter().GetResult();
                Check(recoveredScan.Success && engineApi.Measured.Count > measuredBeforeScan,
                    "释放恢复后可用注入的稳定假测量扫描，失败扫描不得卡住inprogress：" + recoveredScan.Message);
                Check(AppSettingsStore.Read().OptimalClockResolution100ns >= 5100,
                    "引擎只保存安全侧锁定值");
                AppSettingsStore.Update(s => s.FrameModeActive = true);
                engineApi.FailRequestless = true;
                engineHolder.FailStop = true;
                var failedApply = engine.ApplyOptimalClock(5130);
                Check(!failedApply.Success && engine.Snapshot().OptimalClock.Holding && engine.Snapshot().OptimalClock.HasError,
                    "Apply测量抛异常且释放失败时必须保留Holding/HasError");
                Check(!failedApply.Message.Contains("已释放请求")
                    && GameTargetService.Default.GetSwitchBlocker().Contains("钟摆计划还原未完成"),
                    "Apply异常释放未确认不得冒充已释放或清除恢复失败门禁");
                engineApi.FailRequestless = false;
                engineHolder.FailStop = false;
                AppSettingsStore.Update(s => s.FrameModeActive = false);
                Check(engine.ReleaseOptimalClock().Success && !engine.Snapshot().OptimalClock.Holding,
                    "Apply异常残留的释放责任能够显式重试恢复");
            }
            var orphanChild = new MemoryChild { FailStop = true, ReadTimeout = true };
            var orphanApi = new NativeOptimalClockMeasurementApi("memory-only", _ => orphanChild);
            try { orphanApi.MeasureRequestlessMs(5); } catch (InvalidOperationException) { }
            var orphanHolder = new FakeHolder();
            using (var orphanEngine = new ProcmanEngine(Path.Combine(root, "orphan-changes.json"), Path.Combine(root, "orphan-rules.json"),
                clockHolder: orphanHolder, clockMeasurement: orphanApi, clockCurrentResolution: () => 5180))
            {
                orphanEngine.ExperimentalTimerFeaturesForTests = true;
                orphanEngine.GlobalPrecisionForTests = () => new() { Supported = true, Readable = true, Configured = true, BootConfirmed = true };
                Check(!orphanEngine.ReleaseOptimalClock().Success && orphanEngine.Snapshot().OptimalClock.Holding,
                    "真实native measurement内存子进程未退出时引擎Release必须失败");
                Check(orphanEngine.Snapshot().OptimalClock.HasError
                    && GameTargetService.Default.GetSwitchBlocker().Contains("钟摆计划还原未完成"),
                    "临时探针责任必须进入引擎恢复门禁");
                Check(!orphanEngine.ApplyOptimalClock(5180).Success && orphanHolder.EnsureCalls == 0,
                    "引擎Apply不得越过临时探针清理失败");
                Check(!orphanEngine.ScanOptimalClockAsync(true).GetAwaiter().GetResult().Success && orphanApi.HasPendingCleanup,
                    "扫描前Stop必须核对临时探针责任并拒绝");
                orphanChild.FailStop = false;
                Check(orphanEngine.ReleaseOptimalClock().Success && !orphanEngine.Snapshot().OptimalClock.Holding
                    && !orphanApi.HasPendingCleanup && orphanChild.Disposed,
                    "引擎显式Release重试必须清理临时探针并确认退出");
                Check(!GameTargetService.Default.GetSwitchBlocker().Contains("钟摆计划还原未完成"),
                    "临时探针确认退出后才能清除恢复门禁");
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }


        // 19) 稳定延迟模式：临界点上沿在轻载/重载下会翻回 3 tick，算法应退让到「三条件下都稳」的最细值。
        var stableOptions = new OptimalClockScanOptions { Samples = 20, Repeats = 2, VerifySamples = 20, VerifyRepeats = 2, LoadThreads = 6 };
        static OptimalClockSample ThreeTick(uint res) => FakeMeasurement.Sample(res, 1.53, 0.20, 60, samples: 200);
        static OptimalClockSample TwoTick(uint res) => FakeMeasurement.Sample(res, 1.04, 0.002, 0, samples: 200);
        static OptimalClockSample CoarseTick(uint res) => FakeMeasurement.Sample(res, 1.07, 0.01, 5, samples: 200);

        var backoffFake = new FakeMeasurement { Requestless = 1.9 };
        backoffFake.Conditioned = (res, threads) =>
        {
            if (threads == 0 || threads > 6) return res < 5355 ? ThreeTick(res) : CoarseTick(res); // 空闲/重载：临界上沿翻倍
            if (res <= 5100) return ThreeTick(res);
            return res < 5355 ? TwoTick(res) : CoarseTick(res);
        };
        var backoff = new OptimalClockScanner(backoffFake).Scan(stableOptions, OptimalClockScanKind.StableLatency);
        Check(backoff.Success, "稳定延迟扫描应成功：" + backoff.Message);
        Check(backoff.ScanKind == OptimalClockScanKind.StableLatency, "结果应标记为稳定延迟模式");
        // 网格步长 10 + 命中后下探 5，所以 0.5355 / 0.5360 都算退让到了稳定区。
        Check(backoff.BestLock?.Resolution100ns is 5355 or 5360, "应退让到三条件下都稳的最细值 0.5355/0.5360，实际 " + (backoff.BestLock?.Resolution100ns ?? 0));
        Check(Math.Abs(backoff.SafetyMarginMs - 0.071) < 0.002, "安全裕量应约为 0.071ms（2T-1ms），实际 " + backoff.SafetyMarginMs);
        Check(backoff.StableConditionRows.Count == 3, "三条件都必须有记录，实际 " + backoff.StableConditionRows.Count);
        Check(backoff.StableConditions.Contains("空闲") && backoff.StableConditions.Contains("重载"), "条件说明应含空闲与重载：" + backoff.StableConditions);
        Check(backoff.LoadThreads == 6, "标准负载线程数应为 6，实际 " + backoff.LoadThreads);
        Check(backoffFake.LoadRequests.Contains(6) && backoffFake.LoadRequests.Any(t => t > 6), "应分别启动标准与重载负载：" + string.Join("/", backoffFake.LoadRequests));
        Check(backoff.Message.Contains("算法2（更激进）"), "结论文字应写明稳定延迟模式：" + backoff.Message);

        // 20) 全部条件本来就稳：取搜索区间最细的（裕量下限 0.03 → 0.5150）。
        var easyFake = new FakeMeasurement { Requestless = 1.9 };
        easyFake.Conditioned = (res, _) => res >= 5150 ? TwoTick(res) : ThreeTick(res);
        var easy = new OptimalClockScanner(easyFake).Scan(stableOptions, OptimalClockScanKind.StableLatency);
        Check(easy.Success, "全稳场景应成功：" + easy.Message);
        Check(easy.BestLock?.Resolution100ns == 5150, "应取最细的合格值 0.5150，实际 " + (easy.BestLock?.Resolution100ns ?? 0));

        // 21) 空闲下始终 3 tick：没有候选能通过三条件 → 必须失败且不返回任何锁定值。
        var hopelessFake = new FakeMeasurement { Requestless = 1.9 };
        hopelessFake.Conditioned = (res, threads) => threads == 0 ? ThreeTick(res) : (res >= 5200 ? TwoTick(res) : ThreeTick(res));
        var hopeless = new OptimalClockScanner(hopelessFake).Scan(stableOptions, OptimalClockScanKind.StableLatency);
        Check(!hopeless.Success, "空闲始终 3 tick 时不得给出锁定值：" + hopeless.Message);
        Check(hopeless.BestLock is null, "失败时不得带锁定值");
        Check(hopeless.Message.Contains("未保存"), "失败说明应写明未保存：" + hopeless.Message);

        // 22) 稳定模式也必须拒绝临界点以下：标准负载下低值是 2 tick 假象时，仍不得低于 0.5100。
        var belowFloorFake = new FakeMeasurement { Requestless = 1.9 };
        belowFloorFake.Conditioned = (res, threads) => threads == 0 || threads > 6
            ? (res >= 5200 ? TwoTick(res) : ThreeTick(res))
            : TwoTick(res);
        var belowFloor = new OptimalClockScanner(belowFloorFake).Scan(stableOptions, OptimalClockScanKind.StableLatency);
        Check(belowFloor.Success, "该场景应成功：" + belowFloor.Message);
        Check(belowFloor.BestLock!.Resolution100ns >= 5100, "稳定模式锁定值不得低于 0.5100，实际 " + belowFloor.BestLock!.Resolution100ns / 10000.0);

        // 23) 负载起不来时必须失败，不得退回空闲验证。
        var noLoadFake = new FakeMeasurement { Requestless = 1.9, NullLoad = true };
        noLoadFake.Conditioned = (res, _) => TwoTick(res);
        var noLoad = new OptimalClockScanner(noLoadFake).Scan(stableOptions, OptimalClockScanKind.StableLatency);
        Check(!noLoad.Success, "无法启动负载时不得给出锁定值：" + noLoad.Message);
        Check(noLoad.Message.Contains("负载"), "失败说明应提到负载：" + noLoad.Message);


        // 24) 回归（验收问题 1，按 tick 归一化重做）：
        //    a. 某个条件翻到 3 tick（重载 1.56ms @ T=0.52）必须判不通过；
        //    b. 重负载只是唤醒延迟正常上升（空闲 1.04 → 重载 1.30，仍在 2.5 tick 内）必须判通过。
        var tickFlipFake = new FakeMeasurement { Requestless = 1.9 };
        tickFlipFake.Conditioned = (res, threads) => threads == 0 || threads > 6
            ? FakeMeasurement.Sample(res, 1.56, 0.002, 40, samples: 200)
            : TwoTick(res);
        var tickFlip = new OptimalClockScanner(tickFlipFake).Scan(stableOptions, OptimalClockScanKind.StableLatency);
        Check(!tickFlip.Success, "重载翻到 3 tick 时必须判不稳定：" + tickFlip.Message);
        Check(tickFlip.BestLock is null, "翻到 3 tick 时不得给出锁定值");

        var loadRiseFake = new FakeMeasurement { Requestless = 1.9 };
        loadRiseFake.Conditioned = (res, threads) => threads == 0 ? TwoTick(res)
            : threads > 6 ? FakeMeasurement.Sample(res, 1.30, 0.004, 20, samples: 200)
            : TwoTick(res);
        var loadRise = new OptimalClockScanner(loadRiseFake).Scan(stableOptions, OptimalClockScanKind.StableLatency);
        Check(loadRise.Success, "重负载延迟正常上升（1.04→1.30，仍 2.5 tick 内）应判通过：" + loadRise.Message);
        var loadRiseT = loadRise.BestLock!.Resolution100ns / 10000.0;
        Check(1.30 <= 2.5 * loadRiseT + 1e-9, "选中的 T 必须让重载 1.30ms 仍落在 2.5 tick 内（T=" + loadRiseT + "）");

        // 判据本身的直接断言（tick 归一化，防止上层再次漏用）。
        static List<OptimalClockConditionRow> Rows(uint res, double idle, double standard, double heavy, int inBand = 3) =>
        [
            new() { Name = "空闲", LoadThreads = 0, Resolution100ns = res, Median = idle, Spread = 0.002, Over130Pct = 0, Runs = 3, RunsInBand = inBand },
            new() { Name = "标准负载", LoadThreads = 6, Resolution100ns = res, Median = standard, Spread = 0.002, Over130Pct = 0, Runs = 3, RunsInBand = inBand },
            new() { Name = "重载", LoadThreads = 12, Resolution100ns = res, Median = heavy, Spread = 0.002, Over130Pct = 0, Runs = 3, RunsInBand = inBand },
        ];
        var loose = new OptimalClockScanner.StableLimits(2.50, 0.67, 60.0, 0.60);
        Check(OptimalClockScanner.PassesAllConditions(Rows(5200, 1.04, 1.04, 1.56), loose) == false,
            "重载 1.56ms @0.52ms 是 3 tick，必须判不通过");
        Check(OptimalClockScanner.PassesAllConditions(Rows(5200, 1.04, 1.04, 1.30), loose),
            "重载 1.30ms @0.52ms 是 2.5 tick，属于正常负载上升，必须判通过");
        Check(Math.Abs(OptimalClockScanner.CrossSpreadTicks(Rows(5200, 1.04, 1.04, 1.30)) - 0.5) < 0.02,
            "跨条件差异应折算为 0.5 tick，实际 " + OptimalClockScanner.CrossSpreadTicks(Rows(5200, 1.04, 1.04, 1.30)));
        Check(OptimalClockScanner.PassesAllConditions(Rows(5200, 1.04, 1.04, 1.60), loose) == false,
            "跨条件差 0.6ms（>0.6 tick×0.52）同样必须拒绝");
        Check(OptimalClockScanner.PassesAllConditions(Rows(5200, 1.04, 1.04, 1.30, inBand: 2), loose),
            "3 次重复中 2 次在档（一次被外部污染）应仍判通过");
        Check(OptimalClockScanner.PassesAllConditions(Rows(5200, 1.04, 1.04, 1.30, inBand: 1), loose) == false,
            "3 次重复只有 1 次在档属于真不稳定，必须拒绝");
        Check(OptimalClockScanner.PassesAllConditions(Rows(5200, 1.04, 1.04, 1.30), new OptimalClockScanner.StableLimits(2.50, 1.00, 60.0, 0.60)),
            "最严档（要求全部在档）在三次都稳时也应通过");
        Check(OptimalClockScanner.IsTwoTickBand(new OptimalClockCandidate
        {
            Resolution100ns = 5200, Median = 1.30, Mean = 1.30, P95 = 1.30, Spread = 0.01, Over130Pct = 10, Runs = 3, Samples = 120,
        }), "1.30ms @0.52ms 应被认定为仍在 2 tick 档（2.5 tick 内）");
        Check(!OptimalClockScanner.IsTwoTickBand(new OptimalClockCandidate
        {
            Resolution100ns = 5200, Median = 1.56, Mean = 1.56, P95 = 1.56, Spread = 0.01, Over130Pct = 40, Runs = 3, Samples = 120,
        }), "1.56ms @0.52ms 是 3 tick，不得算作 2 tick 档");

        // 25) 回归（验收问题 2a）：最粗端点失败不得整级跳过，中间合格候选必须被选中。
        var coarseFailFake = new FakeMeasurement { Requestless = 1.9 };
        coarseFailFake.Conditioned = (res, threads) =>
        {
            var good = res is >= 5350 and <= 5450;
            if (threads == 0 || threads > 6) return good ? CoarseTick(res) : ThreeTick(res);
            if (res <= 5100) return ThreeTick(res);
            return good ? TwoTick(res) : TwoTick(res); // 标准负载下全是 2 tick，悬崖很早
        };
        var coarseFail = new OptimalClockScanner(coarseFailFake).Scan(stableOptions, OptimalClockScanKind.StableLatency);
        Check(coarseFail.Success, "最粗端点失败但中间有合格候选时必须成功：" + coarseFail.Message);
        Check(coarseFail.BestLock?.Resolution100ns is >= 5350 and <= 5450,
            "应选中中间合格区间，实际 " + (coarseFail.BestLock?.Resolution100ns ?? 0));

        // 26) 回归（验收问题 2b）：非单调（更细的通过、更粗的失败）不得漏掉更细的稳定区间。
        var holeFake = new FakeMeasurement { Requestless = 1.9 };
        holeFake.Conditioned = (res, threads) =>
        {
            var good = res == 5200 || res >= 5500;
            if (threads == 0 || threads > 6) return good ? CoarseTick(res) : ThreeTick(res);
            if (res <= 5100) return ThreeTick(res);
            return TwoTick(res);
        };
        var hole = new OptimalClockScanner(holeFake).Scan(stableOptions, OptimalClockScanKind.StableLatency);
        Check(hole.Success, "非单调场景应成功：" + hole.Message);
        Check(hole.BestLock?.Resolution100ns == 5200, "必须命中更细的合格点 0.5200，实际 " + (hole.BestLock?.Resolution100ns ?? 0));

        // 二次验收回归：用相同测量面只改变一次重复，避免漏扫/污染/越界修复互相掩盖。
        static FakeMeasurement BoundaryFake(Func<uint, int, int, int, double?> value,
            out List<(uint Resolution, int Samples, int Threads)> measured)
        {
            var calls = new List<(uint Resolution, int Samples, int Threads)>();
            measured = calls;
            var counters = new Dictionary<(uint, int, int), int>();
            var fake = new FakeMeasurement();
            fake.Global = (resolution, samples, _) =>
            {
                var key = (resolution, samples, fake.ActiveLoadThreads);
                var count = counters.GetValueOrDefault(key) + 1;
                counters[key] = count;
                calls.Add(key);
                var median = value(resolution, samples, fake.ActiveLoadThreads, count);
                return median is null ? null : FakeMeasurement.Sample(resolution, median.Value, 0,
                    median > 1.3 ? 100 : 0, samples: 200);
            };
            return fake;
        }
        var boundaryOptions = stableOptions with { Samples = 50, Repeats = 3 };

        // F1：合格边缘位于原初筛上限外，但仍在允许区间内（含最粗端点）。
        foreach (var threshold in new uint[] { 5400, 5500 })
        {
            var lateFake = BoundaryFake((res, _, _, _) => res == 10000 ? 2.0
                : (res < threshold ? 3 : 2) * res / 10000.0, out var lateCalls);
            var late = new OptimalClockScanner(lateFake).Scan(boundaryOptions, OptimalClockScanKind.StableLatency);
            Check(late.Success && late.BestLock?.Resolution100ns == threshold,
                $"初筛必须覆盖允许区间的合格边缘 {threshold}：{late.Message}");
            Check(late.StableConditionRows.Count == 3, "后段候选仍必须通过全部三条件验证");
            Check(lateCalls.Where(c => c.Resolution != 10000).All(c => c.Resolution <= 5500),
                "初筛及边缘细化不得越过允许上限 0.5500ms");
        }
        var outsideFake = BoundaryFake((res, _, _, _) => res == 10000 ? 2.0
            : (res > 5500 ? 2 : 3) * res / 10000.0, out _);
        var outside = new OptimalClockScanner(outsideFake).Scan(boundaryOptions, OptimalClockScanKind.StableLatency);
        Check(!outside.Success && outside.BestLock is null, "允许区间外的候选不得被选中");

        // F2：一次污染不应剪掉稳定区间；持续抖动仍须提高裕量，复测缺失必须拒绝。
        static double BoundaryValue(uint res) => res == 10000 ? 2.0
            : (res is >= 5200 and <= 5400 ? 2 : 3) * res / 10000.0;
        var cleanFake = BoundaryFake((res, _, _, _) => BoundaryValue(res), out _);
        var clean = new OptimalClockScanner(cleanFake).Scan(boundaryOptions, OptimalClockScanKind.StableLatency);
        Check(clean.Success && clean.BestLock?.Resolution100ns == 5200, "无污染对照应选 0.5200ms");
        foreach (var pollutedRepeat in new[] { 4, 7, 10 })
        {
            var changed = 0;
            var pollutedFake = BoundaryFake((res, samples, threads, count) =>
            {
                if (res == 5200 && samples == 50 && threads == 6 && count == pollutedRepeat)
                {
                    changed++;
                    return 1.56;
                }
                return BoundaryValue(res);
            }, out _);
            var polluted = new OptimalClockScanner(pollutedFake).Scan(boundaryOptions, OptimalClockScanKind.StableLatency);
            Check(changed == 1, "反例必须只污染一次重复");
            Check(polluted.Success && polluted.BestLock?.Resolution100ns == clean.BestLock?.Resolution100ns,
                $"一次边缘异常不应排除有效区间（重复 {pollutedRepeat}）：{polluted.Message}");
        }
        var persistentFake = BoundaryFake((res, samples, threads, count) =>
            res == 5200 && samples == 50 && threads == 6 && count > 1 && count % 3 == 1
                ? 1.56 : BoundaryValue(res), out var persistentCalls);
        var persistent = new OptimalClockScanner(persistentFake).Scan(boundaryOptions, OptimalClockScanKind.StableLatency);
        Check(!persistent.Success && persistent.BestLock is null, "持续边缘抖动不能被当成一次污染忽略");
        Check(persistentCalls.Where(c => c.Samples >= 120).All(c => c.Resolution == 5500),
            "持续抖动应保留原有最大裕量下界");
        var incompleteFake = BoundaryFake((res, samples, threads, count) =>
            res == 5200 && samples == 50 && threads == 6 && count == 5 ? null : BoundaryValue(res), out _);
        var incomplete = new OptimalClockScanner(incompleteFake).Scan(boundaryOptions, OptimalClockScanKind.StableLatency);
        Check(!incomplete.Success && incomplete.BestLock is null, "边缘确认缺失不得降低裕量后继续扫描");
        Check(incompleteFake.LoadDisposeCalls == incompleteFake.LoadStartCalls, "边缘确认失败必须释放所有负载句柄");

        // F3：全稳时不能半步越过 0.5150；实测抖动抬高下界时也必须遵守。
        foreach (var jitter in new[] { 0.0, 0.005 })
        {
            var floorFake = BoundaryFake((res, samples, threads, count) => res == 10000 ? 2.0
                : res < 5100 ? 3 * res / 10000.0
                : 2 * res / 10000.0 + (samples == 50 && threads == 6 ? count % 3 * jitter : 0), out _);
            var floor = new OptimalClockScanner(floorFake).Scan(boundaryOptions, OptimalClockScanKind.StableLatency);
            var expectedFloor = jitter == 0 ? 5150u : 5200u;
            Check(floor.Success && floor.BestLock?.Resolution100ns == expectedFloor,
                $"半步细化必须遵守计算下界 {expectedFloor}：{floor.Message}");
            Check(floor.SafetyMarginMs + 1e-9 >= OptimalClockScanner.StableMarginFloorMs + 2 * jitter,
                "最终裕量不得低于已确认抖动对应的要求");
        }

        // 27) 回归（验收问题 3）：下拉框选择与「上次扫描模式」必须是两个字段，切换下拉不得改写历史标注。
        var fresh = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Check(fresh.OptimalClockPreferredScanMode == OptimalClockScanMode.LowestLatency, "下拉框默认应为最低延迟");
        Check(fresh.OptimalClockScanMode == OptimalClockScanMode.LowestLatency, "上次扫描模式默认应为最低延迟");
        AppSettingsStore.Update(s => { s.OptimalClockPreferredScanMode = OptimalClockScanMode.StableLatency; });
        Check(AppSettingsStore.Read().OptimalClockScanMode == OptimalClockScanMode.LowestLatency,
            "只改下拉框选择不得改写「上次扫描模式」");
        Check(AppSettingsStore.Read().OptimalClockPreferredScanMode == OptimalClockScanMode.StableLatency,
            "下拉框选择应已保存");
        // 源码级断言：下拉绑定偏好字段、历史标注读扫描字段、扫描调用传偏好字段。
        var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "src", "DeltaNFD", "Views", "OptimalClockPage.xaml.cs"))) repoRoot = repoRoot.Parent;
        Check(repoRoot is not null, "应能定位源码根目录");
        var pageSource = File.ReadAllText(Path.Combine(repoRoot!.FullName, "src", "DeltaNFD", "Views", "OptimalClockPage.xaml.cs"));
        Check(pageSource.Contains("s.OptimalClockPreferredScanMode = mode)"), "下拉框必须写偏好字段");
        Check(pageSource.Contains("ModeLabel(AppSettingsStore.Read().OptimalClockScanMode)"), "历史标注必须读「上次扫描模式」");
        Check(pageSource.Contains("ScanClockAsync(quick, mode"), "扫描必须传偏好模式");
        Check(!pageSource.Contains("s.OptimalClockScanMode = mode)"), "下拉框不得直接改写「上次扫描模式」");
        AppSettingsStore.Update(s => { s.OptimalClockPreferredScanMode = OptimalClockScanMode.LowestLatency; });

        Console.WriteLine($"钟摆计划检查通过（{_checks} 项）：默认关闭、默认锁定值、三级逼近、双赢家、复测去重、按进程语义回退、测量失败、Win11 门禁、越界拒绝、持有失败、全局未生效释放、全局生效锁定、监督重启、关闭释放、扫描结论记录、引擎接线与设置持久化、契约、命令白名单、稳定延迟模式退让与三条件验证、跨条件极差判据、非单调搜索、模式标注不串位。未发出任何真实定时器请求。");
    }
    private static void RunCompleteRepeatChecks()
    {
        var invalid = new Func<OptimalClockSample, OptimalClockSample?>[]
        {
            _ => null, row => row with { Median = double.NaN }, row => row with { Mean = double.NaN },
            row => row with { P95 = double.PositiveInfinity }, row => row with { Min = 0 },
            row => row with { Max = double.NaN }, row => row with { Over130Pct = double.NaN },
            row => row with { Over130Pct = -1 }, row => row with { Over130Pct = 101 },
            row => row with { Samples = 0 }, row => row with { Samples = -1 }, row => row with { Samples = row.Samples - 1 },
        };
        foreach (var verification in new[] { false, true })
        foreach (var failedRun in new[] { 1, 2 })
        foreach (var corrupt in invalid)
        {
            var repeats = new Dictionary<uint, int>();
            var fake = new FakeMeasurement
            {
                Global = (r, n, _) =>
                {
                    var row = FakeMeasurement.Sample(r, 1.04, samples: n);
                    // 模式探针不属于候选重复；初扫或复测的指定一轮失败。
                    if (r == 10000 || (verification && n != 150)) return row;
                    repeats.TryGetValue(r, out var count);
                    repeats[r] = ++count;
                    return count == failedRun ? corrupt(row) : row;
                },
            };
            var scan = new OptimalClockScanner(fake).Scan(new OptimalClockScanOptions
            {
                From100ns = 5100, To100ns = 5300, Samples = 20, Repeats = 2,
                Verify = verification, VerifySamples = 150, VerifyRepeats = 2, LoadThreads = 0,
            });
            Check(!scan.Success && scan.BestLock is null,
                "指定重复任一次缺失/无效都不得以剩余单轮零波动成功：verify=" + verification + ", run=" + failedRun);
        }
        var valid = new OptimalClockScanner(new FakeMeasurement { Global = Plateau }).Scan(new OptimalClockScanOptions
        {
            From100ns = 5100, To100ns = 5300, Samples = 20, Repeats = 2,
            VerifySamples = 150, VerifyRepeats = 2, LoadThreads = 0,
        });
        Check(valid.Success && valid.Verified.All(c => c.Runs == 2 && c.Samples == 150),
            "完整复测才可成功，所有保存的复测候选必须完成全部指定轮次");
    }

    private sealed class FakeTimerPolicy(OptimalClockPowerState initial) : IOptimalClockTimerPolicyApi
    {
        internal OptimalClockPowerState State = initial;
        internal int Reads, Writes;
        internal int FailReadAt;
        internal bool FailWrite, IgnoreWrite;
        public OptimalClockPowerState Read()
        {
            if (++Reads == FailReadAt) throw new IOException("fake policy read denied");
            return State;
        }
        public void Write(OptimalClockPowerState state)
        {
            Writes++;
            if (FailWrite) throw new System.ComponentModel.Win32Exception(5, "fake policy write denied");
            if (!IgnoreWrite) State = state;
        }
    }

    private static void RunTimerPolicyChecks()
    {
        foreach (var row in new (uint control, uint state, uint expectedControl, uint expectedState, int writes)[]
        {
            (0, 0, 4, 0, 1), (0, 1, 4, 0, 1),
            (1, 1, 5, 1, 1), (1, 0, 5, 0, 1),
            (4, 4, 4, 0, 1), (5, 5, 5, 1, 1),
            (4, 0, 4, 0, 0), (5, 1, 5, 1, 0), (3, 3, 7, 3, 1),
        })
        {
            var api = new FakeTimerPolicy(new(row.control, row.state));
            Check(OptimalClockTimerPolicy.TryHonorRequests(out var error, api) && error.Length == 0,
                "专用holder必须显式保留请求：" + row);
            Check(api.State == new OptimalClockPowerState(row.expectedControl, row.expectedState),
                "只改变IGNORE_TIMER_RESOLUTION，保留其他显式状态，不写系统自动状态：" + row);
            Check(api.Reads == 2 && api.Writes == row.writes, "设置后必须回读，已生效时不重复写策略");
        }
        var deniedRead = new FakeTimerPolicy(new(0, 0)) { FailReadAt = 1 };
        Check(!OptimalClockTimerPolicy.TryHonorRequests(out var readError, deniedRead)
            && deniedRead.Writes == 0 && readError.Contains("read denied"), "读失败不得盲写或假报保留请求成功");
        var deniedWrite = new FakeTimerPolicy(new(0, 0)) { FailWrite = true };
        Check(!OptimalClockTimerPolicy.TryHonorRequests(out var writeError, deniedWrite)
            && writeError.Contains("Win32 5"), "写失败应保留原生错误码");
        var wrongReadback = new FakeTimerPolicy(new(4, 4)) { IgnoreWrite = true };
        Check(!OptimalClockTimerPolicy.TryHonorRequests(out var mismatch, wrongReadback)
            && mismatch.Contains("回读不符"), "API假成功但回读仍忽略请求时必须失败");
        var unreadableAfterWrite = new FakeTimerPolicy(new(0, 0)) { FailReadAt = 2 };
        Check(!OptimalClockTimerPolicy.TryHonorRequests(out _, unreadableAfterWrite), "写后不可读不得假报成功");
        var child = new MemoryChild { Ready = "HELD:false:5170:0:10000:无法保留本进程的精度请求（Win32 5）" };
        using var holder = new OptimalClockHolder("memory-only", _ => child);
        Check(!holder.Ensure(5170) && !holder.Holding && child.HasExited && child.Disposed
            && holder.Error.Contains("Win32 5"), "父进程必须接收策略失败并确认子进程退出");
    }

    private sealed class MemoryChild : IOptimalClockChildProcess
    {
        public bool HasExited { get; private set; }
        internal bool FailStop;
        internal bool Disposed;
        internal bool ReadTimeout;
        internal string Ready = "HELD:true:5180:5180:5180:";
        public void CloseInput() { if (FailStop) throw new IOException("fake close denied"); HasExited = true; }
        public bool WaitForExit(int milliseconds) => HasExited;
        public void Kill() { if (FailStop) throw new IOException("fake kill denied"); HasExited = true; }
        public string? ReadReadyLine() => ReadTimeout ? throw new TimeoutException("fake timeout") : Ready;
        public void Dispose() => Disposed = true;
    }

    private static void RunNativeLifecycleChecks()
    {
        var child = new MemoryChild();
        var native = new OptimalClockHolder("memory-only", _ => child);
        Check(native.Ensure(5180), "内存进程持有成功");
        child.FailStop = true;
        var threw = false;
        try { native.Stop(); } catch (InvalidOperationException) { threw = true; }
        Check(threw && native.Holding && !child.Disposed, "原生holder清理失败不得丢引用或Dispose活进程");
        child.FailStop = false;
        native.Stop();
        Check(!native.Holding && child.HasExited && child.Disposed, "原生holder重试确认退出才清引用");

        double age = 0;
        var loadChild = new MemoryChild();
        var replacementChild = new MemoryChild();
        var restarted = 0;
        using var load = new NativeOptimalClockMeasurementApi.NativeLoad(loadChild, 8,
            () => { restarted++; return new NativeOptimalClockMeasurementApi.NativeLoad(replacementChild, 8, () => null, () => 0); }, () => age);
        Check(load.EnsureActive() && restarted == 0, "负载未到续期时间不应重启");
        age = 61;
        Check(load.EnsureActive() && restarted == 1 && loadChild.HasExited && loadChild.Disposed,
            "60秒后测量前有界续期，确认旧进程退出后才新建");
        age = 180;
        Check(load.IsActive, "续期后活跃校验应使用新负载寿命");
        load.Dispose();
        Check(replacementChild.HasExited && !load.IsActive, "Dispose必须关闭当前续期子进程");
        age = 119;
        var expiredChild = new MemoryChild();
        using var expiredLoad = new NativeOptimalClockMeasurementApi.NativeLoad(expiredChild, 8, () => throw new Exception("不得静默重建过期负载"), () => age);
        Check(!expiredLoad.IsActive && !expiredLoad.EnsureActive(), "超过安全寿命必须失败，不得把已空闲区间掩盖为续期成功");
        age = 61;
        var deniedChild = new MemoryChild { FailStop = true };
        var deniedLoad = new NativeOptimalClockMeasurementApi.NativeLoad(deniedChild, 8, () => throw new Exception("未确认退出不得续期"), () => age);
        threw = false;
        try { deniedLoad.EnsureActive(); } catch (IOException) { threw = true; }
        Check(threw && !deniedChild.Disposed, "旧负载退出失败必须中止续期并保留句柄");
        deniedChild.FailStop = false;
        deniedLoad.Dispose();

        var probeHolder = new MemoryChild { FailStop = true };
        var probeMeasure = new MemoryChild { Ready = "MEAS:1.04:1.04:1.04:1.04:1.04:0:50" };
        var starts = 0;
        var api = new NativeOptimalClockMeasurementApi("memory-only", info =>
        {
            starts++;
            return info.ArgumentList[0] == OptimalClockProbeHost.HoldArgument ? probeHolder : probeMeasure;
        });
        threw = false;
        try { api.MeasureGlobal(5180, 5, 0); } catch (InvalidOperationException) { threw = true; }
        Check(threw && api.PendingCleanupCount == 1 && !probeHolder.Disposed && probeMeasure.Disposed,
            "临时holder清理失败必须抛出、保留引用，不Dispose活进程");
        var pendingHolder = new FakeHolder();
        var pendingController = new OptimalClockController(pendingHolder, api, () => 5180);
        Check(!pendingController.Stop() && pendingController.State.Holding && pendingController.State.HasError,
            "仅临时探针未退出时Controller.Stop也必须失败并保留责任");
        Check(!pendingController.Apply(5180, true) && pendingHolder.EnsureCalls == 0,
            "Apply不得越过未清理临时探针启动新holder");
        Check(!pendingController.Ensure(true, 5180, true) && pendingHolder.EnsureCalls == 0,
            "Ensure不得越过未清理临时探针报告成功");
        var oldStarts = starts;
        threw = false;
        try { api.MeasureRequestlessMs(5); } catch (InvalidOperationException) { threw = true; }
        Check(threw && starts == oldStarts, "待清理holder未退出前不得启动后续测量污染候选");
        probeHolder.FailStop = false;
        Check(pendingController.Stop() && !pendingController.State.Holding && !pendingController.State.HasError,
            "确认临时探针退出后Controller.Stop才成功并清错误");
        Check(api.PendingCleanupCount == 0 && probeHolder.HasExited && probeHolder.Disposed,
            "临时holder清理失败后可重试，确认退出才释放引用");

        foreach (var startup in new[] { false, true })
        {
            var timeoutChild = new MemoryChild { FailStop = true, ReadTimeout = true };
            var timeoutApi = new NativeOptimalClockMeasurementApi("memory-only", _ => timeoutChild);
            threw = false;
            try
            {
                if (startup) timeoutApi.StartLoad(8);
                else timeoutApi.MeasureGlobal(5180, 5, 0);
            }
            catch (InvalidOperationException) { threw = true; }
            Check(threw && timeoutApi.PendingCleanupCount == 1 && !timeoutChild.Disposed,
                "超时后的清理失败不得吞掉：" + (startup ? "负载启动" : "临时holder"));
            timeoutChild.FailStop = false;
            timeoutApi.RetryPendingCleanup();
            Check(timeoutChild.HasExited && timeoutApi.PendingCleanupCount == 0,
                "超时清理责任能够重试：" + startup);
        }
        var scanChild = new MemoryChild { FailStop = true, ReadTimeout = true };
        var scanApi = new NativeOptimalClockMeasurementApi("memory-only", _ => scanChild);
        var failedCleanupScan = new OptimalClockScanner(scanApi).Scan(new OptimalClockScanOptions { LoadThreads = 0 });
        Check(!failedCleanupScan.Success && failedCleanupScan.Message.Contains("释放未确认"),
            "临时探针清理失败必须使扫描失败而非保存候选");
        scanChild.FailStop = false;
        scanApi.RetryPendingCleanup();
    }

}
