using DeltaNFD.Services;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class ShaderPage : Page
{
    public ShaderViewModel ViewModel { get; } = new();

    public ShaderPage()
    {
        InitializeComponent();

        // 首次进入页面即自动体检（驱动 + 着色器缓存），无需手动点「重新检测」
        _ = ViewModel.LoadAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    /// <summary>运行库非最适版本提醒「去处理」：跳转到运行库页。</summary>
    private void JumpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            App.MainWindow?.NavigateByTag(tag);
        }
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清除旧着色器文件？",
            Content = "将删除三角洲的 PSOCache 着色器缓存。\n\n"
                      + "下次进入游戏会重新编译着色器，首局可能出现卡顿 / 掉帧，属正常现象；"
                      + "如因驱动问题导致着色器缺失 / 异常，建议先更换驱动再清除。",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await ViewModel.ClearAsync();
        if (result is { Success: false })
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "清理失败",
                Content = result.Message,
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            }.ShowAsync();
        }
        else if (result is { Success: true })
        {
            // 成功提醒：清除后必须重新预热着色器
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "清理完成",
                Content = result.Message + "\n\n"
                          + "⚠ 请记得：进入游戏后重新预热着色器——\n"
                          + "首次进图会重新编译着色器，出现卡顿 / 掉帧属正常现象，多跑两图即恢复。",
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Primary,
            }.ShowAsync();
        }
    }

    private async void DiagnoseButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DiagnoseAsync();
    }

    private async void CleanSystemButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清理系统级着色器缓存？",
            Content = "将清空以下目录的内容（保留目录本身）：\n"
                      + "· %LocalAppData%\\D3DSCache\n"
                      + "· %LocalAppData%\\NVIDIA\\DXCache\n"
                      + "· %LocalAppData%\\AMD\\DxCache\n\n"
                      + "清理后所有游戏首次启动都会重新编译着色器，首局可能出现卡顿，属正常现象。"
                      + "被占用的文件会自动跳过。确定继续？",
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await ViewModel.CleanSystemShadersAsync();
        if (result is null)
        {
            return;
        }

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = result.Success ? "清理完成" : "清理失败",
            Content = result.Message,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();
    }
}
