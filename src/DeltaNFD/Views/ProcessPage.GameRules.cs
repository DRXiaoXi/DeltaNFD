using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeltaNFD.Views;

public sealed partial class ProcessPage
{
    private string _gameRulePath = "", _genericRulePath = "";
    private bool _rulesWorkspaceInGame, _gameRulesLoading, _ruleSaveBusy;
    private readonly List<ToggleButton> _ruleProcessorToggles = [];

    private void ShowRulesWorkspace(bool game)
    {
        if (_rulesWorkspaceInGame == game) return;
        if (game) _genericRulePath = _ruleTargetPath;
        GenericRulesWorkspaceHost.Content = null;
        GameRulesWorkspaceHost.Content = null;
        (game ? GameRulesWorkspaceHost : GenericRulesWorkspaceHost).Content = RulesWorkspace;
        _rulesWorkspaceInGame = game;
        GenericRuleTargetPanel.Visibility = game ? Visibility.Collapsed : Visibility.Visible;
        RulesWorkspaceTitle.Text = game ? "三角洲属性规则" : "总是规则";
        SetRuleTarget(game ? _gameRulePath : _genericRulePath);
        SavedRuleEditor.Visibility = _ruleTargetPath.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RuleNoteText.Text = "";
    }

    private async Task InitializeGameRulesAsync(bool detect = false)
    {
        if (_gameRulesLoading || _ruleSaveBusy) return;
        _gameRulesLoading = true;
        UseGameRulePathButton.IsEnabled = DetectGameRulePathButton.IsEnabled = GameRulePathBox.IsEnabled = false;
        SaveRuleButton.IsEnabled = RuleEditorHost.IsEnabled = false;
        try
        {
            await LoadRulesAsync();
            var path = _gameRulePath;
            if (detect || path.Length == 0)
            {
                GameRuleStatusText.Text = "正在识别游戏安装位置…";
                path = await Task.Run(() => DeltaProcessRules.FindTarget(DeltaForceLocator.FindRoots(), _savedRules));
            }
            if (_detachedFromWindow || !IsGameRulesTab) return;
            if (path.Length == 0)
            {
                GameRuleStatusText.Text = "未识别到主进程路径，请粘贴 " + DeltaProcessRules.ExecutableName + " 的完整路径后点击“使用此路径”。无需启动游戏。";
                return;
            }
            UseGameRulePath(path);
            await LoadRulesAsync();
        }
        catch (Exception ex) { GameRuleStatusText.Text = "主进程路径读取未完成：" + ex.Message; }
        finally
        {
            _gameRulesLoading = false;
            UseGameRulePathButton.IsEnabled = DetectGameRulePathButton.IsEnabled = GameRulePathBox.IsEnabled = true;
            SaveRuleButton.IsEnabled = RuleEditorHost.IsEnabled = true;
        }
    }

    private void UseGameRulePath(string path)
    {
        _gameRulePath = DeltaProcessRules.NormalizeTarget(path);
        GameRulePathBox.Text = _gameRulePath;
        SetRuleTarget(_gameRulePath);
        SavedRuleEditor.Visibility = Visibility.Visible;
        LoadGameCapabilityRule();
        GameRuleStatusText.Text = "规则只针对上面的一个主进程路径。可在游戏未运行时保存，当前与后续实例会按规则尝试应用。";
    }

    private void UseGameRulePath_Click(object sender, RoutedEventArgs e)
    {
        if (_gameRulesLoading || _ruleSaveBusy) return;
        try { UseGameRulePath(GameRulePathBox.Text); _ = LoadRulesAsync(); }
        catch (Exception ex) { GameRuleStatusText.Text = ex.Message; }
    }

    private async void DetectGameRulePath_Click(object sender, RoutedEventArgs e) => await InitializeGameRulesAsync(detect: true);

    private void LoadGameCapabilityRule()
    {
        var capability = RuleCapabilityCombo.SelectedIndex switch
        { 0 => "priority", 1 => "ecoQos", 2 => "priorityBoost", 3 => "affinity", 4 => "cpuSet", 5 => "ioPriority", 6 => "memoryPriority", 7 => "gpuPriority", _ => "" };
        var rule = _savedRules.FirstOrDefault(r => r.ExecutablePath.Equals(_ruleTargetPath, StringComparison.OrdinalIgnoreCase) && r.Capability == capability);
        if (RuleCapabilityCombo.SelectedIndex is 3 or 4 && rule is null) RuleValueBox.Text = "";
        RebuildRuleValues(rule?.Value);
        RuleEnabledToggle.IsOn = rule?.Enabled ?? true;
        RuleAutoTakeoverCheck.IsChecked = rule?.AutoTakeover ?? false;
    }

    private void UseRuleCpus_Click(object sender, RoutedEventArgs e)
    {
        var selected = _ruleProcessorToggles.Where(t => t.IsChecked == true).Select(t => (ProcmanGroupMask)t.Tag!);
        var masks = selected.GroupBy(m => m.Group).Select(g => new ProcmanGroupMask(g.Key, g.Aggregate(0UL, (value, cpu) => value | cpu.Mask)));
        RuleValueBox.Text = string.Join(";", masks.OrderBy(m => m.Group).Select(m => $"g{m.Group}:0x{m.Mask:X}"));
        if (RuleValueBox.Text.Length == 0) RuleNoteText.Text = "请至少选择一个逻辑 CPU。";
    }

    private void SetRuleCpuSelection(string value)
    {
        foreach (var toggle in _ruleProcessorToggles) toggle.IsChecked = false;
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Trim().Split(':', 2);
            if (pieces.Length != 2 || !pieces[0].StartsWith('g') || !ushort.TryParse(pieces[0][1..], out var group)) continue;
            if (!ulong.TryParse(pieces[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out var mask)) continue;
            foreach (var toggle in _ruleProcessorToggles.Where(t => t.Tag is ProcmanGroupMask cpu && cpu.Group == group))
                toggle.IsChecked = (mask & ((ProcmanGroupMask)toggle.Tag!).Mask) != 0;
        }
    }
}
