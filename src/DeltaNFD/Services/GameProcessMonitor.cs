using Microsoft.UI.Dispatching;

namespace DeltaNFD.Services;

/// <summary>One subscription to the backend heartbeat; observation survives a refused automation start.</summary>
internal sealed class GameProcessMonitor : IDisposable
{
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly Backends.BackendCpuFrameService _service = Backends.BackendCpuFrameService.Instance;
    private readonly GameObservationState _state;
    private readonly DeltaObservationFallback _fallback;
    private int _disposed;
    internal event Action<bool, int?>? Tick;
    internal event Action<bool?>? DeltaStateChanged;
    internal event Action? StatusChanged;
    internal bool? DeltaRunning { get; private set; }
    internal bool IsRunning { get; private set; }
    internal int? ProcessId { get; private set; }
    internal bool? TargetRunning => _state.TargetRunning;
    internal CpuFrameStatusSnapshot? Latest => _state.Latest;
    internal string StatusDetail => _state.Detail;
    internal string AutomationError => _service.AutomationError.Length > 0 ? _service.AutomationError
        : _state.AutomationFailure.Length > 0 ? _state.AutomationFailure : _state.HasBackendObservation ? "" : _state.Failure;
    internal bool IsConnecting => _state.IsConnecting;

    internal GameProcessMonitor()
    {
        _state = new(_service.EnsureObservationAsync, _service.EnsureMonitorAsync, () => OfflineModeGuard.BlocksNormalAutomation);
        _fallback = new(DeltaForceLocator.IsGameRunning, _state.AcceptFallback);
        _state.Changed += OnStateChanged;
        _service.StatusUpdated += OnStatusUpdated;
        _service.ConnectionEnded += OnConnectionEnded;
        GameTargetService.Default.Changed += OnTargetChanged;
        _ = _state.ConnectAsync(bootstrapAutomation: true);
    }
    internal Task ReconnectAsync() => _state.ConnectAsync(); // Observation-only; no mutation replay.
    internal IDisposable PauseConnections() => _state.PauseConnections();
    private void OnTargetChanged(GameTarget target) => Dispatch(() => { IsRunning = false; ProcessId = null; Notify(Tick, false, null); });
    private void OnStatusUpdated(CpuFrameStatusSnapshot snapshot) => _state.Accept(snapshot);
    private void OnConnectionEnded() => _state.Unavailable("CPU/帧格状态连接已中断或超时，请点击“重新确认”；如有未确认执行者，请先核对恢复记录。");
    private void OnStateChanged() => Dispatch(() =>
    {
        _fallback.SetEnabled(_state.CanUseReadOnlyFallback);
        var running = _state.DeltaRunning;
        Backends.PageBackendSessions.Manager.ObserveDelta(running);
        if (DeltaRunning != running)
        {
            DeltaRunning = running;
            foreach (Action<bool?> handler in DeltaStateChanged?.GetInvocationList() ?? [])
                try { handler(running); } catch (Exception ex) { Log.Error("栏目游戏状态订阅失败", ex); }
        }
        if (_state.TargetRunning is { } target)
        {
            IsRunning = target;
            ProcessId = target && _state.Latest?.MonitorRunning == true ? _state.Latest.GamePid : null;
            Notify(Tick, target, ProcessId);
        }
        foreach (Action handler in StatusChanged?.GetInvocationList() ?? [])
            try { handler(); } catch (Exception ex) { Log.Error("游戏状态说明订阅失败", ex); }
    });
    private static void Notify(Action<bool, int?>? handlers, bool running, int? pid)
    {
        foreach (Action<bool, int?> handler in handlers?.GetInvocationList() ?? [])
            try { handler(running, pid); } catch (Exception ex) { Log.Error("游戏状态订阅失败", ex); }
    }
    private void Dispatch(Action apply)
    {
        void Guarded() { if (Volatile.Read(ref _disposed) == 0) apply(); }
        if (_dispatcher is null || _dispatcher.HasThreadAccess) Guarded(); else _dispatcher.TryEnqueue(Guarded);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _state.Dispose();
        _fallback.Dispose();
        _state.Changed -= OnStateChanged;
        _service.StatusUpdated -= OnStatusUpdated;
        _service.ConnectionEnded -= OnConnectionEnded;
        GameTargetService.Default.Changed -= OnTargetChanged;
    }
}
