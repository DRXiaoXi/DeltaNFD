using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class ProcessPage
{
    private bool _memoryPrelaunchBusy, _syncMemoryPrelaunch, _memoryPrelaunchShown;
    private TweakStatus? _memoryPrelaunchStatus;

    private async Task<TweakStatus> ReadMemoryPrelaunchAsync() =>
        (await ServiceLocator.SystemTweaks.GetStatusesAsync()).Single(s => s.Tweak == SystemTweak.Prefetch);

    private void DisplayMemoryPrelaunch(TweakStatus status)
    {
        _memoryPrelaunchStatus = status;
        // Keep the control available after a change so the user can restore the same backup.
        _memoryPrelaunchShown |= !status.IsOptimized;
        MemoryPrelaunchPanel.Visibility = _memoryPrelaunchShown ? Visibility.Visible : Visibility.Collapsed;
        _syncMemoryPrelaunch = true;
        try
        {
            MemoryPrelaunchToggle.OnContent = "已关闭";
            MemoryPrelaunchToggle.OffContent = "尚未全部关闭";
            MemoryPrelaunchToggle.IsOn = status.IsOptimized;
        }
        finally { _syncMemoryPrelaunch = false; }
        MemoryPrelaunchStatusText.Text = status.Detail;
    }

    private void MemoryPrelaunchUnknown(string message)
    {
        _memoryPrelaunchStatus = null;
        _memoryPrelaunchShown = true;
        MemoryPrelaunchPanel.Visibility = Visibility.Visible;
        MemoryPrelaunchToggle.OnContent = MemoryPrelaunchToggle.OffContent = "状态待确认";
        MemoryPrelaunchStatusText.Text = message + "\n请重新检测；未将未知状态视为已关闭。";
    }

    private async Task LoadMemoryPrelaunchAsync()
    {
        if (_memoryPrelaunchBusy || _detachedFromWindow) return;
        _memoryPrelaunchBusy = true;
        MemoryPrelaunchToggle.IsEnabled = MemoryPrelaunchRefreshButton.IsEnabled = false;
        MemoryPrelaunchStatusText.Text = "正在检测预启动…";
        try { DisplayMemoryPrelaunch(await ReadMemoryPrelaunchAsync()); }
        catch (Exception ex)
        {
            Log.Error("内存回收页预启动检测失败", ex);
            MemoryPrelaunchUnknown("预启动检测失败：" + ex.Message);
        }
        finally
        {
            _memoryPrelaunchBusy = false;
            MemoryPrelaunchRefreshButton.IsEnabled = true;
            MemoryPrelaunchToggle.IsEnabled = _memoryPrelaunchStatus is not null;
        }
    }

    private async void MemoryPrelaunchRefresh_Click(object sender, RoutedEventArgs e) => await LoadMemoryPrelaunchAsync();

    private async void MemoryPrelaunchToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncMemoryPrelaunch || _memoryPrelaunchBusy || _detachedFromWindow || _memoryPrelaunchStatus is null) return;
        var disable = MemoryPrelaunchToggle.IsOn;
        _memoryPrelaunchBusy = true;
        MemoryPrelaunchToggle.IsEnabled = MemoryPrelaunchRefreshButton.IsEnabled = false;
        MemoryPrelaunchStatusText.Text = disable ? "正在备份并关闭预启动…" : "正在按备份还原预启动…";
        try
        {
            var result = disable
                ? await ServiceLocator.SystemTweaks.DisableAsync(SystemTweak.Prefetch)
                : await ServiceLocator.SystemTweaks.RestoreAsync(SystemTweak.Prefetch);
            OptimizationScan.Invalidate();
            DisplayMemoryPrelaunch(await ReadMemoryPrelaunchAsync());
            MemoryPrelaunchStatusText.Text = result.Message + "\n" + _memoryPrelaunchStatus!.Detail
                + (result.RequiresReboot ? "\n部分配置需重启后生效。" : "");
            if (!result.Success && !_detachedFromWindow && XamlRoot is not null)
                await new ContentDialog { XamlRoot = XamlRoot, Title = "预启动设置未完成",
                    Content = MemoryPrelaunchStatusText.Text, CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync();
        }
        catch (Exception ex)
        {
            OptimizationScan.Invalidate();
            Log.Error("内存回收页预启动设置未完成", ex);
            MemoryPrelaunchUnknown("预启动设置未完成：" + ex.Message);
            if (!_detachedFromWindow && XamlRoot is not null)
                try { await new ContentDialog { XamlRoot = XamlRoot, Title = "预启动设置未完成",
                    Content = MemoryPrelaunchStatusText.Text, CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync(); }
                catch (Exception dialogError) { Log.Error("预启动设置错误提示未能显示", dialogError); }
        }
        finally
        {
            _memoryPrelaunchBusy = false;
            MemoryPrelaunchRefreshButton.IsEnabled = true;
            MemoryPrelaunchToggle.IsEnabled = _memoryPrelaunchStatus is not null;
        }
    }
}
