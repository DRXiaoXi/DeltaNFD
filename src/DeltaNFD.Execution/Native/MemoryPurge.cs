using System.Runtime.InteropServices;

namespace DeltaNFD.Native;

/// <summary>物理内存快照。</summary>
public readonly record struct MemorySnapshot(ulong TotalPhys, ulong AvailPhys, uint LoadPercent)
{
    /// <summary>可用物理内存占比（0~1）。</summary>
    public double AvailRatio => TotalPhys > 0 ? (double)AvailPhys / TotalPhys : 0;
}

/// <summary>待备内存列表清理结果。</summary>
public sealed class PurgeResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    /// <summary>清理前后可用物理内存的变化量（字节；可为负，缓存被立即重用时发生）。</summary>
    public long FreedBytes { get; init; }
}

/// <summary>只读内存统计；旧全机清理保留拒绝执行的兼容入口。</summary>
public static class MemoryPurge
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    /// <summary>只读查询物理内存总量 / 可用量 / 负载百分比。</summary>
    public static MemorySnapshot QueryMemory()
    {
        var status = new MemoryStatusEx();
        status.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        if (GlobalMemoryStatusEx(ref status))
        {
            return new MemorySnapshot(status.TotalPhys, status.AvailPhys, status.MemoryLoad);
        }

        return default;
    }

    /// <summary>旧协议兼容入口：禁止执行全机回收，以免绕过进程保护名单。</summary>
    public static PurgeResult PurgeStandbyList() => new()
    {
        Success = false,
        Message = "旧版全机内存清理已下线；请使用进程管理中的后台内存回收，遵守进程保护与排除名单。",
    };

    /// <summary>字节数格式化（GB/MB 自适应，两位小数）。</summary>
    public static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.##") + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.#") + " MB",
        _ => (bytes / (double)(1L << 10)).ToString("0") + " KB",
    };
}
