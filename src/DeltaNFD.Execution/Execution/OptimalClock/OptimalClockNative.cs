using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DeltaNFD.Execution.OptimalClock;

/// <summary>一次候选值的实测统计（全部单位 ms）。</summary>
internal sealed record OptimalClockSample
{
    public required uint Requested100ns { get; init; }
    public required double Median { get; init; }
    public required double Mean { get; init; }
    public required double P95 { get; init; }
    public required double Min { get; init; }
    public required double Max { get; init; }
    public required double Over130Pct { get; init; }
    public required int Samples { get; init; }
    public required uint Granted100ns { get; init; }
    public required uint Current100ns { get; init; }
    public uint? ObservedBefore100ns { get; init; }
    public uint? ObservedAfter100ns { get; init; }

    public double RequestedMilliseconds => Requested100ns / 10000.0;
}

/// <summary>
/// 计时器分辨率原生原语与 Sleep(1) 采样统计。
/// 说明：Win11 会对「长驻且反复改值」的进程静默忽略高精度请求，所以**每次测量都必须用全新进程**，
/// 本类只负责单次请求/查询/采样，不承担跨候选复用。
/// </summary>
internal static class OptimalClockNative
{
    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimumTime100ns, out uint maximumTime100ns, out uint currentTime100ns);

    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint desiredResolution,
        [MarshalAs(UnmanagedType.U1)] bool setResolution, out uint currentResolution);

    internal const uint Default100ns = 156_250;
    internal const uint OneMillisecond100ns = 10_000;

    /// <summary>请求本进程的定时器分辨率；NTSTATUS=0 视为接受。</summary>
    internal static bool TryRequest(uint desired100ns, out uint granted100ns, out string error)
    {
        try
        {
            var status = NtSetTimerResolution(desired100ns, true, out granted100ns);
            error = status == 0 ? "" : "NtSetTimerResolution 失败：NTSTATUS 0x" + status.ToString("X8");
            return status == 0;
        }
        catch (Exception ex)
        {
            granted100ns = 0;
            error = "请求定时器分辨率异常：" + ex.GetType().Name + "：" + ex.Message;
            return false;
        }
    }

    /// <summary>释放本进程此前请求的定时器分辨率。</summary>
    internal static bool TryRelease(uint desired100ns, out uint granted100ns, out string error)
    {
        try
        {
            var status = NtSetTimerResolution(desired100ns, false, out granted100ns);
            error = status == 0 ? "" : "释放定时器分辨率失败：NTSTATUS 0x" + status.ToString("X8");
            return status == 0;
        }
        catch (Exception ex)
        {
            granted100ns = 0;
            error = "释放定时器分辨率异常：" + ex.GetType().Name + "：" + ex.Message;
            return false;
        }
    }

    /// <summary>只读查询当前系统定时器分辨率（100ns；读取失败返回 0）。</summary>
    internal static uint QueryCurrent100ns()
    {
        try { return NtQueryTimerResolution(out _, out _, out var current) < 0 ? 0 : current; }
        catch { return 0; }
    }

    /// <summary>
    /// 采样 Sleep(1) 的实际等待（ms）。spinMs&gt;0 时在每次 Sleep 前自旋，避免核心进深 C-state 虚增延迟。
    /// </summary>
    internal static OptimalClockSample Measure(uint requested100ns, int samples, int spinMs = 0, uint granted100ns = 0, uint current100ns = 0)
    {
        if (samples < 5) samples = 5;
        var values = new List<double>(samples);
        var watch = Stopwatch.StartNew();
        var previous = watch.ElapsedTicks;
        for (var i = 0; i < samples; i++)
        {
            if (spinMs > 0)
            {
                var spinUntil = watch.ElapsedTicks + spinMs * Stopwatch.Frequency / 1000;
                while (watch.ElapsedTicks < spinUntil) Thread.SpinWait(4);
            }
            Thread.Sleep(1);
            var now = watch.ElapsedTicks;
            values.Add((now - previous) * 1000.0 / Stopwatch.Frequency);
            previous = now;
        }

        values.Sort();
        var n = values.Count;
        var median = n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2.0;
        var over = values.Count(v => v >= 1.30);
        return new OptimalClockSample
        {
            Requested100ns = requested100ns,
            Granted100ns = granted100ns,
            Current100ns = current100ns,
            Median = Math.Round(median, 4),
            Mean = Math.Round(values.Average(), 4),
            P95 = Math.Round(values[Math.Min(n - 1, (int)Math.Floor(n * 0.95))], 4),
            Min = Math.Round(values[0], 4),
            Max = Math.Round(values[^1], 4),
            Over130Pct = Math.Round(100.0 * over / n, 1),
            Samples = n,
        };
    }

    internal static string Format(double value) => value.ToString("0.0000", CultureInfo.InvariantCulture);
}
