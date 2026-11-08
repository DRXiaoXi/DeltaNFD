using System.Diagnostics;
using System.Globalization;
using DeltaNFD.Native;

namespace DeltaNFD.Execution.OptimalClock;

/// <summary>
/// 「钟摆计划」隐藏子进程入口。每个候选值都在**全新进程**里请求并测量，
/// 规避 Win11 对长驻进程反复改值后静默忽略的问题。
///
/// 参数约定（由后端/自检拉起，不面向用户）：
///   --optimal-clock-hold &lt;100ns&gt;            请求该值并常驻持有，打印 HELD 行，等待父进程结束
///   --optimal-clock-measure &lt;samples&gt; [spin] 不请求任何精度，仅实测等待粒度，打印 MEAS 行
///   --optimal-clock-self &lt;100ns&gt; &lt;samples&gt;    请求该值并测量自身，打印 SELF 行（按进程语义的自检路径）
///   --optimal-clock-load &lt;threads&gt; [maxSeconds] 制造 CPU 负载（每线程 100ms 自旋后让出一次），打印 LOAD 行，
///                                                 stdin 关闭或到硬上限即退出（与 timer-tuning/stress.ps1 同款做法）
/// </summary>
public static class OptimalClockProbeHost
{
    internal const string HoldArgument = "--optimal-clock-hold";
    internal const string MeasureArgument = "--optimal-clock-measure";
    internal const string SelfArgument = "--optimal-clock-self";
    internal const string LoadArgument = "--optimal-clock-load";

    /// <summary>返回 true 表示已按子进程模式执行（调用方应直接结束进程）。</summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0) return false;
        try
        {
            if (args[0] == HoldArgument && args.Length == 2 && uint.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var holdResolution))
            {
                exitCode = Hold(holdResolution);
                return true;
            }
            if (args[0] == MeasureArgument && args.Length >= 2 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var samples))
            {
                var spin = args.Length >= 3 && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var spinMs) ? spinMs : 0;
                exitCode = Measure(samples, spin);
                return true;
            }
            if (args[0] == LoadArgument && args.Length >= 2
                && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var loadThreads))
            {
                var maxSeconds = args.Length >= 3 && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var loadSeconds) ? loadSeconds : 120;
                exitCode = Load(loadThreads, maxSeconds);
                return true;
            }
            if (args[0] == SelfArgument && args.Length >= 3
                && uint.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var selfResolution)
                && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var selfSamples))
            {
                exitCode = Self(selfResolution, selfSamples);
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name + "：" + ex.Message);
            exitCode = 3;
            return true;
        }
        return false;
    }

    private static int Hold(uint resolution100ns)
    {
        // 持有进程没有可见窗口，不能把默认后台策略当作持续保留精度请求。
        // 先设置并回读自身策略；失败时不发出请求，也不向父进程假报就绪。
        var supportsPolicy = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        if (!OptimalClockTimerPolicy.TryHonorRequests(out var policyError, supportsPolicy: supportsPolicy))
        {
            Console.Out.WriteLine($"HELD:false:{resolution100ns}:0:{OptimalClockNative.QueryCurrent100ns()}:{policyError}");
            Console.Out.Flush();
            return 2;
        }
        var ok = OptimalClockNative.TryRequest(resolution100ns, out var granted, out var error);
        try
        {
            var current = OptimalClockNative.QueryCurrent100ns();
            Console.Out.WriteLine($"HELD:{ok.ToString().ToLowerInvariant()}:{resolution100ns}:{granted}:{current}:{error}");
            Console.Out.Flush();
            if (!ok) return 2;
            // 常驻持有：父进程结束后自然释放；stdin 关闭时也退出，避免残留。
            var stdin = Console.In;
            try { while (stdin.ReadLine() is not null) { } }
            catch { }
            return 0;
        }
        finally { if (ok) OptimalClockNative.TryRelease(resolution100ns, out _, out _); }
    }

    private static int Measure(int samples, int spinMs)
    {
        var before = OptimalClockNative.QueryCurrent100ns();
        var sample = OptimalClockNative.Measure(0, samples, spinMs);
        var after = OptimalClockNative.QueryCurrent100ns();
        Console.Out.WriteLine($"MEAS:{OptimalClockNative.Format(sample.Median)}:{OptimalClockNative.Format(sample.Mean)}:"
            + $"{OptimalClockNative.Format(sample.P95)}:{OptimalClockNative.Format(sample.Min)}:{OptimalClockNative.Format(sample.Max)}:"
            + $"{OptimalClockNative.Format(sample.Over130Pct)}:{sample.Samples}:{before}:{after}");
        Console.Out.Flush();
        return 0;
    }

    /// <summary>
    /// CPU 负载：每个线程「自旋 100ms → 让出一次」，与 timer-tuning/stress.ps1 的负载方式一致。
    /// 三层保护：父进程关闭 stdin 即退出、硬上限秒数、后台线程不阻止进程结束。
    /// </summary>
    private static int Load(int threads, int maxSeconds)
    {
        threads = Math.Clamp(threads, 1, 256);
        var deadline = Environment.TickCount64 + Math.Max(5, maxSeconds) * 1000L;
        for (var i = 0; i < threads; i++)
        {
            var worker = new Thread(() =>
            {
                var spinTicks = (long)(Stopwatch.Frequency * 0.1);
                while (Environment.TickCount64 < deadline)
                {
                    var start = Stopwatch.GetTimestamp();
                    while (Stopwatch.GetTimestamp() - start < spinTicks) { }
                    Thread.Sleep(0);
                }
            }, 1_000_000) { IsBackground = true };
            worker.Start();
        }
        Console.Out.WriteLine($"LOAD:{threads}");
        Console.Out.Flush();
        try
        {
            var remaining = Math.Max(0, deadline - Environment.TickCount64);
            // 硬上限同时结束主线程；不能只停工作线程后仍存活并假装负载有效。
            Console.In.ReadLineAsync().Wait(TimeSpan.FromMilliseconds(remaining));
        }
        catch { }
        return 0;
    }

    private static int Self(uint resolution100ns, int samples)
    {
        var ok = OptimalClockNative.TryRequest(resolution100ns, out var granted, out var error);
        if (!ok)
        {
            Console.Out.WriteLine($"SELF:false:{resolution100ns}:{granted}:{OptimalClockNative.QueryCurrent100ns()}:{error}");
            Console.Out.Flush();
            return 2;
        }
        Thread.Sleep(10);
        var current = OptimalClockNative.QueryCurrent100ns();
        var sample = OptimalClockNative.Measure(resolution100ns, samples, 0, granted, current);
        OptimalClockNative.TryRelease(resolution100ns, out _, out _);
        Console.Out.WriteLine($"SELF:true:{resolution100ns}:{granted}:{current}:{OptimalClockNative.Format(sample.Median)}:"
            + $"{OptimalClockNative.Format(sample.Mean)}:{OptimalClockNative.Format(sample.P95)}:"
            + $"{OptimalClockNative.Format(sample.Min)}:{OptimalClockNative.Format(sample.Max)}:{OptimalClockNative.Format(sample.Over130Pct)}:{sample.Samples}");
        Console.Out.Flush();
        return 0;
    }
}
