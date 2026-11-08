using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class GameModificationsPage : Page
{
    private readonly IGameRecordingService _recording = ServiceLocator.GameRecording;
    private bool _busy, _loaded, _fsoAvailable, _recordingAvailable, _suppressFso = true;

    public GameModificationsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { _loaded = true; await RefreshAsync(); };
        Unloaded += (_, _) => _loaded = false;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        RecordingItems.IsEnabled = !busy && _recordingAvailable;
        FsoDisableToggle.IsEnabled = !busy && _fsoAvailable;
    }

    private async Task ReadStatesAsync()
    {
        _fsoAvailable = false;
        _recordingAvailable = false;
        RecordingItems.ItemsSource = null;
        var fso = await Task.Run(() => GameCompatibilityService.ResolveGameExePaths()
            .Select(p => (Path: p, Disabled: GameCompatibilityService.IsFullscreenOptimizationsDisabled(p))).ToArray());
        _suppressFso = true;
        FsoDisableToggle.IsOn = fso.Length > 0 && fso.All(i => i.Disabled);
        _fsoAvailable = fso.Length > 0 && !ServiceLocator.GameTarget.IsCustom;
        _suppressFso = false;
        var count = fso.Count(i => i.Disabled);
        FsoStatusText.Text = fso.Length == 0 ? "未找到游戏主程序，请在设置中登记安装目录。"
            : $"{count}/{fso.Length} 个安装已禁用全屏优化。" + (count > 0 && count < fso.Length ? "开启可补全禁用；关闭可全部恢复。" : "")
                + "\n" + string.Join("\n", fso.Select(i => (i.Disabled ? "已禁用 · " : "未禁用 · ") + i.Path));
        var snapshot = await _recording.GetStatusAsync();
        RecordingItems.ItemsSource = snapshot.Items;
        _recordingAvailable = true;
        StatusBar.Title = "文件状态";
        StatusBar.Message = snapshot.Message;
        StatusBar.Severity = snapshot.Complete && !snapshot.Items.Any(i => i.NeedsReview) ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try { await ReadStatesAsync(); }
        catch (Exception ex) { StatusBar.Title = "读取未完成"; StatusBar.Message = ex.Message; StatusBar.Severity = InfoBarSeverity.Warning; }
        finally { SetBusy(false); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Settings_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(SettingsPage));
    private async void Replace_Click(object sender, RoutedEventArgs e) => await ChangeRecordingAsync(sender, false);
    private async void Restore_Click(object sender, RoutedEventArgs e) => await ChangeRecordingAsync(sender, true);

    private async void ReviewRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: GameRecordingItem item } || !item.NeedsReview) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "核对录制组件记录",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = item.FilePath + "\n\n" + item.Detail + "\n\n" + (item.CanArchiveRecord
                    ? "将再次核对当前文件与原件记录的内容哈希和长度，只把旧记录逐字归档到软件数据目录。当前游戏文件、属性和现存备份均不修改；归档后可以重新备份并替换，不会自动替换。"
                    : item.CanRestore ? "原件或备份可按原恢复记录处理。关闭此窗口后点击“恢复原文件”，完成后再重新替换。"
                    : "请完全退出游戏及录制组件，通过游戏启动器检查/修复游戏完整性，然后在本页刷新状态。当前文件缺失、为空、内容不符或仍有中断临时文件时，不会清除旧记录；请保留备份与记录继续核对。"),
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                PrimaryButtonText = item.CanArchiveRecord ? "核对并归档旧记录" : "",
                CloseButtonText = item.CanArchiveRecord ? "取消" : "知道了", DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary) return;
            OperationBar.Title = "正在核对旧记录";
            OperationBar.Message = "正在重新读取当前文件并核对原件内容，请稍候。";
            OperationBar.Severity = InfoBarSeverity.Informational;
            OperationBar.IsOpen = true;
            var result = await _recording.ChangeAsync(new() { InstallationRoot = item.InstallationRoot,
                ComponentId = item.ComponentId, ExpectedFingerprint = item.Fingerprint, ArchiveRecord = true });
            ShowResult(result);
            await ReadStatesAsync();
        }
        catch (Exception ex) { ShowResult(OperationResult.Fail("旧记录核对未完成，未修改游戏文件：" + ex.Message)); }
        finally { SetBusy(false); }
    }

    private async Task ChangeRecordingAsync(object sender, bool restore)
    {
        if (_busy || sender is not Button { DataContext: GameRecordingItem item } || (restore ? !item.CanRestore : !item.CanApply)) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = restore ? "恢复录制组件原文件？" : "替换录制组件为只读空文件？",
                Content = new TextBlock { Text = item.FilePath + "\n\n" + (restore
                    ? "将核对原件备份和当前文件身份，再恢复原字节及原属性。文件已被更新或替换时会停止，保留证据。"
                    : "会将原件备份到软件数据目录，再创建只读空文件，使此组件无法启动。对应录制/精彩时刻功能会受影响，帧率收益尚未实测。可在本页恢复；更新游戏前建议先恢复原件。"), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = restore ? "恢复原文件" : "备份并替换",
                CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary) return;
            var result = await _recording.ChangeAsync(new() { InstallationRoot = item.InstallationRoot, ComponentId = item.ComponentId,
                ExpectedFingerprint = item.Fingerprint, Restore = restore });
            ShowResult(result);
            await ReadStatesAsync();
        }
        catch (Exception ex) { ShowResult(OperationResult.Fail("未确认改动完成，请刷新或在设置中核对未确认操作：" + ex.Message)); }
        finally { SetBusy(false); }
    }

    private async void Fso_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressFso || _busy || !_loaded) return;
        var disable = FsoDisableToggle.IsOn;
        SetBusy(true);
        try
        {
            using var operation = ServiceLocator.GameTarget.BeginOperation();
            var result = await Task.Run(() =>
            {
                if (ServiceLocator.GameTarget.IsCustom) return OperationResult.Fail(GameTargetService.DeltaOnlyMessage);
                var paths = GameCompatibilityService.ResolveGameExePaths();
                if (paths.Count == 0) return OperationResult.Fail("未找到游戏主程序，请先登记安装目录。");
                var failures = new List<string>();
                foreach (var path in paths)
                {
                    if (PageBackendSessions.Manager.DeltaRunning != false || DeltaForceLocator.IsGameRunning())
                    { failures.Add("游戏运行或状态待确认，请退出游戏后重试。"); break; }
                    var item = disable ? GameCompatibilityService.DisableFullscreenOptimizations(path)
                        : GameCompatibilityService.RestoreFullscreenOptimizations(path);
                    if (!item.Success) failures.Add(path + "：" + item.Message);
                }
                return failures.Count == 0 ? OperationResult.Ok(disable ? "已为所有识别安装禁用全屏优化，下次启动游戏生效。" : "已恢复所有识别安装的全屏优化原设置。")
                    : OperationResult.Fail("部分操作未完成，请核对各安装状态。" + string.Join("；", failures));
            });
            ShowResult(result);
            await ReadStatesAsync();
        }
        catch (Exception ex) { ShowResult(OperationResult.Fail("全屏优化设置未完成：" + ex.Message)); }
        finally { SetBusy(false); }
    }

    private void ShowResult(OperationResult result)
    {
        OperationBar.Title = result.Success ? "操作完成" : "操作未完成";
        OperationBar.Message = result.Message;
        OperationBar.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        OperationBar.IsOpen = true;
        if (!_loaded) App.MainWindow?.ReportPageOperationResult("游戏改动", result.Message);
    }
}
