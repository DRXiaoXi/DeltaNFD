using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Controls;

public sealed partial class DisplayInterruptPriorityCard : UserControl
{
    public sealed record AdapterChoice(DisplayInterruptPriorityItem Item)
    {
        public string Label => Item.Adapter.Name;
    }

    private readonly BackendCaller _caller = new(BackendModules.System);
    private DisplayInterruptPriorityReport? _report;
    private bool _active;
    private bool _busy;
    private int _epoch;
    private DisplayInterruptPriorityItem? Selected => (Adapters?.SelectedItem as AdapterChoice)?.Item;

    public DisplayInterruptPriorityCard()
    {
        InitializeComponent();
        ExperimentalWarning.IsOpen = ReleaseFeaturePolicy.DisplayInterruptPriorityEnabled;
        ApplyHigh.Visibility = ReleaseFeaturePolicy.DisplayInterruptPriorityEnabled ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            _active = true;
            _epoch++;
            PageBackendSessions.Manager.Changed += OnGameStateChanged;
            _ = RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            _active = false;
            _epoch++;
            PageBackendSessions.Manager.Changed -= OnGameStateChanged;
        };
    }

    private void OnGameStateChanged() => DispatcherQueue.TryEnqueue(() => { if (_active) UpdateControls(); });
    private void Adapter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_active) UpdateControls();
    }

    private void UpdateControls()
    {
        var item = Selected;
        var blocker = PageBackendSessions.SystemWriteBlocker;
        var editable = _active && !_busy && _report is { Complete: true } && blocker is null;
        RefreshAdapters.IsEnabled = _active && !_busy;
        Adapters.IsEnabled = _report is { Items.Count: > 0 } && !_busy;
        ApplyHigh.IsEnabled = editable && ReleaseFeaturePolicy.DisplayInterruptPriorityEnabled && item is { CanApply: true };
        RestoreOriginal.IsEnabled = editable && item is { CanRestore: true };
        RestoreOriginal.Visibility = _report is { BackupCount: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (_busy) { AdapterStatus.Text = "正在核对显卡中断设置…"; return; }
        if (_report is null) { AdapterStatus.Text = "请刷新显卡状态。"; return; }
        if (item is null) { AdapterStatus.Text = _report is { Items.Count: > 0 } ? "请选择要核对的显卡。" : _report?.Message ?? "请刷新显卡状态。"; return; }
        var adapter = item.Adapter;
        var priority = !adapter.Readable ? "未能读取" : adapter.Priority switch
        {
            null => "默认（无显式设置）", 0 => "Undefined", 1 => "Low", 2 => "Normal", 3 => "High", var value => $"DWORD {value}",
        };
        AdapterStatus.Text = $"当前登记 {priority}\n{adapter.InstanceId}";
        if (item.Detail.Length > 0) AdapterStatus.Text += "\n" + item.Detail;
        if (blocker is not null) AdapterStatus.Text += "\n请完全退出游戏并等待状态确认后再修改或恢复。";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Apply_Click(object sender, RoutedEventArgs e) => await ChangeAsync(restore: false);
    private async void Restore_Click(object sender, RoutedEventArgs e) => await ChangeAsync(restore: true);

    private Task RefreshAsync() => RunAsync(async epoch => await ReadAsync(epoch));

    private async Task ReadAsync(int epoch)
    {
        var previous = Selected?.Adapter.InstanceId;
        var response = await _caller.InvokeAsync(BackendCommands.SystemDisplayInterruptList, new { }, null, CancellationToken.None);
        if (!_active || epoch != _epoch) return;
        _report = BackendResults.RequireData<DisplayInterruptPriorityReport>(response, "读取显卡中断优先级");
        var choices = _report.Items.Select(item => new AdapterChoice(item)).ToArray();
        Adapters.ItemsSource = choices;
        Adapters.SelectedItem = choices.FirstOrDefault(c => c.Item.Adapter.InstanceId == previous)
            ?? (choices.Length == 1 ? choices[0] : null);
    }

    private Task ChangeAsync(bool restore) => RunAsync(async epoch =>
    {
        var item = Selected;
        if (_report is not { Complete: true } || item is null || PageBackendSessions.SystemWriteBlocker is not null
            || (restore ? !item.CanRestore : !item.CanApply || !ReleaseFeaturePolicy.DisplayInterruptPriorityEnabled)) return;
        var prompt = restore ? "将恢复此显卡的原值。恢复后请手动重启电脑。"
            : "将备份原值，再把选中显卡的硬件中断优先级设为 High。该设置影响使用此显卡的应用，可能导致游戏不稳定。保存后请手动重启电脑。";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = restore ? "恢复显卡中断优先级？" : "将显卡中断优先级设为 High？",
            Content = new TextBlock { Text = $"{item.Adapter.Name}\n{item.Adapter.InstanceId}\n\n{prompt}", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = restore ? "恢复原值" : "备份并应用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary || !_active || epoch != _epoch
            || PageBackendSessions.SystemWriteBlocker is not null) return;
        var response = await _caller.InvokeAsync(BackendCommands.SystemDisplayInterruptChange,
            new DisplayInterruptPriorityArgs(item.Adapter.InstanceId, item.Fingerprint, restore, true), null, CancellationToken.None);
        if (!_active || epoch != _epoch) return;
        ShowResult(response.Message, response.Status == BackendResultStatuses.Ok);
        if (response.RequiresReview || response.PendingRestore) _report = null;
        else await ReadAsync(epoch);
    });

    private async Task RunAsync(Func<int, Task> work)
    {
        if (!_active || _busy) return;
        _busy = true;
        var epoch = _epoch;
        UpdateControls();
        try { await work(epoch); }
        catch (Exception ex)
        {
            if (_active && epoch == _epoch) { _report = null; ShowResult(ex.Message, false); }
        }
        finally
        {
            _busy = false;
            if (_active) UpdateControls();
        }
    }

    private void ShowResult(string message, bool success)
    {
        Result.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        Result.Message = message;
        Result.IsOpen = true;
    }
}
