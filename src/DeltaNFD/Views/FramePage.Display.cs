using DeltaNFD.Native;
using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class FramePage
{
    private bool _syncingResolutionOptions;
    private int _resolutionLoadVersion;

    private async Task LoadDisplayResolutionAsync()
    {
        UpdateResolutionControls();
        var version = ++_resolutionLoadVersion;
        try
        {
            var status = await FrameDisplayService.StatusAsync();
            var current = await Task.Run(() => new FrameDisplayMode().Read());
            if (!IsLoaded || version != _resolutionLoadVersion) return;
            CurrentResolutionText.Text = current is null
                ? "无法读取主显示器分辨率。"
                : $"主显示器当前分辨率为 {current.Width} × {current.Height}。";
            FrameResolutionStatus.Text = status.State != FrameDisplayState.Idle || !status.Success ? status.Message : "";
        }
        catch (Exception ex)
        {
            Log.Warn("无法读取帧格分辨率状态：" + ex.Message);
            if (IsLoaded && version == _resolutionLoadVersion)
                FrameResolutionStatus.Text = "暂时无法读取分辨率状态。";
        }
    }

    private void UpdateResolutionControls()
    {
        if (FrameSrv is not FrameService frame) return;
        var (enabled, width, height) = frame.DisplayResolutionOptions;
        _syncingResolutionOptions = true;
        try
        {
            ResolutionEnableToggle.IsOn = enabled;
            ResolutionWidthBox.Text = width;
            ResolutionHeightBox.Text = height;
            ResolutionEnableToggle.IsEnabled = frame.DisplayResolutionOptionsEditable;
            ResolutionWidthBox.IsEnabled = ResolutionHeightBox.IsEnabled = enabled && frame.DisplayResolutionOptionsEditable;
        }
        finally { _syncingResolutionOptions = false; }
    }

    private void ResolutionEnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (SaveResolutionOptions()) UpdateResolutionControls();
    }

    private void ResolutionSize_Changed(object sender, TextChangedEventArgs e) => SaveResolutionOptions();

    private bool SaveResolutionOptions()
    {
        if (!IsLoaded || _syncingResolutionOptions || FrameSrv is not FrameService frame) return false;
        frame.ConfigureDisplayResolution(ResolutionEnableToggle.IsOn, ResolutionWidthBox.Text, ResolutionHeightBox.Text);
        return true;
    }
}
