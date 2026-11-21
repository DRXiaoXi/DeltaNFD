using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace DeltaNFD.Execution.OptimalClock;

internal static class WakeDeadlineProbe
{
    internal const string Argument = "--pendulum-wake-block-v1";
    internal static int Run(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out var n) || n is not (40 or 96)) return 2;
        var before = OptimalClockNative.QueryCurrent100ns();
        var sleep = OptimalClockNative.Measure(0,n,0);
        var late = new double[n]; long origin = Stopwatch.GetTimestamp(), freq = Stopwatch.Frequency, slot = 1;
        int skipped = 0;
        for (int i=0; i<n; i++)
        {
            long deadline = origin + slot * freq / 144, now;
            while ((now = Stopwatch.GetTimestamp()) < deadline)
                Thread.Sleep(Math.Max(1,(int)((deadline-now)*1000/freq)));
            late[i] = (now-deadline)*1000d/freq;
            long next = Math.Max(slot+1, (now-origin)*144/freq+1);
            skipped = checked(skipped + (int)(next-slot-1)); slot = next;
        }
        Array.Sort(late);
        var block = new WakeBlock(n,before,OptimalClockNative.QueryCurrent100ns(),sleep.Median,sleep.P95,
            (late[n/2-1]+late[n/2])/2,late[(int)Math.Ceiling(.95*n)-1], skipped);
        Console.WriteLine("WAKE:"+JsonSerializer.Serialize(block)); return 0;
    }
}

internal sealed partial class NativeOptimalClockMeasurementApi
{
    internal WakeBlock? MeasureWakeBlock(uint? request, int n)
    {
        uint apiReading=0; IOptimalClockChildProcess? holder=null;
        try
        {
            if (request is uint value)
            {
                holder = StartChild(OptimalClockProbeHost.HoldArgument,value.ToString(CultureInfo.InvariantCulture));
                var fields = ReadLine(holder,"HELD:")?.Split(':');
                if (fields is not {Length: >=6} || fields[1] != "true" || !uint.TryParse(fields[2],out var echoed) || echoed!=value || !uint.TryParse(fields[3],out apiReading) || apiReading==0) return null;
            }
            var observer = StartChild(WakeDeadlineProbe.Argument,n.ToString(CultureInfo.InvariantCulture));
            try
            {
                var line=ReadLine(observer,"WAKE:");
                if (line is null || holder?.HasExited == true) return null;
                var block=JsonSerializer.Deserialize<WakeBlock>(line[5..]);
                return block is null ? null : block with {ApiReading=apiReading};
            }
            finally { Kill(observer); }
        }
        finally { if (holder is not null) Kill(holder); }
    }
}
