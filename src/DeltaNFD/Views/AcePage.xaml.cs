using DeltaNFD.Services;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class AcePage : Page
{
    public AceViewModel ViewModel { get; } = new();

    public AcePage()
    {
        InitializeComponent();

        _ = ViewModel.LoadAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private async void CleanButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "一键清除 ACE？",
            Content = "将结束 ACE 进程、接管并删除全部 ACE 服务（含自保护驱动）、清理注册表残留、"
                      + "删除安装目录与游戏目录内的 AntiCheatExpert 文件夹。\n\n"
                      + "· 请确认已完全退出游戏（含 sguard64 后台进程）\n"
                      + "· 被占用的文件会在重启电脑后自动删除\n"
                      + "· 下次启动游戏时 ACE 会自动重新安装",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await ViewModel.CleanAsync();
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
            // 清除成功：引导用启动器/WeGame 修复游戏（不要深度修复）
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "ACE 清理完成",
                Content = result.Message + "\n\n"
                          + "下一步（重要）：\n"
                          + "① 打开 WeGame / 游戏启动器\n"
                          + "② 对三角洲行动执行「修复」——普通修复即可，不要使用深度修复\n"
                          + "③ 修复会重新安装干净的 ACE 组件，之后正常启动游戏即可。",
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Primary,
            }.ShowAsync();
        }
    }

    private async void CheckCoreButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CheckCoreFilesAsync();
    }
}
