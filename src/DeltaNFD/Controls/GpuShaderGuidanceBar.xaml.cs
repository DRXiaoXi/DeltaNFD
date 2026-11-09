using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Controls;

/// <summary>当次型号改写后的可关闭引导；不保存到设置，不在下次启动时恢复。</summary>
public sealed partial class GpuShaderGuidanceBar : UserControl
{
    public bool MaintenanceMode { get; set; }
    private bool _attached;
    private Guid? _shownRevision;

    public GpuShaderGuidanceBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_attached) return;
        _attached = true;
        GpuShaderGuidanceSession.Shared.Changed += QueueRefresh;
        ServiceLocator.GameTarget.Changed += OnTargetChanged;
        PageBackendSessions.Manager.Changed += QueueRefresh;
        Refresh();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _attached = false;
        GpuShaderGuidanceSession.Shared.Changed -= QueueRefresh;
        ServiceLocator.GameTarget.Changed -= OnTargetChanged;
        PageBackendSessions.Manager.Changed -= QueueRefresh;
    }
    private void OnTargetChanged(GameTarget target) => QueueRefresh();
    private void QueueRefresh() => DispatcherQueue.TryEnqueue(() => { if (_attached) Refresh(); });
    private void Refresh()
    {
        _shownRevision = GpuShaderGuidanceSession.Shared.Revision;
        Notice.IsOpen = _shownRevision is not null;
        Notice.Message = GpuShaderGuidanceSession.Instructions;
        ActionButton.Content = MaintenanceMode ? "知道了" : "前往着色器维护";
        ActionButton.IsEnabled = MaintenanceMode || !ServiceLocator.GameTarget.IsCustom && PageBackendSessions.Manager.DeltaRunning == false;
        if (!MaintenanceMode && !ActionButton.IsEnabled)
            Notice.Message += " 请切换到三角洲目标并确认游戏已退出后进入维护。";
    }
    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (!_attached) return;
        if (MaintenanceMode) Dismiss();
        else if (!ServiceLocator.GameTarget.IsCustom && PageBackendSessions.Manager.DeltaRunning == false)
            App.MainWindow?.NavigateByTag("shader");
    }
    private void Notice_CloseButtonClick(InfoBar sender, object args) => Dismiss();
    private void Dismiss()
    {
        if (_shownRevision is { } revision) GpuShaderGuidanceSession.Shared.Dismiss(revision);
        Refresh();
    }
}
