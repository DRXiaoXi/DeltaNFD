using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class RuntimePage : Page
{
    public RuntimeViewModel ViewModel { get; } = new();
    private bool _guardToggleHandling;

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
                      + "① 卸载检测到的全部 Visual C++ 运行库条目\n"
                      + "② 静默运行内置的 VC++ AIO 安装器（SHA256 校验），重装 2005–2026 全系列运行库并复检\n"
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
            Title = result.Success ? (result.RequiresReboot ? "修复通过，需重启" : "修复完成") : "修复未完成",
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock { Text = result.Message, TextWrapping = TextWrapping.Wrap },
            },
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
        if (_guardToggleHandling || sender is not ToggleSwitch toggle || toggle.IsOn == ViewModel.GuardIsEnabled)
        {
            return;
        }

        _guardToggleHandling = true;
        try
        {
            var requested = toggle.IsOn;
            var result = await ViewModel.SetGuardAsync(requested);
            if (result is { Success: false } && ViewModel.CanRepairLegacyUe4Acl)
            {
                toggle.IsOn = ViewModel.GuardIsEnabled;
                var confirm = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "修复旧 UE4 权限残留？",
                    Content = ViewModel.GuardUe4Path + "\n\n缺少旧版原始权限记录，无法证明规则来源。是否授权仅移除该文件一条显式 Everyone 拒绝执行规则？\n会先保存当前权限快照，保留其余权限及继承状态，不重置整个目录。取消则不修改。",
                    PrimaryButtonText = "备份并修复", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                result = await ViewModel.RepairLegacyUe4AclAsync();
                if (result is { Success: true }) result = await ViewModel.SetGuardAsync(requested);
            }
            toggle.IsOn = ViewModel.GuardIsEnabled;
            if (result is { Success: false })
            {
                await new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "操作失败", Content = result.Message,
                    CloseButtonText = "知道了", DefaultButton = ContentDialogButton.Close,
                }.ShowAsync();
            }
        }
        catch (Exception ex)
        {
            DeltaNFD.Services.Log.Error("运行库防护界面切换失败", ex);
            await ViewModel.LoadGuardAsync();
        }
        finally { toggle.IsOn = ViewModel.GuardIsEnabled; _guardToggleHandling = false; }
    }
}
