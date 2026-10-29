using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class RuntimePage : Page
{
    private readonly DeltaNFD.Services.Backends.PageBackendLease? _pageOwner = DeltaNFD.Services.Backends.PageBackendSessions.Manager.Current;
    public RuntimeViewModel ViewModel { get; } = RuntimeViewModel.Current;
    private bool _guardToggleHandling;
    private CancellationTokenSource? _repairCancellation;
    private bool _baselinePageLoaded;
    private int _baselineReadGeneration;

    private void CancelRepair_Click(object sender, RoutedEventArgs e)
    {
        _repairCancellation?.Cancel();
        ViewModel.RepairStatusText = "已请求停止后续步骤；不会强杀正在运行的安装器，请等待事务结束后核对。";
    }

    private async void Repair14_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUseBaselineActions) return;
        ViewModel.ManualRepairGuidanceText = "";
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
            if (!preview.CanRepair) { ViewModel.RecordRepairBlocker(preview.Blocker); return; }
            if (mode != Vc14RepairMode.InstallOnly && preview.RemovalPlan.Any(p => p.CanRemove)
                && !await ChooseRuntimeEnvironmentAsync(cancellation.Token, async () =>
            {
                var current = await ViewModel.PreviewRepairAsync(mode, allowTemporary);
                if (!current.CanRepair) ViewModel.RecordRepairBlocker(current.Blocker);
                return current.CanRepair;
            })) return;
            if (!await HandleRuntimeFileUseAsync(cancellation.Token)) return;
            preview = await ViewModel.PreviewRepairAsync(mode, allowTemporary);
            if (!preview.CanRepair) { ViewModel.RecordRepairBlocker(preview.Blocker); return; }
            var list = string.Join("\n", preview.RemovalPlan.Select(p =>
                p.Name + " · " + p.Architecture + " · " + p.Version + "（" + (p.CanRemove ? p.Kind switch
                    { "msi" => "已核验 14.x MSI", "bundle" => "已核验包装", "inf" => "已核验旧 14.x INF", _ => "登记卸载命令" } : "保留不处理") + "）\n  " + p.Method));
            var skipped = string.Join("\n", preview.RemovalPlan.Where(p => !p.CanRemove).Select(p => p.Name + "：" + p.Detail));
            var installMissing = preview.Scan.IsMissing && preview.RemovalPlan.Count == 0 && mode != Vc14RepairMode.UninstallOnly;
            var selected = preview.Baseline;
            var targetVersion = selected.FullVersion();
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = mode == Vc14RepairMode.UninstallOnly ? "确认仅卸载 C++ 14.x？"
                    : installMissing ? "确认补装 C++ " + selected.Display() + "？" : mode == Vc14RepairMode.InstallOnly ? "确认继续安装 " + selected.Display() + "？" : "确认仅修复 C++ 14.x？",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                    Text = (installMissing
                        ? "已完成检测，未发现已安装的 C++ 14.x 组件。将安装 2015–2022 x64/x86 " + targetVersion + " 微软原包，并复检组件与实际 DLL。失败时保留待安装记录，供下次继续安装。\n本次不会执行运行库卸载；安装前会核对并受控处理旧包装残留，无法确认时停止。\n"
                        : mode == Vc14RepairMode.InstallOnly
                        ? "将在当前正常模式安装/修复 " + selected.Display() + " 双架构基线；不会执行卸载。失败时保留待安装记录供核对重试。\n\n"
                        : "将在当前环境卸载下列 x86/x64 C++ 14.x。优先使用已核验的微软 Redistributable 原包装，缺少可用原包装的组件才按 MSI 产品码处理；仍保留依赖与结果复检。\n" + list
                        + "\n旧 INF 卸载节会实际调用其中的 MSI 卸载命令；已核对其目标属于 14.x，执行前会备份包装登记及 INF/PNF。"
                        + (mode == Vc14RepairMode.UninstallOnly ? "\n\n本次仅卸载和复检，保存待安装记录，不安装基线。完成后请手动重启回正常模式，在“恢复与续装”中点击“继续安装 " + selected.Display() + "”并重新确认。不会自动重启或续跑；期间其他依赖 C++ 的软件可能无法运行。"
                            : "\n\n随后安装 2015–2022 x64/x86 " + targetVersion + " 原包并复检组件与 DLL；若 DLL 版本未对齐会自动用原包修复。")
                        + "\n卸载完成后、安装前会自动备份并清理旧版工具留下的 v14/2026 残留登记（避免其阻止基线安装），备份会保留。"
                        + (skipped.Length > 0 ? "\n\n以下登记重装阶段不卸载（由残留清理阶段评估）：\n" + skipped : "")
                        + "\n保留 2005–2013、SDK/Debug、ARM64 和 DirectX。")
                        + "\n请先关闭游戏与其他应用及运行库防护。共享运行库变更（" + selected.Display() + "）可能影响需要其他版本的软件。"
                        + (mode == Vc14RepairMode.UninstallOnly ? "" : "\n普通原包修复后DLL仍是错误版本时，将自动尝试一次忽略文件版本的 " + selected.Display() + " 原始MSI重装，并再次检查实际DLL。")
                        + "\n失败、身份变化、需要重启或结果未确认时停止；不会强杀安装器。" } },
                PrimaryButtonText = mode == Vc14RepairMode.UninstallOnly ? "确认仅卸载" : installMissing || mode == Vc14RepairMode.InstallOnly ? "确认安装" : "确认仅修复",
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
            if (await dialog.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner) != ContentDialogResult.Primary || cancellation.IsCancellationRequested)
            { ViewModel.RepairStatusText = "已取消，未执行卸载或安装。"; return; }
            if (installerConsent is not null && installerConsent.IsChecked != true) return;
            var result = await ViewModel.RepairAsync(preview.Fingerprint, cancellation.Token, mode, allowTemporary);
            var stageMessage = fullUninstallStage && result.Success
                ? RuntimeRepairGuidance.OriginalText(result) + "\n\n这是全量修复的 V14 卸载阶段。临时 Installer 设置会随本次操作自动恢复（若失败会在结果中提示并可手动恢复）；请手动重启回正常模式，再点击“预览全量修复”重新确认；2005–2013 与其他组件尚未安装，不会自动续跑。"
                : RuntimeRepairGuidance.OriginalText(result);
            ViewModel.RepairStatusText = stageMessage;
            ViewModel.RecordRepairResult(result, cancellation.IsCancellationRequested);
            await new ContentDialog { XamlRoot = XamlRoot, Title = result.Success
                    ? mode == Vc14RepairMode.UninstallOnly ? "卸载阶段完成，等待正常模式安装" : "14.x 安装及复检完成" : "14.x 操作未完成",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = RuntimeRepairGuidance.DialogText(stageMessage, ViewModel.ManualRepairGuidanceText), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
        }
        catch (Exception ex)
        {
            ViewModel.RepairStatusText = "操作未完成：" + ex.Message;
            ViewModel.RecordRepairResult(new OperationResult { Message = ViewModel.RepairStatusText, OutcomeUnknown = true }, cancellation.IsCancellationRequested || ex is OperationCanceledException);
            await ShowRuntimeExceptionAsync(ex);
        }
        finally { _repairCancellation = null; ViewModel.RepairIsBusy = false; if (!_startupExitPending) await ViewModel.LoadGuardAsync(); }
    }

    public RuntimePage()
    {
        if (!ReleaseFeatures.RuntimeUiEnabled) throw new InvalidOperationException("此测试版未开放运行库，请更新到完整版。");
        ViewModel.SynchronizeRuntimeDetectionPreference();
        InitializeComponent();
        Loaded += RuntimePage_Loaded;
        Unloaded += RuntimePage_Unloaded;
        _ = ViewModel.LoadGuardAsync();
    }

    private async void RuntimePage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_baselinePageLoaded) return;
        _baselinePageLoaded = true;
        var generation = ++_baselineReadGeneration;
        ViewModel.BeginBaselineDriverCheck();
        DeltaNFD.Inspection.NvidiaDriverSeriesStatus status;
        try { status = await DeltaNFD.Inspection.NvidiaDriverSeriesInspection.ReadAsync(); }
        catch (Exception ex)
        {
            Log.Warn("运行库基线：显卡驱动检查失败：" + ex.Message);
            status = new DeltaNFD.Inspection.NvidiaDriverSeriesStatus(false, true);
        }
        if (!_baselinePageLoaded || generation != _baselineReadGeneration) return;
        var visibility = Visibility.Visible;
        RepairBaseline144.Visibility = visibility;
        FullBaseline144.Visibility = visibility;
        ViewModel.CompleteBaselineDriverCheck(status.Has596Series, status.DriverUnknown);
    }

    private void RuntimePage_Unloaded(object sender, RoutedEventArgs e)
    {
        _baselinePageLoaded = false;
        _baselineReadGeneration++;
    }

    private async void ForceReplace14_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUseBaselineActions) return;
        ViewModel.ManualRepairGuidanceText = "";
        ViewModel.RepairIsBusy = true;
        using var cancellation = new CancellationTokenSource();
        _repairCancellation = cancellation;
        try
        {
            ViewModel.RepairStatusText = "正在核对内置 " + ViewModel.SelectedBaseline.Display() + " 载荷与系统 14.x DLL…";
            var preview = await ViewModel.PreviewForceReplaceAsync(cancellation.Token);
            if (!preview.CanRun) { ViewModel.RecordRepairBlocker(preview.Blocker); return; }
            var list = string.Join("\n", preview.Items.Where(item => item.Status is "replace" or "acl").Select(item =>
                item.Architecture + " " + item.File + "："
                + (item.Status == "acl"
                    ? "内容已是 " + item.ExpectedVersion + "，仅修复权限"
                    : (item.CurrentVersion.Length == 0 ? "版本未知" : item.CurrentVersion) + " → " + item.ExpectedVersion
                      + (item.Downgrade ? "（降级）" : "") + (item.InUse ? "（占用，将安排重启后替换）" : ""))
                + (item.AclBroken ? "（普通用户不可读，将修复权限）" : "")
                + (item.Locked ? "（管理员只读，将临时接管所有权）" : "")));
            var warning = "将绕过 Windows Installer，直接覆盖 %SystemRoot%\\System32 与 SysWOW64 中的 14.x DLL。\n"
                + "目标版本：" + preview.PayloadVersion + "（" + Vc14BaselineOption.Display(preview.Baseline) + "）\n"
                + "待替换 " + preview.ReplaceCount + " 个，仅修复权限 " + preview.AclRepairCount + " 个，已匹配 "
                + preview.SkipCount + " 个。\n\n" + (list.Length == 0 ? "（无）" : list)
                + (preview.DowngradeCount > 0 ? "\n\n注意：其中 " + preview.DowngradeCount + " 个当前版本较新，执行后会降级到目标版本。" : "")
                + (preview.LockedCount > 0 ? "\n\n其中 " + preview.LockedCount + " 个文件属主为 TrustedInstaller / 管理员只读："
                    + "执行时会先临时接管所有权取得写权限，替换成功后恢复原属主与 ACL。" : "")
                + (preview.Items.Any(item => item.AclBroken) ? "\n\n普通用户不可读的权限异常也会一并重置为目录继承权限；"
                    + "被占用文件仍安排到下次启动替换。" : "\n\n被占用文件安排到下次启动替换。")
                + "\n\n执行前自动备份并生成 inventory.json、acl.json、registry 只读备份、restore.ps1 与 README.txt。\n"
                + "不会修改 MSI/ARP 安装登记；如需登记一致，请改用“预览并仅修复 14.x”。\n"
                + "请先关闭游戏、其他应用和运行库防护；共享运行库变更可能影响需要其他版本的软件。";
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "确认强制替换 14.x DLL？（高风险）",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap } },
                PrimaryButtonText = "确认强制替换",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner) != ContentDialogResult.Primary
                || cancellation.IsCancellationRequested)
            {
                ViewModel.RepairStatusText = "已取消，未修改系统 DLL。";
                return;
            }
            var result = await ViewModel.ForceReplaceAsync(preview.Fingerprint, cancellation.Token);
            var message = RuntimeRepairGuidance.OriginalText(result);
            ViewModel.RepairStatusText = message;
            ViewModel.RecordRepairResult(result, cancellation.IsCancellationRequested);
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = result.Success && !result.RequiresReboot ? "强制替换完成"
                    : result.Success ? "已替换，部分文件待重启生效" : "强制替换未完全成功",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock
                {
                    Text = RuntimeRepairGuidance.DialogText(message, ViewModel.ManualRepairGuidanceText),
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                } },
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
        }
        catch (Exception ex)
        {
            ViewModel.RepairStatusText = "强制替换未完成：" + ex.Message;
            ViewModel.RecordRepairResult(new OperationResult
            {
                Message = ViewModel.RepairStatusText,
                OutcomeUnknown = true,
            }, cancellation.IsCancellationRequested || ex is OperationCanceledException);
            await ShowRuntimeExceptionAsync(ex);
        }
        finally
        {
            _repairCancellation = null;
            ViewModel.RepairIsBusy = false;
            if (!_startupExitPending) await ViewModel.LoadGuardAsync();
        }
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
        if (await confirm.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner) != ContentDialogResult.Primary) return;
        ViewModel.RepairIsBusy = true;
        try
        {
            var result = await ViewModel.RestoreTemporaryInstallerAsync();
            ViewModel.RepairStatusText = result.Message;
            await new ContentDialog { XamlRoot = XamlRoot, Title = result.Success ? "临时 Installer 设置已恢复" : "恢复未完成",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = result.Message, TextWrapping = TextWrapping.Wrap } },
                CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
        }
        catch (Exception ex) { ViewModel.RepairStatusText = ex.Message; await ShowRuntimeExceptionAsync(ex); }
        finally { ViewModel.RepairIsBusy = false; await ViewModel.LoadGuardAsync(); }
    }

    private async Task RunSafeUninstallAllCppAsync()
    {
        ViewModel.ManualRepairGuidanceText = "";
        ViewModel.RepairIsBusy = true;
        using var cancellation = new CancellationTokenSource();
        _repairCancellation = cancellation;
        try
        {
            ViewModel.RepairStatusText = "正在核对安全模式 C++ 卸载清单与 Installer 状态…";
            var preview = await ViewModel.PreviewSafeUninstallAsync(cancellation.Token);
            if (!preview.CanRun) { ViewModel.RecordRepairBlocker(preview.Blocker); return; }
            if (!await HandleRuntimeFileUseAsync(cancellation.Token, RuntimeFileUseScope.FullRepair)) return;

            var shown = preview.Entries.Take(30).Select(e => "· " + e.Name + " · " + e.Architecture + " · " + e.Version);
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "将卸载检测到的全部 C++ 运行库（2005–2013 / 2015–2022 / 14.x，共 " + preview.Entries.Count + " 项）。"
                    + "依赖其他安装包、身份变化、卸载器要求重启或结果未知时会停止。"
                    + "卸载完成后必须回正常模式重新预览全量修复，由正常模式安装 C++ 基线；不会自动重启或续跑。",
            });
            body.Children.Add(new ScrollViewer
            {
                MaxHeight = 300,
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = string.Join("\n", shown) + (preview.Entries.Count > 30 ? "\n…" : "") },
            });
            CheckBox? consent = null;
            if (preview.RequiresTemporaryInstaller)
            {
                body.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = preview.TemporaryInstallerDetail });
                consent = new CheckBox { Content = "允许工具临时启用 Windows Installer，执行后恢复；中断时保留恢复记录" };
                body.Children.Add(consent);
            }
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "安全模式：卸载全部 C++（高风险）",
                Content = new ScrollViewer { MaxHeight = 420, Content = body },
                PrimaryButtonText = "确认卸载全部 C++",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = consent is null,
            };
            if (consent is not null)
            {
                consent.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
                consent.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
            }
            if (await dialog.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner) != ContentDialogResult.Primary || cancellation.IsCancellationRequested)
            {
                ViewModel.RepairStatusText = "已取消，未卸载任何运行库。";
                return;
            }
            if (consent is not null && consent.IsChecked != true) return;

            var result = await ViewModel.SafeUninstallAllCppAsync(preview.Fingerprint, preview.RequiresTemporaryInstaller, cancellation.Token);
            ViewModel.RepairStatusText = RuntimeRepairGuidance.OriginalText(result);
            var resultText = ViewModel.RecordRepairResult(result, cancellation.IsCancellationRequested);
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = result.Success ? "安全模式卸载完成，请回正常模式安装" : "安全模式卸载未完成",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = resultText, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                CloseButtonText = "知道了",
            }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
        }
        catch (Exception ex)
        {
            ViewModel.RepairStatusText = "操作未完成：" + ex.Message;
            ViewModel.RecordRepairResult(new OperationResult { Message = ViewModel.RepairStatusText, OutcomeUnknown = true }, cancellation.IsCancellationRequested || ex is OperationCanceledException);
            await ShowRuntimeExceptionAsync(ex);
        }
        finally
        {
            _repairCancellation = null;
            ViewModel.RepairIsBusy = false;
            if (!_startupExitPending) await ViewModel.LoadGuardAsync();
        }
    }

    private async void RepairHighFps_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUseBaselineActions) return;
        ViewModel.ManualRepairGuidanceText = "";
        if (AdvancedStartupRestart.IsSafeMode)
        {
            var proceed = false;
            var stageResult = ContentDialogResult.None;
            ViewModel.RepairIsBusy = true;
            try
            {
                var stageDialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "全量修复：安全模式卸载阶段",
                    Content = "安全模式下可选择只卸载 V14，或显式确认“卸载全部 C++（高风险）”。卸载全部 C++ 会处理检测到的 2005–2013、2015–2022 和 14.x 运行库；依赖阻塞、身份变化或结果未知时停止。2005–2013、VSTO 等安装步骤仍需回正常模式手动继续。\n\n临时启用 Installer 必须单独确认；若系统仍禁止启动 Windows Installer（如 1084），会停止并提示回正常模式；不会自动重启或执行安装。",
                    PrimaryButtonText = "预览 V14 卸载阶段",
                    SecondaryButtonText = "卸载全部 C++（高风险）",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                };
                stageResult = await stageDialog.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
                proceed = stageResult == ContentDialogResult.Primary;
            }
            catch (Exception ex) { ViewModel.RepairStatusText = "操作未完成：" + ex.Message; await ShowRuntimeExceptionAsync(ex); }
            finally { ViewModel.RepairIsBusy = false; }
            if (stageResult == ContentDialogResult.Secondary)
            {
                // 必须等 finally 清除 RepairIsBusy 后再进入新入口，否则入口的忙碌守卫会直接返回。
                await RunSafeUninstallAllCppAsync();
                return;
            }
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
            if (!preview.CanRepair) { ViewModel.RecordRepairBlocker(preview.Blocker); return; }
            if (preview.HasUninstallSteps && !await ChooseRuntimeEnvironmentAsync(cancellation.Token, async () =>
            {
                var current = await ViewModel.PreviewHighFpsAsync(false);
                if (!current.CanRepair) ViewModel.RecordRepairBlocker(current.Blocker);
                return current.CanRepair;
            }, fullRepair: true)) return;
            if (!await HandleRuntimeFileUseAsync(cancellation.Token, RuntimeFileUseScope.FullRepair)) return;
            preview = await ViewModel.PreviewHighFpsAsync(false);
            if (!preview.CanRepair) { ViewModel.RecordRepairBlocker(preview.Blocker); return; }
            var selected = preview.Baseline;
            var description = (preview.HasUninstallSteps ? "将在当前环境受控卸载清单中的 V14，优先使用已核验的微软原包装并复检。" : "已核对无待卸载项或仅需继续补装已核验的 " + selected.Display() + " 基线。")
                + "C++ 2005–2013 不先卸载，使用十个微软 x86/x64 原包逐包安装或修复。另处理 VSTO 与旧 VC/VB 辅助组件，最后安装工具内置的 " + selected.FullVersion() + " 双架构基线。已核验的较新旧版 C++ 组件保留，不盲目降级。"
                + "\n" + selected.Display() + " 普通原包修复后DLL仍是错误版本时，会自动尝试一次忽略文件版本的原始MSI重装，并再次检查实际DLL。";
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
            if (await confirm.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner) != ContentDialogResult.Primary || cancellation.IsCancellationRequested)
            { ViewModel.RepairStatusText = "已取消，未执行修复。"; return; }
            var result = await ViewModel.RepairHighFpsAsync(preview.Fingerprint, false, cancellation.Token);
            ViewModel.RepairStatusText = RuntimeRepairGuidance.OriginalText(result);
            var resultText = ViewModel.RecordRepairResult(result, cancellation.IsCancellationRequested);
            await new ContentDialog { XamlRoot = XamlRoot, Title = result.Success ? "修复及复检完成" : "修复未完成",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = resultText, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
        }
        catch (Exception ex)
        {
            ViewModel.RepairStatusText = "操作未完成：" + ex.Message;
            ViewModel.RecordRepairResult(new OperationResult { Message = ViewModel.RepairStatusText, OutcomeUnknown = true }, cancellation.IsCancellationRequested || ex is OperationCanceledException);
            await ShowRuntimeExceptionAsync(ex);
        }
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
                }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
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
            if (await confirm.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner) != ContentDialogResult.Primary) return;
            var result = await ViewModel.ForceCleanupGuardAsync();
            if (result is not null) await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = result.Success ? "清理完成" : "清理未完成", Content = new ScrollViewer
                {
                    MaxHeight = 420, Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = result.Message },
                },
                CloseButtonText = "知道了",
            }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
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
            }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
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
                .ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
        }
        catch (Exception dialogError) { DeltaNFD.Services.Log.Error("运行库错误及日志引导弹窗无法显示", dialogError); }
    }
}
