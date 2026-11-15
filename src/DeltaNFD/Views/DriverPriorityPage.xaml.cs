using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed class DriverPriorityRow(DriverPriorityItem info) : INotifyPropertyChanged
{
    public DriverPriorityItem Info { get; } = info;
    private bool _enabled;
    private readonly int _initialMsi = info.Device.Settings?.Msi is { Exists: true, Value: 1 } ? 1 : 0;
    private readonly int _initialPriority = (int)(info.Device.Settings?.Priority.Value ?? 0);
    private readonly string _initialLimit = info.Device.Settings?.MessageLimit is { Exists: true } value ? value.Value.ToString(CultureInfo.InvariantCulture) : "";
    private int _msi = info.Device.Settings?.Msi is { Exists: true, Value: 1 } ? 1 : 0;
    private int _priority = (int)(info.Device.Settings?.Priority.Value ?? 0);
    private string _limit = info.Device.Settings?.MessageLimit is { Exists: true } value ? value.Value.ToString(CultureInfo.InvariantCulture) : "";
    public int MsiIndex { get => _msi; set { _msi = value; Changed(); } }
    public int PriorityIndex { get => _priority; set { _priority = value; Changed(); } }
    public string LimitText { get => _limit; set { _limit = value; Changed(); } }
    public bool Editable => _enabled && Info.CanApply;
    public bool CanEditMsi => Editable && Info.Device.SupportedModes is { } modes && (modes & 6) != 0;
    public bool CanEditLimit => CanEditMsi && Info.Device.MaximumMessages is > 0;
    private bool Dirty => _msi != _initialMsi || _priority != _initialPriority || _limit != _initialLimit;
    public bool CanApply => Editable && Dirty && ValidationText.Length == 0;
    public bool CanRestore => _enabled && Info.CanRestore;
    public bool CanKeepCurrent => _enabled && Info.HasBackup;
    public Visibility BackupVisibility => Info.HasBackup ? Visibility.Visible : Visibility.Collapsed;
    public string Detail => string.Join(" ", new[] { Info.Device.Detail, Info.BackupDetail }.Where(s => s.Length > 0));
    public string Description => $"{Info.Device.ClassName} · 驱动 {Info.Device.DriverVersion}\n支持模式：{Modes(Info.Device.SupportedModes)}；最大消息数：{Info.Device.MaximumMessages?.ToString() ?? "未知"}；当前IRQ："
        + (Info.Device.Irqs.Count == 0 ? "未分配或未读取" : string.Join(", ", Info.Device.Irqs));
    public string CurrentText => Info.Device.Settings is not { } s ? "当前中断设置未完整读取。"
        : $"登记设置：MSI {Slot(s.Msi)}；消息上限 {Slot(s.MessageLimit)}；优先级 {Slot(s.Priority)}。";
    private static string Slot(InterruptDword value) => value.Exists ? value.Value.ToString(CultureInfo.InvariantCulture) : "驱动默认（值不存在）";
    private static string Modes(uint? bits) => bits is null or 0 ? "未知" : string.Join(" / ", new[] { (1u, "传统IRQ"), (2u, "MSI"), (4u, "MSI-X") }.Where(p => (bits.Value & p.Item1) != 0).Select(p => p.Item2));
    public string ValidationText
    {
        get { if (!Dirty) return ""; try { _ = DriverPriorityPolicy.Desired(Info.Device, Args(DriverPriorityAction.Apply)); return ""; } catch (Exception ex) { return ex.Message; } }
    }
    public DriverPriorityArgs Args(DriverPriorityAction action)
    {
        if (action != DriverPriorityAction.Apply) return new(Info.Device.InstanceId, Info.Fingerprint, action, true);
        var text = _limit.Trim(); uint? limit = null;
        if (text.Length > 0) { if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) throw new IOException("消息上限请输入非负整数。"); limit = parsed; }
        if (_msi is < 0 or > 1 || _priority is < 0 or > 3) throw new IOException("请选择有效的MSI状态和中断优先级。");
        return new(Info.Device.InstanceId, Info.Fingerprint, action, true, _msi == _initialMsi ? null : _msi == 1,
            _limit != _initialLimit, _limit == _initialLimit ? null : limit, _priority == _initialPriority ? null : _priority);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Enable(bool value) { _enabled = value; Changed(); }
    private void Changed()
    { foreach (var name in new[] { nameof(MsiIndex), nameof(PriorityIndex), nameof(LimitText), nameof(Editable), nameof(CanEditMsi), nameof(CanEditLimit), nameof(CanApply), nameof(CanRestore), nameof(CanKeepCurrent), nameof(ValidationText) }) PropertyChanged?.Invoke(this, new(name)); }
}

public sealed partial class DriverPriorityPage : Page
{
    private readonly PageBackendLease? _owner = PageBackendSessions.Manager.Current;
    private readonly BackendCaller _caller = new(BackendModules.System, PageBackendSessions.Manager.Current);
    public ObservableCollection<DriverPriorityRow> Rows { get; } = [];
    private readonly List<DriverPriorityRow> _all = [];
    private CancellationTokenSource _queries = new();
    private bool _active, _busy, _writing, _needsReview, _complete;
    private int _generation;
    public DriverPriorityPage()
    {
        if (!ReleaseFeaturePolicy.DriverPriorityEnabled) throw new InvalidOperationException("当前版本未开放驱动优先级。");
        InitializeComponent();
        Loaded += async (_, _) => { _active = true; _generation++; _queries = new(); PageBackendSessions.Manager.Changed += OnGate; await RunAsync(RefreshAsync); };
        Unloaded += (_, _) => { _active = false; _generation++; _queries.Cancel(); PageBackendSessions.Manager.Changed -= OnGate; };
    }
    private void OnGate() => DispatcherQueue.TryEnqueue(() => { if (_active) UpdateButtons(); });
    private void UpdateButtons()
    {
        var blocked = PageBackendSessions.SystemWriteBlocker;
        GateNotice.IsOpen = blocked is not null; GateNotice.Message = blocked ?? "";
        RefreshButton.IsEnabled = !_busy;
        var enabled = _active && !_busy && !_needsReview && _complete && blocked is null;
        foreach (var row in _all) row.Enable(enabled);
        RestoreAllButton.IsEnabled = enabled && _all.Any(r => r.Info.HasBackup);
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || !_active) return;
        _busy = true; BusyRing.IsActive = true; UpdateButtons();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        { if (_active) { _needsReview |= _writing; if (!_writing) { _complete = false; Rows.Clear(); _all.Clear(); }
            ResultBar.IsOpen = true; ResultBar.Severity = InfoBarSeverity.Warning; ResultBar.Message = ex.Message; } }
        finally { _busy = _writing = false; if (_active) { BusyRing.IsActive = false; UpdateButtons(); } }
    }
    private async Task RefreshAsync()
    {
        var generation = _generation;
        var response = await _caller.InvokeAsync(BackendCommands.SystemDriverPriorityList, new { }, null, _queries.Token);
        var report = BackendResults.RequireData<DriverPriorityReport>(response, "读取驱动中断设置");
        if (!_active || generation != _generation) return;
        _complete = report.Complete; _needsReview = false; _all.Clear(); _all.AddRange(report.Items.Select(i => new DriverPriorityRow(i))); Filter();
        SummaryText.Text = $"共 {report.Items.Count} 个设备，{report.BackupCount} 项原始备份。";
        if (!report.Complete) { ResultBar.IsOpen = true; ResultBar.Severity = InfoBarSeverity.Warning; ResultBar.Message = string.Join("；", report.Errors); }
    }
    private void Filter()
    {
        var query = SearchBox?.Text.Trim() ?? ""; Rows.Clear();
        foreach (var row in _all.Where(r => query.Length == 0 || (r.Info.Device.Name + r.Info.Device.ClassName + r.Info.Device.InstanceId).Contains(query, StringComparison.OrdinalIgnoreCase))) Rows.Add(row);
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_active) Filter(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);
    private async void Apply_Click(object sender, RoutedEventArgs e) => await ChangeAsync(sender, DriverPriorityAction.Apply);
    private async void Restore_Click(object sender, RoutedEventArgs e) => await ChangeAsync(sender, DriverPriorityAction.Restore);
    private async void KeepCurrent_Click(object sender, RoutedEventArgs e) => await ChangeAsync(sender, DriverPriorityAction.KeepCurrent);
    private async Task ChangeAsync(object sender, DriverPriorityAction action)
    {
        if (sender is not FrameworkElement { DataContext: DriverPriorityRow row }) return;
        await RunAsync(async () =>
        {
            if (!_complete || PageBackendSessions.SystemWriteBlocker is { }) throw new IOException("当前状态不允许修改，请刷新并核对。");
            var args = row.Args(action);
            var detail = action == DriverPriorityAction.Apply ? $"MSI：{(args.MsiEnabled is { } m ? m ? "开启" : "关闭" : "保持")}；消息上限：{(args.SetMessageLimit ? args.MessageLimit is > 0 ? args.MessageLimit.ToString() : "驱动默认" : "保持")}；优先级：{(args.Priority is { } p ? new[] { "驱动默认", "低", "正常", "高" }[p] : "保持")}。\n写入后需手动重启。"
                : action == DriverPriorityAction.Restore ? "按原始备份恢复此设备。发现驱动身份变化或外部改动时保留记录，修改后需手动重启。"
                : "当前设备设置不变；原始记录移入软件备份归档目录，退出直接恢复列表。";
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = action == DriverPriorityAction.Apply ? "应用此设备的中断设置？" : action == DriverPriorityAction.Restore ? "恢复此设备的原值？" : "保留当前并归档？",
                Content = new TextBlock { Text = row.Info.Device.Name + "\n" + row.Info.Device.InstanceId + "\n\n" + detail, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                PrimaryButtonText = "确认", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowWithBackendRecoveryAsync(pageOwner: _owner) != ContentDialogResult.Primary || !_active) return;
            if (PageBackendSessions.SystemWriteBlocker is { } blocked) throw new IOException(blocked);
            _writing = true; UpdateButtons();
            var response = await _caller.InvokeAsync(BackendCommands.SystemDriverPriorityChange, args, null, CancellationToken.None);
            ShowResult(response); if (_active && !_needsReview) await RefreshAsync();
        });
    }
    private async void RestoreAll_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!_complete || PageBackendSessions.SystemWriteBlocker is { }) throw new IOException("当前状态不允许恢复，请刷新并核对。");
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "恢复本工具修改的全部设备？", Content = "逐设备按原始备份恢复中断设置。冲突或失败项目保留记录；不重启设备，完成后需手动重启电脑。",
            PrimaryButtonText = "恢复", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowWithBackendRecoveryAsync(pageOwner: _owner) != ContentDialogResult.Primary || !_active) return;
        if (PageBackendSessions.SystemWriteBlocker is { } blocked) throw new IOException(blocked);
        _writing = true; UpdateButtons();
        var response = await _caller.InvokeAsync(BackendCommands.SystemDriverPriorityRestoreAll, new { }, null, CancellationToken.None);
        ShowResult(response); if (_active && !_needsReview) await RefreshAsync();
    });
    private void ShowResult(BackendResult response)
    {
        if (!_active) return;
        var result = BackendResults.Map(response, "驱动中断设置"); _needsReview = response.PendingRestore || response.RequiresReview || result.OutcomeUnknown;
        ResultBar.IsOpen = true; ResultBar.Message = result.Message;
        ResultBar.Severity = result.Success ? InfoBarSeverity.Success : result.OutcomeUnknown ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
    }
}
