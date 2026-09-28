using System.Runtime.InteropServices;

namespace DeltaNFD.Native;

// ---------------- 进程级性能原语（公开 Win32 API） ----------------
// 全部为运行时行为：不写系统配置、不动电源计划；调用方负责对称使用（禁用/恢复、请求/释放）。

/// <summary>进程电源限制（EcoQoS）与系统定时器分辨率控制。</summary>
public static class ProcessPerf
{
    // ---- 进程电源限制（PROCESS_POWER_THROTTLING / EcoQoS） ----

    private const int ProcessInformationClassPowerThrottling = 4; // ProcessPowerThrottling
    private const uint PowerThrottlingCurrentVersion = 1;
    private const uint PowerThrottlingExecutionSpeed = 0x1;       // PROCESS_POWER_THROTTLING_EXECUTION_SPEED

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass, ref PROCESS_POWER_THROTTLING_STATE processInformation, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint ProcessSetInformation = 0x0400; // PROCESS_SET_INFORMATION

    /// <summary>禁用指定进程的电源限制（EcoQoS off，强制高性能电源状态）。</summary>
    public static bool DisablePowerThrottling(int pid)
    {
        try
        {
            var handle = OpenProcess(ProcessSetInformation, false, pid);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = PowerThrottlingCurrentVersion,
                    ControlMask = PowerThrottlingExecutionSpeed,
                    StateMask = 0, // 该位清零 = 不允许降速 = 高性能状态
                };
                return SetProcessInformation(handle, ProcessInformationClassPowerThrottling, ref state,
                    Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>恢复指定进程由系统自控的电源限制策略（交还 EcoQoS 控制）。</summary>
    public static bool RestorePowerThrottling(int pid)
    {
        try
        {
            var handle = OpenProcess(ProcessSetInformation, false, pid);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = PowerThrottlingCurrentVersion,
                    ControlMask = 0, // 掩码清零 = 该项交还系统策略
                    StateMask = 0,
                };
                return SetProcessInformation(handle, ProcessInformationClassPowerThrottling, ref state,
                    Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch
        {
            return false;
        }
    }

    // ---- 系统定时器分辨率（NtSetTimerResolution，ntdll 原生路径） ----

    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint desiredTime100ns, bool setResolution, out uint actualTime100ns);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimumTime100ns, out uint maximumTime100ns, out uint currentTime100ns);

    private const uint Desired5000Us = 5000; // 0.5ms = 5000 × 100ns

    /// <summary>把系统定时器分辨率请求提升到 0.5ms（对本进程生效；结合「计时器分辨率常驻」优化可全局生效）。</summary>
    public static bool TimerRequestHighResolution()
    {
        try
        {
            var status = NtSetTimerResolution(Desired5000Us, true, out _);
            return status == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>释放本进程的高分辨率定时器请求（分辨率回归系统其余请求决定）。</summary>
    public static bool TimerReleaseResolution()
    {
        try
        {
            var status = NtSetTimerResolution(0, false, out _);
            return status == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前系统定时器分辨率（ms；读取失败返回 null）。</summary>
    public static double? QueryTimerResolutionMs()
    {
        try
        {
            if (NtQueryTimerResolution(out _, out _, out var current100ns) != 0)
            {
                return null;
            }

            return current100ns / 10000.0 / 1000.0;
        }
        catch
        {
            return null;
        }
    }
}
