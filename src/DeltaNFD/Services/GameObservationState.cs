namespace DeltaNFD.Services;

/// <summary>Subscription state only. No polling, process lookup, system write or executor retry.</summary>
internal sealed class GameObservationState : IDisposable
{
    private readonly Func<CancellationToken, Task> _observe;
    private readonly Func<CancellationToken, Task> _automate;
    private readonly Func<bool> _blocked;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stateGate = new();
    private int _connecting, _disposed;
    internal GameObservationState(Func<CancellationToken, Task> observe, Func<CancellationToken, Task> automate,
        Func<bool> blocked, Func<DateTimeOffset>? clock = null)
    { _observe = observe; _automate = automate; _blocked = blocked; _clock = clock ?? (() => DateTimeOffset.UtcNow); }

    internal event Action? Changed;
    internal CpuFrameStatusSnapshot? Latest { get; private set; }
    internal string Failure { get; private set; } = "正在连接只读游戏状态观测。";
    internal string AutomationFailure { get; private set; } = "";
    internal bool IsConnecting => Volatile.Read(ref _connecting) != 0;
    internal bool? DeltaRunning => _blocked() ? null : ReadDelta(Latest, _clock());
    internal bool? TargetRunning
    {
        get
        {
            var snapshot = Latest;
            var now = _clock();
            return !Fresh(snapshot, now) || _blocked() ? null
                : snapshot!.IsCustomTarget ? snapshot.MonitorRunning ? snapshot.GameRunning : null : ReadDelta(snapshot, now);
        }
    }
    internal string Detail => DeltaRunning is not null ? "" : Failure.Length > 0 ? Failure
        : Latest?.DeltaForceObservationError is { Length: > 0 } error ? error : "尚未收到新鲜、可靠的三角洲运行状态。";

    internal static bool Fresh(CpuFrameStatusSnapshot? snapshot, DateTimeOffset now) => snapshot is not null
        && snapshot.Module == "cpuframe" && snapshot.UpdatedUtc <= now.AddSeconds(2) && now - snapshot.UpdatedUtc <= TimeSpan.FromSeconds(10);
    internal static bool? ReadDelta(CpuFrameStatusSnapshot? snapshot, DateTimeOffset now) => Fresh(snapshot, now)
        && snapshot!.DeltaForceObservedUtc is { } observed && observed <= now.AddSeconds(2)
        && now - observed <= TimeSpan.FromSeconds(10) ? snapshot.DeltaForceRunning : null;

    internal void Accept(CpuFrameStatusSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_blocked()) { Unavailable("脱机或恢复流程中，普通游戏观测未启动。"); return; }
        if (snapshot.Module != "cpuframe") { Unavailable("收到的游戏状态来源不匹配。"); return; }
        if (snapshot.UpdatedUtc > _clock().AddSeconds(2)) { Unavailable("游戏状态时间异常，请重新确认。"); return; }
        lock (_stateGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || Latest is { } previous && snapshot.UpdatedUtc < previous.UpdatedUtc) return;
            Latest = snapshot;
            Failure = "";
            if (snapshot.MonitorRunning) AutomationFailure = "";
            Notify();
        }
    }
    internal void Unavailable(string reason)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_stateGate) { Latest = null; Failure = reason; Notify(); }
    }

    // Bootstrap retains the existing normal automation behavior. Explicit reconnect is observation-only;
    // it must never replay a failed/unknown mutation or erase the retained recovery evidence.
    internal async Task ConnectAsync(bool bootstrapAutomation = false)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _connecting, 1, 0) != 0) return;
        try
        {
            if (_blocked()) { Unavailable("脱机或恢复流程中，请先完成核对；未启动普通游戏观测。"); return; }
            Failure = "正在重新确认三角洲运行状态。"; Notify();
            await _observe(_cts.Token).ConfigureAwait(false);
            if (bootstrapAutomation && !_cts.IsCancellationRequested && !_blocked())
            {
                try { await _automate(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    AutomationFailure = ex.Message;
                    Log.Warn("游戏只读观测保留，帧格自动执行未启动：" + ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) { Unavailable("游戏状态连接失败：" + ex.Message); Log.Warn(Failure); }
        finally { Interlocked.Exchange(ref _connecting, 0); if (Volatile.Read(ref _disposed) == 0) Notify(); }
    }
    private void Notify()
    {
        foreach (Action handler in Changed?.GetInvocationList() ?? [])
            try { handler(); } catch (Exception ex) { Log.Error("游戏观测状态订阅失败", ex); }
    }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _cts.Cancel(); }
}
