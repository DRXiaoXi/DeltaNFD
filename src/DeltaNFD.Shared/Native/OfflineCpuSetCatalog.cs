using System.Numerics;
using System.Runtime.InteropServices;

namespace DeltaNFD.Native;

/// <summary>一个系统 CPU Set 的调度位置与分配状态。</summary>
public readonly record struct SystemCpuSetDescriptor(
    uint Id,
    ushort Group,
    byte LogicalProcessorIndex,
    byte Flags)
{
    private const byte Allocated = 0x02;
    private const byte AllocatedToTargetProcess = 0x04;

    /// <summary>未被预留，或预留给当前查询进程。</summary>
    public bool IsAvailable => (Flags & Allocated) == 0 || (Flags & AllocatedToTargetProcess) != 0;
}

/// <summary>
/// 脱机 CPU Sets 只读目录。映射只接受完整、无冲突的单组拓扑，避免猜测缺失核心或分组关系。
/// </summary>
public static class OfflineCpuSetCatalog
{
    private const int CpuSetInformationType = 0;
    private const int HeaderBytes = 8;
    private const int CpuSetRecordBytes = 24;
    private const int MinimumEntryBytes = HeaderBytes + CpuSetRecordBytes;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(
        IntPtr information,
        uint informationLength,
        out uint returnedLength,
        IntPtr process,
        uint flags);

    /// <summary>读取系统 CPU Set 清单。未知记录类型会跳过；结构损坏或查询失败会返回 false。</summary>
    public static bool TryRead(out SystemCpuSetDescriptor[] descriptors, out string error)
    {
        descriptors = [];
        error = "";
        try
        {
            var firstSucceeded = GetSystemCpuSetInformation(IntPtr.Zero, 0, out var required, IntPtr.Zero, 0);
            var firstError = Marshal.GetLastWin32Error();
            if (required == 0 || required > int.MaxValue ||
                (!firstSucceeded && firstError is not (ErrorInsufficientBuffer or ErrorMoreData)))
            {
                error = $"读取系统 CPU Sets 大小失败（Win32 {firstError}）。";
                return false;
            }

            var buffer = Marshal.AllocHGlobal(checked((int)required));
            try
            {
                if (!GetSystemCpuSetInformation(buffer, required, out var written, IntPtr.Zero, 0) || written > required)
                {
                    error = $"读取系统 CPU Sets 失败（Win32 {Marshal.GetLastWin32Error()}）。";
                    return false;
                }

                if (written == 0 || !TryParse(buffer, checked((int)written), out descriptors, out error))
                    return false;
                return descriptors.Length > 0;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception ex)
        {
            descriptors = [];
            error = "读取系统 CPU Sets 异常：" + ex.Message;
            return false;
        }
    }

    /// <summary>将单处理器组掩码映射为 Win10 API 所需的 CPU Set ID；不完整映射一律拒绝。</summary>
    public static bool TryMapMaskToIds(
        IReadOnlyList<SystemCpuSetDescriptor> descriptors,
        int groupCount,
        ushort group,
        ulong mask,
        out uint[] ids,
        out string error)
    {
        ids = [];
        error = "";
        if (groupCount != 1 || group != 0)
        {
            error = "脱机 CPU Sets 目前只支持已确认的单处理器组拓扑。";
            return false;
        }
        if (mask == 0)
        {
            error = "CPU Sets 掩码为空。";
            return false;
        }

        var groupSets = descriptors.Where(d => d.Group == group).ToArray();
        if (groupSets.Select(d => d.LogicalProcessorIndex).Distinct().Count() != groupSets.Length)
        {
            error = "CPU Sets 清单中同一逻辑处理器出现重复记录。";
            return false;
        }

        var selected = new List<uint>(BitOperations.PopCount(mask));
        for (var index = 0; index < 64; index++)
        {
            if ((mask & (1UL << index)) == 0) continue;
            var matches = groupSets.Where(d => d.LogicalProcessorIndex == index).ToArray();
            if (matches.Length != 1)
            {
                error = $"CPU Sets 清单缺少逻辑处理器 {group}:{index}。";
                return false;
            }
            if (!matches[0].IsAvailable)
            {
                error = $"逻辑处理器 {group}:{index} 已分配给其他目标，拒绝使用。";
                return false;
            }
            selected.Add(matches[0].Id);
        }

        ids = selected.ToArray();
        return true;
    }

    /// <summary>验证单组掩码覆盖的每个逻辑处理器均能映射为当前可用的 CPU Set。</summary>
    public static bool IsMaskUsable(
        IReadOnlyList<SystemCpuSetDescriptor> descriptors,
        int groupCount,
        ushort group,
        ulong mask)
        => TryMapMaskToIds(descriptors, groupCount, group, mask, out _, out _);

    internal static bool TryParse(
        IntPtr buffer,
        int byteCount,
        out SystemCpuSetDescriptor[] descriptors,
        out string error)
    {
        descriptors = [];
        error = "";
        if (buffer == IntPtr.Zero || byteCount <= 0)
        {
            error = "CPU Sets 缓冲区为空。";
            return false;
        }

        var result = new List<SystemCpuSetDescriptor>();
        var offset = 0;
        while (offset < byteCount)
        {
            if (byteCount - offset < HeaderBytes)
            {
                error = "CPU Sets 记录头不完整。";
                return false;
            }

            var size = Marshal.ReadInt32(buffer, offset);
            if (size < HeaderBytes || size > byteCount - offset)
            {
                error = "CPU Sets 记录长度无效。";
                return false;
            }

            var type = Marshal.ReadInt32(buffer, offset + 4);
            if (type == CpuSetInformationType)
            {
                if (size < MinimumEntryBytes)
                {
                    error = "CPU Set 记录长度不足。";
                    return false;
                }

                var id = unchecked((uint)Marshal.ReadInt32(buffer, offset + 8));
                var group = unchecked((ushort)Marshal.ReadInt16(buffer, offset + 12));
                var logicalProcessorIndex = Marshal.ReadByte(buffer, offset + 14);
                var flags = Marshal.ReadByte(buffer, offset + 19);
                result.Add(new SystemCpuSetDescriptor(id, group, logicalProcessorIndex, flags));
            }

            offset += size;
        }

        descriptors = result.ToArray();
        return true;
    }
}
