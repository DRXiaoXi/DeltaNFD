using System.Diagnostics;
using System.Runtime.InteropServices;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Native;

/// <summary>
/// 判别实验（仅用于本轮取证，自建子进程）：
/// 1) 子进程请求 0.5ms 后自身 Sleep(1) 粒度是多少；
/// 2) 父进程对它设置 0x4 位后，粒度回落到多少（系统当前值 or 硬默认 15.6ms）；
/// 3) 父进程再持有 1ms 请求，子进程粒度能否变成 1ms；
/// 4) 父进程持有 0.5ms 请求时，被忽略的子进程粒度是否跟随系统值。
/// 只操作本测试自建子进程与自身进程，不触碰真实游戏。
/// </summary>
internal static class TimerGranularityProbe
{
    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint desiredResolution,
        [MarshalAs(UnmanagedType.U1)] bool setResolution, out uint currentResolution);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    private static double MeasureGranularity(int samples = 25)
    {
        var sw = Stopwatch.StartNew();
        var prev = sw.ElapsedTicks;
        var deltas = new List<double>(samples);
        for (var i = 0; i < samples; i++)
        {
            Thread.Sleep(1);
            var now = sw.ElapsedTicks;
            deltas.Add((now - prev) * 1000.0 / Stopwatch.Frequency);
            prev = now;
        }
        deltas.Sort();
        return Math.Round(deltas[samples / 2], 2);
    }

    private static string MeasureLine()
    {
        var median = MeasureGranularity();
        NtQueryTimerResolution(out _, out _, out var current);
        return "GRAN:" + median.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            + ":SYS:" + (current / 10000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>子进程：响应父进程命令（M=测量、Q=请求0.5ms、R=释放、X=退出）。</summary>
    internal static void Child()
    {
        Console.Out.WriteLine("READY");
        Console.Out.Flush();
        while (true)
        {
            var line = Console.In.ReadLine();
            if (line is null) break;
            line = line.Trim();
            if (line == "X") break;
            if (line == "Q")
            {
                var st = NtSetTimerResolution(5_000, true, out var actual);
                Console.Out.WriteLine("ACK:Q:" + st + ":" + actual);
            }
            else if (line == "R")
            {
                var st = NtSetTimerResolution(5_000, false, out var actual);
                Console.Out.WriteLine("ACK:R:" + st + ":" + actual);
            }
            else if (line == "M")
            {
                Console.Out.WriteLine(MeasureLine());
            }
            Console.Out.Flush();
        }
    }

    internal static void Run()
    {
        var baseline = ProcessPerf.QueryTimerResolutionMs();
        Console.WriteLine("系统读数基线：" + baseline + " ms");

        using var child = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.ProcessPath!, "--timer-granularity-child")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        child.Start();
        try
        {
            var ready = child.StandardOutput.ReadLine();
            Console.WriteLine("子进程：" + ready);
            void Send(string cmd) { child.StandardInput.WriteLine(cmd); child.StandardInput.Flush(); }
            string Read() => child.StandardOutput.ReadLine() ?? "(null)";

            // 阶段 0：子进程未请求任何精度。
            Send("M");
            Console.WriteLine("阶段0 子进程未请求      → " + Read());

            // 阶段 1：子进程请求 0.5ms。
            Send("Q");
            Console.WriteLine("阶段1 子进程请求0.5ms   → " + Read());
            Send("M");
            Console.WriteLine("阶段1 粒度              → " + Read());

            // 阶段 2：父进程对该子进程设置 0x4 位（忽略其精度请求）。
            var id = ProcmanIdentity.TryIdentify(child.Id);
            var before = ProcmanCapabilities.ReadPowerThrottling(child.Id);
            var original = new FrameTimerPowerState(before.Control, before.State);
            var applied = original.Apply();
            var write = ProcmanCapabilities.WritePowerThrottling(child.Id, applied.Control, applied.State);
            Console.WriteLine("阶段2 置0x4位           → ok=" + write.Ok + " " + write.Error);
            Thread.Sleep(300);
            Send("M");
            Console.WriteLine("阶段2 粒度（被忽略）    → " + Read());

            // 阶段 3：父进程持有 1ms 请求。
            var req1 = NtSetTimerResolution(10_000, true, out var act1);
            Console.WriteLine("阶段3 父进程请求1ms     → status=" + req1 + " actual=" + act1 + " (" + (act1 / 10000.0) + "ms)");
            Thread.Sleep(300);
            Send("M");
            Console.WriteLine("阶段3 粒度（1ms地板）   → " + Read());

            // 阶段 4：父进程改持 0.5ms 请求，观察被忽略子进程是否跟随系统值。
            var req05 = NtSetTimerResolution(5_000, true, out var act05);
            Console.WriteLine("阶段4 父进程请求0.5ms   → status=" + req05 + " actual=" + act05 + " (" + (act05 / 10000.0) + "ms)");
            Thread.Sleep(300);
            Send("M");
            Console.WriteLine("阶段4 粒度（系统0.5ms） → " + Read());

            // 阶段 5：父进程回到 1ms。
            NtSetTimerResolution(10_000, true, out _);
            Thread.Sleep(300);
            Send("M");
            Console.WriteLine("阶段5 粒度（回到1ms）   → " + Read());

            // 清理：撤销子进程的位、释放父进程请求。
            ProcmanCapabilities.WritePowerThrottling(child.Id, original.Control, original.State);
            NtSetTimerResolution(10_000, false, out _);
            Thread.Sleep(300);
            Send("M");
            Console.WriteLine("清理 粒度（子进程自持0.5ms）→ " + Read());
        }
        finally
        {
            try { child.StandardInput.WriteLine("X"); child.StandardInput.Flush(); } catch { }
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
            try { child.WaitForExit(3000); } catch { }
            Thread.Sleep(500);
            Console.WriteLine("清理后系统读数：" + ProcessPerf.QueryTimerResolutionMs() + " ms（基线 " + baseline + " ms）");
        }
    }
}
