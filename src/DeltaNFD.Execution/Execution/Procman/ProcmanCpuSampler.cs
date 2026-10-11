using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DeltaNFD.Execution.Procman;

internal sealed record ProcmanCpuSample(double? Total, IReadOnlyList<(ProcmanProcessIdentity Identity, double Cpu)> Processes);
internal sealed class ProcmanCpuSampler
{
    private readonly Dictionary<string, (long Cpu, long Time)> _previous = new();
    private ulong _lastIdle, _lastKernel, _lastUser;
    private long _lastSample;
    private ProcmanCpuSample _cached = new(null, []);
    internal ProcmanCpuSample Sample()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastSample != 0 && Stopwatch.GetElapsedTime(_lastSample, now).TotalMilliseconds < 500) return _cached;
        _lastSample = now;
        double? total = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var elapsed = kernel - _lastKernel + user - _lastUser;
            if (_lastKernel != 0 && elapsed > 0) total = Math.Clamp(100.0 * (elapsed - (idle - _lastIdle)) / elapsed, 0, 100);
            _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
        }
        var cpus = Math.Max(1, ProcmanTopology.SystemGroupMasks().Sum(m => BitOperations.PopCount(m.Mask)));
        var seen = new HashSet<string>();
        var result = new List<(ProcmanProcessIdentity, double)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var identity = ProcmanIdentity.TryIdentify(process.Id);
                    if (identity is null || process.StartTime.ToUniversalTime().Ticks != identity.StartTimeUtcTicks) continue;
                    var cpu = process.TotalProcessorTime.Ticks;
                    var key = ProcmanChangeLog.ChangeKey(identity.Pid, identity.StartTimeUtcTicks, identity.ExecutablePath, "cpu");
                    seen.Add(key);
                    if (_previous.TryGetValue(key, out var previous))
                    {
                        var seconds = Stopwatch.GetElapsedTime(previous.Time, now).TotalSeconds;
                        if (seconds > 0 && cpu >= previous.Cpu)
                            result.Add((identity, Math.Clamp(100 * (cpu - previous.Cpu) / (TimeSpan.TicksPerSecond * seconds * cpus), 0, 100)));
                    }
                    _previous[key] = (cpu, now);
                }
                catch { }
            }
        }
        foreach (var key in _previous.Keys.Where(k => !seen.Contains(k)).ToArray()) _previous.Remove(key);
        return _cached = new(total, result);
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
