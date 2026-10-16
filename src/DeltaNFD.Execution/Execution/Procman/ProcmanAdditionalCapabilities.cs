using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Execution.Procman;

public static partial class ProcmanCapabilities
{
    public static bool CanOpenWrite(int pid)
    { using var handle = TryOpen(pid, AccessSetInformation); return handle is not null; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessInformation(SafeProcessHandle h, int kind, out uint value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(SafeProcessHandle h, int kind, ref uint value, int size);
    [DllImport("gdi32.dll")] private static extern int D3DKMTGetProcessSchedulingPriorityClass(SafeProcessHandle h, out int value);
    [DllImport("gdi32.dll")] private static extern int D3DKMTSetProcessSchedulingPriorityClass(SafeProcessHandle h, int value);
    public static ProcmanCapabilityResult ReadMemoryPriority(int pid)
    {
        using var h = TryOpen(pid, 0x1400);
        return h is not null && GetProcessInformation(h, 0, out var value, 4)
            ? ProcmanCapabilityResult.Success(value.ToString()) : ProcmanCapabilityResult.Failure("内存优先级不可读取：" + Marshal.GetLastWin32Error());
    }
    public static ProcmanCapabilityResult SetMemoryPriority(int pid, int value)
    {
        if (value is < 1 or > 5) return ProcmanCapabilityResult.Failure("内存优先级仅允许 1–5。");
        using var h = TryOpen(pid, AccessSetInformation); var native = (uint)value;
        if (h is null || !SetProcessInformation(h, 0, ref native, 4)) return ProcmanCapabilityResult.Failure("内存优先级写入失败：" + Marshal.GetLastWin32Error());
        var actual = ReadMemoryPriority(pid);
        return actual.Ok && actual.Value == value.ToString() ? actual : ProcmanCapabilityResult.Failure("内存优先级回读不一致。");
    }
    public static ProcmanCapabilityResult ReadGpuPriority(int pid)
    {
        try
        {
            using var h = TryOpen(pid, 0x1400);
            if (h is null) return ProcmanCapabilityResult.Failure("无法读取 GPU 进程句柄。");
            var status = D3DKMTGetProcessSchedulingPriorityClass(h, out var value);
            return status == 0 ? ProcmanCapabilityResult.Success(value.ToString()) : ProcmanCapabilityResult.Failure($"GPU 调度不支持/不可读：0x{status:X8}");
        }
        catch (Exception ex) { return ProcmanCapabilityResult.Failure("GPU 调度接口不可用：" + ex.Message); }
    }
    public static ProcmanCapabilityResult SetGpuPriority(int pid, int value)
    {
        if (value is < 0 or > 4) return ProcmanCapabilityResult.Failure("GPU 实时调度档位不开放。");
        try
        {
            using var h = TryOpen(pid, AccessSetInformation);
            if (h is null) return ProcmanCapabilityResult.Failure("GPU 调度写入权限不可用。");
            var status = D3DKMTSetProcessSchedulingPriorityClass(h, value);
            if (status != 0) return ProcmanCapabilityResult.Failure($"GPU 调度写入失败：0x{status:X8}");
            var actual = ReadGpuPriority(pid);
            return actual.Ok && actual.Value == value.ToString() ? actual : ProcmanCapabilityResult.Failure("GPU 调度回读不一致。");
        }
        catch (Exception ex) { return ProcmanCapabilityResult.Failure(ex.Message); }
    }
}
