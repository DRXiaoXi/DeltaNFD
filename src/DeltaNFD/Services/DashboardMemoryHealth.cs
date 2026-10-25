using DeltaNFD.Inspection;
using DeltaNFD.ViewModels;

namespace DeltaNFD.Services;

internal sealed record DashboardMemoryReading(DashboardHealthState State, string Text);

internal static class DashboardMemoryHealth
{
    internal const ulong GB = 1024UL * 1024 * 1024;
    internal static bool SuggestRecovery(DashboardHealthState state) =>
        state is DashboardHealthState.Warning or DashboardHealthState.Abnormal;
    internal static DashboardMemoryReading Evaluate(PhysicalMemoryReadResult result, bool? gameRunning)
    {
        if (result.Reading is not { IsValid: true } reading)
            return new(DashboardHealthState.Information, "内存信息读取失败，待核对" + (result.Error.Length > 0 ? "：" + result.Error : "。"));
        var available = reading.AvailableBytes;
        // Compare raw bytes. Rounding the display must never alter the requested strict thresholds.
        var state = gameRunning switch
        {
            false => available < 8 * GB ? DashboardHealthState.Warning : DashboardHealthState.Normal,
            true when available < GB => DashboardHealthState.Abnormal,
            true when available < 2 * GB => DashboardHealthState.Warning,
            true => DashboardHealthState.Normal,
            _ => DashboardHealthState.Information,
        };
        static string Amount(ulong bytes) => (Math.Floor(bytes / (double)GB * 100) / 100).ToString("0.00");
        var usedPercent = (reading.TotalBytes - available) / (double)reading.TotalBytes * 100;
        var mode = gameRunning is true ? "三角洲运行中" : gameRunning is false ? "三角洲未运行" : "游戏状态待确认";
        var detail = gameRunning switch
        {
            false when state == DashboardHealthState.Warning => "；可用内存少于 8 GB",
            true when state == DashboardHealthState.Abnormal => "；可用内存少于 1 GB",
            true when state == DashboardHealthState.Warning => "；可用内存少于 2 GB",
            null => "；内存等级待确认",
            _ => "",
        };
        return new(state, $"{mode} · 可用 {Amount(available)} GB / 总计 {Amount(reading.TotalBytes)} GB · 已用 {usedPercent:0}%{detail}");
    }
    internal static string PausedSummary(DashboardHealthState state, string componentPauseReason) => state switch
    {
        DashboardHealthState.Abnormal => "可用内存严重不足；" + componentPauseReason,
        DashboardHealthState.Warning => "内存占用提醒；" + componentPauseReason,
        _ => componentPauseReason,
    };
}

/// <summary>Called by the existing heartbeat; never creates a timer or starts a backend.</summary>
internal sealed class DashboardMemorySampler(Func<PhysicalMemoryReadResult> read, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long? _lastRead;
    private bool? _lastGame;
    internal DashboardMemoryReading? Current { get; private set; }
    internal DashboardMemoryReading Refresh(bool? gameRunning, bool force = false)
    {
        var now = _time.GetTimestamp();
        if (!force && Current is not null && _lastGame == gameRunning && _lastRead is { } previous
            && _time.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(2)) return Current;
        PhysicalMemoryReadResult result;
        try { result = read(); } catch (Exception ex) { result = new(null, ex.Message); }
        _lastRead = now; _lastGame = gameRunning;
        return Current = DashboardMemoryHealth.Evaluate(result, gameRunning);
    }
}

/// <summary>Coalesced UI subscription with generation checks for leaving/re-entering the homepage.</summary>
internal sealed class DashboardMemoryObserver(DashboardMemorySampler sampler, Func<bool?> game,
    Func<Action, bool> dispatch)
{
    private readonly object _gate = new();
    private bool _active, _queued, _force;
    private int _generation;
    internal event Action<DashboardMemoryReading>? Updated;
    internal void Start() { lock (_gate) { _active = true; _generation++; _queued = false; _force = false; } Pulse(true); }
    internal void Stop() { lock (_gate) { _active = false; _generation++; _queued = false; _force = false; } }
    internal void Pulse(bool force = false)
    {
        int generation;
        lock (_gate)
        {
            if (!_active) return;
            _force |= force;
            if (_queued) return;
            _queued = true; generation = _generation;
        }
        if (!dispatch(() => Refresh(generation)))
            lock (_gate) if (_active && generation == _generation) _queued = false;
    }
    private void Refresh(int generation)
    {
        bool force;
        lock (_gate)
        {
            if (!_active || generation != _generation) return;
            force = _force; _force = false; _queued = false;
        }
        var result = sampler.Refresh(game(), force);
        lock (_gate) if (!_active || generation != _generation) return;
        Updated?.Invoke(result);
    }
}
