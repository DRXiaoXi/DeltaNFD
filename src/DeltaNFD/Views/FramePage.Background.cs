using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class FramePage
{
    private bool _frameBackgroundBusy, _syncFrameBackgroundChoices;

    private void SyncFrameBackgroundChoices(AppSettings settings)
    {
        _syncFrameBackgroundChoices = true;
        try
        {
            FrameAutomaticMemoryToggle.IsOn = settings.FrameAutomaticMemoryEnabled;
        }
        finally { _syncFrameBackgroundChoices = false; }
    }

    private async Task LoadFrameBackgroundAsync()
    {
        if (_frameBackgroundBusy) return;
        _frameBackgroundBusy = true;
        FrameBackgroundSettingsPanel.IsEnabled = false;
        try
        {
            var memory = BackendResults.RequireData<ProcmanBackgroundStatus>(await BackendProcmanService.Instance.InvokeAsync(BackendCommands.ProcmanBackgroundGet, new { }), "自动回收状态");
            var settings = AppSettingsStore.Read();
            SyncFrameBackgroundChoices(settings);
            AutomaticMemoryStatusText.Text = !settings.FrameAutomaticMemoryEnabled ? "未启用"
                : memory.AutomaticMemoryActive ? "自动内存回收：随一键帧格运行，按内存压力触发。" : FrameSrv.FrameModeEnabled ? "已选择，当前未确认运行。" : "已选择，等待开启一键帧格。";
            if (settings.FrameAutomaticMemoryEnabled && FrameSrv.FrameModeEnabled && memory.Note.Length > 0)
                AutomaticMemoryStatusText.Text += "\n" + memory.Note;
            if (settings.FrameAutomaticMemoryEnabled && memory.Feedback.Length > 0) AutomaticMemoryStatusText.Text += "\n" + memory.Feedback;
            FrameBackgroundNoteText.Text = FrameSrv.FrameModeEnabled && settings.FrameAutomaticMemoryEnabled && !memory.AutomaticMemoryActive
                ? "帧格已开启，但部分后台功能尚未确认运行，请查看帧格结果提示或刷新状态。"
                : "";
            FrameBackgroundNoteText.Visibility = FrameBackgroundNoteText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            FrameBackgroundNoteText.Text = "后台功能状态读取失败，请刷新后重试：" + ex.Message;
            FrameBackgroundNoteText.Visibility = Visibility.Visible;
        }
        finally { _frameBackgroundBusy = false; FrameBackgroundSettingsPanel.IsEnabled = true; }
    }

    private async void RefreshFrameBackground_Click(object sender, RoutedEventArgs e) => await LoadFrameBackgroundAsync();
    private void OpenProcessBackground_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateToProcessBackground();

    private async void FrameBackgroundChoice_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncFrameBackgroundChoices || sender is not ToggleSwitch toggle) return;
        if (!FrameSrv.FeaturesEditable)
        {
            SyncFrameBackgroundChoices(AppSettingsStore.Read());
            return;
        }
        var enabled = toggle.IsOn;
        try
        {
            AppSettingsStore.Update(s => s.FrameAutomaticMemoryEnabled = enabled);
            var saved = AppSettingsStore.Read();
            if (saved.FrameAutomaticMemoryEnabled != enabled)
                throw new IOException("功能选项没有成功保存，请重试。");
            await LoadFrameBackgroundAsync();
        }
        catch (Exception ex)
        {
            SyncFrameBackgroundChoices(AppSettingsStore.Read());
            FrameBackgroundNoteText.Text = "功能开关保存未完成：" + ex.Message;
            FrameBackgroundNoteText.Visibility = Visibility.Visible;
        }
    }
}
