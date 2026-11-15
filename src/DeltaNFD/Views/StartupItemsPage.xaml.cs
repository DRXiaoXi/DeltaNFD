using System.Collections.ObjectModel;
using System.ComponentModel;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed class StartupItemRow(StartupItemInfo info) : INotifyPropertyChanged
{
    public StartupItemInfo Info { get; } = info;
    private bool _editable;
    public bool CanDisable => _editable && Info.CanDisable;
    public bool CanRestore => _editable && Info.CanRestore;
    public bool CanKeepCurrent => _editable && Info.CanKeepCurrent;
    public Visibility RestoreVisibility => Info.HasBackup ? Visibility.Visible : Visibility.Collapsed;
    public Visibility KeepCurrentVisibility => Info.CanKeepCurrent ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void SetEditable(bool value)
    {
        if (_editable == value) return; _editable = value;
        foreach (var name in new[] { nameof(CanDisable), nameof(CanRestore), nameof(CanKeepCurrent) }) PropertyChanged?.Invoke(this, new(name));
    }
}

public sealed partial class StartupItemsPage : Page
{
    private readonly PageBackendLease? _owner = PageBackendSessions.Manager.Current;
    private readonly BackendCaller _caller = new(BackendModules.System, PageBackendSessions.Manager.Current);
    public ObservableCollection<StartupItemRow> Rows { get; } = [];
    private readonly List<StartupItemRow> _all = [];
    private CancellationTokenSource _queries = new();
    private bool _active, _busy, _writing, _needsReview;
    private int _generation;
    public StartupItemsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _active = true; _generation++; _queries = new();
            PageBackendSessions.Manager.Changed += OnGateChanged;
            await RunAsync(RefreshAsync);
        };
        Unloaded += (_, _) => { _active = false; _generation++; _queries.Cancel(); PageBackendSessions.Manager.Changed -= OnGateChanged; };
    }
    private void OnGateChanged() => DispatcherQueue.TryEnqueue(() => { if (_active) UpdateButtons(); });
    private void UpdateButtons()
    {
        var blocked = PageBackendSessions.SystemWriteBlocker;
        GateNotice.IsOpen = blocked is not null; GateNotice.Message = blocked ?? "";
        BackButton.IsEnabled = !_writing; RefreshButton.IsEnabled = !_busy;
        RestoreAllButton.IsEnabled = _active && !_busy && !_needsReview && blocked is null && _all.Any(i => i.Info.HasBackup);
        foreach (var row in _all) row.SetEditable(_active && !_busy && !_needsReview && blocked is null);
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || !_active) return;
        _busy = true; BusyRing.IsActive = true; UpdateButtons();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_active)
            {
                _needsReview |= _writing;
                if (!_writing) { _all.Clear(); Rows.Clear(); SummaryText.Text = "列表读取未完成，请重新核对。"; }
                ResultBar.IsOpen = true; ResultBar.Severity = InfoBarSeverity.Warning; ResultBar.Message = ex.Message;
            }
        }
        finally { _busy = _writing = false; if (_active) { BusyRing.IsActive = false; UpdateButtons(); } }
    }
    private async Task RefreshAsync()
    {
        var generation = _generation;
        var response = await _caller.InvokeAsync(BackendCommands.SystemStartupList, new { }, null, _queries.Token);
        var report = BackendResults.RequireData<StartupItemReport>(response, "读取自启动项目");
        if (!_active || generation != _generation) return;
        _needsReview = false;
        _all.Clear(); _all.AddRange(report.Items.Select(i => new StartupItemRow(i))); Filter();
        SummaryText.Text = $"共 {report.Items.Count} 项登记，其中 {report.BackupCount} 项有原始备份。" + (report.Items.Count == 0 && report.Complete ? " 当前范围没有可管理项目。" : "");
        if (!report.Complete)
        { ResultBar.IsOpen = true; ResultBar.Severity = InfoBarSeverity.Warning; ResultBar.Message = "部分项目未能读取：" + string.Join("；", report.Errors); }
        UpdateButtons();
    }
    private void Filter()
    {
        var query = SearchBox?.Text.Trim() ?? "";
        Rows.Clear();
        foreach (var row in _all)
            if (query.Length == 0 || (row.Info.Name + " " + row.Info.Source + " " + row.Info.Command).Contains(query, StringComparison.OrdinalIgnoreCase)) Rows.Add(row);
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (StartupList is not null) Filter(); }
    private void Back_Click(object sender, RoutedEventArgs e) { if (!_writing) App.MainWindow?.NavigateByTag("system"); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);
    private async void Disable_Click(object sender, RoutedEventArgs e) => await ChangeAsync(sender, StartupItemAction.Disable);
    private async void Restore_Click(object sender, RoutedEventArgs e) => await ChangeAsync(sender, StartupItemAction.Restore);
    private async void KeepCurrent_Click(object sender, RoutedEventArgs e) => await ChangeAsync(sender, StartupItemAction.KeepCurrent);
    private async Task ChangeAsync(object sender, StartupItemAction action)
    {
        if (sender is not Button { DataContext: StartupItemRow row } || !_all.Contains(row)) return;
        await RunAsync(async () =>
        {
            if (PageBackendSessions.SystemWriteBlocker is { } blocked) throw new InvalidOperationException(blocked);
            var title = action switch { StartupItemAction.Disable => "停用此自启动登记？", StartupItemAction.Restore => "按备份恢复启动登记？", _ => "保留当前并归档旧备份？" };
            var detail = action switch
            {
                StartupItemAction.Disable => "将保存原始备份后移除此启动登记，不卸载应用，也不结束当前程序。下次登录生效。",
                StartupItemAction.Restore => "仅恢复本工具保存的原始登记；发现不同的同名登记时停止，Windows自身的其他启动设置不变。",
                _ => "保留当前登记，旧备份移入软件数据目录的归档区；该记录将离开本页的直接恢复列表。",
            };
            if (row.Info.Source.Contains("Run", StringComparison.Ordinal) && action != StartupItemAction.KeepCurrent)
                detail += " 请先退出对应应用及其更新程序，避免它们在操作期间同时改写启动登记。";
            var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = title,
                Content = new ScrollViewer { MaxHeight = 360, Content = new TextBlock { Text = row.Info.Name + "\n" + row.Info.Source + "\n\n" + row.Info.Command + "\n\n" + detail, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                PrimaryButtonText = "确认", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await confirm.ShowWithBackendRecoveryAsync(pageOwner: _owner) != ContentDialogResult.Primary || !_active) return;
            if (PageBackendSessions.SystemWriteBlocker is { } changed) throw new InvalidOperationException(changed);
            _writing = true; UpdateButtons();
            var response = await _caller.InvokeAsync(BackendCommands.SystemStartupChange,
                new StartupItemChangeArgs(row.Info.Id, row.Info.Fingerprint, action), null, CancellationToken.None);
            ShowResult(response); if (_active && !_needsReview) await RefreshAsync();
        });
    }
    private async void RestoreAll_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (PageBackendSessions.SystemWriteBlocker is { } blocked) throw new InvalidOperationException(blocked);
        var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = "恢复本工具停用的全部自启动项目？",
            Content = "将逐项恢复原始备份，发现不同的同名新登记时停止该项，失败项目保留备份。请先退出对应应用及其更新程序，避免同时改写启动登记。不会结束当前程序或立即重启。",
            PrimaryButtonText = "恢复", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await confirm.ShowWithBackendRecoveryAsync(pageOwner: _owner) != ContentDialogResult.Primary || !_active) return;
        _writing = true; UpdateButtons();
        var response = await _caller.InvokeAsync(BackendCommands.SystemStartupRestoreAll, new { }, null, CancellationToken.None);
        ShowResult(response); if (_active && !_needsReview) await RefreshAsync();
    });
    private void ShowResult(BackendResult response)
    {
        if (!_active) return;
        var result = BackendResults.Map(response, "自启动操作");
        _needsReview = response.PendingRestore || response.RequiresReview || result.OutcomeUnknown;
        ResultBar.IsOpen = true; ResultBar.Message = result.Message + (_needsReview ? " 请到设置中的未确认操作入口核对，原始备份保留。" : "");
        ResultBar.Severity = result.Success ? InfoBarSeverity.Success : result.OutcomeUnknown ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
    }
    private async void WindowsSettings_Click(object sender, RoutedEventArgs e)
    {
        try { if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps"))) throw new IOException("Windows未打开启动应用设置。"); }
        catch (Exception ex) { if (_active) { ResultBar.IsOpen = true; ResultBar.Severity = InfoBarSeverity.Warning; ResultBar.Message = ex.Message; } }
    }
}
