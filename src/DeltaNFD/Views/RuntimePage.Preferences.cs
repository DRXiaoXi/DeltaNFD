using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class RuntimePage
{
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
        if (!ViewModel.CanUseBaselineActions) return;
        try
        {
            var selected = ViewModel.SelectedBaseline;
            var directory = "Vc14Baseline" + selected.Display();
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
