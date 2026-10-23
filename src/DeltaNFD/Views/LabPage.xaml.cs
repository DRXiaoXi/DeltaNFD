using DeltaNFD.Services;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeltaNFD.Views;

public sealed partial class LabPage : Page
{
    public string TargetLabel => "当前主进程：" + DeltaNFD.Services.GameTargetService.Default.Current.DisplayName;
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

    private async void PreferIntelPcores_Click(object sender, RoutedEventArgs e) => await ViewModel.SetIntelPcorePreferenceAsync();
    private async void RestoreIntelPcores_Click(object sender, RoutedEventArgs e) => await ViewModel.SetIntelPcorePreferenceAsync(restore: true);

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

        if (await confirm.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary)
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

}
