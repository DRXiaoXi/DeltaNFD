using System.Diagnostics;
using System.Globalization;

namespace DeltaNFD.Execution.Procman;

internal interface IFrameTimerReachabilityApi
{
    /// <summary>在一个「不请求任何精度」的进程里实测等待粒度中位数（ms）；失败返回 null。</summary>
    double? MeasureRequestlessGranularityMs();
}

/// <summary>
/// 时钟可达性探针：实测「无请求进程」的等待粒度。
/// 只有实测 ≤ 5ms（说明系统 tick 已较细，被忽略的进程会继承全局 tick）才允许施加忽略位；
/// 否则忽略位会把目标进程压到 15.625ms，必须拒绝施加。
/// </summary>
internal sealed class FrameTimerLegacyProbe
{
    /// <summary>判定阈值：全局 tick 1ms/0.5ms 下实测约 1～2ms，15.625ms 下约 15.6ms，5ms 分离充分。</summary>
    internal const double ReachableThresholdMs = 5.0;

    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly IFrameTimerReachabilityApi _api;
    private double? _granularity;
    private bool? _reachable;
    private string _message = "尚未核对 1ms 可达性";
    private DateTime _measuredUtc = DateTime.MinValue;

    internal FrameTimerLegacyProbe(IFrameTimerReachabilityApi? api = null) => _api = api ?? new NativeFrameTimerReachabilityApi();

    internal double? GranularityMs { get { lock (_gate) return _granularity; } }
    internal bool? Reachable { get { lock (_gate) return _reachable; } }
    internal string Message { get { lock (_gate) return _message; } }

    internal bool? Probe(bool force = false)
    {
        lock (_gate)
        {
            if (!force && _reachable is not null && DateTime.UtcNow - _measuredUtc < Freshness) return _reachable;
            try
            {
                var measured = _api.MeasureRequestlessGranularityMs();
                _measuredUtc = DateTime.UtcNow;
                if (measured is null)
                {
                    _granularity = null;
                    _reachable = null;
                    _message = "无法核对系统时钟粒度（探针未能完成），未施加忽略位";
                    return null;
                }
                _granularity = measured;
                _reachable = measured <= ReachableThresholdMs;
                _message = _reachable.Value
                    ? $"已确认无请求进程可获得较细时钟（实测 {measured.Value:0.##} ms），忽略位不会降低三角洲精度"
                    : $"未确认：无请求进程实测 {measured.Value:0.##} ms（系统 tick 过粗），忽略位会把三角洲压到该值，已拒绝施加";
                return _reachable;
            }
            catch (Exception ex)
            {
                _measuredUtc = DateTime.UtcNow;
                _granularity = null;
                _reachable = null;
                _message = "1ms 可达性探针异常：" + ex.Message + "，未施加忽略位";
                return null;
            }
        }
    }

    internal void Invalidate()
    {
        lock (_gate)
        {
            _granularity = null;
            _reachable = null;
            _message = "尚未核对 1ms 可达性";
            _measuredUtc = DateTime.MinValue;
        }
    }
    internal void ResetForTests() => Invalidate();
}

internal sealed class NativeFrameTimerReachabilityApi : IFrameTimerReachabilityApi
{
    public double? MeasureRequestlessGranularityMs()
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(exe)) return null;
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo(exe, "--timer-granularity-probe-child")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        if (!child.Start()) return null;
        try
        {
            // 子进程异常/被安全软件拦截时不能让规则 tick 无限阻塞；超时后 finally 会终止它。
            var line = child.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(10))
                .GetAwaiter().GetResult();
            if (line is null || !line.StartsWith("GRAN:", StringComparison.Ordinal)) return null;
            return double.TryParse(line[5..], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ? ms : null;
        }
        finally
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
            child.Dispose();
        }
    }
}
