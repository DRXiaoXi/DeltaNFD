using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class ProcessPage
{
    private bool _backgroundBusy;
    private bool _timerIgnoreBusy;
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
            MemoryPressureBox.Value = status.Config.MemoryPressurePercent;
            MemoryBatchLimitBox.Value = status.Config.MemoryBatchLimit;
            MemoryCooldownBox.Value = status.Config.MemoryCooldownSeconds / 60d;
            MemoryModeCombo.SelectedIndex = status.Config.MemoryMode switch
            { ProcmanMemoryModes.Aggressive => 1, ProcmanMemoryModes.DeltaStable => 2, _ => 0 };
        }
        UpdateMemoryModeDescription();
        MemoryBatchPolicyText.Text = "可设置范围：1–256 个进程。";
        MemoryCooldownPolicyText.Text = "可设置范围：1–1440 分钟。";
        Controls.HelpTip.SetText(MemoryParametersTitle,
            $"自动回收需内存持续高压至少 30 秒，并满足清理间隔。当前策略间隔 {status.EffectiveMemoryCooldownSeconds / 60d:0.#} 分钟。"
            + "自动批次与同一进程重复回收共用该间隔；紧急策略可能提高最短间隔。普通模式使用单次数量上限，激进和稳洲模式不使用。修改后点击保存生效。");
        AutomaticMemoryStatusText.Text = status.AutomaticMemoryActive ? $"自动回收已启用，模式：{(status.Config.MemoryMode switch { ProcmanMemoryModes.Aggressive => "激进回收", ProcmanMemoryModes.DeltaStable => "稳洲回收", _ => "普通回收" })}。" : status.Config.AutomaticMemoryEnabled
            ? "自动回收等待帧格同步，请在帧格页核对状态。" : "自动回收已随帧格停用。";
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

    private async void RefreshBackground_Click(object sender, RoutedEventArgs e)
    {
        await LoadBackgroundAsync(syncControls: false);
        await LoadMemoryPrelaunchAsync();
    }

    private async Task<bool> SaveBackgroundAsync(Func<ProcmanBackgroundConfig, ProcmanBackgroundConfig> edit)
    {
        if (_backgroundBusy) return false;
        _backgroundBusy = true; BackgroundSettingsPanel.IsEnabled = false;
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
        finally { _backgroundBusy = false; BackgroundSettingsPanel.IsEnabled = true; UpdateSelectedExclusion(); UpdateSelectedTimerIgnoreList(); }
    }

    private Task<bool> AddExclusionAsync(string path) => SaveBackgroundAsync(c => c with
    { ExcludedExecutablePaths = c.ExcludedExecutablePaths.Append(path.Trim().Trim('"')).Distinct(StringComparer.OrdinalIgnoreCase).ToList() });

    private static int BackgroundInteger(NumberBox box, int minimum, int maximum, string label)
    {
        if (!double.IsFinite(box.Value) || box.Value < minimum || box.Value > maximum || box.Value != Math.Truncate(box.Value))
            throw new ArgumentException($"{label}请输入 {minimum}–{maximum} 之间的整数。");
        return (int)box.Value;
    }

    private async void SaveBackgroundParameters_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var memory = BackgroundInteger(MemoryPressureBox, 30, 95, "内存回收阈值：");
            var memoryBatch = BackgroundInteger(MemoryBatchLimitBox, 1, 256, "单次内存回收进程数：");
            var memoryCooldown = BackgroundInteger(MemoryCooldownBox, 1, 1440, "内存回收间隔（分钟）：");
            await SaveBackgroundAsync(c => c with
            {
                MemoryPressurePercent = memory,
                MemoryBatchLimit = memoryBatch,
                MemoryCooldownSeconds = memoryCooldown * 60,
                MemoryMode = MemoryModeCombo.SelectedIndex switch
                { 1 => ProcmanMemoryModes.Aggressive, 2 => ProcmanMemoryModes.DeltaStable, _ => ProcmanMemoryModes.Normal },
            });
        }
        catch (Exception ex) { BackgroundConfigNoteText.Text = "参数未保存：" + ex.Message; }
    }
    private void MemoryMode_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMemoryModeDescription();
    private void UpdateMemoryModeDescription()
    {
        if (MemoryModeHelpTitle is null || MemoryBatchLimitBox is null) return;
        MemoryBatchLimitBox.IsEnabled = MemoryModeCombo.SelectedIndex == 0;
        Controls.HelpTip.SetText(MemoryModeHelpTitle, MemoryModeCombo.SelectedIndex switch
        {
            1 => "激进：不统计进程，直接回收全局工作集和低优先级待备缓存，忽略逐进程保护和排除名单，可能影响 ACE、游戏、前台和系统。保留其他待备缓存，不强制写回修改页列表；需要管理员权限，仍可能出现换页或卡顿。",
            2 => "稳洲：跳过三角洲主进程、系统进程、ACE 保护进程和有规则的进程（含停用规则）；其余进程包含前台和工具自身，不使用普通排除名单或数量上限。不清空全局待备缓存、不强制写回修改页列表；不可访问或冷却中的目标跳过。",
            _ => "普通：沿用后台进程保护和排除名单，不检测 CPU 占用，不设工作集大小门槛；按工作集排序，使用单次数量设置。",
        });
    }

    public void ShowBackgroundSettings() => ProcessTabs.SelectedItem = BackgroundTab;

    private async void ExcludeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } row || string.IsNullOrWhiteSpace(row.ExecutablePath)) { ShowResult(false, "请先选择路径可确认的进程。"); return; }
        if (ProcmanProcessProtection.IsAceProtected(row.ExecutablePath)) { ShowResult(false, ProcmanProcessProtection.AceReason); return; }
        var path = row.ExecutablePath;
        var saved = await SaveBackgroundAsync(c => c with { ExcludedExecutablePaths = c.ExcludedExecutablePaths.Contains(path, StringComparer.OrdinalIgnoreCase)
            ? c.ExcludedExecutablePaths.Where(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList()
            : c.ExcludedExecutablePaths.Append(path).ToList() });
        ShowResult(saved, BackgroundConfigNoteText.Text);
    }

    /// <summary>把选中进程的映像名加入/移出「定时器精度请求忽略列表」，然后让后端立即评估（2 秒 tick 内施加/恢复）。</summary>
    private async void TimerIgnoreList_Click(object sender, RoutedEventArgs e)
    {
        if (_timerIgnoreBusy) return;
        if (_selected is not { } row || string.IsNullOrWhiteSpace(row.ExecutablePath))
        {
            ShowResult(false, "请先选择路径可确认的进程。");
            return;
        }
        var name = Path.GetFileName(row.ExecutablePath);
        if (name.Length == 0) { ShowResult(false, "无法确认该进程的映像名。"); return; }
        var list = (AppSettingsStore.Read().TimerIgnoreExtraProcesses ?? new List<string>()).ToList();
        var inList = list.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (inList) list.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        else list.Add(name);
        _timerIgnoreBusy = true;
        try
        {
            AppSettingsStore.Update(s => s.TimerIgnoreExtraProcesses = list);
            UpdateSelectedTimerIgnoreList();
            var synced = await BackendProcmanService.Instance.SyncFrameBackgroundAsync();
            var backend = BackendProcmanService.Instance;
            var state = backend.LatestStatus?.TimerIgnoreList;
            var confirmed = backend.HasCurrentStatus && state is not null
                && BackendProcmanService.IsFresh(state.UpdatedUtc) && !state.HasError && !state.RestorePending && state.Supported;
            ShowResult(synced.Success && confirmed,
                (inList ? "移出配置已保存：" : "加入配置已保存：") + name + "\n" + TimerIgnoreBackendText()
                + (synced.Success ? "" : "\n后端同步未完成：" + synced.Message));
        }
        catch (Exception ex) { ShowResult(false, "忽略列表操作未完成：" + ex.Message); }
        finally { _timerIgnoreBusy = false; UpdateSelectedTimerIgnoreList(); }
    }

    private async void AddExclusion_Click(object sender, RoutedEventArgs e) => await AddExclusionAsync(BackgroundExcludePathBox.Text);

    private async void RemoveExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
            await SaveBackgroundAsync(c => c with { ExcludedExecutablePaths = c.ExcludedExecutablePaths.Where(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList() });
    }
}
