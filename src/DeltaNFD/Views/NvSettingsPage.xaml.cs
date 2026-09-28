using System.ComponentModel;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class NvSettingsPage : Page, INotifyPropertyChanged
{
    public NvSettingsViewModel ViewModel { get; } = new();

    /// <summary>非 N 卡（或 NVAPI 不可用）且检测已完成时显示提示（加载中不闪红）。</summary>
    public bool ShowNoNvidia => !ViewModel.IsLoading && !ViewModel.EnvironmentReady;

    /// <summary>锁定按钮可用：环境就绪、未锁定、不忙。</summary>
    public bool CanLock => ViewModel.EnvironmentReady && !ViewModel.DrsFilesLocked && !ViewModel.LockBusy;

    /// <summary>解锁按钮可用：已锁定、不忙。</summary>
    public bool CanUnlock => ViewModel.DrsFilesLocked && !ViewModel.LockBusy;

    public bool CanRefresh => !ViewModel.IsLoading && !ViewModel.IsApplying;

    public bool CanReset => !ViewModel.IsApplying && !ViewModel.IsLoading && ViewModel.EnvironmentReady && !ViewModel.DrsFilesLocked;

    public event PropertyChangedEventHandler? PropertyChanged;

    public NvSettingsPage()
    {
        InitializeComponent();

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(NvSettingsViewModel.EnvironmentReady)
                or nameof(NvSettingsViewModel.DrsFilesLocked)
                or nameof(NvSettingsViewModel.LockBusy)
                or nameof(NvSettingsViewModel.IsLoading)
                or nameof(NvSettingsViewModel.IsApplying))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowNoNvidia)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanLock)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUnlock)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRefresh)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanReset)));
            }
        };
    }

    private void RowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 任意行选项变化 → 刷新待应用计数
        ViewModel.OnRowSelectionChanged();
    }

    private void ScaleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 缩放行覆盖开关变化 → 刷新待应用计数
        ViewModel.OnRowSelectionChanged();
    }

    private void ScaleSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // 滑条拖动（含程序化回填触发的初值）→ 刷新待应用计数；TwoWay 绑定先更新 SelectedScale
        ViewModel.OnRowSelectionChanged();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var result = await ViewModel.ApplyChangesAsync();
        if (result is { Success: false })
        {
            await ShowErrorAsync(result.Message);
        }
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "全部恢复默认？",
            Content = "将删除三角洲驱动配置中本页管理的全部覆盖项（含 DLSS 覆盖总开关），交还给游戏内选项与驱动默认值。确定继续？",
            PrimaryButtonText = "恢复默认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await ViewModel.ResetAllAsync();
        if (result is { Success: false })
        {
            await ShowErrorAsync(result.Message);
        }
    }

    private async void LockButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "锁定驱动配置数据库为只读？",
            Content = "将把 nvdrsdb0.bin / nvdrsdb1.bin 设为只读：\n\n" +
                      "· 驱动更新、NVIDIA App、其他工具都无法再改写驱动配置（你的优化不会被重置）\n" +
                      "· 本工具之后也无法写入设置——需要修改时先回来解锁\n" +
                      "· 个别驱动在长期无法写库时可能重建数据库，解锁后重新应用即可\n\n确定锁定？",
            PrimaryButtonText = "锁定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await ViewModel.SetLockAsync(true);
        if (result is { Success: false })
        {
            await ShowErrorAsync(result.Message);
        }
    }

    private async void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        var result = await ViewModel.SetLockAsync(false);
        if (result is { Success: false })
        {
            await ShowErrorAsync(result.Message);
        }
    }

    private async Task ShowErrorAsync(string message) => await new ContentDialog
    {
        XamlRoot = XamlRoot,
        Title = "操作失败",
        Content = message,
        CloseButtonText = "知道了",
        DefaultButton = ContentDialogButton.Close,
    }.ShowAsync();
}
