using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeltaNFD.Views;

/// <summary>
/// 调度改善 · 三角洲规则（原进程管理「三角洲规则」页签）：只编辑三角洲主进程的持久规则，
/// 保存、删除与执行仍由进程管理后端完成，规则仅在帧格后台功能启动后应用。
/// </summary>
public sealed partial class LabPage
{
    private readonly List<ToggleButton> _gameRuleProcessorToggles = [];
    private IReadOnlyList<ProcmanRuleEntry> _gameRules = [];
    private ProcmanTopologyResult? _gameRuleTopology;
    private string _gameRulePath = "";
    private bool _gameRulesLoading;
    private bool _gameRuleSaveBusy;
    private bool _detachedFromWindow;

    private async Task InitializeGameRulesAsync()
    {
        if (_gameRulesLoading) return;
        _gameRulesLoading = true;
        GameRuleEditor.IsEnabled = false;
        GameRuleStatusText.Text = "正在查找三角洲主进程…";
        try
        {
            _gameRuleTopology ??= await BackendProcmanService.Instance.GetTopologyAsync();
            if (_detachedFromWindow) return;
            CpuSelectionPanel.Build(GameRuleCpuGroupPanel, _gameRuleProcessorToggles, _gameRuleTopology, showDetectionNote: true);
            await LoadGameRulesAsync();
            if (_detachedFromWindow) return;
            var path = DeltaProcessRules.FindTarget(DeltaForceLocator.FindRoots(), _gameRules);
            if (path.Length > 0) UseGameRulePath(path);
            else GameRuleStatusText.Text = "未找到三角洲主进程，请手动选择 " + DeltaProcessRules.ExecutableName + "。";
        }
        catch (Exception ex)
        {
            GameRuleStatusText.Text = "三角洲规则读取未完成：" + ex.Message;
        }
        finally
        {
            _gameRulesLoading = false;
        }
    }

    private void UseGameRulePath(string path)
    {
        _gameRulePath = DeltaProcessRules.NormalizeTarget(path);
        GameRulePathBox.Text = _gameRulePath;
        GameRuleStatusText.Text = "规则目标：" + _gameRulePath;
        GameRuleEditor.IsEnabled = true;
        LoadGameCapabilityRule();
    }

    private void LoadGameCapabilityRule()
    {
        var capability = ProcessRuleChoices.CapabilityAt(GameRuleCapabilityCombo.SelectedIndex);
        var rule = _gameRules.FirstOrDefault(r => r.Capability == capability
            && r.ExecutablePath.Equals(_gameRulePath, StringComparison.OrdinalIgnoreCase));
        GameRuleEnabledToggle.IsOn = rule?.Enabled ?? true;
        GameRuleAutoTakeoverCheck.IsChecked = rule?.AutoTakeover ?? false;
        RebuildGameRuleValues(rule?.Value);
    }

    private void RebuildGameRuleValues(string? selectedValue = null)
    {
        var index = GameRuleCapabilityCombo.SelectedIndex;
        var mask = ProcessRuleChoices.UsesMask(index);
        GameRuleValueCombo.Visibility = mask ? Visibility.Collapsed : Visibility.Visible;
        GameRuleMaskPanel.Visibility = mask ? Visibility.Visible : Visibility.Collapsed;
        if (mask)
        {
            CpuSelectionPanel.Select(_gameRuleProcessorToggles, selectedValue ?? "");
            GameRuleValueBox.Text = CpuSelectionPanel.ToRuleValue(_gameRuleProcessorToggles);
            return;
        }
        var choices = ProcessRuleChoices.ValuesFor(index);
        GameRuleValueCombo.Items.Clear();
        foreach (var (label, value) in choices)
            GameRuleValueCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        var selected = Array.FindIndex(choices, c => c.Value.Equals(selectedValue, StringComparison.OrdinalIgnoreCase));
        if (selected < 0) selected = ProcessRuleChoices.DefaultValueIndex(index);
        GameRuleValueCombo.SelectedIndex = choices.Length == 0 ? -1 : selected;
    }

    private async Task LoadGameRulesAsync()
    {
        var result = await BackendProcmanService.Instance.InvokeAsync(BackendCommands.ProcmanRulesList, new { });
        if (_detachedFromWindow) return;
        if (result.Data is not { } data) { GameRuleNoteText.Text = "规则读取失败：" + result.Message; return; }
        var all = BackendJson.Deserialize<ProcmanRulesListResult>(data).Rules;
        // 只显示三角洲主进程规则；其他程序规则仍在进程管理中编辑
        _gameRules = all.Where(r => Path.GetFileName(r.ExecutablePath).Equals(DeltaProcessRules.ExecutableName, StringComparison.OrdinalIgnoreCase)).ToList();
        GameRuleListPanel.Children.Clear();
        GameRuleCountText.Text = _gameRules.Count == 0 ? "暂无三角洲规则" : $"共 {_gameRules.Count} 条三角洲规则";
        foreach (var rule in _gameRules)
        {
            var line = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var value = rule.Capability == "memoryPriority" ? ProcessPriorityDisplay.Memory(rule.Value) : rule.Value;
            line.Children.Add(new TextBlock
            {
                Text = $"{ProcessRuleChoices.Title(rule.Capability)} = {value}" + (rule.Enabled ? "（总是）" : "（已停用）"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var edit = new Button { Content = "编辑", Tag = rule };
            edit.Click += (_, _) =>
            {
                UseGameRulePath(rule.ExecutablePath);
                GameRuleCapabilityCombo.SelectedIndex = ProcessRuleChoices.IndexOf(rule.Capability);
                LoadGameCapabilityRule();
            };
            Grid.SetColumn(edit, 1);
            line.Children.Add(edit);
            var delete = new Button { Content = "删除", Tag = rule };
            delete.Click += GameRuleDelete_Click;
            Grid.SetColumn(delete, 2);
            line.Children.Add(delete);
            GameRuleListPanel.Children.Add(line);
        }
    }

    private async void LoadGameRules_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadGameRulesAsync(); }
        catch (Exception ex) { GameRuleNoteText.Text = "规则读取未完成：" + ex.Message; }
    }

    private void UseGameRulePath_Click(object sender, RoutedEventArgs e)
    {
        try { UseGameRulePath(GameRulePathBox.Text); }
        catch (ArgumentException ex) { GameRuleStatusText.Text = ex.Message; }
    }

    private void DetectGameRulePath_Click(object sender, RoutedEventArgs e)
    {
        var path = DeltaProcessRules.FindTarget(DeltaForceLocator.FindRoots(), _gameRules);
        if (path.Length > 0) UseGameRulePath(path);
        else GameRuleStatusText.Text = "未找到三角洲主进程，请手动选择 " + DeltaProcessRules.ExecutableName + "。";
    }

    private void GameRuleCapabilityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameRuleValueCombo is null) return;
        LoadGameCapabilityRule();
    }

    private void UseGameRuleCpus_Click(object sender, RoutedEventArgs e)
    {
        GameRuleValueBox.Text = CpuSelectionPanel.ToRuleValue(_gameRuleProcessorToggles);
        if (GameRuleValueBox.Text.Length == 0) GameRuleNoteText.Text = "请至少选择一个逻辑 CPU。";
    }

    private void OpenFrameBackground_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateByTag("frame");

    private async void SaveGameRule_Click(object sender, RoutedEventArgs e)
    {
        if (_gameRuleSaveBusy) return;
        if (_gameRulePath.Length == 0) { GameRuleNoteText.Text = "请先确定三角洲主进程路径。"; return; }
        var index = GameRuleCapabilityCombo.SelectedIndex;
        var capability = ProcessRuleChoices.CapabilityAt(index);
        if (capability is null) { GameRuleNoteText.Text = "请选择能力。"; return; }
        var value = ProcessRuleChoices.UsesMask(index) ? GameRuleValueBox.Text.Trim()
            : (GameRuleValueCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        if (value.Length == 0) { GameRuleNoteText.Text = "请选择规则设置，CPU 规则需至少选择一个处理器。"; return; }

        var rule = new ProcmanRuleEntry
        {
            ExecutablePath = _gameRulePath,
            Capability = capability,
            Value = value,
            Enabled = GameRuleEnabledToggle.IsOn,
            AutoTakeover = GameRuleAutoTakeoverCheck.IsChecked == true,
        };
        _gameRuleSaveBusy = true;
        GameRuleEditor.IsEnabled = false;
        GameRuleNoteText.Text = "正在保存规则…";
        try
        {
            var result = await BackendProcmanService.Instance.InvokeAsync(BackendCommands.ProcmanRulesSave,
                new ProcmanRulesSaveArgs { Rule = rule });
            GameRuleNoteText.Text = result.Status == BackendResultStatuses.Ok
                ? "规则已保存" + (rule.Enabled ? "（帧格后台功能运行时应用）" : "（已停用）") + "。"
                : "规则保存失败：" + result.Message;
            await LoadGameRulesAsync();
        }
        catch (Exception ex)
        {
            GameRuleNoteText.Text = "规则保存未完成：" + ex.Message;
        }
        finally
        {
            _gameRuleSaveBusy = false;
            GameRuleEditor.IsEnabled = true;
        }
    }

    private async void GameRuleDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProcmanRuleEntry rule }) return;
        try
        {
            var result = await BackendProcmanService.Instance.InvokeAsync(BackendCommands.ProcmanRulesDelete,
                new ProcmanRulesDeleteArgs { ExecutablePath = rule.ExecutablePath, Capability = rule.Capability });
            GameRuleNoteText.Text = result.Status == BackendResultStatuses.Ok
                ? "规则已删除，其拥有的修改已还原。"
                : "删除失败：" + result.Message;
            await LoadGameRulesAsync();
        }
        catch (Exception ex)
        {
            GameRuleNoteText.Text = "删除未完成：" + ex.Message;
        }
    }
}
