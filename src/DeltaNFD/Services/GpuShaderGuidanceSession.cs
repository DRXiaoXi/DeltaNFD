namespace DeltaNFD.Services;

/// <summary>只在当前进程内显示、可随时关闭的型号改写后引导，不读写提醒记录。</summary>
internal sealed class GpuShaderGuidanceSession
{
    internal static GpuShaderGuidanceSession Shared { get; } = new();
    internal const string Instructions = "先确认型号通过显卡重载或重启生效，再退出游戏并到「着色器维护」清除旧缓存。加入完整预热指令后启动游戏，等待预热完成再进入对局。";
    private readonly object _gate = new();
    private Guid? _revision;
    internal Guid? Revision { get { lock (_gate) return _revision; } }
    internal event Action? Changed;
    internal bool Observe(OperationResult result)
    {
        if (!result.Success || result.IsSkipped || result.IsPartial || result.OutcomeUnknown || !result.SuggestShaderMaintenance) return false;
        lock (_gate) _revision = Guid.NewGuid();
        Notify();
        return true;
    }
    internal bool Dismiss(Guid revision)
    {
        lock (_gate)
        {
            if (_revision != revision) return false;
            _revision = null;
        }
        Notify();
        return true;
    }
    private void Notify()
    {
        foreach (var listener in Changed?.GetInvocationList() ?? [])
            try { ((Action)listener)(); } catch { /* 提示刷新不得影响原操作回执。 */ }
    }
}
