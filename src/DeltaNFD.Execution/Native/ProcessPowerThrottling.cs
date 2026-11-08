using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessPowerState
{
    internal uint Version, ControlMask, StateMask;
}

internal interface IProcessPowerThrottlingApi
{
    int ReadWin32(SafeProcessHandle process, ref ProcessPowerState state);
    int WriteWin32(SafeProcessHandle process, ref ProcessPowerState state);
    int ReadNative(SafeProcessHandle process, ref ProcessPowerState state, out uint length);
    int WriteNative(SafeProcessHandle process, ref ProcessPowerState state);
    int Win32Error(int status);
}

/// <summary>同一句柄、同一策略结构；仅在 Win32 接口明确不可用时尝试内核接口，绝不吞掉拒绝访问或参数错误。</summary>
internal static class ProcessPowerThrottling
{
    private const uint Size = 12;
    private static bool Unavailable(int error) => error is 1 or 50 or 120;

    internal static ProcessPowerState Read(SafeProcessHandle process, IProcessPowerThrottlingApi? api = null)
    {
        api ??= new NativeApi();
        var state = new ProcessPowerState { Version = 1 };
        var error = api.ReadWin32(process, ref state);
        if (error != 0)
        {
            if (!Unavailable(error)) throw new Win32Exception(error, $"读取进程电源节流状态失败（Win32 {error}）");
            state = new ProcessPowerState { Version = 1 };
            var status = api.ReadNative(process, ref state, out var length);
            if (status < 0) throw Failure("读取", error, status, api);
            if (length != Size) throw new IOException($"电源节流兼容查询长度异常：{length}，未采用结果（Win32 {error}）");
        }
        if (state.Version != 1) throw new IOException("电源节流查询结构版本异常，未采用结果。");
        return state;
    }

    internal static void Write(SafeProcessHandle process, uint control, uint value, IProcessPowerThrottlingApi? api = null)
    {
        if ((value & ~control) != 0) throw new ArgumentException("状态位超出控制掩码，未写入。");
        api ??= new NativeApi();
        var state = new ProcessPowerState { Version = 1, ControlMask = control, StateMask = value };
        var error = api.WriteWin32(process, ref state);
        if (error == 0) return;
        if (!Unavailable(error)) throw new Win32Exception(error, $"写入进程电源节流状态失败（Win32 {error}）");
        state = new ProcessPowerState { Version = 1, ControlMask = control, StateMask = value };
        var status = api.WriteNative(process, ref state);
        if (status < 0) throw Failure("写入", error, status, api);
        // 调用者仍须回读、按其拥有的位验证；接口接受不代表功能已经生效。
    }

    private static Win32Exception Failure(string operation, int error, int status, IProcessPowerThrottlingApi api) =>
        new(api.Win32Error(status), $"{operation}进程电源节流状态失败（Win32 {error}；兼容接口 NTSTATUS 0x{status:X8}）");

    private sealed class NativeApi : IProcessPowerThrottlingApi
    {
        public int ReadWin32(SafeProcessHandle process, ref ProcessPowerState state) =>
            GetProcessInformation(process, 4, ref state, Size) ? 0 : Marshal.GetLastWin32Error();
        public int WriteWin32(SafeProcessHandle process, ref ProcessPowerState state) =>
            SetProcessInformation(process, 4, ref state, Size) ? 0 : Marshal.GetLastWin32Error();
        public int ReadNative(SafeProcessHandle process, ref ProcessPowerState state, out uint length) =>
            NtQueryInformationProcess(process, 77, ref state, Size, out length);
        public int WriteNative(SafeProcessHandle process, ref ProcessPowerState state) =>
            NtSetInformationProcess(process, 77, ref state, Size);
        public int Win32Error(int status) => unchecked((int)RtlNtStatusToDosError(status));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(SafeProcessHandle process, int kind, ref ProcessPowerState state, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(SafeProcessHandle process, int kind, ref ProcessPowerState state, uint size);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int kind, ref ProcessPowerState state, uint size, out uint length);
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(SafeProcessHandle process, int kind, ref ProcessPowerState state, uint size);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
