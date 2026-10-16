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
        var mode = ((sender as Button)?.Tag as string) switch
        {
            "uninstall" or "full-uninstall" => Vc14RepairMode.UninstallOnly,
            "install" => Vc14RepairMode.InstallOnly,
            _ => Vc14RepairMode.Full,
        };
        if (AdvancedStartupRestart.IsSafeMode && mode == Vc14RepairMode.Full) mode = Vc14RepairMode.UninstallOnly;
        var fullUninstallStage = (sender as Button)?.Tag as string == "full-uninstall";
        var allowTemporary = AdvancedStartupRestart.IsSafeMode && mode == Vc14RepairMode.UninstallOnly;
        ViewModel.RepairIsBusy = true;
        using var cancellation = new CancellationTokenSource();
        _repairCancellation = cancellation;
        try
        {
            ViewModel.RepairStatusText = "正在核对 14.x 登记、安装原包和事务状态…";
            var preview = await ViewModel.PreviewRepairAsync(mode, allowTemporary);
            if (!preview.CanRepair) { ViewModel.RepairStatusText = preview.Blocker; return; }
            if (mode != Vc14RepairMode.InstallOnly && !await ChooseRuntimeEnvironmentAsync(cancellation.Token, async () =>
            {
                var current = await ViewModel.PreviewRepairAsync(mode, allowTemporary);
                if (!current.CanRepair) ViewModel.RepairStatusText = current.Blocker;
                return current.CanRepair;
            })) return;
            if (!await HandleRuntimeFileUseAsync(cancellation.Token)) return;
            preview = await ViewModel.PreviewRepairAsync(mode, allowTemporary);
            if (!preview.CanRepair) { ViewModel.RepairStatusText = preview.Blocker; return; }
            var list = string.Join("\n", preview.RemovalPlan.Select(p =>
                p.Name + " · " + p.Architecture + " · " + p.Version + "（" + (p.CanRemove ? p.Kind switch
                    { "msi" => "已核验 14.x MSI", "bundle" => "已核验包装", "inf" => "已核验旧 14.x INF", _ => "登记卸载命令" } : "保留不处理") + "）\n  " + p.Method));
            var skipped = string.Join("\n", preview.RemovalPlan.Where(p => !p.CanRemove).Select(p => p.Name + "：" + p.Detail));
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = mode == Vc14RepairMode.UninstallOnly ? "确认仅卸载 C++ 14.x？"
                    : mode == Vc14RepairMode.InstallOnly ? "确认继续安装 14.42？" : "确认全量重装 C++ 14.x？",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                    Text = (mode == Vc14RepairMode.InstallOnly
                        ? "将读取已保存的待安装记录，在当前正常模式安装/修复 14.42 双架构基线；不会执行卸载。失败时保留记录供核对重试。\n\n"
                        : "将在当前环境卸载下列 x86/x64 C++ 14.x。优先使用已核验的微软 Redistributable 原包装，缺少可用原包装的组件才按 MSI 产品码处理；仍保留依赖与结果复检。\n" + list
                        + "\n旧 INF 卸载节会实际调用其中的 MSI 卸载命令；已核对其目标属于 14.x，执行前会备份包装登记及 INF/PNF。"
                        + (mode == Vc14RepairMode.UninstallOnly ? "\n\n本次仅卸载和复检，保存待安装记录，不安装基线。完成后请手动重启回正常模式，点击“继续安装 14.42”并重新确认。不会自动重启或续跑；期间其他依赖 C++ 的软件可能无法运行。"
                            : "\n\n随后安装 2015–2022 x64/x86 14.42.34433 原包并复检组件与 DLL；若 DLL 版本未对齐会自动用原包修复。")
                        + "\n卸载完成后、安装前会自动备份并清理旧版工具留下的 v14/2026 残留登记（避免其阻止基线安装），备份会保留。"
                        + (skipped.Length > 0 ? "\n\n以下登记重装阶段不卸载（由残留清理阶段评估）：\n" + skipped : "")
                        + "\n保留 2005–2013、SDK/Debug、ARM64 和 DirectX。")
                        + "\n请先关闭游戏与其他应用及运行库防护。共享运行库降级可能影响需要较新版本的软件。"
                        + "\n失败、身份变化、需要重启或结果未确认时停止；不会强杀安装器。" } },
                PrimaryButtonText = mode == Vc14RepairMode.UninstallOnly ? "确认仅卸载" : mode == Vc14RepairMode.InstallOnly ? "确认安装" : "确认全量重装",
                CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            };
            CheckBox? installerConsent = null;
            if (preview.TemporaryInstaller is { Required: true } temporary)
            {
                var panel = new StackPanel { Spacing = 12 };
                panel.Children.Add(new TextBlock { Text = temporary.Detail, TextWrapping = TextWrapping.Wrap });
                installerConsent = new CheckBox { Content = "允许工具临时启用 Installer，执行后恢复；中断时保留恢复记录" };
                panel.Children.Add(installerConsent);
                var oldContent = (ScrollViewer)dialog.Content;
                var text = (TextBlock)oldContent.Content; oldContent.Content = null;
                panel.Children.Add(text); oldContent.Content = panel;
                dialog.IsPrimaryButtonEnabled = false;
                installerConsent.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
                installerConsent.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
            }
            if (await dialog.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary || cancellation.IsCancellationRequested)
            { ViewModel.RepairStatusText = "已取消，未执行卸载或安装。"; return; }
            if (installerConsent is not null && installerConsent.IsChecked != true) return;
            var result = await ViewModel.RepairAsync(preview.Fingerprint, cancellation.Token, mode, allowTemporary);
            var stageMessage = fullUninstallStage
                ? result.Message + "\n\n这是全量修复的 V14 卸载阶段。恢复临时 Installer 设置后，请手动重启回正常模式，再点击“预览全量修复”重新确认；2005–2013 与其他组件尚未安装，不会自动续跑。"
                : result.Message;
            ViewModel.RepairStatusText = stageMessage;
            await new ContentDialog { XamlRoot = XamlRoot, Title = result.Success
                    ? mode == Vc14RepairMode.UninstallOnly ? "卸载阶段完成，等待正常模式安装" : "14.x 安装及复检完成" : "14.x 操作未完成",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = stageMessage, TextWrapping = TextWrapping.Wrap } },
                CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync();
        }
        catch (Exception ex) { ViewModel.RepairStatusText = "操作未完成：" + ex.Message; await ShowRuntimeExceptionAsync(ex); }
        finally { _repairCancellation = null; ViewModel.RepairIsBusy = false; if (!_startupExitPending) await ViewModel.LoadGuardAsync(); }
    }

    public RuntimePage()
    {
        if (!ReleaseFeatures.RuntimeUiEnabled) throw new InvalidOperationException("此测试版未开放运行库，请更新到完整版。");
        InitializeComponent(); _ = ViewModel.LoadGuardAsync();
    }

    private async void RestoreInstaller_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.RepairIsBusy || ViewModel.GuardIsBusy) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "恢复临时 Installer 设置？",
            Content = "将核对保存的原值及工具归属，恢复临时服务实例和安全模式准入。安装事务仍忙、安装器身份未确认或原值被外部修改时会保留记录，不强停、不覆盖。此操作不会安装或卸载运行库。",
            PrimaryButtonText = "核对并恢复", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary) return;
        ViewModel.RepairIsBusy = true;
        try
        {
            var result = await ViewModel.RestoreTemporaryInstallerAsync();
            ViewModel.RepairStatusText = result.Message;
            await new ContentDialog { XamlRoot = XamlRoot, Title = result.Success ? "临时 Installer 设置已恢复" : "恢复未完成",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = result.Message, TextWrapping = TextWrapping.Wrap } },
                CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync();
        }
        catch (Exception ex) { ViewModel.RepairStatusText = ex.Message; await ShowRuntimeExceptionAsync(ex); }
        finally { ViewModel.RepairIsBusy = false; await ViewModel.LoadGuardAsync(); }
    }

    private async void RepairHighFps_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.GuardCleanupEnabled) return;
        if (AdvancedStartupRestart.IsSafeMode)
        {
            var proceed = false;
            ViewModel.RepairIsBusy = true;
            try
            {
                var stage = await new ContentDialog { XamlRoot = XamlRoot, Title = "全量修复：安全模式卸载阶段",
                    Content = "安全模式下仅处理 V14 卸载，2005–2013、VSTO 等安装步骤将在正常模式手动继续。下一步仍须预览确认；临时启用 Installer 必须单独勾选。不会自动重启或执行安装。",
                    PrimaryButtonText = "预览 V14 卸载阶段", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }.ShowWithBackendRecoveryAsync();
                proceed = stage == ContentDialogResult.Primary;
            }
            catch (Exception ex) { ViewModel.RepairStatusText = "操作未完成：" + ex.Message; await ShowRuntimeExceptionAsync(ex); }
            finally { ViewModel.RepairIsBusy = false; }
            if (proceed) Repair14_Click(new Button { Tag = "full-uninstall" }, e);
            return;
        }
        ViewModel.RepairIsBusy = true;
        using var cancellation = new CancellationTokenSource();
        _repairCancellation = cancellation;
        try
        {
            ViewModel.RepairStatusText = "正在校验全量修复资源与处理清单…";
            var preview = await ViewModel.PreviewHighFpsAsync(false);
            if (!preview.CanRepair) { ViewModel.RepairStatusText = preview.Blocker; return; }
            if (preview.HasUninstallSteps && !await ChooseRuntimeEnvironmentAsync(cancellation.Token, async () =>
            {
                var current = await ViewModel.PreviewHighFpsAsync(false);
                if (!current.CanRepair) ViewModel.RepairStatusText = current.Blocker;
                return current.CanRepair;
            }, fullRepair: true)) return;
            if (!await HandleRuntimeFileUseAsync(cancellation.Token, RuntimeFileUseScope.FullRepair)) return;
            preview = await ViewModel.PreviewHighFpsAsync(false);
            if (!preview.CanRepair) { ViewModel.RepairStatusText = preview.Blocker; return; }
            var description = (preview.HasUninstallSteps ? "将在当前环境受控卸载清单中的 V14，优先使用已核验的微软原包装并复检。" : "已核对无待卸载项或仅需继续补装已核验的 14.42 基线。")
                + "C++ 2005–2013 不先卸载，使用十个微软 x86/x64 原包逐包安装或修复。另处理 VSTO 与旧 VC/VB 辅助组件，最后安装你提供的 14.42 双架构基线。已核验的较新旧版 C++ 组件保留，不盲目降级。";
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "确认全量修复？",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                    Text = description + "\n\n不安装 C++2026。V14 单独修复仍保留原入口。\n\n当前清单：\n"
                        + string.Join("\n", preview.Entries)
                        + "\n\n请先关闭游戏、其他应用及运行库防护。共享运行库变更可能影响其他软件。"
                        + "\n安全模式为可选卸载环境；选择安全模式时，安装须回正常模式手动继续。"
                        + "\n失败、需要重启或状态未确认时停止。取消会停止后续步骤，不强杀正在运行的安装器或撤销已完成操作。" } },
                PrimaryButtonText = "确认修复", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary || cancellation.IsCancellationRequested)
            { ViewModel.RepairStatusText = "已取消，未执行修复。"; return; }
            var result = await ViewModel.RepairHighFpsAsync(preview.Fingerprint, false, cancellation.Token);
            ViewModel.RepairStatusText = result.Message;
            await new ContentDialog { XamlRoot = XamlRoot, Title = result.Success ? "修复及复检完成" : "修复未完成",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = result.Message, TextWrapping = TextWrapping.Wrap } },
                CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync();
        }
        catch (Exception ex) { ViewModel.RepairStatusText = "操作未完成：" + ex.Message; await ShowRuntimeExceptionAsync(ex); }
        finally { _repairCancellation = null; ViewModel.RepairIsBusy = false; if (!_startupExitPending) await ViewModel.LoadGuardAsync(); }
    }
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
