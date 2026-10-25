using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.Views;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DeltaNFD;

public sealed partial class MainWindow
{
    private static readonly HashSet<string> GameLockedPages = ["spoof", "shader", "ace", "runtime"];
    private readonly HashSet<PageBackendLease> _pageLeases = [];
    private bool _optimizationPreloadStarted;
    private void InitializePageBackendNavigation()
    {
        ContentFrame.Navigating += PageFrameNavigating;
        ContentFrame.Navigated += PageFrameNavigated;
        ContentFrame.Loaded += (_, _) =>
        {
            if (_optimizationPreloadStarted) return;
            _optimizationPreloadStarted = true;
            _ = OptimizationScan.RunIfNotScannedAsync();
        };
        ServiceLocator.GameMonitor.DeltaStateChanged += OnDeltaPageState;
        ServiceLocator.GameMonitor.StatusChanged += OnGameObservationStatus;
        PageBackendSessions.Manager.Changed += OnPageBackendState;
        Closed += (_, _) =>
        {
            ServiceLocator.GameMonitor.DeltaStateChanged -= OnDeltaPageState;
            ServiceLocator.GameMonitor.StatusChanged -= OnGameObservationStatus;
            ServiceLocator.GameMonitor.Dispose();
            PageBackendSessions.Manager.Changed -= OnPageBackendState;
            PageBackendSessions.Manager.Navigate("");
        };
    }
    private static string PageRoute(Type? page) => page == typeof(SystemOptimizePage) ? "system"
        : page == typeof(GpuSpoofPage) ? "spoof" : page == typeof(ShaderPage) || page == typeof(ShaderRecoveryPage) ? "shader"
        : page == typeof(AcePage) ? "ace" : page == typeof(RuntimePage) ? "runtime"
        : page == typeof(NvSettingsPage) ? "nvsettings" : "";
    private static bool CanEnterGamePage(string route) => !GameLockedPages.Contains(route)
        || PageBackendSessions.Manager.DeltaRunning == false;
    private void PageFrameNavigating(object sender, NavigatingCancelEventArgs e)
    {
        var route = PageRoute(e.SourcePageType);
        if (PageBackendSessionManager.Restricted.Contains(route) && PageBackendSessions.Manager.DeltaRunning == false)
        {
            try { if (DeltaForceLocator.IsGameRunning()) PageBackendSessions.Manager.ObserveDelta(true); }
            catch { PageBackendSessions.Manager.ObserveDelta(null); }
        }
        if (!CanEnterGamePage(route)) { e.Cancel = true; UpdatePageAvailability(); return; }
        PageBackendSessions.Manager.Navigate(route);
        if (PageBackendSessions.Manager.Current is { } lease) _pageLeases.Add(lease);
    }
    private async void PageFrameNavigated(object sender, NavigationEventArgs e)
    {
        UpdatePageAvailability();
        var owner = PageBackendSessions.Manager.Current;
        if (owner is null || PageBackendSessions.Manager.DeltaRunning != false && owner.Route != "nvsettings") return;
        var module = owner.Route switch { "spoof" or "nvsettings" => BackendModules.Gpu,
            "runtime" => BackendModules.Runtime, "ace" => BackendModules.Ace, _ => BackendModules.System };
        try
        {
            using var creating = PageBackendSessions.ConstructFor(owner);
            await new BackendCaller(module).WarmAsync();
        }
        catch (PageBackendUnavailableException) { /* A rapid navigation/game start revoked startup. */ }
        catch (Exception ex) { ReportPageOperationResult("栏目后端未能启动", ex.Message); }
    }
    private void OnDeltaPageState(bool? running)
    {
        UpdatePageAvailability();
        if (!CanEnterGamePage(PageRoute(ContentFrame.CurrentSourcePageType))) NavigateByTag("dashboard");
        if (ContentFrame.Content is SystemOptimizePage system) system.RefreshGameReadOnlyState();
    }
    private void OnGameObservationStatus() => UpdatePageAvailability();
    private async void GameStateRetry_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ServiceLocator.GameMonitor.ReconnectAsync();
    }
    private void OnPageBackendState() => DispatcherQueue.TryEnqueue(() =>
    {
        UpdatePageAvailability();
        if (!CanEnterGamePage(PageRoute(ContentFrame.CurrentSourcePageType))) NavigateByTag("dashboard");
        if (ContentFrame.Content is SystemOptimizePage page) page.RefreshGameReadOnlyState();
    });
    internal void ReportPageOperationResult(string title, string message) => DispatcherQueue.TryEnqueue(() =>
    { PageOperationResult.Title = title; PageOperationResult.Message = message; PageOperationResult.IsOpen = true; });
    private void UpdatePageAvailability()
    {
        foreach (var lease in _pageLeases) lease.RefreshReviewedExit();
        var running = PageBackendSessions.Manager.DeltaRunning;
        foreach (var item in EnumerateNavItems())
        {
            var tag = item.Tag?.ToString() ?? "";
            var targetAllowed = !(ServiceLocator.GameTarget.IsCustom && tag is "shader" or "ace");
            item.IsEnabled = targetAllowed && CanEnterGamePage(tag);
            if (GameLockedPages.Contains(tag) || tag == "system")
            {
                var help = !targetAllowed ? GameTargetService.DeltaOnlyMessage : running == false ? ""
                    : tag == "system" ? PageBackendSessions.SystemWriteBlocker! : running == true
                        ? "三角洲运行中，请退出游戏后使用。" : "无法确认三角洲运行状态，请等待监控恢复。";
                ToolTipService.SetToolTip(item, help); AutomationProperties.SetHelpText(item, help);
            }
        }
        GamePageRestriction.IsOpen = running != false;
        GamePageRestriction.Title = running == true ? "三角洲运行期间的功能限制" : "三角洲运行状态待确认";
        GamePageRestriction.Message = running == true
            ? "显卡伪装、着色器、ACE相关和运行库暂不可用；系统优化仅供查看。完全退出三角洲后恢复。"
            : ServiceLocator.GameMonitor.StatusDetail + " 系统优化暂时仅供查看，其他维护栏目确认后开放。";
        GameStateRetryButton.Visibility = running is null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        GameStateRetryButton.IsEnabled = !ServiceLocator.GameMonitor.IsConnecting;
        ShaderCacheButton.IsEnabled = !ServiceLocator.GameTarget.IsCustom && CanEnterGamePage("shader");
        _pageLeases.RemoveWhere(l => !ReferenceEquals(l, PageBackendSessions.Manager.Current) && l.Stopping.IsCompleted && l.Status == "栏目后端已关闭");
        var pending = _pageLeases.Where(l => l.Status.Length > 0 && l.Status != "栏目后端已关闭").Select(l => l.Status).Distinct().ToArray();
        PageBackendStatus.IsOpen = pending.Length > 0;
        PageBackendStatus.Message = string.Join("；", pending);
    }
}
