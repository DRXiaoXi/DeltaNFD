using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class RuntimePage : Page
{
    public RuntimeViewModel ViewModel { get; } = RuntimeViewModel.Current;
    private bool _guardToggleHandling;
    private CancellationTokenSource? _repairCancellation;
    private async void ProcessLegacy_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.LegacyProcessingEnabled) return;
        ViewModel.RepairIsBusy = true;
        using var cancellation = new CancellationTokenSource(); _repairCancellation = cancellation;
        try
        {
            var report = await ViewModel.PreviewLegacyAsync();
            if (report.Issues.Count > 0 || report.Entries.Any(e => !e.CanProcess))
            { ViewModel.RepairStatusText = string.Join("\n", report.Issues.Concat(report.Entries.Select(e => e.Detail))); return; }
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "确认仅处理旧版包装残留？",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                    Text = string.Join("\n", report.Entries.Select(e => e.Name + " · " + e.Version))
                        + "\n\n已核验原 MSI 不再安装，当前 14.42 组件及核心 DLL 已确认。\n将备份后清理以上旧包装登记与对应 INF/PNF；不执行旧卸载命令，不卸载或重装当前 14.42，不清理共享依赖。\n状态变化或备份失败时停止，部分完成会保留证据供核对。" } },
                PrimaryButtonText = "确认处理残留", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary || cancellation.IsCancellationRequested) return;
            var result = await ViewModel.ProcessLegacyAsync(report.Fingerprint, cancellation.Token);
            ViewModel.RepairStatusText = result.Message;
            await new ContentDialog { XamlRoot = XamlRoot, Title = result.Success ? "旧包装处理完成" : "旧包装未处理完",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = result.Message } },
                CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync();
        }
        catch (Exception ex) { ViewModel.RepairStatusText = "旧包装处理失败：" + ex.Message; await ShowRuntimeExceptionAsync(ex); }
        finally { _repairCancellation = null; ViewModel.RepairIsBusy = false; await ViewModel.LoadGuardAsync(); }
    }
    private void CancelRepair_Click(object sender, RoutedEventArgs e)
    {
        _repairCancellation?.Cancel();
        ViewModel.RepairStatusText = "已请求停止后续步骤；不会强杀正在运行的安装器，请等待事务结束后核对。";
    }
    private async void Repair14_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.GuardCleanupEnabled) return;
        ViewModel.RepairIsBusy = true;
        using var cancellation = new CancellationTokenSource();
        _repairCancellation = cancellation;
        try
        {
            ViewModel.RepairStatusText = "正在核对 14.x 清单、安装原包和事务状态…";
            var preview = await ViewModel.PreviewRepairAsync();
            if (!preview.CanRepair) { ViewModel.RepairStatusText = preview.Blocker; return; }
            var list = string.Join("\n", preview.Scan.Products.Select(p => p.Name + " · " + p.Architecture + " · " + p.Version));
            var legacy = string.Join("\n", preview.Scan.Legacy.Entries.Select(p => p.Name + " · " + p.Version));
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "确认定向修复 C++ 14.x？",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                    Text = "将卸载以下已核验的 14.x MSI 组件：\n" + (list.Length == 0 ? "未检测到可卸载组件，只安装原包。" : list)
                        + "\n\n随后安装 2015–2022 x64/x86 14.42.34433。保留 2005–2013、SDK/Debug、ARM64 和 DirectX。"
                        + (legacy.Length > 0 ? "\n\n在 14.42 复检通过且旧 MSI 已移除后，备份并处理以下旧 INF 包装登记与 INF/PNF：\n" + legacy : "")
                        + (preview.Scan.Legacy.Issues.Count > 0 ? "\n旧包装待核对，无法自动清理：\n" + string.Join("\n", preview.Scan.Legacy.Issues) : "")
                        + "\n请先关闭游戏与其他应用及运行库防护。共享运行库降级可能影响需要较新版本的软件。\n失败、身份变化、需要重启或结果未确认时停止；不会忽略其他软件共享依赖或强杀安装器。" } },
                PrimaryButtonText = "确认修复", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary || cancellation.IsCancellationRequested)
            { ViewModel.RepairStatusText = "已取消，未执行卸载或安装。"; return; }
            var result = await ViewModel.RepairAsync(preview.Fingerprint, cancellation.Token);
            ViewModel.RepairStatusText = result.Message;
            await new ContentDialog { XamlRoot = XamlRoot, Title = result.Success ? "14.x 修复完成" : "14.x 修复未完成",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = result.Message, TextWrapping = TextWrapping.Wrap } },
                CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync();
        }
        catch (Exception ex) { ViewModel.RepairStatusText = "操作未完成：" + ex.Message; await ShowRuntimeExceptionAsync(ex); }
        finally { _repairCancellation = null; ViewModel.RepairIsBusy = false; await ViewModel.LoadGuardAsync(); }
    }
    public RuntimePage() { InitializeComponent(); _ = ViewModel.LoadGuardAsync(); }
    private async void RefreshGuard_Click(object sender, RoutedEventArgs e) => await ViewModel.LoadGuardAsync();
    private async void ReviewRuntimeOperation_Click(object sender, RoutedEventArgs e)
    {
        await BackendReviewFlow.RunAsync(XamlRoot, [DeltaNFD.Backends.BackendModules.Runtime]);
        await ViewModel.LoadGuardAsync();
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
                }.ShowWithBackendRecoveryAsync();
            }
        }
        catch (Exception ex)
        {
            DeltaNFD.Services.Log.Error("运行库防护界面切换失败", ex);
            await ViewModel.LoadGuardAsync();
            await ShowRuntimeExceptionAsync(ex);
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
            if (await confirm.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary) return;
            var result = await ViewModel.ForceCleanupGuardAsync();
            if (result is not null) await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = result.Success ? "清理完成" : "清理未完成", Content = new ScrollViewer
                {
                    MaxHeight = 420, Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = result.Message },
                },
                CloseButtonText = "知道了",
            }.ShowWithBackendRecoveryAsync();
        }
        catch (Exception ex)
        {
            DeltaNFD.Services.Log.Error("强制清理拦截界面失败", ex);
            await ViewModel.LoadGuardAsync();
            await ShowRuntimeExceptionAsync(ex);
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
            }.ShowWithBackendRecoveryAsync();
        }
        catch (Exception ex)
        {
            DeltaNFD.Services.Log.Error("防护范围切换失败", ex);
            await ViewModel.LoadGuardAsync();
            combo.SelectedIndex = ViewModel.GuardModeIndex;
            await ShowRuntimeExceptionAsync(ex);
        }
    }

    private async Task ShowRuntimeExceptionAsync(Exception exception)
    {
        try
        {
            await new ContentDialog { XamlRoot = XamlRoot, Title = "运行库操作未完成", Content = exception.Message,
                CloseButtonText = "知道了", DefaultButton = ContentDialogButton.Close }
                .ShowWithBackendRecoveryAsync();
        }
        catch (Exception dialogError) { DeltaNFD.Services.Log.Error("运行库错误及日志引导弹窗无法显示", dialogError); }
    }
}
