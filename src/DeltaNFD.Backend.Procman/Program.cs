using DeltaNFD.Backends;

namespace DeltaNFD.Backend.Procman;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // 「钟摆计划」隐藏子进程：全新进程持有/测量（Win11 会对长驻反复改值的进程静默忽略请求）。
        if (DeltaNFD.Execution.OptimalClock.OptimalClockProbeHost.TryRun(args, out var clockExitCode))
            return clockExitCode;

        // 隐藏探针子进程：只测量等待粒度，不请求任何定时器精度。
        // 用于判定「无请求进程能否获得 1ms」，从而决定是否允许施加忽略位。
        if (args is ["--timer-granularity-probe-child"])
        {
            var median = DeltaNFD.Native.ProcessPerf.MeasureWaitGranularityMedianMs();
            Console.Out.WriteLine("GRAN:" + median.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            Console.Out.Flush();
            return 0;
        }
        return await BackendHost.RunAsync(ProcmanCommands.Options, args);
    }
}
