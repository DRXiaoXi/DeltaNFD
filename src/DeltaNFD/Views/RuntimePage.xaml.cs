using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class RuntimePage : Page
{
    public RuntimeViewModel ViewModel { get; } = new();

    public RuntimePage()
    {
        InitializeComponent();

        // 进页自动检测问题运行库 + 读取防护状态
        _ = ViewModel.LoadAsync();
        _ = ViewModel.LoadGuardAsync();
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private async void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "修复运行库？",
            Content = "修复分两步：\n"
                      + "① 卸载检测到的全部 v14 伪装运行库条目\n"
                      + "② 静默运行内置的 VC++ AIO 修复包（SHA256 校验），重装 2005–2022 全系列官方运行库\n"
                      + "· 约 3–5 分钟，期间请勿关闭电脑\n\n"
                      + "⚠ 重要提醒：修复会改变系统运行库环境——如果你近期有在用 C++ 写东西"
                      + "（开发环境依赖特定的运行库版本），请谨慎修复。\n\n"
                      + "注意：如果「防护模式」处于开启状态，修复包自身的安装器也会被拦截——"
                      + "请先关闭防护再修复，完成后重新开启。确定继续？",
            PrimaryButtonText = "修复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await ViewModel.RepairAsync();
        if (result is null)
        {
            return;
        }

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = result.Success ? "修复完成" : "修复失败",
            Content = result.Message,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();
    }

    private async void UninstallV14Button_Click(object sender, RoutedEventArgs e)
    {
        // 先枚举实际会卸载的条目，让用户看清清单再确认
        var list = await ViewModel.BuildV14ListTextAsync();
        if (list.Length == 0)
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "无需卸载",
                Content = "未检测到名字带「v14」的运行库条目，无需卸载。",
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            }.ShowAsync();
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "单独卸载 V14 运行库？",
            Content = "将只卸载以下名字带「v14」的条目（年份命名条目与 2005-2013 各分支一律不动）：\n"
                      + list + "\n\n"
                      + "· 逐条静默卸载，约 1 分钟\n"
                      + "· 卸载后依赖该运行库的游戏 / 软件将无法启动，直到重装运行库\n"
                      + "· Steam 启动游戏时可能自动重装（Steamworks 共享运行库机制）；游戏侧重装可用「运行库防护模式」拦截\n"
                      + "· 若「运行库防护模式」当前开启，请先关闭（否则卸载器会被拦截）\n\n"
                      + "确定继续？",
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close, // 高风险操作默认停在取消
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await ViewModel.UninstallV14Async();
        if (result is null)
        {
            return;
        }

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = result.Success ? "卸载完成" : "卸载失败",
            Content = result.Message,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();
    }

    private async void GuardToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.IsOn == ViewModel.GuardIsEnabled)
        {
            return;
        }

        var result = await ViewModel.SetGuardAsync(toggle.IsOn);
        // 后端可能拒绝写入，VM 的布尔值未变化时 x:Bind 不会主动拨回控件。
        toggle.IsOn = ViewModel.GuardIsEnabled;
        if (result is { Success: false })
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "操作失败",
                Content = result.Message,
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            }.ShowAsync();
        }
    }
}
