using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class ProcessPage
{
    private bool _workspaceReady, _gameScope;
    private bool IsProcessTab => ProcessTabs.SelectedItem is TabViewItem selected && (selected == GeneralTab || selected == GameTab);

    private void ProcessTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_workspaceReady || !IsProcessTab) return;
        var game = ProcessTabs.SelectedItem is TabViewItem selected && selected == GameTab;
        if (game != _gameScope)
        {
            GeneralWorkspaceHost.Content = null;
            GameWorkspaceHost.Content = null;
            (game ? GameWorkspaceHost : GeneralWorkspaceHost).Content = ProcessWorkspace;
            _gameScope = game;
            _listGeneration++;
            _selected = null; ShowEmptyDetail();
            _suppressSelection = true;
            try { _rows.Clear(); ProcessList.SelectedItem = null; }
            finally { _suppressSelection = false; }
            ListNoteText.Text = "正在读取进程…";
            ListSummaryText.Text = "";
            ApplyScopePicker.SelectedIndex = 0;
            _offset = 0; _search = "";
            SearchBox.Text = "";
            SearchBox.IsEnabled = !game;
            ProcessListTitle.Text = game ? "三角洲主进程" : "其他运行中的进程";
            ScopeNoteText.Text = game ? "此页只管理三角洲主进程。后台抑制和内存回收始终跳过它；属性与帧格控制冲突时需确认接管。"
                : "按名称稳定排列，只更新变化的数值。三角洲主进程在独立页签管理。";
        }
        _ = RefreshListAsync(preserveSelection: true);
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
}
