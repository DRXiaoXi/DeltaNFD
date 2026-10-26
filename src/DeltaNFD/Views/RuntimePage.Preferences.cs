using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class RuntimePage
{
    private async void RuntimeDetection_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.IsOn == ViewModel.RuntimeDetectionEnabled) return;
        if (!ViewModel.CanChangeRuntimeDetection) { toggle.IsOn = ViewModel.RuntimeDetectionEnabled; return; }
        try { await ViewModel.SetRuntimeDetectionEnabledAsync(toggle.IsOn); }
        catch (Exception ex)
        {
            toggle.IsOn = ViewModel.RuntimeDetectionEnabled;
            Log.Error("运行库检测设置未保存", ex);
            await new ContentDialog { XamlRoot = XamlRoot, Title = "检测设置未保存", Content = ex.Message,
                CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
        }
    }

    private async void OpenInstalledApps_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:appsfeatures")))
                throw new IOException("无法打开系统设置，请手动打开 Windows 设置 → 应用 → 已安装的应用。");
        }
        catch (Exception ex) { await ShowRuntimeExceptionAsync(ex); }
    }

    private async void OpenBaselineFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = ViewModel.SelectedBaseline;
            var directory = selected == Vc14Baseline.V144 ? "Vc14Baseline14.44" : "Vc14Baseline14.42";
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", directory);
            if (!File.Exists(Path.Combine(path, "vc_redist.x64.exe")) || !File.Exists(Path.Combine(path, "vc_redist.x86.exe")))
                throw new IOException("当前程序目录缺少完整的 " + selected.Display() + " 双架构原包，请使用完整安装包恢复文件后重试。");
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path);
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder))
                throw new IOException("无法打开原包文件夹：" + path);
        }
        catch (Exception ex) { await ShowRuntimeExceptionAsync(ex); }
    }
}
