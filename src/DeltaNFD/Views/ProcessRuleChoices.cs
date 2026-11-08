namespace DeltaNFD.Views;

/// <summary>
/// 持久规则编辑器共用的能力列表与取值（进程管理「总是规则」与调度改善「三角洲规则」共用，保持同一字节值）。
/// 下标与两个页面能力下拉框的条目顺序一致。
/// </summary>
internal static class ProcessRuleChoices
{
    internal static readonly string[] Capabilities =
        ["priority", "ecoQos", "priorityBoost", "affinity", "cpuSet", "ioPriority", "memoryPriority", "gpuPriority"];

    internal static string? CapabilityAt(int index) =>
        index >= 0 && index < Capabilities.Length ? Capabilities[index] : null;

    internal static int IndexOf(string capability) =>
        Array.FindIndex(Capabilities, c => c.Equals(capability, StringComparison.OrdinalIgnoreCase));

    /// <summary>CPU 亲和性与 CPU 集合使用处理器组掩码，其余能力使用固定取值。</summary>
    internal static bool UsesMask(int index) => index is 3 or 4;

    internal static (string Label, string Value)[] ValuesFor(int index) => index switch
    {
        0 => [("低", "Idle"), ("低于正常", "BelowNormal"), ("正常", "Normal"), ("高于正常", "AboveNormal"), ("高", "High")],
        1 => [("开启效率模式", "true"), ("关闭效率模式", "false")],
        2 => [("启用动态提升", "True"), ("关闭动态提升", "False")],
        5 => [("很低", "VeryLow"), ("低", "Low"), ("正常", "Normal"), ("高", "High")],
        6 => [("很低", "1"), ("低", "2"), ("中等", "3"), ("低于正常", "4"), ("正常（最高可用）", "5")],
        7 => [("低", "0"), ("低于正常", "1"), ("正常", "2"), ("高于正常", "3"), ("高", "4")],
        _ => [],
    };

    internal static int DefaultValueIndex(int index) => index switch { 0 or 5 or 7 => 2, 6 => 4, _ => 0 };

    internal static string Title(string capability) => capability switch
    {
        "priority" => "CPU 优先级",
        "affinity" => "CPU 亲和性",
        "cpuSet" => "CPU 集合",
        "memoryPriority" => "内存优先级",
        "ecoQos" => "效率模式（EcoQoS）",
        "priorityBoost" => "动态优先级提升",
        "ioPriority" => "I/O 优先级",
        "gpuPriority" => "GPU 调度优先级",
        "gpuPreference" => "GPU 首选项",
        _ => capability,
    };
}
