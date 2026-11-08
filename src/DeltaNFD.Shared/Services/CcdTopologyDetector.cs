using System.Numerics;

namespace DeltaNFD.Services;

internal readonly record struct CpuDomain(int Group, ulong Mask, int CacheSizeBytes = 0);

internal static class CcdTopologyDetector
{
    internal static (List<CcdInfo> Ccds, CcdDetectionSource Source) Detect(
        IReadOnlyList<CpuCoreInfo> cores,
        IReadOnlyList<CpuDomain> dies,
        IReadOnlyList<CpuDomain> l3Domains,
        AmdCcdModel? model = null)
    {
        // 型号只能证明出厂设计，不能证明 Windows 当前的 CPU 编号或启用的核心数。
        if (!ValidCores(cores) || model is not null && cores.Count > model.PhysicalCores)
            return ([], CcdDetectionSource.Unknown);

        var hasDies = TryBuild(cores, dies, l3Domains, out var dieCcds) && FitsModel(dieCcds, model);
        var l3Ccds = new List<CcdInfo>();
        var hasL3 = model?.L3IsCcd != false &&
            !l3Domains.GroupBy(d => (d.Group, d.Mask)).Any(g => g.Select(d => d.CacheSizeBytes).Distinct().Count() != 1) &&
            l3Domains.All(d => d.CacheSizeBytes == 32 * 1024 * 1024 ||
                d.CacheSizeBytes == 96 * 1024 * 1024 && (model is null || model.MaxL3Mb == 96)) &&
            TryBuild(cores, l3Domains, l3Domains, out l3Ccds) && FitsModel(l3Ccds, model);

        // 某些固件把整个封装报告为一个 Die。已知 Zen3+ 型号可用实际 L3 覆盖纠正，
        // 但不能把 Zen2 的一个 CCX（16MB L3）当成一个 CCD。
        if (hasL3 && (!hasDies || model is not null && l3Ccds.Count > dieCcds.Count))
            return (l3Ccds, CcdDetectionSource.L3Fallback);
        if (hasDies) return (dieCcds, CcdDetectionSource.ProcessorDie);
        return ([], CcdDetectionSource.Unknown);
    }

    private static bool FitsModel(List<CcdInfo> ccds, AmdCcdModel? model)
        => model is null || ccds.Count <= model.CcdCount;

    private static bool ValidCores(IReadOnlyList<CpuCoreInfo> cores)
    {
        if (cores.Count == 0) return false;
        var covered = new Dictionary<int, ulong>();
        foreach (var core in cores)
        {
            if (core.Group < 0 || core.Mask == 0 || (covered.GetValueOrDefault(core.Group) & core.Mask) != 0)
                return false;
            covered[core.Group] = covered.GetValueOrDefault(core.Group) | core.Mask;
        }
        return true;
    }

    private static bool TryBuild(
        IReadOnlyList<CpuCoreInfo> cores,
        IReadOnlyList<CpuDomain> domains,
        IReadOnlyList<CpuDomain> l3Domains,
        out List<CcdInfo> ccds)
    {
        ccds = [];
        if (domains.Count == 0 || domains.Any(d => d.Group < 0 || d.Mask == 0)) return false;
        var ordered = domains.DistinctBy(d => (d.Group, d.Mask))
            .OrderBy(d => d.Group).ThenBy(d => BitOperations.TrailingZeroCount(d.Mask)).ToList();
        var covered = new Dictionary<int, ulong>();
        foreach (var domain in ordered)
        {
            var prior = covered.GetValueOrDefault(domain.Group);
            if ((prior & domain.Mask) != 0) return false;
            covered[domain.Group] = prior | domain.Mask;
            var memberCores = cores.Where(c => c.Group == domain.Group && (c.Mask & domain.Mask) != 0).ToList();
            if (memberCores.Count is 0 or > 8 || memberCores.Any(c => (c.Mask & domain.Mask) != c.Mask))
                return false;
            var l3Bytes = l3Domains.Where(c => c.Group == domain.Group && c.Mask != 0 && (c.Mask & domain.Mask) == c.Mask)
                .DistinctBy(c => (c.Group, c.Mask)).Sum(c => (long)Math.Max(0, c.CacheSizeBytes));
            ccds.Add(new CcdInfo
            {
                Index = ccds.Count, Group = domain.Group, Mask = domain.Mask,
                CoreCount = memberCores.Count, LogicalCount = BitOperations.PopCount(domain.Mask),
                L3CacheMb = Math.Round(l3Bytes / 1024.0 / 1024.0, 1),
            });
        }
        var coreCoverage = cores.GroupBy(c => c.Group)
            .ToDictionary(g => g.Key, g => g.Aggregate(0UL, (mask, c) => mask | c.Mask));
        return covered.Count == coreCoverage.Count &&
            coreCoverage.All(g => covered.TryGetValue(g.Key, out var mask) && mask == g.Value);
    }
}
