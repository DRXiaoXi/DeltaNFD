using System.Runtime.InteropServices;
using DeltaNFD.Execution.OptimalClock;

// 独立诊断进程：模拟已有的“忽略精度请求”状态，再执行实际产品 Host。
// 只设置自身，不调整其他应用、注册表或软件设置。
if (args is ["--preset-ignore", var resolution])
{
    var state = new Native.Power { Version = 1 };
    if (!Native.GetProcessInformation(Native.GetCurrentProcess(), 4, ref state, 12)) return 10;
    state.ControlMask |= 4;
    state.StateMask = (state.StateMask | 4) & state.ControlMask;
    if (!Native.SetProcessInformation(Native.GetCurrentProcess(), 4, ref state, 12)) return 11;
    args = ["--optimal-clock-hold", resolution];
}
return OptimalClockProbeHost.TryRun(args, out var exitCode) ? exitCode : 12;

static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Power { internal uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")] internal static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetProcessInformation(nint process, int kind, ref Power state, int size);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetProcessInformation(nint process, int kind, ref Power state, int size);
}

// Host 中未使用的历史 using；实际测量和策略源码均直接链接产品原文件。
namespace DeltaNFD.Native { internal static class NamespaceMarker { } }
