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

    private static void VerifyAffinityMaskBoundaries()
    {
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
