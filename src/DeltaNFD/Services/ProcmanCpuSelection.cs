using DeltaNFD.Backends;

namespace DeltaNFD.Services;

internal static class ProcmanCpuSelection
{
    internal sealed record Preset(string Label, List<ProcmanGroupMask> Masks);

    internal static List<ProcmanGroupMask> Masks(IEnumerable<ProcmanLogicalCpu> cpus)
        => cpus.GroupBy(c => c.Group).Select(g => new ProcmanGroupMask(g.Key,
            g.Aggregate(0UL, (mask, cpu) => mask | (1UL << cpu.Index)))).ToList();

    internal static List<Preset> Presets(ProcmanTopologyResult topology)
    {
        var result = new List<Preset>();
        void Add(string name, IEnumerable<ProcmanLogicalCpu> cpus)
        {
            var masks = Masks(cpus);
            if (masks.Count > 0) result.Add(new(name, masks));
        }
        foreach (var kind in new[] { "P", "E" })
            Add(kind == "P" ? "性能核 (P)" : "能效核 (E)", topology.Processors.Where(c => c.CoreKind == kind));
        foreach (var ccd in topology.Processors.Where(c => c.Ccd.HasValue).Select(c => c.Ccd!.Value).Distinct().Order())
            Add($"CCD{ccd} · L3 推定", topology.Processors.Where(c => c.Ccd == ccd));
        if (!topology.Processors.Any(c => c.Ccd.HasValue))
        {
            foreach (var die in topology.Processors.Where(c => c.Die.HasValue).Select(c => c.Die!.Value).Distinct().Order())
                Add($"物理 Die {die}", topology.Processors.Where(c => c.Die == die));
            foreach (var cache in topology.Processors.Where(c => c.L3.HasValue).Select(c => c.L3!.Value).Distinct().Order())
                Add($"L3 分组 {cache}", topology.Processors.Where(c => c.L3 == cache));
        }
        // 部分缺失时不能生成“每核一线程”，否则会静默排除未识别 CPU。
        if (topology.Processors.Count > 0 && topology.Processors.All(c => c.Core.HasValue && c.Thread.HasValue)
            && topology.Processors.Any(c => c.ThreadCount > 1))
        {
            Add("每核一个线程", topology.Processors.Where(c => c.Thread == 0));
            Add("SMT 其余线程", topology.Processors.Where(c => c.Thread > 0));
        }
        return result;
    }

    internal static string Describe(ProcmanLogicalCpu cpu)
    {
        var parts = new List<string> { $"组 {cpu.Group} · CPU {cpu.Index}" };
        parts.Add(cpu.CoreKind switch { "P" => "性能核 (P)", "E" => "能效核 (E)", _ => "P/E 类型未确认" });
        parts.Add(cpu.Core is { } core ? $"物理核 {core} · 线程 {cpu.Thread}/{cpu.ThreadCount}" : "物理核未知");
        if (cpu.Ccd is { } ccd) parts.Add($"CCD{ccd}（共享 L3 推定）");
        if (cpu.L3 is { } l3) parts.Add($"L3 分组 {l3}" + (cpu.L3Bytes is { } size ? $" · {size / (1024 * 1024)} MB" : ""));
        if (cpu.Package is { } package) parts.Add($"封装 {package}");
        if (cpu.Die is { } die) parts.Add($"物理 Die {die}");
        if (cpu.NumaNode is { } node) parts.Add($"NUMA {node}");
        if (cpu.CpuSetId is { } id) parts.Add($"CPU Set ID {id}");
        if (cpu.EfficiencyClass is { } rank) parts.Add($"Windows 性能等级 {rank}（不等同于 P/E）");
        return string.Join("\n", parts);
    }
}
