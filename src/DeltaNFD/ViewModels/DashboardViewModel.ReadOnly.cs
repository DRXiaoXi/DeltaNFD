using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

namespace DeltaNFD.ViewModels;

public partial class DashboardViewModel
{
    private int _healthGeneration;
    private bool _healthAttached;
    private bool _healthRefreshPending = true;
    private bool _healthStateObserved;
    private bool? _lastHealthGameState;
    private HealthValues? _healthBefore;
    private sealed record HealthValues(DashboardHealthState Shader, string ShaderText, DashboardHealthState Ace, string AceText,
        DashboardHealthState Runtime, string RuntimeText, bool CacheAttention);
    private string HealthPauseReason => PageBackendSessions.Manager.DeltaRunning == true
        ? "三角洲运行中：其他组件体检已暂停，显示最近结果；内存占用持续检测。" : "正在确认三角洲运行状态，组件体检暂未执行；内存等级待确认。";
    private bool CanApplyHealth(int generation) => _healthAttached && generation == _healthGeneration && PageBackendSessions.Manager.DeltaRunning == false;
    public void AttachHealth()
    {
        if (_healthAttached) return;
        _healthAttached = true;
        _healthStateObserved = false;
        PageBackendSessions.Manager.Changed -= OnHealthManagerState;
        PageBackendSessions.Manager.Changed += OnHealthManagerState;
        OnHealthGameState(PageBackendSessions.Manager.DeltaRunning);
    }
    public void DetachHealth()
    {
        if (!_healthAttached) return;
        _healthAttached = false; _healthGeneration++;
        PageBackendSessions.Manager.Changed -= OnHealthManagerState;
        RestoreHealthBefore();
        IsHealthScanning = false;
        _healthRefreshPending = true;
        _healthStateObserved = false;
    }
    private void OnHealthManagerState()
    {
        if (!_healthAttached) return;
        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
        {
            var generation = _healthGeneration;
            _dispatcher.TryEnqueue(() => { if (_healthAttached && generation == _healthGeneration) OnHealthManagerState(); });
            return;
        }
        OnHealthGameState(PageBackendSessions.Manager.DeltaRunning);
    }
    private void OnHealthGameState(bool? running)
    {
        if (!_healthAttached) return;
        if (_healthStateObserved && _lastHealthGameState == running) return;
        _healthStateObserved = true; _lastHealthGameState = running;
        if (running != false)
        {
            _healthGeneration++;
            RestoreHealthBefore();
            IsHealthScanning = false;
            _healthRefreshPending = true;
        }
        OnPropertyChanged(nameof(CanRefreshHealth)); OnPropertyChanged(nameof(HealthSummary));
        if (running == false && _healthRefreshPending && _healthAttached && App.MainWindow?.StartupGameDirectoryCheckCompleted == true) _ = RefreshHealthAsync();
    }
    private void RestoreHealthBefore()
    {
        if (_healthBefore is { } saved)
        {
            ShaderHealthState = saved.Shader; ShaderStatusText = saved.ShaderText;
            AceHealthState = saved.Ace; AceStatusText = saved.AceText;
            RuntimeHealthState = saved.Runtime; RuntimeStatusText = saved.RuntimeText;
            ShaderCacheNeedsAttention = saved.CacheAttention;
            _healthBefore = null;
        }
        if (ShaderHealthState == DashboardHealthState.Checking) { ShaderHealthState = DashboardHealthState.Information; ShaderStatusText = "尚未检测"; }
        if (AceHealthState == DashboardHealthState.Checking) { AceHealthState = DashboardHealthState.Information; AceStatusText = "尚未检测"; }
        if (RuntimeHealthState == DashboardHealthState.Checking) { RuntimeHealthState = DashboardHealthState.Information; RuntimeStatusText = "尚未检测"; }
    }

    /// <summary>刷新主页组件状态；仅使用共享只读服务，不启动栏目后端。</summary>
    public async Task RefreshHealthAsync()
    {
        // Navigation can arrive before Loaded; it must not consume the first attached observation.
        if (!_healthAttached)
            return;
        RefreshMemoryHealth();
        RefreshRuntimeDetectionPreference();
        if (PageBackendSessions.Manager.DeltaRunning != false)
        {
            OnHealthGameState(Services.Backends.PageBackendSessions.Manager.DeltaRunning);
            return;
        }

        if (IsHealthScanning)
            return;
        var generation = ++_healthGeneration;
        _healthBefore = new(ShaderHealthState, ShaderStatusText, AceHealthState, AceStatusText, RuntimeHealthState, RuntimeStatusText, ShaderCacheNeedsAttention);
        _healthRefreshPending = false;
        IsHealthScanning = true;
        try
        {
            foreach (var read in new (string Area, Func<Task> Read)[]
            {
                ("着色器", () => LoadShaderHealthAsync(generation)),
                ("ACE", () => LoadAceHealthAsync(generation)),
                ("运行库", async () =>
                {
                    if (RuntimeUiEnabled)
                        await LoadRuntimeHealthAsync(generation);
                })
            }

            )
            {
                if (!CanApplyHealth(generation))
                    return;
                try
                {
                    await read.Read();
                }
                catch (Exception ex)
                {
                    if (!CanApplyHealth(generation))
                        return;
                    if (read.Area == "着色器")
                    {
                        ShaderHealthState = DashboardHealthState.Failed;
                        ShaderStatusText = "检测失败：" + ex.Message;
                    }
                    else if (read.Area == "ACE")
                    {
                        AceHealthState = DashboardHealthState.Failed;
                        AceStatusText = "检测失败：" + ex.Message;
                    }
                    else
                    {
                        RuntimeHealthState = DashboardHealthState.Failed;
                        RuntimeStatusText = "检测失败：" + ex.Message;
                    }
                }
            }

            if (CanApplyHealth(generation))
            {
                LastHealthCheckText = "最近检测 " + DateTimeOffset.Now.ToString("HH:mm:ss");
                _healthBefore = null;
            }
        }
        finally
        {
            if (generation == _healthGeneration)
                IsHealthScanning = false;
        }
    }

}
