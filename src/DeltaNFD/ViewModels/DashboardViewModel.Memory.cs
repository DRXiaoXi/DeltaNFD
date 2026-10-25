using CommunityToolkit.Mvvm.ComponentModel;
using DeltaNFD.Inspection;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

namespace DeltaNFD.ViewModels;

public partial class DashboardViewModel
{
    [ObservableProperty]
    private DashboardHealthState memoryHealthState = DashboardHealthState.Checking;
    [ObservableProperty]
    private string memoryStatusText = "正在读取可用物理内存…";
    private BackendCpuFrameService? _memoryHeartbeat;
    private DashboardMemoryObserver? _memoryObserver;
    private bool _memoryAttached;

    public bool MemoryRecoverySuggested => DashboardMemoryHealth.SuggestRecovery(MemoryHealthState);
    partial void OnMemoryHealthStateChanged(DashboardHealthState value)
    {
        OnPropertyChanged(nameof(HealthSummary));
        OnPropertyChanged(nameof(MemoryRecoverySuggested));
    }
    private bool? ReadMemoryGameState()
    {
        var current = GameObservationState.ReadDelta(_memoryHeartbeat?.Latest, DateTimeOffset.UtcNow);
        return current == PageBackendSessions.Manager.DeltaRunning ? current : null;
    }
    public void AttachMemoryHealth()
    {
        if (_memoryAttached) return;
        _memoryAttached = true;
        // Subscription only: do not EnsureClient/EnsureMonitor, retry an executor, or enumerate processes.
        _memoryHeartbeat = BackendCpuFrameService.Instance;
        _memoryObserver ??= CreateMemoryObserver();
        _memoryHeartbeat.StatusUpdated += OnMemoryHeartbeat;
        _memoryHeartbeat.ConnectionEnded += OnMemoryConnectionEnded;
        PageBackendSessions.Manager.Changed += OnMemoryManagerChanged;
        _memoryObserver.Start();
    }
    private DashboardMemoryObserver CreateMemoryObserver()
    {
        var observer = new DashboardMemoryObserver(new(PhysicalMemoryInspection.Read), ReadMemoryGameState, action =>
        {
            if (_dispatcher is null || _dispatcher.HasThreadAccess) { action(); return true; }
            return _dispatcher.TryEnqueue(() => action());
        });
        observer.Updated += reading => { MemoryStatusText = reading.Text; MemoryHealthState = reading.State; };
        return observer;
    }
    public void DetachMemoryHealth()
    {
        _memoryAttached = false;
        _memoryObserver?.Stop();
        if (_memoryHeartbeat is { } heartbeat)
        {
            heartbeat.StatusUpdated -= OnMemoryHeartbeat;
            heartbeat.ConnectionEnded -= OnMemoryConnectionEnded;
        }
        PageBackendSessions.Manager.Changed -= OnMemoryManagerChanged;
        _memoryHeartbeat = null;
    }
    private void OnMemoryHeartbeat(CpuFrameStatusSnapshot snapshot) => _memoryObserver?.Pulse();
    private void OnMemoryConnectionEnded() => _memoryObserver?.Pulse(true);
    private void OnMemoryManagerChanged() => _memoryObserver?.Pulse();
    public void RefreshMemoryHealth() => _memoryObserver?.Pulse(true);
}
