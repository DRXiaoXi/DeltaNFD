using DeltaNFD.Services;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeltaNFD.Views;

public sealed partial class LabPage : Page
{
    public LabViewModel ViewModel { get; } = new();

    public LabPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ViewModel.AttachGameState();
        Unloaded += (_, _) => ViewModel.DetachGameState();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private void ScenarioChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle && toggle.DataContext is LabScenarioVm chip)
        {
            ViewModel.SelectedScenarioIndex = chip.Index;
        }
    }

    private async void ImportSchemeButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ImportSchemeAsync();
    }

    private async void SwitchSchemeButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SwitchToSchemeAsync();
    }

    private async void HeteroPolicyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.DataContext is not HeteroPolicyVm item || !item.Ready)
        {
            return;
        }

        // TwoWay 绑定先更新 SelectedIndex，此处直接应用所选值
        await ViewModel.ApplyHeteroPolicyAsync(item);
    }

    private async void ApplyDualCcdButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "应用双CCD专属调度？",
            Content = ViewModel.DualCcdApplyModeIndex == 1
                ? "将登记到帧格模式：\n"
                  + $"· 开启帧格时自动把游戏独占到 {(ViewModel.DualCcdGameCcdIndex == 0 ? "CCD0" : "CCD1")}\n"
                  + $"· 其他进程推到 {(ViewModel.DualCcdGameCcdIndex == 0 ? "CCD1" : "CCD0")}\n"
                  + "· 关闭帧格时自动撤销\n\n"
                  + "确定继续？"
                : "将立即调整所有进程的 CPU 亲和性：\n"
                  + $"· 游戏独占 {(ViewModel.DualCcdGameCcdIndex == 0 ? "CCD0" : "CCD1")}\n"
                  + $"· 其他进程推到 {(ViewModel.DualCcdGameCcdIndex == 0 ? "CCD1" : "CCD0")}\n"
                  + "· CPU0 留给系统中断\n\n"
                  + "正在运行的应用可能会短暂卡顿。确定继续？",
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await ViewModel.ApplyDualCcdAsync();
        if (result is not null)
        {
            ViewModel.DualCcdStatusText = result.Message;
        }
    }

    private async void RevertDualCcdButton_Click(object sender, RoutedEventArgs e)
    {
        var result = await ViewModel.RevertDualCcdAsync();
        if (result is not null)
        {
            ViewModel.DualCcdStatusText = result.Message;
        }
    }

    private async void RemoveMemoryLimitButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "移除 12GB 内存限制？",
            Content = "将执行 bcdedit /deletevalue removememory 并移除内存限制。\n"
                      + "重启电脑后系统将恢复全部可用内存。\n\n"
                      + "建议使用「显卡伪装」来影响游戏行为——不会损失任何内存。",
            PrimaryButtonText = "移除限制",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await ViewModel.RemoveMemoryLimitAsync();
        if (result is { Success: false })
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "移除失败",
                Content = result.Message,
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            }.ShowAsync();
        }
        else if (result is { Success: true, RequiresReboot: true })
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "内存限制已移除",
                Content = result.Message + "\n\n是否立即重启电脑？",
                PrimaryButtonText = "立即重启",
                CloseButtonText = "稍后自己重启",
                DefaultButton = ContentDialogButton.Primary,
            }.ShowAsync();
        }
    }

    private async void ExcludeCpu0Button_Click(object sender, RoutedEventArgs e)
    {
        var result = await ViewModel.ExcludeCpu0Async();
        if (result is not null)
        {
            ViewModel.SingleCcdStatusText = result.Message;
        }
    }

    private async void RestoreCpu0Button_Click(object sender, RoutedEventArgs e)
    {
        var result = await ViewModel.RestoreFullCoresAsync();
        if (result is not null)
        {
            ViewModel.SingleCcdStatusText = result.Message;
        }
    }

    // ---------------- CPU 亲和性（Process Lasso 风格快捷预设） ----------------

    private void AffinityAllButton_Click(object sender, RoutedEventArgs e)
        => ViewModel.AffinitySetAllCores();

    private void AffinityExcludeCpu0Button_Click(object sender, RoutedEventArgs e)
        => ViewModel.AffinityExcludeCpu0();

    private void AffinityCcd0Button_Click(object sender, RoutedEventArgs e)
        => ViewModel.AffinityOnlyCcd(0);

    private void AffinityCcd1Button_Click(object sender, RoutedEventArgs e)
        => ViewModel.AffinityOnlyCcd(1);
}
