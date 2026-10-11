using System.Globalization;

namespace DeltaNFD.Services;

/// <summary>同一套属性控件的“总是”模式映射；null 字段不生成规则，空掩码表示解除限制。</summary>
internal static class ProcmanRuleProjection
{
    internal static IReadOnlyList<ProcmanRuleEntry> Create(ProcmanSetArgs args, bool autoTakeover)
    {
        if (!Path.IsPathFullyQualified(args.Identity.ExecutablePath)) throw new InvalidDataException("规则目标必须是完整 EXE 路径。");
        if (args.GpuPreference is not null) throw new InvalidDataException("GPU 首选项由 Windows 按应用保存，不作为运行时规则重复写入。");
        var rules = new List<ProcmanRuleEntry>();
        void Add(string capability, string value) => rules.Add(new()
        {
            ExecutablePath = args.Identity.ExecutablePath, Capability = capability, Value = value,
            Enabled = true, AutoTakeover = autoTakeover,
        });
        if (args.CpuPriority is { } priority) Add("priority", priority);
        if (args.AffinityMasks is { } affinity) Add("affinity", Masks(affinity));
        if (args.CpuSetMasks is { } cpuSet) Add("cpuSet", Masks(cpuSet));
        if (args.MemoryPriority is { } memory) Add("memoryPriority", memory.ToString(CultureInfo.InvariantCulture));
        if (args.GpuPriority is { } gpu) Add("gpuPriority", gpu.ToString(CultureInfo.InvariantCulture));
        if (args.EcoQosEnabled is { } eco) Add("ecoQos", eco ? "true" : "false");
        if (args.PriorityBoost is { } boost) Add("priorityBoost", boost ? "True" : "False");
        if (args.IoPriority is { } io) Add("ioPriority", io);
        if (rules.Count == 0) throw new InvalidDataException("没有可保存的属性设置。");
        return rules;
    }

    private static string Masks(IEnumerable<ProcmanGroupMask> masks) =>
        string.Join(";", masks.OrderBy(m => m.Group).Select(m => $"g{m.Group}:0x{m.Mask:X}"));
}
