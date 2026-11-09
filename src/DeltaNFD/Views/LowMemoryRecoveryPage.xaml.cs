using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;
public sealed partial class LowMemoryRecoveryPage : Page
{
    private readonly PageBackendLease? _owner = PageBackendSessions.Manager.Current;
    private readonly BackendCaller _caller = new(BackendModules.System, PageBackendSessions.Manager.Current);
    private LowMemoryStatus? _status;
    private bool _busy, _active, _writing;
    private int _generation;
    public LowMemoryRecoveryPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { _active = true; _generation++; PageBackendSessions.Manager.Changed += OnGateChanged; await RunAsync(RefreshAsync); };
        Unloaded += (_, _) => { _active = false; _generation++; PageBackendSessions.Manager.Changed -= OnGateChanged; };
    }
    private void OnGateChanged() => DispatcherQueue.TryEnqueue(() => { if (_active) UpdateButtons(); });
    private void UpdateButtons()
    {
        BackButton.IsEnabled = !_writing;
        RefreshButton.IsEnabled = !_busy;
        ApplyButton.IsEnabled = !_busy && _active && _status is { IsLowCapacity: true, NeedsAttention: true }
            && (_status.Complete || _status.CanRepairSysMain)
            && PageBackendSessions.SystemWriteBlocker is null;
    }
    private async Task RunAsync(Func<Task> work)
    {
        if (_busy) return;
        _busy = true; BusyRing.IsActive = true; UpdateButtons();
        try { await work(); }
        catch (Exception ex)
        {
            if (!_active) return;
            _status = null; StatusBar.Title = "未完成，请重新核对"; StatusBar.Message = ex.Message; StatusBar.Severity = InfoBarSeverity.Warning;
            ResultText.Text += (ResultText.Text.Length > 0 ? "\n" : "") + "保留已完成步骤和原始备份，未继续后续修改。";
        }
        finally { _busy = _writing = false; if (_active) { BusyRing.IsActive = false; UpdateButtons(); } }
    }
    private static string State(LowMemoryStatus status, LowMemoryAction action) => status.Needs(action) switch
    { true => "需要调整", false => "已符合建议", _ => "状态待确认" };
    private async Task RefreshAsync()
    {
        var generation = _generation;
        var status = await LowMemoryHealthCache.ReadAsync(true);
        if (!_active || generation != _generation) return;
        _status = status;
        ApplyButton.Content = !status.Complete && status.CanRepairSysMain ? "先预览并启用 SysMain" : "预览并按建议修正";
        ServiceText.Text = State(status, LowMemoryAction.SysMain) + " · "
            + (status.SysMainStart switch { 2 => "自动启动", 3 => "手动启动", 4 => "已禁用", _ => "启动方式待确认" })
            + "；" + (status.SysMainRunning switch { true => "正在运行", false => "已停止", _ => "运行状态待确认" });
        CompressionText.Text = State(status, LowMemoryAction.MemoryCompression);
        CombiningText.Text = State(status, LowMemoryAction.PageCombining);
        PrefetchText.Text = State(status, LowMemoryAction.Prefetch);
        StatusBar.Title = !status.CapacityKnown ? "物理总内存待确认" : !status.IsLowCapacity ? "此电脑物理内存超过 16 GB"
            : !status.Complete ? "部分配置未能读取" : status.NeedsAttention ? "低内存配置需要调整" : "配置已符合低内存建议";
        StatusBar.Severity = status.Ready ? InfoBarSeverity.Success : status.IsLowCapacity ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        StatusBar.Message = (status.TotalBytes is { } bytes ? $"物理总内存：{bytes / 1073741824d:0.##} GB。 " : "") + status.Error
            + (PageBackendSessions.SystemWriteBlocker is { } blocker ? " " + blocker : "");
        UpdateButtons();
    }
    private void Back_Click(object sender, RoutedEventArgs e) { if (!_writing) App.MainWindow?.NavigateByTag("dashboard"); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);
    private async void Apply_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await RefreshAsync();
        if (_status is not { IsLowCapacity: true, NeedsAttention: true } || !(_status.Complete || _status.CanRepairSysMain)) return;
        if (PageBackendSessions.SystemWriteBlocker is { } blocked) throw new InvalidOperationException(blocked);
        if (await new ContentDialog { XamlRoot = XamlRoot, Title = "按低内存建议修正配置？",
            Content = _status.Complete ? "将按需启用 SysMain、内存压缩和分页合并，并禁用预读取。每一步保存原始状态并复检。完成后请保存工作并手动重启电脑。"
                : "将先启用已确认需要修正的 SysMain，然后重新检测内存功能。其余配置无法确认时会停止后续步骤；保存原始状态并复检，完成后请手动重启。",
            PrimaryButtonText = "确认修正", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        }.ShowWithBackendRecoveryAsync(pageOwner: _owner) != ContentDialogResult.Primary) return;
        var generation = _generation;
        _writing = true; UpdateButtons();
        Progress.Visibility = Visibility.Visible; Progress.Value = 0;
        var messages = new List<string>();
        foreach (var action in Enum.GetValues<LowMemoryAction>())
        {
            if (!_active || generation != _generation) return;
            await RefreshAsync();
            if (!_active || generation != _generation) return;
            if (_status is not { IsLowCapacity: true } || !_status.CanCheckTarget(action)) throw new InvalidOperationException("该项配置未确认，已停止后续步骤。");
            if (PageBackendSessions.SystemWriteBlocker is { } blocker) throw new InvalidOperationException(blocker);
            ProgressText.Text = $"步骤 {(int)action + 1}/4：{action switch { LowMemoryAction.SysMain => "启用 SysMain", LowMemoryAction.MemoryCompression => "启用内存压缩", LowMemoryAction.PageCombining => "启用分页合并", _ => "禁用预读取" }}";
            if (_status.Needs(action) == true)
            {
                var response = await _caller.InvokeAsync(BackendCommands.SystemLowMemoryApply, new LowMemoryApplyArgs(action, _status.Fingerprint), null, CancellationToken.None);
                if (!_active || generation != _generation) return;
                var result = response.Data is { } data ? BackendJson.Deserialize<OperationResult>(data) : OperationResult.Fail(response.Message);
                OptimizationScan.Invalidate();
                messages.Add(result.Message); ResultText.Text = string.Join("\n", messages);
                if (!result.Success) { await RefreshAsync(); return; }
            }
            Progress.Value = (int)action + 1;
        }
        await RefreshAsync();
        if (_active && generation == _generation)
            ProgressText.Text = _status?.Ready == true ? "四项配置均已核对。请保存工作并手动重启电脑，重启后再次检测。" : "复检未全部通过，请重新核对。";
    });
}
