using System.Runtime.InteropServices;

namespace DeltaNFD.Execution.Procman;

/// <summary>本机处理器组拓扑（只读缓存）：系统全部组的掩码 + 每组逻辑处理器数。</summary>
public static class ProcmanTopology
{
    [StructLayout(LayoutKind.Sequential)]
    private struct GROUP_AFFINITY
    {
        public nuint Mask;
        public ushort Group;
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSOR_NUMBER
    {
        public ushort Group;
        public byte Number;
        public byte Reserved;
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll")]
    private static extern nuint GetActiveProcessorCount(ushort groupNumber);

    private static readonly Lazy<IReadOnlyList<ProcmanGroupMaskValue>> LazySystem = new(ReadSystem);

    private static IReadOnlyList<ProcmanGroupMaskValue> ReadSystem()
    {
        var result = new List<ProcmanGroupMaskValue>();
        try
        {
            var groups = GetActiveProcessorGroupCount();
            for (ushort group = 0; group < groups; group++)
            {
                var count = GetActiveProcessorCount(group);
                if (count == 0) continue;
                var mask = count >= 64 ? ulong.MaxValue : (1UL << (int)count) - 1;
                result.Add(new(group, mask));
            }
        }
        catch { /* 读取失败时返回空；调用方按“未知拓扑”处理，不猜测 */ }
        return result;
    }

    /// <summary>系统全部处理器组掩码（只读缓存）。空列表 = 拓扑未知。</summary>
    public static IReadOnlyList<ProcmanGroupMaskValue> SystemGroupMasks() => LazySystem.Value;

    public static int ProcessorGroupCount => SystemGroupMasks().Count;

    /// <summary>组掩码格式化为 g{n}:0x{mask} 列表（展示用）。</summary>
    public static string Describe(IEnumerable<ProcmanGroupMaskValue> masks)
        => string.Join(";", masks.Select(m => $"g{m.Group}:0x{m.Mask:X}"));
}
