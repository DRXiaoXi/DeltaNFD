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
        // 方案 1：优先使用 ProcessorDie 拓扑（最可靠）
        if (TryBuild(cores, dies, l3Domains, out var ccds))
        {
            return (ccds, CcdDetectionSource.ProcessorDie);
        }

        // 方案 2：使用 L3 Cache 域（降级，但仍然可靠）
        if (TryBuild(cores, l3Domains, l3Domains, out ccds))
        {
            return (ccds, CcdDetectionSource.L3Fallback);
        }

        // 方案 3：宽松的核心分组检测（适用于 8940HX 等移动处理器）
        // 按每 8 个物理核心分为一组，适配标准的双 CCD 布局
        if (TryBuildByEvenSplit(cores, l3Domains, out ccds))
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

    /// <summary>
    /// 宽松的核心分组检测（适用于 8940HX 等移动处理器）。
    /// 当标准的 Die/L3 域检测失败时，尝试按物理核心数量均分为 CCD。
    /// 假设：16 核及以上的 AMD 处理器通常是双 CCD 设计，每个 CCD 8 核。
    /// </summary>
    private static bool TryBuildByEvenSplit(
        IReadOnlyList<CpuCoreInfo> cores,
        IReadOnlyList<CpuDomain> l3Domains,
        out List<CcdInfo> ccds)
    {
        ccds = [];
        
        // 只适用于单处理器组、物理核心数为 8 的倍数且至少 16 核的情况
        if (cores.Count < 16 || cores.Count % 8 != 0)
        {
            return false;
        }

        // 检查所有核心都在组 0
        if (cores.Any(c => c.Group != 0 || c.Mask == 0))
        {
            return false;
        }

        // 按核心掩码的最低位排序（连续的核心编号）
        var sortedCores = cores
            .OrderBy(c => BitOperations.TrailingZeroCount(c.Mask))
            .ToList();

        var ccdCount = cores.Count / 8;
        var coresPerCcd = 8;

        // 将核心均分为多个 CCD
        for (var ccdIndex = 0; ccdIndex < ccdCount; ccdIndex++)
        {
            var ccdCores = sortedCores
                .Skip(ccdIndex * coresPerCcd)
                .Take(coresPerCcd)
                .ToList();

            if (ccdCores.Count != coresPerCcd)
            {
                ccds.Clear();
                return false;
            }

            // 计算该 CCD 的掩码
            ulong ccdMask = 0;
            foreach (var core in ccdCores)
            {
                ccdMask |= core.Mask;
            }

            // 计算该 CCD 的 L3 缓存大小
            var l3Bytes = l3Domains
                .Where(l3 => l3.Group == 0 && (l3.Mask & ccdMask) != 0)
                .DistinctBy(l3 => (l3.Group, l3.Mask))
                .Sum(l3 => (long)Math.Max(0, l3.CacheSizeBytes));

            ccds.Add(new CcdInfo
            {
                Index = ccdIndex,
                Group = 0,
                Mask = ccdMask,
                CoreCount = coresPerCcd,
                LogicalCount = BitOperations.PopCount(ccdMask),
                L3CacheMb = Math.Round(l3Bytes / 1024.0 / 1024.0, 1),
            });
        }

        // 验证没有核心重叠
        ulong totalMask = 0;
        foreach (var ccd in ccds)
        {
            if ((totalMask & ccd.Mask) != 0)
            {
                ccds.Clear();
                return false;
            }
            totalMask |= ccd.Mask;
        }

        // 验证覆盖了所有核心
        var allCoresMask = cores.Aggregate(0UL, (mask, c) => mask | c.Mask);
        if (totalMask != allCoresMask)
        {
            ccds.Clear();
            return false;
        }

        Log.Info($"CCD 检测：使用均分方案检测到 {ccds.Count} 个 CCD（每个 {coresPerCcd} 核）");
        return true;
    }
}
