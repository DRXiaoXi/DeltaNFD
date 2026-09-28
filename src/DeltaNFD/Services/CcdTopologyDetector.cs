using System.Numerics;

namespace DeltaNFD.Services;

internal readonly record struct CpuDomain(int Group, ulong Mask, int CacheSizeBytes = 0);

internal static class CcdTopologyDetector
{
    internal static (List<CcdInfo> Ccds, CcdDetectionSource Source) Detect(
        IReadOnlyList<CpuCoreInfo> cores,
        IReadOnlyList<CpuDomain> dies,
        IReadOnlyList<CpuDomain> l3Domains)
    {
        if (TryBuild(cores, dies, l3Domains, out var ccds))
        {
            return (ccds, CcdDetectionSource.ProcessorDie);
        }

        if (TryBuild(cores, l3Domains, l3Domains, out ccds))
        {
            return (ccds, CcdDetectionSource.L3Fallback);
        }

        return ([], CcdDetectionSource.Unknown);
    }

    private static bool TryBuild(
        IReadOnlyList<CpuCoreInfo> cores,
        IReadOnlyList<CpuDomain> domains,
        IReadOnlyList<CpuDomain> l3Domains,
        out List<CcdInfo> ccds)
    {
        ccds = [];
        if (cores.Count == 0 || domains.Count == 0 ||
            cores.Any(c => c.Mask == 0) || domains.Any(d => d.Mask == 0))
        {
            return false;
        }

        var ordered = domains
            .Where(d => d.Mask != 0)
            .DistinctBy(d => (d.Group, d.Mask))
            .OrderBy(d => d.Group)
            .ThenBy(d => BitOperations.TrailingZeroCount(d.Mask))
            .ToList();
        if (ordered.Count == 0)
        {
            return false;
        }

        var covered = new Dictionary<int, ulong>();
        foreach (var domain in ordered)
        {
            var prior = covered.GetValueOrDefault(domain.Group);
            if ((prior & domain.Mask) != 0)
            {
                return false;
            }
            covered[domain.Group] = prior | domain.Mask;

            var memberCores = cores.Where(c => c.Group == domain.Group && (c.Mask & domain.Mask) != 0).ToList();
            if (memberCores.Count == 0 || memberCores.Any(c => (c.Mask & domain.Mask) != c.Mask))
            {
                return false;
            }

            var l3Bytes = l3Domains
                .Where(c => c.Group == domain.Group && (c.Mask & domain.Mask) == c.Mask)
                .DistinctBy(c => (c.Group, c.Mask))
                .Sum(c => (long)Math.Max(0, c.CacheSizeBytes));
            ccds.Add(new CcdInfo
            {
                Index = ccds.Count,
                Group = domain.Group,
                Mask = domain.Mask,
                CoreCount = memberCores.Count,
                LogicalCount = BitOperations.PopCount(domain.Mask),
                L3CacheMb = Math.Round(l3Bytes / 1024.0 / 1024.0, 1),
            });
        }

        var coreCoverage = cores
            .GroupBy(c => c.Group)
            .ToDictionary(g => g.Key, g => g.Aggregate(0UL, (mask, c) => mask | c.Mask));
        if (covered.Count != coreCoverage.Count ||
            coreCoverage.Any(g => !covered.TryGetValue(g.Key, out var mask) || mask != g.Value))
        {
            ccds.Clear();
            return false;
        }

        return true;
    }
}
