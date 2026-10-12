using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class ProcessPage
{
    private bool _backgroundBusy;
    private bool _backgroundConfigLoaded;
    private readonly HashSet<string> _backgroundExcludedPaths = new(StringComparer.OrdinalIgnoreCase);

    private async Task<ProcmanBackgroundStatus> ReadBackgroundAsync()
    {
        var response = await BackendProcmanService.Instance.InvokeAsync(BackendCommands.ProcmanBackgroundGet, new { });
        return BackendResults.RequireData<ProcmanBackgroundStatus>(response, "后台管理设置");
    }

    private void DisplayBackground(ProcmanBackgroundStatus status, bool syncControls)
    {
        _backgroundExcludedPaths.Clear();
        _backgroundExcludedPaths.UnionWith(status.Config.ExcludedExecutablePaths);
        _backgroundConfigLoaded = true;
        foreach (var row in _rows) row.BackgroundExcluded = _backgroundExcludedPaths.Contains(row.ExecutablePath);
        UpdateSelectedExclusion();
        if (syncControls)
        {
            AutomaticMemoryToggle.IsOn = status.Config.AutomaticMemoryEnabled;
            MemoryPressureBox.Value = status.Config.MemoryPressurePercent;
        }
        AutomaticMemoryStatusText.Text = status.AutomaticMemoryActive ? "自动回收已启用。" : status.Config.AutomaticMemoryEnabled
            ? "已保存自动回收偏好，当前自动任务已停止。重新启用并保存可恢复。" : "自动回收已关闭。";
        if (status.Note.Length > 0) AutomaticMemoryStatusText.Text = "自动回收暂停：" + status.Note;
        if (status.LastCheckUtc is { } check) AutomaticMemoryStatusText.Text += $" 最近检查：{check.LocalDateTime:HH:mm:ss}。";
        if (status.Feedback.Length > 0) AutomaticMemoryStatusText.Text += "\n" + status.Feedback;
        if (status.LastReport is { } report)
        {
            AutomaticMemoryStatusText.Text += "\n最近一轮：\n" + string.Join("\n", report.Items.Select(i => $"{i.Name} · PID {i.Pid}：{i.Message}"));
            AutomaticMemoryStatusText.Text += MemoryTelemetryText(report);
        }
        BackgroundExclusionsPanel.Children.Clear();
        if (status.Config.ExcludedExecutablePaths.Count == 0)
            BackgroundExclusionsPanel.Children.Add(new TextBlock { Text = "尚无自定义排除项。游戏、工具、系统等保护仍然生效。", TextWrapping = TextWrapping.Wrap });
        foreach (var path in status.Config.ExcludedExecutablePaths)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center });
            var remove = new Button { Content = "移除", Tag = path };
            Grid.SetColumn(remove, 1);
            remove.Click += RemoveExclusion_Click;
            row.Children.Add(remove); BackgroundExclusionsPanel.Children.Add(row);
        }
    }

    private static string MemoryTelemetryText(ProcmanMemoryTrimReport report)
    {
        if (report.Before is not { } before || report.After is not { } after) return "\n系统内存快照不完整，无法确认可用内存变化。";
        var text = $"\n系统可用内存 {before.AvailablePhysicalBytes / 1048576d:0} → {after.AvailablePhysicalBytes / 1048576d:0} MiB（同期变化，不归因于单一操作）。";
        if (after.CommitBytes is { } commit && after.CommitLimitBytes is > 0)
            text += $"\n系统提交量 {commit / 1073741824d:0.##}/{after.CommitLimitBytes.Value / 1073741824d:0.##} GiB；工作集回收不会等量减少提交量。";
        return text;
    }

    private async Task LoadBackgroundAsync(bool syncControls)
    {
        try { DisplayBackground(await ReadBackgroundAsync(), syncControls); }
        catch (Exception ex) { BackgroundConfigNoteText.Text = "后台设置读取失败：" + ex.Message; }
    }

    private async void RefreshBackground_Click(object sender, RoutedEventArgs e) => await LoadBackgroundAsync(syncControls: false);

    private async Task<bool> SaveBackgroundAsync(Func<ProcmanBackgroundConfig, ProcmanBackgroundConfig> edit)
    {
        if (_backgroundBusy) return false;
        _backgroundBusy = true; SaveBackgroundButton.IsEnabled = false;
        UpdateSelectedExclusion();
        BackgroundConfigNoteText.Text = "正在保存后台管理设置…";
        try
        {
            // Read the latest shared settings: editing exclusions must not accidentally enable an unsaved toggle.
            var current = await ReadBackgroundAsync();
            var response = await BackendProcmanService.Instance.InvokeAsync(BackendCommands.ProcmanBackgroundSave, edit(current.Config));
            var saved = BackendResults.RequireData<ProcmanBackgroundStatus>(response, "保存后台管理设置");
            DisplayBackground(saved, syncControls: true);
            BackgroundConfigNoteText.Text = response.Message;
            return true;
        }
        catch (Exception ex) { BackgroundConfigNoteText.Text = "后台设置保存未完成：" + ex.Message; return false; }
        finally { _backgroundBusy = false; SaveBackgroundButton.IsEnabled = true; UpdateSelectedExclusion(); }
    }

    private async void SaveBackground_Click(object sender, RoutedEventArgs e)
    {
        var threshold = MemoryPressureBox.Value;
        if (!double.IsFinite(threshold) || threshold is < 30 or > 95 || threshold != Math.Truncate(threshold))
        { BackgroundConfigNoteText.Text = "请输入 30–95 之间的整数内存百分比。"; return; }
        var enabled = AutomaticMemoryToggle.IsOn;
        await SaveBackgroundAsync(c => c with { AutomaticMemoryEnabled = enabled, MemoryPressurePercent = (int)threshold });
    }

    private Task<bool> AddExclusionAsync(string path) => SaveBackgroundAsync(c => c with
    { ExcludedExecutablePaths = c.ExcludedExecutablePaths.Append(path.Trim().Trim('"')).Distinct(StringComparer.OrdinalIgnoreCase).ToList() });

    private async void ExcludeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } row || string.IsNullOrWhiteSpace(row.ExecutablePath)) { ShowResult(false, "请先选择路径可确认的进程。"); return; }
        var path = row.ExecutablePath;
        var saved = await SaveBackgroundAsync(c => c with { ExcludedExecutablePaths = c.ExcludedExecutablePaths.Contains(path, StringComparer.OrdinalIgnoreCase)
            ? c.ExcludedExecutablePaths.Where(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList()
            : c.ExcludedExecutablePaths.Append(path).ToList() });
        ShowResult(saved, BackgroundConfigNoteText.Text);
    }

    private async void AddExclusion_Click(object sender, RoutedEventArgs e) => await AddExclusionAsync(BackgroundExcludePathBox.Text);

    private async void RemoveExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
            await SaveBackgroundAsync(c => c with { ExcludedExecutablePaths = c.ExcludedExecutablePaths.Where(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList() });
    }
}
