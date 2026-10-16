using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class ProcessPage
{
    private bool _workspaceReady;
    private object? _lastProcessTab;
    private bool IsProcessTab => ProcessTabs.SelectedItem is TabViewItem selected && selected == GeneralTab;
    private bool IsGameRulesTab => ProcessTabs.SelectedItem is TabViewItem selected && selected == GameTab;
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
        if (IsGameRulesTab)
        {
            ShowRulesWorkspace(game: true);
            _ = InitializeGameRulesAsync();
            return;
        }
        if (ProcessTabs.SelectedItem is TabViewItem rules && rules == RulesTab)
        {
            ShowRulesWorkspace(game: false);
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
        var fixedProtection = IsFixedBackgroundProtection(row.ExecutablePath);
        SelectedExclusionStatusText.Text = fixedProtection
            ? "此应用固定受保护，不参与后台抑制和内存回收。" + (excluded ? "另有自定义排除项，可移除；固定保护仍会保留。" : "")
            : excluded ? "已排除：此应用不参与后台抑制和内存回收，重启后保留。"
            : "未加入自定义排除名单。前台、系统及已有属性设置或规则的自动保护仍然生效。";
        ExcludeSelectedButton.Content = excluded ? "取消此应用的排除" : fixedProtection ? "此应用固定受保护" : "排除后台抑制与内存回收";
        ExcludeSelectedButton.IsEnabled = !_backgroundBusy && (excluded || !fixedProtection) && !string.IsNullOrWhiteSpace(row.ExecutablePath);
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
