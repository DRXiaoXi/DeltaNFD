using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class ProcessPage
{
    private bool _workspaceReady;
    private object? _lastProcessTab;
    private bool IsProcessTab => ProcessTabs.SelectedItem is TabViewItem selected && selected == GeneralTab;
    private bool IsBackgroundTab => ProcessTabs.SelectedItem is TabViewItem selected && selected == BackgroundTab;

    private void ProcessTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_workspaceReady) return;
        if (ReferenceEquals(_lastProcessTab, ProcessTabs.SelectedItem)) return;
        _lastProcessTab = ProcessTabs.SelectedItem;
        if (IsBackgroundTab)
        {
            _ = LoadMemoryPrelaunchAsync();
            return;
        }
        if (ProcessTabs.SelectedItem is TabViewItem rules && rules == RulesTab)
        {
            _ = LoadRulesAsync();
            return;
        }
        if (IsProcessTab) _ = RefreshListAsync(preserveSelection: true);
    }

    private static bool IsFixedBackgroundProtection(string path)
    {
        if (IsProtectedPath(path)) return true;
        try
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Equals(DeltaForceLocator.GameProcessName, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("DeltaOptimizer", StringComparison.OrdinalIgnoreCase) || name.StartsWith("三角帧不掉洲", StringComparison.OrdinalIgnoreCase)) return true;
            return Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    private void UpdateSelectedExclusion()
    {
        if (SelectedExclusionStatusText is null || ExcludeSelectedButton is null) return;
        if (_selected is not { } row) { ExcludeSelectedButton.IsEnabled = false; SelectedExclusionStatusText.Text = "请选择应用。"; return; }
        if (!_backgroundConfigLoaded) { ExcludeSelectedButton.IsEnabled = false; SelectedExclusionStatusText.Text = "正在读取排除状态…"; return; }
        var excluded = _backgroundExcludedPaths.Contains(row.ExecutablePath);
        if (ProcmanProcessProtection.IsAceProtected(row.ExecutablePath))
        {
            SelectedExclusionStatusText.Text = ProcmanProcessProtection.AceReason;
            ExcludeSelectedButton.Content = "ACE 进程已保护";
            ExcludeSelectedButton.IsEnabled = false;
            return;
        }
        var fixedProtection = IsFixedBackgroundProtection(row.ExecutablePath);
        SelectedExclusionStatusText.Text = fixedProtection
            ? "此应用固定受保护，不参与普通模式的内存回收。" + (excluded ? "另有自定义排除项，可移除；固定保护仍会保留。" : "")
            : excluded ? "已排除：此应用不参与普通模式的内存回收，重启后保留。"
            : "未加入自定义排除名单。前台、系统及已有属性设置或规则的自动保护仍然生效。";
        ExcludeSelectedButton.Content = excluded ? "取消此应用的排除" : fixedProtection ? "此应用固定受保护" : "排除内存回收";
        ExcludeSelectedButton.IsEnabled = !_backgroundBusy && (excluded || !fixedProtection) && !string.IsNullOrWhiteSpace(row.ExecutablePath);
    }

    private bool _timerIgnoreRefreshAttached;
    private bool _timerIgnoreRefreshing;

    private async Task RefreshTimerIgnoreStatusAsync()
    {
        if (_timerIgnoreRefreshing || _detachedFromWindow || !IsProcessTab) return;
        _timerIgnoreRefreshing = true;
        try { await Services.Backends.BackendProcmanService.Instance.InvokeAsync(DeltaNFD.Backends.BackendCommands.ProcmanStatus, new { }); }
        catch (Exception ex) { Log.Warn("定时器忽略列表状态读取未完成：" + ex.Message); }
        finally { _timerIgnoreRefreshing = false; if (!_detachedFromWindow) UpdateSelectedTimerIgnoreList(); }
    }

    private string TimerIgnoreBackendText()
    {
        var backend = Services.Backends.BackendProcmanService.Instance;
        if (!backend.HasCurrentStatus) return "后端状态未确认：" + (backend.StatusError.Length > 0 ? backend.StatusError : "采样已过期，请刷新");
        var state = backend.LatestStatus!.TimerIgnoreList;
        if (!Services.Backends.BackendProcmanService.IsFresh(state.UpdatedUtc)) return "忽略列表采样已过期，实际效果未确认。";
        if (state.RestorePending) return "列表有恢复待处理记录：" + state.Message;
        if (state.HasError) return "列表施加被拒绝或未完成：" + state.Message;
        if (!state.Supported) return "当前系统不支持定时器忽略列表。";
        return $"列表整体：已确认 {state.AppliedCount}/{state.InstanceCount} 个实例；" + state.Message
            + "（整体计数不代表当前选中进程已生效）";
    }

    private void UpdateSelectedTimerIgnoreList()
    {
        if (TimerIgnoreListStatusText is null || TimerIgnoreListButton is null) return;
        if (!_timerIgnoreRefreshAttached)
        {
            _timerIgnoreRefreshAttached = true;
            // Reuse the page's existing timer; StopPolling stops it on unload.
            _timer.Tick += async (_, _) =>
            {
                if (_detachedFromWindow || !IsProcessTab) return;
                UpdateSelectedTimerIgnoreList();
                if (AutoRefreshToggle.IsChecked == true) await RefreshTimerIgnoreStatusAsync();
            };
            _ = RefreshTimerIgnoreStatusAsync();
        }
        if (_selected is not { } row || string.IsNullOrWhiteSpace(row.ExecutablePath))
        {
            TimerIgnoreListButton.IsEnabled = false;
            TimerIgnoreListStatusText.Text = "请选择路径可确认的进程。";
            return;
        }
        var name = Path.GetFileName(row.ExecutablePath);
        var list = AppSettingsStore.Read().TimerIgnoreExtraProcesses ?? new List<string>();
        var inList = name.Length > 0 && list.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        TimerIgnoreListStatusText.Text = inList
            ? $"配置已加入：{name}；是否生效由后端核验身份、权限与时钟门禁。"
            : $"配置未加入：{name}；移除配置后的原值恢复仍须后端确认。";
        TimerIgnoreListStatusText.Text += "\n" + TimerIgnoreBackendText();
        TimerIgnoreListButton.Content = inList ? "从定时器忽略列表移除" : "加入定时器忽略列表";
        TimerIgnoreListButton.IsEnabled = !_timerIgnoreRefreshing && !_timerIgnoreBusy && name.Length > 0;
    }

    private void OpenSelectedRules_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } row) return;
        SetRuleTarget(row.ExecutablePath);
        StandaloneRulePathBox.Text = row.ExecutablePath;
        SavedRuleEditor.Visibility = Visibility.Visible;
        ProcessTabs.SelectedItem = RulesTab;
    }

    private void SetStandaloneRuleTarget_Click(object sender, RoutedEventArgs e)
    {
        var path = StandaloneRulePathBox.Text.Trim().Trim('"');
        try
        {
            if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请输入完整 EXE 路径。");
            SetRuleTarget(Path.GetFullPath(path));
            SavedRuleEditor.Visibility = Visibility.Visible;
            RuleNoteText.Text = "已选择应用，保存后作为独立的总是规则应用。";
        }
        catch (Exception ex) { RuleNoteText.Text = ex.Message; }
    }

    private void OpenFrameBackground_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateByTag("frame");
}
