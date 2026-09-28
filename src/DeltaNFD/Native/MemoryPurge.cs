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

/// <summary>
/// 待备内存列表清理（standby list purge）：
/// 启用 SeProfileSingleProcessPrivilege 特权后，经 NtSetSystemInformation(SystemMemoryListInformation)
/// 依次执行「清空待备列表 / 清空低优先级待备页 / 清空优先级 0 待备页」，
/// 把文件缓存占用的待备物理内存立即释放为可用内存——与 RAMMap / EmptyStandbyList 同源的公开 NTAPI 机制。
/// 纯运行时行为：不写任何系统配置，待备缓存由系统按需自动重建。
/// </summary>
internal static class MemoryPurge
{
    private const int SystemMemoryListInformation = 80;

    /// <summary>SystemMemoryListInformation 命令：清空待备列表 / 低优先级待备页 / 优先级 0 待备页。</summary>
    private static readonly uint[] PurgeCommands = [2, 3, 4];

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref uint info, int length);

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

    /// <summary>
    /// 清空待备内存列表。需管理员运行（特权在管理员令牌中但默认禁用，先启用）。
    /// 命令 2 为主清理（清空全部待备页），3/4 为细分清理；部分系统可能不支持后两者，任一成功即视为成功。
    /// </summary>
    public static PurgeResult PurgeStandbyList()
    {
        try
        {
            Privilege.Enable("SeProfileSingleProcessPrivilege");
        }
        catch (Exception ex)
        {
            return new PurgeResult
            {
                Success = false,
                Message = "启用内存清理特权失败：" + ex.Message + "。请以管理员身份运行本程序。",
            };
        }

        var before = QueryMemory();
        var errors = new List<string>();
        var okCommands = 0;
        foreach (var command in PurgeCommands)
        {
            var value = command;
            var status = NtSetSystemInformation(SystemMemoryListInformation, ref value, sizeof(uint));
            if (status == 0)
            {
                okCommands++;
            }
            else
            {
                errors.Add($"操作 {command} 失败（NTSTATUS 0x{status:X8}）");
            }
        }

        if (okCommands == 0)
        {
            return new PurgeResult
            {
                Success = false,
                Message = "清空待备内存列表失败：" + string.Join("；", errors) + "。",
            };
        }

        var after = QueryMemory();
        var freed = (long)after.AvailPhys - (long)before.AvailPhys;
        var note = okCommands < PurgeCommands.Length
            ? "（" + okCommands + "/" + PurgeCommands.Length + " 项操作成功：" + string.Join("；", errors) + "）"
            : "";
        return new PurgeResult
        {
            Success = true,
            Message = "已清空待备内存列表" + note + "，可用内存 " + FormatBytes(after.AvailPhys) + "。",
            FreedBytes = freed,
        };
    }

    /// <summary>字节数格式化（GB/MB 自适应，两位小数）。</summary>
    public static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.##") + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.#") + " MB",
        _ => (bytes / (double)(1L << 10)).ToString("0") + " KB",
    };
}
