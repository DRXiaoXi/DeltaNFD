using System.Runtime.InteropServices;
using DeltaNFD.Native;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

// Independent implementation using Windows memory statistics. No Mem Reduct code is included.
internal static class ProcmanMemoryTelemetryReader
{
    internal static ProcmanMemoryTelemetry? Read()
    {
        var memory = MemoryPurge.QueryMemory();
        if (memory.TotalPhys == 0 || memory.AvailPhys > memory.TotalPhys || memory.LoadPercent > 100) return null;
        ulong? commit = null, limit = null;
        if (GetPerformanceInfo(out var info, (uint)Marshal.SizeOf<PerformanceInfo>()) && info.PageSize > 0)
        {
            try
            {
                commit = checked((ulong)info.CommitTotal * (ulong)info.PageSize);
                limit = checked((ulong)info.CommitLimit * (ulong)info.PageSize);
            }
            catch (OverflowException) { commit = limit = null; }
        }
        return new(memory.TotalPhys, memory.AvailPhys, memory.LoadPercent, commit, limit);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInfo
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out PerformanceInfo info, uint size);
}
