using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class RuntimePage : Page
{
    public RuntimeViewModel ViewModel { get; } = new();
    private bool _guardToggleHandling;
    private bool _repairDialogHandling;

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
        if (_repairDialogHandling || !ViewModel.RepairEnabled) return;
        _repairDialogHandling = true;
        try
        {
            var mode = ViewModel.RepairMode;
            var strongMode = ViewModel.StrongRepairMode;
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "高帧性修复（原 C++ AIO 流程）？",
                Content = "将卸载检测到的全部 Visual C++ 运行库条目，再静默运行完整 AIO 安装器，安装官方 x86/x64 基线并复检。\n\n"
                    + "这会改变系统运行库环境；开发环境依赖特定版本时请谨慎。请先关闭运行库防护。\n\n"
                    + (strongMode ? "已开启强力修复模式：\n" + RuntimeViewModel.StrongRepairConfirmation + "\n\n" : "")
                    + "停止后续步骤不等于撤销当前安装事务。确定继续？",
                PrimaryButtonText = "修复",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            var result = await ViewModel.RepairAsync(mode, strongMode);
            if (result is null) return;

            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = result.Success ? (result.RequiresReboot ? "修复通过，需重启" : "修复完成")
                    : result.OutcomeUnknown ? "安装事务未确认结束" : "修复未完成",
                Content = new ScrollViewer
                {
                    MaxHeight = 420,
                    Content = new TextBlock { Text = result.Message, TextWrapping = TextWrapping.Wrap },
                },
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            }.ShowAsync();
        }
        catch (Exception ex) { DeltaNFD.Services.Log.Error("运行库修复对话框失败", ex); }
        finally { _repairDialogHandling = false; }
    }

    private async void CompatibilityRepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (_repairDialogHandling || !ViewModel.RepairEnabled) return;
        _repairDialogHandling = true;
        try
        {
            var strongMode = ViewModel.StrongRepairMode;
            var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = "兼容性修复？",
                Content = "将检测并安装／修复微软 DirectX 旧版游戏组件和 C++ 运行库，不预先卸载已有 C++ 运行库。\n\n"
                    + (strongMode ? "已开启强力修复模式：\n" + RuntimeViewModel.StrongRepairConfirmation + "\n\n" : "")
                    + "请先关闭游戏和运行库防护。停止后续步骤不会强杀正在执行的安装事务。",
                PrimaryButtonText = "修复", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await ViewModel.RepairCompatibilityAsync(strongMode);
            if (result is null) return;
            await new ContentDialog { XamlRoot = XamlRoot,
                Title = result.OutcomeUnknown ? "修复事务尚未确认结束" : result.RequiresReboot ? "修复需要重启后继续"
                    : result.Success ? "兼容性修复完成" : "兼容性修复未全部完成",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = result.Message, TextWrapping = TextWrapping.Wrap } },
                CloseButtonText = "知道了" }.ShowAsync();
        }
        catch (Exception ex) { DeltaNFD.Services.Log.Error("兼容性修复对话框失败", ex); }
        finally { _repairDialogHandling = false; }
    }

    private void CancelRepairButton_Click(object sender, RoutedEventArgs e) => ViewModel.CancelRepair();

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

    private async void CleanupGuard_Click(object sender, RoutedEventArgs e)
    {
        if (_guardToggleHandling || !ViewModel.GuardCleanupEnabled) return;
        _guardToggleHandling = true;
        try
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "强制清理运行库安装器拦截？",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "将移除 VC++ / DirectX / UE4 前置包共 11 个安装器名上的全部 IFEO 拦截，包括：\n" +
                           "· 本工具任何版本（含上一版本）写入的拦截及其归属标记\n" +
                           "· 独立运行库防护工具写入的拦截\n" +
                           "· 其他程序在这些安装器名上设置的 Debugger\n" +
                           "同时移除旧版本在 UE4 前置包上写入的拒绝执行权限，并重置防护记录。\n\n" +
                           "清理前的原值会写入日志留档。若独立防护工具仍在运行，请先退出它，否则拦截会被重新写入。",
                } },
                PrimaryButtonText = "强制清理", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await ViewModel.ForceCleanupGuardAsync();
            if (result is not null) await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = result.Success ? "清理完成" : "清理未完成", Content = new ScrollViewer
                {
                    MaxHeight = 420, Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = result.Message },
                },
                CloseButtonText = "知道了",
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            DeltaNFD.Services.Log.Error("强制清理拦截界面失败", ex);
            await ViewModel.LoadGuardAsync();
        }
        finally { _guardToggleHandling = false; }
    }

    private async void GuardMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || !ViewModel.GuardModeEnabled || combo.SelectedIndex == ViewModel.GuardModeIndex) return;
        try
        {
            var result = await ViewModel.ChangeGuardModeAsync(combo.SelectedIndex);
            combo.SelectedIndex = ViewModel.GuardModeIndex;
            if (result is { Success: false }) await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "范围切换失败", Content = result.Message, CloseButtonText = "知道了",
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            DeltaNFD.Services.Log.Error("防护范围切换失败", ex);
            await ViewModel.LoadGuardAsync();
            combo.SelectedIndex = ViewModel.GuardModeIndex;
        }
    }
}
