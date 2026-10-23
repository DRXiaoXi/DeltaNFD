using DeltaNFD.Services.Backends;

namespace DeltaNFD.ViewModels;

public partial class DashboardViewModel
{
    private int _healthGeneration;
    private bool _healthAttached;
    private bool _healthEverStarted;
    private bool _healthStateObserved;
    private bool? _lastHealthGameState;
    private HealthValues? _healthBefore;
    private sealed record HealthValues(DashboardHealthState Shader, string ShaderText, DashboardHealthState Ace, string AceText,
        DashboardHealthState Runtime, string RuntimeText, bool CacheAttention);
    private string HealthPauseReason => PageBackendSessions.Manager.DeltaRunning == true
        ? "三角洲运行期间，组件体检已暂停；显示最近检测结果。" : "正在确认三角洲运行状态，组件体检暂未执行。";
    private bool CanApplyHealth(int generation) => _healthAttached && generation == _healthGeneration && PageBackendSessions.Manager.DeltaRunning == false;
    public void AttachHealth()
    {
        _healthAttached = true;
        PageBackendSessions.Manager.Changed -= OnHealthManagerState;
        PageBackendSessions.Manager.Changed += OnHealthManagerState;
        OnHealthGameState(PageBackendSessions.Manager.DeltaRunning);
    }
    public void DetachHealth()
    {
        _healthAttached = false; _healthGeneration++;
        PageBackendSessions.Manager.Changed -= OnHealthManagerState;
    }
    private void OnHealthManagerState()
    {
        if (_dispatcher is not null && !_dispatcher.HasThreadAccess) { _dispatcher.TryEnqueue(OnHealthManagerState); return; }
        OnHealthGameState(PageBackendSessions.Manager.DeltaRunning);
    }
    private void OnHealthGameState(bool? running)
    {
        if (_healthStateObserved && _lastHealthGameState == running) return;
        _healthStateObserved = true; _lastHealthGameState = running;
        if (running != false)
        {
            _healthGeneration++;
            if (_healthBefore is { } saved)
            {
                ShaderHealthState = saved.Shader; ShaderStatusText = saved.ShaderText;
                AceHealthState = saved.Ace; AceStatusText = saved.AceText;
                RuntimeHealthState = saved.Runtime; RuntimeStatusText = saved.RuntimeText; ShaderCacheNeedsAttention = saved.CacheAttention;
                _healthBefore = null;
            }
            if (!_healthEverStarted)
            {
                ShaderHealthState = AceHealthState = RuntimeHealthState = DashboardHealthState.Information;
                ShaderStatusText = AceStatusText = RuntimeStatusText = "尚未检测";
            }
            IsHealthScanning = false;
            if (ShaderHealthState == DashboardHealthState.Checking) { ShaderHealthState = DashboardHealthState.Information; ShaderStatusText = "尚未检测"; }
            if (AceHealthState == DashboardHealthState.Checking) { AceHealthState = DashboardHealthState.Information; AceStatusText = "尚未检测"; }
            if (RuntimeHealthState == DashboardHealthState.Checking) { RuntimeHealthState = DashboardHealthState.Information; RuntimeStatusText = "尚未检测"; }
        }
        OnPropertyChanged(nameof(CanRefreshHealth)); OnPropertyChanged(nameof(HealthSummary));
        if (running == false && !_healthEverStarted && _healthAttached && App.MainWindow?.StartupGameDirectoryCheckCompleted == true) _ = RefreshHealthAsync();
    }
}
