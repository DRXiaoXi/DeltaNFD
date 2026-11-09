using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.Inspection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;
public sealed partial class LowMemoryRecoveryPage : Page
{
    private readonly PageBackendLease? _owner = PageBackendSessions.Manager.Current;
    private readonly BackendCaller _caller = new(BackendModules.System, PageBackendSessions.Manager.Current);
    private LowMemoryStatus? _status;
    private bool _busy, _active, _writing;
    private bool _syncingPrefetch, _prefetchBackupKnown, _hasPrefetchBackup;
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
        var canPrefetch = !_busy && _active && _status is { IsLowCapacity: true, PrefetchDisabled: not null }
            && _prefetchBackupKnown && PageBackendSessions.SystemWriteBlocker is null;
        PrefetchToggle.IsEnabled = canPrefetch && (_status?.PrefetchDisabled != true || _hasPrefetchBackup);
        PrefetchRestoreButton.IsEnabled = canPrefetch && _hasPrefetchBackup;
        PrefetchRestoreButton.Visibility = _hasPrefetchBackup ? Visibility.Visible : Visibility.Collapsed;
        _syncingPrefetch = true;
        try
        {
            PrefetchToggle.OffContent = _status?.PrefetchDisabled is null ? "状态待确认" : "未全部禁用";
            PrefetchToggle.IsOn = _status?.PrefetchDisabled == true;
        }
        finally { _syncingPrefetch = false; }
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
        var backupTask = Task.Run(() =>
        {
            try { return (Known: true, HasBackup: new SystemTweakInspection().GetBackedUpTweaks().Contains(SystemTweak.Prefetch), Error: ""); }
            catch (Exception ex) { return (Known: false, HasBackup: false, Error: "预读取备份未能读取：" + ex.Message); }
        });
        var status = await LowMemoryHealthCache.ReadAsync(true);
        var backup = await backupTask;
        if (!_active || generation != _generation) return;
        _status = status;
        _prefetchBackupKnown = backup.Known; _hasPrefetchBackup = backup.HasBackup;
        ApplyButton.Content = !status.Complete && status.CanRepairSysMain ? "先预览并启用 SysMain" : "预览并按建议修正";
        ServiceText.Text = State(status, LowMemoryAction.SysMain) + " · "
            + (status.SysMainStart switch { 2 => "自动启动", 3 => "手动启动", 4 => "已禁用", _ => "启动方式待确认" })
            + "；" + (status.SysMainRunning switch { true => "正在运行", false => "已停止", _ => "运行状态待确认" });
        CompressionText.Text = State(status, LowMemoryAction.MemoryCompression);
        CombiningText.Text = State(status, LowMemoryAction.PageCombining);
        PrefetchText.Text = State(status, LowMemoryAction.Prefetch);
        PrefetchHint.Text = !backup.Known ? backup.Error : status.PrefetchDisabled is null ? "该项状态待确认，请重新检测后操作。"
            : status.PrefetchDisabled == true && !backup.HasBackup ? "当前已禁用，但没有本工具保存的原始备份，暂不能在此恢复。"
            : backup.HasBackup ? "已保存原始备份，可单独恢复预读取配置。" : "禁用前会保存原始配置。";
        StatusBar.Title = !status.CapacityKnown ? "物理总内存待确认" : !status.IsLowCapacity ? "此电脑物理内存超过 16 GB"
            : !status.Complete ? "部分配置未能读取" : status.NeedsAttention ? "低内存配置需要调整" : "配置已符合低内存建议";
        StatusBar.Severity = status.Ready ? InfoBarSeverity.Success : status.IsLowCapacity ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        StatusBar.Message = (status.TotalBytes is { } bytes ? $"物理总内存：{bytes / 1073741824d:0.##} GB。 " : "") + status.Error
            + (PageBackendSessions.SystemWriteBlocker is { } blocker ? " " + blocker : "");
        UpdateButtons();
    }
    private void Back_Click(object sender, RoutedEventArgs e) { if (!_writing) App.MainWindow?.NavigateByTag("dashboard"); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);
    private async void Prefetch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingPrefetch || !_active) return;
        var disable = PrefetchToggle.IsOn;
        UpdateButtons(); // A requested position is not a confirmed system state.
        await RunAsync(() => ChangePrefetchAsync(disable));
    }
    private async void PrefetchRestore_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ChangePrefetchAsync(false));
    private async Task ChangePrefetchAsync(bool disable)
    {
        var generation = _generation;
        await RefreshAsync();
        if (!_active || generation != _generation) return;
        if (_status is not { IsLowCapacity: true, PrefetchDisabled: not null } || !_prefetchBackupKnown)
            throw new InvalidOperationException("物理内存、预读取状态或备份尚未确认，请重新检测。");
        if (PageBackendSessions.SystemWriteBlocker is { } blocked) throw new InvalidOperationException(blocked);
        if (disable && _status.PrefetchDisabled == true) { ResultText.Text = "预读取已禁用，无需重复修改。"; return; }
        if (!disable && !_hasPrefetchBackup) { ResultText.Text = "没有本工具保存的预读取原始备份，未修改。"; return; }
        var fingerprint = _status.Fingerprint;
        if (await new ContentDialog { XamlRoot = XamlRoot, Title = disable ? "单独禁用预读取？" : "恢复预读取原始配置？",
            Content = disable ? "只禁用预读取，修改前保存原始配置并重新检测。SysMain、内存压缩和分页合并保持当前配置。完成后请手动重启。"
                : "只按本工具保存的原始备份恢复预读取。原始配置本来已禁用时，恢复后仍会显示已禁用。完成后请手动重启。",
            PrimaryButtonText = disable ? "禁用预读取" : "按备份恢复", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        }.ShowWithBackendRecoveryAsync(pageOwner: _owner) != ContentDialogResult.Primary) return;
        if (!_active || generation != _generation) return;
        if (PageBackendSessions.SystemWriteBlocker is { } blocker) throw new InvalidOperationException(blocker);
        _writing = true; UpdateButtons();
        Progress.Visibility = Visibility.Visible; Progress.Maximum = 1; Progress.Value = 0;
        ProgressText.Text = disable ? "正在单独禁用预读取并复检…" : "正在按备份恢复预读取并重新检测…";
        var response = await _caller.InvokeAsync(BackendCommands.SystemLowMemoryApply,
            new LowMemoryApplyArgs(LowMemoryAction.Prefetch, fingerprint, RestorePrefetch: !disable), null, CancellationToken.None);
        if (!_active || generation != _generation) return;
        var result = response.Data is { } data ? BackendJson.Deserialize<OperationResult>(data) : OperationResult.Fail(response.Message);
        OptimizationScan.Invalidate(); ResultText.Text = result.Message;
        await RefreshAsync();
        if (!_active || generation != _generation) return;
        Progress.Value = result.Success ? 1 : 0;
        ProgressText.Text = result.Success ? "已重新检测当前配置，请手动重启后再检测。" : "本项未完成，请核对结果和保留的备份。";
    }
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
        Progress.Visibility = Visibility.Visible; Progress.Maximum = 4; Progress.Value = 0;
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
