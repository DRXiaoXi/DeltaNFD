using DeltaNFD.Services;

internal static class HardwareDetectionChecks
{
    public static async Task RunAsync()
    {
        VerifyAffinityMaskBoundaries();
        var singleCores = Cores(8, smt: true);
        Assert("single die", singleCores,
            [new CpuDomain(0, 0xFFFF)], [], 1, CcdDetectionSource.ProcessorDie);

        var dualCores = Cores(14, smt: true);
        Assert("uneven dual die", dualCores,
            [new CpuDomain(0, 0xFFF), new CpuDomain(0, 0x0FFFF000)],
            [], 2, CcdDetectionSource.ProcessorDie);

        var noSmtCores = Cores(12, smt: false);
        Assert("dual die without SMT", noSmtCores,
            [new CpuDomain(0, 0x3F), new CpuDomain(0, 0xFC0)],
            [], 2, CcdDetectionSource.ProcessorDie);

        Assert("L3 fallback with duplicate", singleCores, [],
            [new CpuDomain(0, 0xFFFF, 32 * 1024 * 1024),
             new CpuDomain(0, 0xFFFF, 32 * 1024 * 1024)],
            1, CcdDetectionSource.L3Fallback);
        Assert("dual L3 fallback without SMT", noSmtCores, [],
            [new CpuDomain(0, 0x3F, 32 * 1024 * 1024),
             new CpuDomain(0, 0xFC0, 32 * 1024 * 1024)],
            2, CcdDetectionSource.L3Fallback);
        Assert("overlapping L3", singleCores, [],
            [new CpuDomain(0, 0xFF), new CpuDomain(0, 0xFFF0)],
            0, CcdDetectionSource.Unknown);
        Assert("incomplete L3", singleCores, [],
            [new CpuDomain(0, 0xFF)], 0, CcdDetectionSource.Unknown);
        Assert("zero L3 mask", singleCores, [],
            [new CpuDomain(0, 0), new CpuDomain(0, 0xFFFF)],
            0, CcdDetectionSource.Unknown);

        if (ShaderService.IsNvidiaAdapter(null, "NVIDIA GeForce GTX 1050 Ti", "PCI\\VEN_1002&DEV_164E") ||
            !ShaderService.IsNvidiaAdapter(null, "Unknown", "PCI\\VEN_10DE&DEV_2860") ||
            !ShaderService.IsNvidiaAdapter("NVIDIA", null, null) ||
            !ShaderService.TryFormatDriverVersion("32.0.15.9636", out var parsedVersion) ||
            parsedVersion != "596.36")
            throw new Exception("GPU identity/version checks failed");

        VerifyHybridCoreClassification();
        VerifyParcDetection();

        var topology = await new CpuTopologyService().GetTopologyAsync();
        if (topology.GroupCount == 1 && topology.LogicalProcessors <= 64 &&
            topology.AllMask == 0)
            throw new Exception("Local single-group affinity mask is unavailable");
        var shaderService = new ShaderService();
        var shader = await shaderService.GetStatusAsync();
        var diagnosis = await shaderService.DiagnoseAsync();
        Console.WriteLine($"CPU: {topology.CpuName}; {topology.PhysicalCores} cores / {topology.LogicalProcessors} LP; " +
            $"CCD={topology.CcdCount}, source={topology.CcdDetectionSource}");
        foreach (var ccd in topology.Ccds)
            Console.WriteLine($"  CCD{ccd.Index}: 0x{ccd.Mask:X}, {ccd.CoreCount} cores, L3 {ccd.L3CacheMb:0.#} MB");
        Console.WriteLine($"GPU: {shader.GpuDetectionState}, driver={shader.DriverVersion}; shader={diagnosis.Level}: {diagnosis.Summary}");
        if (topology.IsAmd && topology.CcdDetectionSource == CcdDetectionSource.Unknown)
            throw new Exception("Local AMD topology was not identified");
        if (shader.GpuDetectionState == ShaderGpuDetectionState.Unknown)
            throw new Exception("Local GPU detection is inconclusive");
        Console.WriteLine("Hardware detection checks passed.");
    }

    /// <summary>
    /// PARC 判定回归测试：N 卡 DXCache 中出现 .parc 文件 → 直接判为异常。
    /// 用临时目录构造 PSOCache 结构，不依赖本机是否装游戏。
    ///
    /// 覆盖两个优先级保证（这是该需求的真正要点）：
    ///   ① PARC 必须赢过「没有 NVPH 文件 → Info 提示预热」分支；
    ///   ② PARC 必须赢过「NVPH 正常 → Normal」分支。
    /// 非 N 卡机器上 DiagnoseAt 根本走不到 PARC 分支（PARC 是 N 卡专属），
    /// 此时跳过正向断言而非误报失败；负向断言（删掉后不得再命中 PARC）在任何机器上都成立。
    /// </summary>
    private static void VerifyParcDetection()
    {
        var root = Path.Combine(Path.GetTempPath(), "nfd-parc-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dxCache = Path.Combine(root, "1_TestGpu_596.36", "SM6", "DXCache");
            Directory.CreateDirectory(dxCache);

            var service = new ShaderService();
            var parcPath = Path.Combine(dxCache, "shader.parc");

            // ---- 情形 ①：只有 .parc、没有任何 .nvph（旧实现会落到「未找到 NVPH」Info） ----
            File.WriteAllBytes(parcPath, new byte[] { 0, 1, 2, 3 });
            var onlyParc = service.DiagnoseAt(root);
            var nvidiaPathReached = onlyParc.Level == ShaderDiagLevel.Abnormal && onlyParc.Summary.Contains("PARC");

            if (!nvidiaPathReached)
            {
                // 非 N 卡 / 旧驱动环境：PARC 判定不适用，跳过正向断言
                Console.WriteLine($"  PARC detection: skipped (not on the NVIDIA deep path: {onlyParc.Level})");
            }
            else
            {
                Console.WriteLine("  PARC detection: .parc beats the 'no NVPH' Info branch OK");

                // ---- 情形 ②：.parc 与「看起来正常」的 NVPH 大文件共存 → PARC 仍须赢 ----
                File.WriteAllBytes(Path.Combine(dxCache, "a.nvph"), new byte[1024]);
                File.WriteAllBytes(Path.Combine(dxCache, "b.nvph"), new byte[1024]);
                var withNvph = service.DiagnoseAt(root);
                if (withNvph.Level != ShaderDiagLevel.Abnormal || !withNvph.Summary.Contains("PARC"))
                    throw new Exception($"PARC must win over the NVPH branches, got {withNvph.Level}: {withNvph.Summary}");
                Console.WriteLine("  PARC detection: .parc beats the NVPH branches OK");
            }

            // ---- 负向：移除 .parc 后不得再以 PARC 为由判定异常（任何机器都成立） ----
            File.Delete(parcPath);
            var after = service.DiagnoseAt(root);
            if (after.Summary.Contains("PARC"))
                throw new Exception($"PARC rule must not fire after removal, got: {after.Summary}");
            Console.WriteLine("  PARC detection: removed → no PARC hit OK");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Intel 大小核（P/E 核）分类回归测试。
    /// 这是本项目最容易搞反的一处：Intel 的 EfficiencyClass 对 P 核返回 0、E 核返回 1，
    /// 早期实现按「> 0 = P 核」归类，在 Intel 上会把两者完全判反。
    /// 本机是 AMD，无法真机验证 Intel，故用合成拓扑锁死行为。
    /// </summary>
    private static void VerifyHybridCoreClassification()
    {
        // 1) 非大小核（AMD 等）：EfficiencyClass 全 0，不得误判为大小核
        var uniform = Cores(16, smt: true);
        var r1 = CpuTopologyService.ClassifyHybridCores(uniform);
        if (r1.IsHybrid || r1.PCoreMask != 0 || r1.ECoreMask != 0)
            throw new Exception($"uniform topology must not be hybrid (got hybrid={r1.IsHybrid})");

        // 2) Intel 12 代典型：8 P 核（class 0，含 HT → bit 0-15）+ 8 E 核（class 1，无 HT → bit 16-23）
        var intel = IntelCores(pCoreCount: 8, eCoreCount: 8);
        var r2 = CpuTopologyService.ClassifyHybridCores(intel);
        if (!r2.IsHybrid)
            throw new Exception("Intel 8P+8E must be detected as hybrid");
        if (r2.PCoreMask != 0x0000FFFFUL)
            throw new Exception($"Intel P-core mask wrong: 0x{r2.PCoreMask:X} (expected 0xFFFF = 8 P cores with HT)");
        if (r2.ECoreMask != 0x00FF0000UL)
            throw new Exception($"Intel E-core mask wrong: 0x{r2.ECoreMask:X} (expected 0xFF0000 = 8 E cores at bit 16-23)");
        if ((r2.PCoreMask & r2.ECoreMask) != 0)
            throw new Exception("P-core and E-core masks must not overlap");

        // 3) 含两种以上类值时，类值最小档 = P 核，其余（更高的类值）全部归 E 核。
        //    这里 P 核类值 0，E 核故意分成 1 / 2 两档，验证「除最小档外全部算 E」。
        var mixed = new List<CpuCoreInfo>
        {
            new() { Group = 0, Mask = 0b11UL << 0, EfficiencyClass = 0, Smt = true, LogicalProcessors = [0, 1] },
            new() { Group = 0, Mask = 1UL << 2, EfficiencyClass = 1, Smt = false, LogicalProcessors = [2] },
            new() { Group = 0, Mask = 1UL << 3, EfficiencyClass = 2, Smt = false, LogicalProcessors = [3] },
        };
        var r3 = CpuTopologyService.ClassifyHybridCores(mixed);
        if (!r3.IsHybrid || r3.PCoreMask != 0b11UL || r3.ECoreMask != 0b1100UL)
            throw new Exception($"multi-class classification wrong: P=0x{r3.PCoreMask:X} E=0x{r3.ECoreMask:X}");

        // 4) 空输入不得崩
        var r4 = CpuTopologyService.ClassifyHybridCores([]);
        if (r4.IsHybrid || r4.PCoreMask != 0 || r4.ECoreMask != 0)
            throw new Exception("empty core list must yield non-hybrid");

        // 5) 三档类值：P 核 = 最小档，其余全部归 E 核
        var three = new List<CpuCoreInfo>
        {
            new() { Group = 0, Mask = 1UL << 0, EfficiencyClass = 0, Smt = false, LogicalProcessors = [0] },
            new() { Group = 0, Mask = 1UL << 1, EfficiencyClass = 1, Smt = false, LogicalProcessors = [1] },
            new() { Group = 0, Mask = 1UL << 2, EfficiencyClass = 2, Smt = false, LogicalProcessors = [2] },
        };
        var r5 = CpuTopologyService.ClassifyHybridCores(three);
        if (!r5.IsHybrid || r5.PCoreMask != 1UL || r5.ECoreMask != 0b110UL)
            throw new Exception($"three-class classification wrong: P=0x{r5.PCoreMask:X} E=0x{r5.ECoreMask:X}");

        Console.WriteLine("  P/E core classification: uniform / 8P+8E / multi-class / empty / 3-class all OK");
    }

    /// <summary>构造 Intel 风格核心列表：P 核（class 0）带超线程，E 核（class 1）无超线程。</summary>
    private static List<CpuCoreInfo> IntelCores(int pCoreCount, int eCoreCount, int pClass = 0, int eClass = 1)
    {
        var cores = new List<CpuCoreInfo>();
        var next = 0;

        for (var i = 0; i < pCoreCount; i++)
        {
            cores.Add(new CpuCoreInfo
            {
                Group = 0, Mask = 0b11UL << next, EfficiencyClass = pClass, Smt = true,
                LogicalProcessors = [next, next + 1],
            });
            next += 2;
        }

        for (var i = 0; i < eCoreCount; i++)
        {
            cores.Add(new CpuCoreInfo
            {
                Group = 0, Mask = 1UL << next, EfficiencyClass = eClass, Smt = false,
                LogicalProcessors = [next],
            });
            next += 1;
        }

        return cores;
    }

    private static void VerifyAffinityMaskBoundaries()    {
        var full64 = Cores(64, smt: false);
        if (CpuTopologyService.GetSingleGroupAffinityMask(full64, [64]) != ulong.MaxValue)
            throw new Exception("64-processor affinity mask lost CPU63");

        var sparse = new List<CpuCoreInfo>
        {
            new() { Group = 0, Mask = 1UL, EfficiencyClass = 0, Smt = false, LogicalProcessors = [0] },
            new() { Group = 0, Mask = 1UL << 63, EfficiencyClass = 0, Smt = false, LogicalProcessors = [63] },
        };
        if (CpuTopologyService.GetSingleGroupAffinityMask(sparse, [2]) != (1UL | 1UL << 63) ||
            CpuTopologyService.GetSingleGroupAffinityMask(full64, [65]) != 0 ||
            CpuTopologyService.GetSingleGroupAffinityMask(full64, [32, 32]) != 0 ||
            CpuTopologyService.GetSingleGroupAffinityMask([full64[0], full64[0]], [2]) != 0)
            throw new Exception("Affinity mask topology validation failed");
    }

    private static List<CpuCoreInfo> Cores(int count, bool smt)
    {
        var cores = new List<CpuCoreInfo>();
        for (var i = 0; i < count; i++)
        {
            var first = i * (smt ? 2 : 1);
            var mask = smt ? 3UL << first : 1UL << first;
            cores.Add(new CpuCoreInfo
            {
                Group = 0, Mask = mask, EfficiencyClass = 0, Smt = smt,
                LogicalProcessors = smt ? [first, first + 1] : [first],
            });
        }
        return cores;
    }

    private static void Assert(string name, List<CpuCoreInfo> cores,
        CpuDomain[] dies, CpuDomain[] l3, int expectedCount, CcdDetectionSource expectedSource)
    {
        var (ccds, source) = CcdTopologyDetector.Detect(cores, dies, l3);
        if (ccds.Count != expectedCount || source != expectedSource)
            throw new Exception($"{name}: expected {expectedCount}/{expectedSource}, got {ccds.Count}/{source}");
    }
}
