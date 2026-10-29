using System.Runtime.InteropServices;

namespace DeltaNFD.Native;

// ---------------- 系统定时器分辨率原语 ----------------
// 运行时请求；调用方负责对称请求与释放，不改写系统配置或电源计划。

/// <summary>系统定时器分辨率控制。</summary>
public static class ProcessPerf
{
    // ---- 系统定时器分辨率（NtSetTimerResolution，ntdll 原生路径） ----

    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint desiredTime100ns, bool setResolution, out uint actualTime100ns);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimumTime100ns, out uint maximumTime100ns, out uint currentTime100ns);

    private const uint DesiredHalfMillisecond100ns = 5000;

    /// <summary>本进程申请 0.5ms；调用成功不保证实际达到目标，也不证明游戏端等待精度。</summary>
    public static bool TimerRequestHighResolution() => RequestTimerResolutionHighDetailed().Succeeded;

    public static TimerResolutionRequestResult RequestTimerResolutionHighDetailed()
    {
        try
        {
            var status = NtSetTimerResolution(DesiredHalfMillisecond100ns, true, out var actual100ns);
            return new TimerResolutionRequestResult(true, status, DesiredHalfMillisecond100ns, actual100ns, QueryTimerResolutionMs());
        }
        catch
        {
            return new TimerResolutionRequestResult(false, -1, DesiredHalfMillisecond100ns, 0, QueryTimerResolutionMs());
        }
    }

    /// <summary>释放本进程的高分辨率定时器请求（分辨率回归系统其余请求决定）。</summary>
    public static bool TimerReleaseResolution() => ReleaseTimerResolutionDetailed().Succeeded;

    public static TimerResolutionRequestResult ReleaseTimerResolutionDetailed()
    {
        try
        {
            var status = NtSetTimerResolution(0, false, out var actual100ns);
            return new TimerResolutionRequestResult(true, status, 0, actual100ns, QueryTimerResolutionMs());
        }
        catch
        {
            return new TimerResolutionRequestResult(false, -1, 0, 0, QueryTimerResolutionMs());
        }
    }

    /// <summary>当前系统定时器分辨率（ms；读取失败返回 null）。</summary>
    public static double? QueryTimerResolutionMs()
    {
        try
        {
            if (NtQueryTimerResolution(out _, out _, out var current100ns) < 0 || current100ns == 0)
            {
                return null;
            }

            return ConvertTimerResolution100nsToMilliseconds(current100ns);
        }
        catch
        {
            return null;
        }
    }

    internal static double ConvertTimerResolution100nsToMilliseconds(uint value100ns)
        => value100ns / 10000.0;
}

public readonly record struct TimerResolutionRequestResult(
    bool CallCompleted,
    int NtStatus,
    uint Requested100ns,
    uint ReturnedActual100ns,
    double? SystemCurrentMilliseconds)
{
    public bool Succeeded => CallCompleted && NtStatus >= 0;
    public double RequestedMilliseconds => ProcessPerf.ConvertTimerResolution100nsToMilliseconds(Requested100ns);
    public double ReturnedActualMilliseconds => ProcessPerf.ConvertTimerResolution100nsToMilliseconds(ReturnedActual100ns);

    public string DiagnosticText(string action)
        => $"{action}：请求={RequestedMilliseconds:0.####}ms/{Requested100ns}×100ns，" +
           $"NTSTATUS=0x{unchecked((uint)NtStatus):X8}，返回实际={ReturnedActualMilliseconds:0.####}ms/{ReturnedActual100ns}×100ns，" +
           $"系统读数={(SystemCurrentMilliseconds.HasValue ? $"{SystemCurrentMilliseconds.Value:0.####}ms" : "不可读")}";
}
