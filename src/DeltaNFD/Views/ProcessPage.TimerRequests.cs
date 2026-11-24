using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class ProcessPage
{
    private TimerRequestSurvey? _timerRequestSurvey;
    private bool _timerRequestBusy;

    private IReadOnlyList<string> SavedTimerRequestPaths()
        => AppSettingsStore.Read().TimerIgnoreDetectedPaths ?? [];

    private void ShowTimerRequestRows()
    {
        var old = TimerRequestList.SelectedItem as TimerRequestProcess;
        var rows = _timerRequestSurvey?.Processes.ToList() ?? [];
        foreach (var path in SavedTimerRequestPaths().Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (rows.Any(row => row.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            rows.Add(new(0, 0, Path.GetFileName(path), path, 0, false, false));
        }
        TimerRequestList.ItemsSource = rows;
        if (old is not null) TimerRequestList.SelectedItem = rows.FirstOrDefault(row => row.Pid == old.Pid && row.Path == old.Path);
        UpdateTimerRequestActions();
    }

    private void UpdateTimerRequestActions()
    {
        if (TimerRequestList is null || TimerRequestPath is null) return;
        var row = TimerRequestList.SelectedItem as TimerRequestProcess;
        var saved = row is not null && SavedTimerRequestPaths().Contains(row.Path, StringComparer.OrdinalIgnoreCase);
        TimerRequestPath.Text = row?.Path ?? "";
        BlockTimerRequestButton.IsEnabled = !_timerRequestBusy && row?.CanIgnore == true && !saved;
        UnblockTimerRequestButton.IsEnabled = !_timerRequestBusy && saved;
        DetectTimerRequestsButton.IsEnabled = !_timerRequestBusy;
    }

    private void TimerRequest_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTimerRequestActions();

    private async void DetectTimerRequests_Click(object sender, RoutedEventArgs e)
    {
        if (_timerRequestBusy) return;
        _timerRequestBusy = true;
        TimerRequestStatus.Text = "正在读取 Windows 定时器请求…";
        UpdateTimerRequestActions();
        try
        {
            _timerRequestSurvey = await BackendProcmanService.Instance.ReadTimerRequestsAsync();
            TimerRequestStatus.Text = $"检测到 {_timerRequestSurvey.Processes.Count} 个请求进程。"
                + (_timerRequestSurvey.KernelPeriod100ns > 0
                    ? $" Windows 另报告 { _timerRequestSurvey.KernelPeriod100ns / 10000d:0.0000} ms 的内核请求。" : "");
        }
        catch (Exception ex)
        {
            _timerRequestSurvey = null;
            TimerRequestStatus.Text = "检测未完成。" + ex.Message;
        }
        finally
        {
            _timerRequestBusy = false;
            ShowTimerRequestRows();
        }
    }

    private async void BlockTimerRequest_Click(object sender, RoutedEventArgs e) => await SetTimerRequestRuleAsync(true);

    private async void UnblockTimerRequest_Click(object sender, RoutedEventArgs e) => await SetTimerRequestRuleAsync(false);

    private async Task SetTimerRequestRuleAsync(bool block)
    {
        if (_timerRequestBusy || TimerRequestList.SelectedItem is not TimerRequestProcess row
            || !Path.IsPathFullyQualified(row.Path) || (block && !row.CanIgnore)) return;
        _timerRequestBusy = true;
        UpdateTimerRequestActions();
        try
        {
            AppSettingsStore.UpdateRequired(settings =>
            {
                var paths = settings.TimerIgnoreDetectedPaths ?? [];
                settings.TimerIgnoreDetectedPaths = paths.Where(path => !path.Equals(row.Path, StringComparison.OrdinalIgnoreCase))
                    .Concat(block ? [row.Path] : Array.Empty<string>()).ToList();
            });
            var result = await BackendProcmanService.Instance.SyncFrameBackgroundAsync();
            var summary = block ? "已保存所选路径的屏蔽规则。" : "已移除所选路径的屏蔽规则。";
            TimerRequestStatus.Text = summary + "\n" + TimerIgnoreBackendText() + (result.Success ? "" : "\n" + result.Message);
        }
        catch (Exception ex) { TimerRequestStatus.Text = "屏蔽规则未完成。" + ex.Message; }
        finally
        {
            _timerRequestBusy = false;
            ShowTimerRequestRows();
        }
    }
}

