using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// 钟摆计划临时探针（本轮取证用）：
/// 每个候选值都在**全新进程**里请求并测量自身 Sleep(1)，避免 Win11 对老化进程静默忽略高精度请求。
/// </summary>
internal static class OptimalClockProbe
{
    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint desiredResolution,
        [MarshalAs(UnmanagedType.U1)] bool setResolution, out uint currentResolution);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    internal static void Child(string resolutionText, string samplesText)
    {
        var resolution = uint.Parse(resolutionText, System.Globalization.CultureInfo.InvariantCulture);
        var samples = int.Parse(samplesText, System.Globalization.CultureInfo.InvariantCulture);
        var status = NtSetTimerResolution(resolution, true, out var granted);
        Thread.Sleep(15);
        NtQueryTimerResolution(out _, out _, out var current);
        var values = new List<double>(samples);
        var sw = Stopwatch.StartNew();
        var previous = sw.ElapsedTicks;
        for (var i = 0; i < samples; i++)
        {
            Thread.Sleep(1);
            var now = sw.ElapsedTicks;
            values.Add((now - previous) * 1000.0 / Stopwatch.Frequency);
            previous = now;
        }
        NtSetTimerResolution(resolution, false, out _);
        values.Sort();
        var n = values.Count;
        var median = n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
        var over = values.Count(v => v >= 1.30);
        Console.Out.WriteLine($"RES:{resolution}:{status}:{granted}:{current}:{median.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}:"
            + $"{values.Min().ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}:"
            + $"{values.Max().ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}:"
            + $"{values.Average().ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}:"
            + $"{(100.0 * over / n).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}");
    }
}
