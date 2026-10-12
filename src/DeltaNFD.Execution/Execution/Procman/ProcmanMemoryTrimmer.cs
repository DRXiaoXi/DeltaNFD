using System.ComponentModel;
using System.Runtime.InteropServices;
using DeltaNFD.Services;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Execution.Procman;

internal static class ProcmanMemoryTrimmer
{
    internal static ProcmanMemoryTrimItem Trim(ProcmanProcessIdentity identity, Func<bool> stillEligible)
    {
        var name = Path.GetFileName(identity.ExecutablePath);
        using var handle = OpenProcess(0x1000 | 0x0100, false, identity.Pid); // query limited + set quota
        if (handle.IsInvalid || !ProcmanIdentity.Matches(handle, identity))
            return new(identity.Pid, name, "skipped", "无法确认进程身份或没有工作集操作权限。");
        if (!GetProcessMemoryInfo(handle, out var before, (uint)Marshal.SizeOf<MemoryCounters>()))
            return new(identity.Pid, name, "skipped", "无法读取操作前工作集，未执行回收。");
        // Keep the same handle through identity recheck, working-set request, and readback.
        if (!stillEligible() || !ProcmanIdentity.Matches(handle, identity))
            return new(identity.Pid, name, "skipped", "进程转为前台、受保护、已有设置或身份变化，未执行回收。");
        if (!EmptyWorkingSet(handle))
            return new(identity.Pid, name, "failed", new Win32Exception(Marshal.GetLastWin32Error()).Message, (long)before.WorkingSet);
        if (!GetProcessMemoryInfo(handle, out var after, (uint)Marshal.SizeOf<MemoryCounters>()))
            return new(identity.Pid, name, "requested", "工作集回收请求成功，回读失败，效果未知。", (long)before.WorkingSet);
        return new(identity.Pid, name, "completed", "请求已完成并回读；仅表示工作集变化，不等于释放了等量物理内存。",
            (long)before.WorkingSet, (long)after.WorkingSet);
    }

    [StructLayout(LayoutKind.Sequential)] private struct MemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool, Pagefile, PeakPagefile, PrivateUsage;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyWorkingSet(SafeProcessHandle handle);
    [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessMemoryInfo(SafeProcessHandle handle, out MemoryCounters counters, uint size);
}
