using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeltaNFD.Inspection;

public readonly record struct PhysicalMemoryReading(ulong TotalBytes, ulong AvailableBytes)
{
    public bool IsValid => TotalBytes > 0 && AvailableBytes <= TotalBytes;
}
public sealed record PhysicalMemoryReadResult(PhysicalMemoryReading? Reading, string Error = "");

/// <summary>One query only; no CPU/process sampling, cache purge, allocation pressure or executor.</summary>
public static class PhysicalMemoryInspection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    public static PhysicalMemoryReadResult Read()
    {
        try
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref status))
                return new(null, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            var reading = new PhysicalMemoryReading(status.TotalPhysical, status.AvailablePhysical);
            return reading.IsValid ? new(reading) : new(null, "系统返回的物理内存数据无效。");
        }
        catch (Exception ex) { return new(null, ex.Message); }
    }
}
