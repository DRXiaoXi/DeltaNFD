using System.Runtime.InteropServices;

namespace DeltaNFD.Native;

// ---------------- 系统定时器分辨率：只读查询 + 等待粒度实测 ----------------
// 本产品不再持有任何「1ms 地板」请求。时钟由「钟摆计划」模块（procman 后端）单独负责；
// 这里只保留只读查询与「无请求进程等待粒度」实测，供忽略位门禁判断系统当前时钟是否够细。

/// <summary>系统定时器分辨率只读查询与等待粒度实测。</summary>
public static class ProcessPerf
{
    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimumTime100ns, out uint maximumTime100ns, out uint currentTime100ns);

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

    /// <summary>
    /// 实测本进程的等待粒度（Sleep(1) 的中位数，ms）。
    /// 默认 15.625ms 分辨率下约 15.6ms；全局锁定较细时钟时约 1.0～1.5ms。
    /// 用于判定「无请求进程能否获得较细时钟」。
    /// </summary>
    public static double MeasureWaitGranularityMedianMs(int samples = 21)
    {
        if (samples < 5) samples = 5;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var previous = sw.ElapsedTicks;
        var deltas = new List<double>(samples);
        for (var i = 0; i < samples; i++)
        {
            Thread.Sleep(1);
            var now = sw.ElapsedTicks;
            deltas.Add((now - previous) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            previous = now;
        }
        deltas.Sort();
        return deltas[samples / 2];
    }

    internal static double ConvertTimerResolution100nsToMilliseconds(uint value100ns)
        => value100ns / 10000.0;
}
