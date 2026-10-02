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

    // 大小核机型（Intel 12 代+）：P 核 / E 核预设
    private void AffinityPCoreButton_Click(object sender, RoutedEventArgs e)
        => ViewModel.AffinityOnlyPCores();

    private void AffinityECoreButton_Click(object sender, RoutedEventArgs e)
        => ViewModel.AffinityOnlyECores();

    // ---------------- 优化方案：保存 / 导入 ----------------

    /// <summary>导出当前方案为 txt（默认文件名带 CPU 型号与核心数，便于分享时辨认）。</summary>
    private async void SavePlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null)
        {
            return;
        }

        var plan = await ViewModel.CapturePlanAsync();
        if (plan is null)
        {
            return;
        }

        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
            SuggestedFileName = BuildSuggestedFileName(plan),
        };
        picker.FileTypeChoices.Add("CPU 优化方案", new List<string> { CpuPlanService.FileExtension });
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        ViewModel.SavePlanToFile(plan, file.Path, out _);
    }

    /// <summary>导入方案：先校验（CPU/核心/线程完全一致），通过后再询问是否应用。</summary>
    private async void ImportPlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null)
        {
            return;
        }

        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
        };
        picker.FileTypeFilter.Add(CpuPlanService.FileExtension);
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        // 校验不通过时 ViewModel 已把具体差异写进 PlanStatusText，这里再弹一次让用户看见
        var plan = ViewModel.LoadAndValidatePlanFile(file.Path, out var error);
        if (plan is null)
        {
            await ShowDialogAsync("无法导入此方案", error);
            return;
        }

        var summary = $"CPU：{plan.CpuName}\n"
            + $"核心 / 线程：{plan.PhysicalCores} / {plan.LogicalProcessors}\n"
            + $"亲和性：勾选 {plan.AffinityCores.Count} 核（{plan.AffinityMask}）· 规则{(plan.AffinityRuleEnabled ? "开启" : "关闭")}\n"
            + $"异类调度策略：{plan.Hetero.Count} 项"
            + (string.IsNullOrEmpty(plan.SavedAt) ? "" : $"\n\n方案保存于 {plan.SavedAt}");

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "应用此方案？",
            Content = summary + "\n\n将覆盖本机当前的 CPU 亲和性与异类调度策略。",
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.ImportPlanAsync(plan);
        await ShowDialogAsync("导入完成", ViewModel.PlanStatusText);
    }

    private async Task ShowDialogAsync(string title, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();
    }

    /// <summary>建议文件名：DeltaNFD_CPU方案_&lt;型号&gt;_&lt;核&gt;C&lt;线程&gt;T。</summary>
    private static string BuildSuggestedFileName(CpuPlanService.CpuPlan plan)
    {
        var safeName = new string(plan.CpuName
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
            .ToArray()).Trim();
        if (safeName.Length > 60)
        {
            safeName = safeName[..60].Trim();
        }

        return $"DeltaNFD_CPU方案_{safeName}_{plan.PhysicalCores}C{plan.LogicalProcessors}T";
    }
}
