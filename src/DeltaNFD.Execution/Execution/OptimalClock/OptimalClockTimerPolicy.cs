using System.ComponentModel;
using DeltaNFD.Native;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Execution.OptimalClock;

internal readonly record struct OptimalClockPowerState(uint ControlMask, uint StateMask);

internal interface IOptimalClockTimerPolicyApi
{
    OptimalClockPowerState Read();
    void Write(OptimalClockPowerState state);
}

/// <summary>
/// 只由专用持有子进程调用：显式要求系统保留本进程的计时器请求。
/// 不改父进程、游戏或系统设置；子进程退出即结束该策略的生命周期。
/// </summary>
internal static class OptimalClockTimerPolicy
{
    internal const uint IgnoreTimerResolution = 4;

    internal static bool TryHonorRequests(out string error, IOptimalClockTimerPolicyApi? api = null, bool supportsPolicy = true)
    {
        // Win10 不写尚不支持的 IGNORE_TIMER_RESOLUTION 位；请求能否跨进程生效另由实测决定。
        if (!supportsPolicy) { error = ""; return true; }
        try
        {
            api ??= new NativeApi();
            var before = api.Read();
            if ((before.ControlMask & IgnoreTimerResolution) == 0
                || (before.StateMask & IgnoreTimerResolution) != 0)
            {
                var control = before.ControlMask | IgnoreTimerResolution;
                // 查询可能返回系统自动决定的状态位，写入只能包含已接管的状态位。
                api.Write(new(control, before.StateMask & control & ~IgnoreTimerResolution));
            }
            var after = api.Read();
            if ((after.ControlMask & IgnoreTimerResolution) == 0
                || (after.StateMask & IgnoreTimerResolution) != 0)
                throw new InvalidOperationException("精度请求保留策略回读不符");
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = "无法保留本进程的精度请求：" + ex.Message
                + (ex is Win32Exception win32 ? $"（Win32 {win32.NativeErrorCode}）" : "");
            return false;
        }
    }

    private sealed class NativeApi : IOptimalClockTimerPolicyApi
    {
        public OptimalClockPowerState Read()
        {
            using var process = new SafeProcessHandle(new nint(-1), false);
            var state = ProcessPowerThrottling.Read(process);
            return new(state.ControlMask, state.StateMask);
        }

        public void Write(OptimalClockPowerState value)
        {
            using var process = new SafeProcessHandle(new nint(-1), false);
            ProcessPowerThrottling.Write(process, value.ControlMask, value.StateMask);
        }
    }
}
