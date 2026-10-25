using DeltaNFD.Services.Backends;

namespace DeltaNFD.ViewModels;

// UI properties/dispatcher and read-only probes are substituted. Attach/detach, observation,
// refresh orchestration, checkpoint restore and generation checks compile from production ReadOnly.cs.
public partial class DashboardViewModel
{
    internal readonly DashboardHealthTestDispatcher? _dispatcher;
    internal DashboardViewModel(DashboardHealthTestDispatcher? dispatcher = null) => _dispatcher = dispatcher;
    internal Func<string, Task<string>> Probe = area => Task.FromResult(area + " verified");
    internal readonly List<string> Reads = [];
    internal int MemoryRefreshes, PreferenceRefreshes;
    public bool RuntimeUiEnabled { get; set; } = true;
    public bool IsHealthScanning { get; set; }
    public bool CanRefreshHealth => !IsHealthScanning && PageBackendSessions.Manager.DeltaRunning == false;
    public DashboardHealthState ShaderHealthState { get; set; } = DashboardHealthState.Checking;
    public DashboardHealthState AceHealthState { get; set; } = DashboardHealthState.Checking;
    public DashboardHealthState RuntimeHealthState { get; set; } = DashboardHealthState.Checking;
    public string ShaderStatusText { get; set; } = "检测中…";
    public string AceStatusText { get; set; } = "检测中…";
    public string RuntimeStatusText { get; set; } = "检测中…";
    public bool ShaderCacheNeedsAttention { get; set; }
    public string LastHealthCheckText { get; set; } = "尚未检测";
    public string HealthSummary => PageBackendSessions.Manager.DeltaRunning != false ? HealthPauseReason
        : DashboardHealthPresentation.Summary(IsHealthScanning, ShaderHealthState, AceHealthState, RuntimeHealthState);
    internal void RefreshMemoryHealth() => MemoryRefreshes++;
    internal void RefreshRuntimeDetectionPreference() => PreferenceRefreshes++;
    private void OnPropertyChanged(string name) { }
    private Task LoadShaderHealthAsync(int generation) => ReadAsync("shader", generation);
    private Task LoadAceHealthAsync(int generation) => ReadAsync("ace", generation);
    private Task LoadRuntimeHealthAsync(int generation) => ReadAsync("runtime", generation);
    private async Task ReadAsync(string area, int generation)
    {
        Reads.Add(area);
        var text = await Probe(area);
        if (!CanApplyHealth(generation)) return;
        switch (area)
        {
            case "shader": ShaderHealthState = DashboardHealthState.Normal; ShaderStatusText = text; break;
            case "ace": AceHealthState = DashboardHealthState.Normal; AceStatusText = text; break;
            case "runtime": RuntimeHealthState = DashboardHealthState.Normal; RuntimeStatusText = text; break;
        }
    }
}

internal sealed class DashboardHealthTestDispatcher
{
    internal bool HasThreadAccess { get; set; } = true;
    internal Queue<Action> Pending { get; } = new();
    internal bool TryEnqueue(Action action) { Pending.Enqueue(action); return true; }
    internal void Drain() { HasThreadAccess = true; while (Pending.TryDequeue(out var next)) next(); }
}
