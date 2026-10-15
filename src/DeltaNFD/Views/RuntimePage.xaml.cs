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
            ViewModel.RepairStatusText = "正在核对 14.x 登记、安装原包和事务状态…";
            var preview = await ViewModel.PreviewRepairAsync();
            if (!preview.CanRepair) { ViewModel.RepairStatusText = preview.Blocker; return; }
            var list = string.Join("\n", preview.RemovalPlan.Select(p =>
                p.Name + " · " + p.Architecture + " · " + p.Version + "（" + (p.CanRemove ? p.Kind switch
                    { "msi" => "已核验 14.x MSI", "bundle" => "已核验包装", "inf" => "已核验旧 14.x INF", _ => "登记卸载命令" } : "保留不处理") + "）\n  " + p.Method));
            var skipped = string.Join("\n", preview.RemovalPlan.Where(p => !p.CanRemove).Select(p => p.Name + "：" + p.Detail));
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "确认全量重装 C++ 14.x？",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                    Text = "卸载前建议：先进入 Windows 安全模式，再打开工具执行，以减少后台程序占用。这不是强制要求，你也可以直接确认，在当前环境继续重装。\n"
                        + "如需进入安全模式：按住 Shift 点击 Windows 的“重启”，依次选择“疑难解答 → 高级选项 → 启动设置 → 重启”，再按 4。工具不会自动修改启动设置。\n"
                        + "安全模式下如果提示 Windows Installer 服务不可用，请返回正常模式重试。\n\n"
                        + "采用 Beta0.9 的命令执行方式，仅处理以下 x86/x64 C++ 14.x：MSI 按产品码卸载，EXE 和已核验旧 INF 按登记/固定命令执行。\n" + list
                        + "\n旧 INF 卸载节会实际调用其中的 MSI 卸载命令；已核对其目标属于 14.x，执行前会备份包装登记及 INF/PNF。"
                        + "\n\n随后安装 2015–2022 x64/x86 14.42.34433 原包并复检组件与 DLL；若 DLL 版本未对齐会自动用原包修复。"
                        + "\n卸载完成后、安装前会自动备份并清理旧版工具留下的 v14/2026 残留登记（避免其阻止基线安装），备份会保留。"
                        + (skipped.Length > 0 ? "\n\n以下登记重装阶段不卸载（由残留清理阶段评估）：\n" + skipped : "")
                        + "\n保留 2005–2013、SDK/Debug、ARM64 和 DirectX。"
                        + "\n请先关闭游戏与其他应用及运行库防护。共享运行库降级可能影响需要较新版本的软件。"
                        + "\n失败、身份变化、需要重启或结果未确认时停止；不会强杀安装器。" } },
                PrimaryButtonText = "确认全量重装", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary || cancellation.IsCancellationRequested)
            { ViewModel.RepairStatusText = "已取消，未执行卸载或安装。"; return; }
            var result = await ViewModel.RepairAsync(preview.Fingerprint, cancellation.Token);
            ViewModel.RepairStatusText = result.Message;
            await new ContentDialog { XamlRoot = XamlRoot, Title = result.Success ? "14.x 全量重装完成" : "14.x 全量重装未完成",
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
