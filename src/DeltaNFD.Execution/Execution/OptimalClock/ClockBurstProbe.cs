using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.OptimalClock;

internal sealed record ClockBurstMeasurement(uint Before, uint After, double[] JudgedMedians, double[]? Waits = null, ClockSamplingMask? SampledAffinity = null)
{
    internal bool IsComplete(uint requested) => ClockBurstProbe.Tracks(Before, requested) && ClockBurstProbe.Tracks(After, requested)
        && JudgedMedians is { Length: ClockBurstProbe.JudgedRounds }
        && JudgedMedians.All(IsValidWait);

    internal bool HasAllWaits => Waits is { Length: ClockBurstProbe.JudgedRounds * ClockBurstProbe.SamplesPerRound }
        && Waits.All(IsValidWait);
    internal bool Passes(uint requested) => IsComplete(requested) && JudgedMedians.Count(ClockBurstProbe.OverLimit) <= 1;

    private static bool IsValidWait(double value) => double.IsFinite(value) && value > 0;
}

internal enum ClockBurstProbeMode { FirstPassing, Full, Screening }

/// <summary>One no-request observer per candidate, with the holder alive across all rounds.</summary>
internal static class ClockBurstProbe
{
    internal const string Argument = "--pendulum-clock-bursts-v1";
    internal const string FullRangeArgument = "--pendulum-clock-bursts-v2";
    internal const string ScreeningArgument = "--pendulum-clock-screen-v1";
    internal const string Cpu0Option = "--cpu0";
    internal const string Cpu2Option = "--cpu2";
    internal const string CpuOption = "--cpu";
    internal const string CpuMaskOption = "--cpu-mask";
    internal const string AffinityErrorPrefix = Prefix + "AFFINITY-ERROR:";
    private const int ScreeningIntervalMs = 200;
    private const int FullIntervalMs = 1000;
    internal const int SamplesPerRound = 5;
    internal const int JudgedRounds = 13;
    private const int ObservationRounds = 2;
    internal const string Prefix = "BURSTS:";
    internal static bool OverLimit(double milliseconds) => milliseconds > 1.4;

    // Allow sub-step kernel rounding. An adjacent candidate must never pass this check.
    internal static bool Tracks(uint actual, uint requested)
        => actual > 0 && Math.Abs((long)actual - requested) < PendulumFramePolicy.BurstScanStep100ns / 2;

    internal static int Run(string[] args)
    {
        ClockSamplingMask? samplingCpu = args switch
        {
            [_, _, Cpu0Option] => new(0, 1),
            [_, _, Cpu2Option] => new(0, 4),
            [_, _, CpuOption, var group, var index] when ushort.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture, out var groupNumber)
                && byte.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var processor) && processor < 64
                => new(groupNumber, 1UL << processor),
            [_, _, CpuMaskOption, var group, var bits] when ushort.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture, out var groupNumber)
                && ulong.TryParse(bits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var mask) && mask != 0
                => new(groupNumber, mask),
            _ => null,
        };
        if ((args.Length != 2 && samplingCpu is null) || !uint.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var requested)
            || !PendulumFramePolicy.IsBurstScanValue(requested) || (samplingCpu is not null && args[0] == Argument)) return 2;

        var mode = args[0] switch
        {
            ScreeningArgument => ClockBurstProbeMode.Screening,
            FullRangeArgument => ClockBurstProbeMode.Full,
            _ => ClockBurstProbeMode.FirstPassing,
        };

        // The observer is a short-lived process. Its synchronous sampling thread keeps the mask until it exits.
        if (samplingCpu is { } target && ClockSamplingAffinity.TryPin(target) is { } error) return AffinityFailed(error);
        var result = Sample(requested, OptimalClockNative.QueryCurrent100ns, MeasureSleep, Thread.Sleep, mode);
        if (result is null) return 2;
        if (samplingCpu is { } cpu && !ClockSamplingAffinity.Matches(cpu)) return AffinityFailed($"采样结束时无法确认 {cpu.Display} 亲和性。");
        Console.WriteLine(Prefix + JsonSerializer.Serialize(result with { SampledAffinity = samplingCpu }));
        return 0;
    }

    private static int AffinityFailed(string error)
    {
        Console.WriteLine(AffinityErrorPrefix + error);
        return 2;
    }

    internal static ClockBurstMeasurement? Sample(uint requested, Func<uint> query, Func<double> measureSleep, Action<int> wait,
        ClockBurstProbeMode mode = ClockBurstProbeMode.FirstPassing)
    {
        var full = mode != ClockBurstProbeMode.FirstPassing;
        var interval = mode == ClockBurstProbeMode.Screening ? ScreeningIntervalMs : FullIntervalMs;
        var before = query();
        if (!Tracks(before, requested)) return null;
        for (var warmup = 0; warmup < 2; warmup++)
        {
            wait(interval);
            measureSleep();
        }

        var medians = new List<double>(JudgedRounds);
        var waits = full ? new double[JudgedRounds * SamplesPerRound] : null;
        var failures = 0;
        for (var round = 0; round < ObservationRounds + JudgedRounds; round++)
        {
            if (round > 0) wait(interval);
            if (!Tracks(query(), requested)) return null;
            var sampleSlot = waits is not null && round >= ObservationRounds
                ? waits.AsSpan((round - ObservationRounds) * SamplesPerRound, SamplesPerRound) : Span<double>.Empty;
            var median = RoundMedian(measureSleep, sampleSlot);
            if (round < ObservationRounds) continue;
            medians.Add(median);
            if (!full && OverLimit(median) && ++failures == 2) break;
        }
        return new ClockBurstMeasurement(before, query(), medians.ToArray(), waits);
    }

    private static double RoundMedian(Func<double> measureSleep, Span<double> sampleSlot)
    {
        Span<double> values = stackalloc double[SamplesPerRound];
        for (var i = 0; i < values.Length; i++) values[i] = measureSleep();
        if (!sampleSlot.IsEmpty) values.CopyTo(sampleSlot);
        values.Sort();
        return values[values.Length / 2];
    }

    private static double MeasureSleep()
    {
        var start = Stopwatch.GetTimestamp();
        Thread.Sleep(1);
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
}

internal static class ClockSamplingAffinity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity
    {
        public nuint Mask;
        public ushort Group, Reserved0, Reserved1, Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorNumber
    {
        public ushort Group;
        public byte Number, Reserved;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadGroupAffinity(nint thread, in GroupAffinity affinity, out GroupAffinity previous);

    [DllImport("kernel32.dll")]
    private static extern void GetCurrentProcessorNumberEx(out ProcessorNumber number);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadGroupAffinity(nint thread, out GroupAffinity affinity);

    internal static string? TryPin(ClockSamplingMask cpu)
    {
        if (cpu.Mask == 0 || (IntPtr.Size == 4 && cpu.Mask > uint.MaxValue)) throw new ArgumentOutOfRangeException(nameof(cpu));
        var affinity = new GroupAffinity { Group = cpu.Group, Mask = (nuint)cpu.Mask };
        if (!SetThreadGroupAffinity(GetCurrentThread(), in affinity, out _))
            return $"设置 {cpu.Display} 亲和性失败（Win32 错误 {Marshal.GetLastWin32Error()}）。";
        return Matches(cpu) ? null : $"设置亲和性后未确认 {cpu.Display}。";
    }

    internal static bool Matches(ClockSamplingMask cpu)
    {
        if (!GetThreadGroupAffinity(GetCurrentThread(), out var affinity)
            || affinity.Group != cpu.Group || (ulong)affinity.Mask != cpu.Mask) return false;
        GetCurrentProcessorNumberEx(out var current);
        return current.Group == cpu.Group && current.Number < 64 && (cpu.Mask & (1UL << current.Number)) != 0;
    }
}

internal sealed partial class NativeOptimalClockMeasurementApi
{
    public ClockBurstMeasurement? MeasureBurst(uint resolution100ns, CancellationToken token,
        ClockBurstProbeMode mode = ClockBurstProbeMode.FirstPassing, ClockSamplingMask? samplingCpu = null)
    {
        token.ThrowIfCancellationRequested();
        var value = resolution100ns.ToString(CultureInfo.InvariantCulture);
        var holder = StartChild(OptimalClockProbeHost.HoldArgument, value);
        try
        {
            var fields = ReadLine(holder, "HELD:", token)?.Split(':');
            if (fields is not { Length: >= 6 } || fields[1] != "true" || fields[2] != value
                || !uint.TryParse(fields[3], out var granted) || granted == 0
                || !uint.TryParse(fields[4], out var current) || !ClockBurstProbe.Tracks(current, resolution100ns))
                return null;

            // Full-range attribution comes from the query readback here and throughout the independent observation.
            // The set call's immediate readout can differ from the following query on the same holder.
            if (mode == ClockBurstProbeMode.FirstPassing && !ClockBurstProbe.Tracks(granted, resolution100ns)) return null;

            token.ThrowIfCancellationRequested();
            var argument = mode switch
            {
                ClockBurstProbeMode.Screening => ClockBurstProbe.ScreeningArgument,
                ClockBurstProbeMode.Full => ClockBurstProbe.FullRangeArgument,
                _ => ClockBurstProbe.Argument,
            };
            var observer = StartChild(argument, samplingCpu switch
            {
                null => [value],
                { Group: 0, Mask: 1 } => [value, ClockBurstProbe.Cpu0Option],
                { Group: 0, Mask: 4 } => [value, ClockBurstProbe.Cpu2Option],
                { } cpu => [value, ClockBurstProbe.CpuMaskOption, cpu.Group.ToString(CultureInfo.InvariantCulture), cpu.Mask.ToString("X", CultureInfo.InvariantCulture)],
            });
            try
            {
                var line = ReadLine(observer, ClockBurstProbe.Prefix, token);
                if (line is null || holder.HasExited) return null;
                if (samplingCpu is not null && line.StartsWith(ClockBurstProbe.AffinityErrorPrefix, StringComparison.Ordinal))
                    throw new InvalidOperationException(line[ClockBurstProbe.AffinityErrorPrefix.Length..]);
                var measurement = JsonSerializer.Deserialize<ClockBurstMeasurement>(line[ClockBurstProbe.Prefix.Length..]);
                if (samplingCpu is { } cpu && measurement?.SampledAffinity != cpu)
                    throw new InvalidOperationException($"采样进程未确认绑定 {cpu.Display}，已中止扫描。");
                return measurement;
            }
            finally { Kill(observer); }
        }
        finally { Kill(holder); }
    }
}
