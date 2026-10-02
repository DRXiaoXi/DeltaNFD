using DeltaNFD.Models;
using DeltaNFD.Services;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeltaNFD.Views;

public sealed partial class SystemOptimizePage : Page
{
    private bool _suppressBxToggle;
    public SystemOptimizeViewModel ViewModel { get; } = new();

    public SystemOptimizePage()
    {
        InitializeComponent();

        // 扩展优化库高危操作确认弹窗（ViewModel 不依赖 UI 类型，通过钩子注入）
        BxDbViewModel.ConfirmHook = async name =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "确认操作",
                Content = name == "一键恢复全部备份"
                    ? "将按现有备份尽可能恢复扩展库、深度优化和虚拟内存设置。失败项会列出；已移除的 APPX 无法自动重装。部分设置需要重启。确定继续？"
                    : $"「{name}」会降低系统安全防护或影响系统功能，确定要应用吗？（可通过备份尽可能恢复）",
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        };

        // 服务禁用影响警告弹窗：逐组复选框（默认勾选=应用），取消勾选的组本次跳过（勾选意图保留）
        BxDbViewModel.ServiceGroupWarningHook = async warnings =>
        {
            var excluded = new HashSet<string>();
            var panel = new StackPanel { Spacing = 12 };

            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "本批优化将禁用以下服务组，其中部分服务仍被其他功能依赖（已译出具体影响）。"
                    + "不想动的组请取消勾选——本次将跳过它们（勾选保留，可稍后单独应用）：",
            });

            foreach (var warning in warnings)
            {
                var groupPanel = new StackPanel { Spacing = 4 };
                var header = new CheckBox
                {
                    Content = BuildGroupHeader(warning.GroupDisplayName, warning.Lines.Count),
                    IsChecked = true,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                };
                groupPanel.Children.Add(header);
                groupPanel.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(28, 0, 0, 4),
                    FontSize = 12,
                    Opacity = 0.85,
                    Text = string.Join(Environment.NewLine, warning.Lines),
                });
                header.Checked += (_, _) => excluded.Remove(warning.RowId);
                header.Unchecked += (_, _) => excluded.Add(warning.RowId);
                panel.Children.Add(groupPanel);
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "服务禁用影响警告",
                Content = new ScrollViewer
                {
                    MaxHeight = 420,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = panel,
                },
                PrimaryButtonText = "仍要应用",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            var choice = await dialog.ShowAsync();
            return choice == ContentDialogResult.Primary ? excluded : null;
        };

        // Defender 预检引导弹窗 + 应用完成后的防护恢复提醒（见 DefenderGuard 类型注释）
        BxDbViewModel.DefenderGuardHook = ShowDefenderGuardDialogAsync;
        BxDbViewModel.DefenderRestoreRemindHook = ShowDefenderRestoreDialogAsync;

        // 虚拟内存：所选盘为机械盘/游戏安装盘时，应用前的建议更换弹窗（true = 仍要执行）
        SystemOptimizeViewModel.PagefileDriveWarningHook = async message =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "建议更换虚拟内存盘符",
                Content = message,
                PrimaryButtonText = "仍要执行",
                CloseButtonText = "返回更换盘符",
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        };

        // 关闭 Hyper-V/VBS 前的 Windows Hello 操作引导（见 BxDbViewModel.WindowsHelloGuidanceHook 注释）
        BxDbViewModel.WindowsHelloGuidanceHook = ShowWindowsHelloGuidanceAsync;
    }

    private static string BuildGroupHeader(string groupName, int warningCount) =>
        $"{groupName}（{warningCount} 项影响）";

    // ---------------- 扩展优化库子栏目 ----------------

    private void BxChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle && toggle.DataContext is BxSectionChipVm chip)
        {
            ViewModel.Bx.SelectedSection = chip;
        }
    }

    /// <summary>扩展优化库开关拨动：仅记录意图，应用由「应用更改」统一执行。</summary>
    private async void BxToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressBxToggle || sender is not ToggleSwitch toggle || toggle.DataContext is not BxItemVm row)
        {
            return;
        }

        try
        {
            await ViewModel.Bx.ToggleAsync(row, toggle.IsOn);
        }
        catch (Exception ex)
        {
            Log.Error($"扩展库开关意图处理失败：{row.DisplayName}", ex);
            _suppressBxToggle = true;
            try
            {
                row.IsOn = row.ActualOn;
                row.HasUserIntent = false;
                row.RefreshStateText(row.ActualState);
                ViewModel.Bx.RefreshPendingFlags();
            }
            finally { _suppressBxToggle = false; }
            ViewModel.Bx.StatusText = $"{row.DisplayName}：开关更改未完成，已撤回该项意图。{ex.Message}";
        }
    }

    /// <summary>多档位条目：切换档位仅记录意图，应用由「应用更改」统一执行。</summary>
    private void GearSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.DataContext is not BxItemVm row)
        {
            return;
        }

        // 仅在用户实际改变选择且与系统真实状态不同时标记待应用
        ViewModel.Bx.GearChanged(row);
    }

    private void BxDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BxItemVm row })
        {
            row.DetailsVisible = !row.DetailsVisible;
        }
    }

    private async void BxRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        // 显式刷新：作废主页扫描缓存后全量重读
        OptimizationScan.Invalidate();
        ServiceLocator.Bx.InvalidateTaskCache();
        ServiceLocator.Bx.InvalidateAppxCache();
        await ViewModel.Bx.LoadItemsAsync();
    }

    // ---------------- UP推荐：虚拟内存（页面文件）设置 ----------------

    /// <summary>「关闭 Hyper-V/VBS」准备禁用时的 Windows Hello 操作引导（true = 已了解，继续禁用）。</summary>
    private async Task<bool> ShowWindowsHelloGuidanceAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "关闭 Hyper-V / VBS 前请先操作 Windows Hello",
            Content = "请先取消依赖 Windows Hello 的登录方式：\n\n"
                      + "先取消面部/指纹识别登录，删除 PIN 码登录等方式"
                      + "（打开电脑设置 -- 账户 -- 登录选项 处查看）。\n\n"
                      + "提示：如无法删除（删除按钮呈灰色显示），先关闭“要求 Microsoft 账户使用 Windows Hello 登录”"
                      + "之后，再操作删除 PIN 码。",
            PrimaryButtonText = "已了解，继续禁用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void ApplyPagefileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ViewModel.PagefileMinText.Trim(), out var minGb) || minGb < 16)
        {
            ViewModel.PagefileStatusText = "最小值不能低于 16 GB。";
            return;
        }

        if (!int.TryParse(ViewModel.PagefileMaxText.Trim(), out var maxGb) || maxGb < 16)
        {
            ViewModel.PagefileStatusText = "最大值不能低于 16 GB。";
            return;
        }

        if (maxGb < minGb)
        {
            ViewModel.PagefileStatusText = "最大值不能小于最小值。";
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "设置虚拟内存？",
            Content = $"将在 {ViewModel.PagefileSelectedDriveText ?? "系统盘"} 创建固定大小页面文件：\n"
                      + $"· 最小 {minGb} GB / 最大 {maxGb} GB（关闭系统自动管理）\n"
                      + "· 重启电脑后生效\n"
                      + "· 随时可点「恢复系统托管」交还给 Windows 自动管理\n\n"
                      + "确定继续？",
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await ViewModel.ApplyPagefileAsync();
        if (result is { Success: false })
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "设置失败",
                Content = result.Message,
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            }.ShowAsync();
        }
    }

    private async void RestorePagefileButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "恢复系统托管？",
            Content = "将交还 Windows 自动管理所有驱动器的分页文件大小（重启电脑后生效）。\n\n确定继续？",
            PrimaryButtonText = "恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await ViewModel.RestorePagefileAutomaticAsync();
        if (result is { Success: false })
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "恢复失败",
                Content = result.Message,
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            }.ShowAsync();
        }
    }

    private async void RestoreOriginalPagefileButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "恢复修改前的虚拟内存配置？",
            Content = "将使用首次修改前的备份恢复页面文件配置，重启电脑后生效。",
            PrimaryButtonText = "恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await ViewModel.RestorePagefileOriginalAsync();
        if (result is { Success: false })
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "恢复失败",
                Content = result.Message,
                CloseButtonText = "知道了",
            }.ShowAsync();
        }
    }

    // ---------------- Defender 预检引导（优化前 / 优化后） ----------------

    /// <summary>
    /// 优化前安全检查弹窗：批次含安全防护类条目且本机防护在位时展示。
    /// 「我已关闭，重新检测」= 复查实时保护，通过才继续；「仍然继续」= 跳过引导（相关条目可能失败）；默认停在「取消」。
    /// </summary>
    private async Task<DefenderGuardDecision> ShowDefenderGuardDialogAsync(DefenderPreflightReport report)
    {
        var addExclusion = report.DefenderPresent;
        var rtpStillOn = false;
        var tamperStillOn = false;
        while (true)
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = BuildGuardGuidanceText(report, rtpStillOn, tamperStillOn),
            });

            var openLink = new HyperlinkButton { Content = "打开 Windows 安全中心" };
            openLink.Click += (_, _) => OpenWindowsSecurity();
            panel.Children.Add(openLink);

            if (report.DefenderPresent)
            {
                var exclusionBox = new CheckBox
                {
                    Content = "优化期间把本工具目录加入 Defender 排除项（应用结束后自动移除）",
                    IsChecked = addExclusion,
                };
                exclusionBox.Checked += (_, _) => addExclusion = true;
                exclusionBox.Unchecked += (_, _) => addExclusion = false;
                panel.Children.Add(exclusionBox);
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "优化前安全检查",
                Content = panel,
                PrimaryButtonText = report.DefenderPresent ? "我已关闭，重新检测" : "继续应用",
                SecondaryButtonText = report.DefenderPresent ? "仍然继续" : "",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            var choice = await dialog.ShowAsync();

            switch (choice)
            {
                case ContentDialogResult.Primary:
                    if (addExclusion)
                    {
                        await DefenderGuard.AddSessionExclusionsAsync(report);
                    }

                    if (!report.DefenderPresent)
                    {
                        return DefenderGuardDecision.Proceed;
                    }

                    // 重新检测：实时保护与篡改防护都关闭才算通过（篡改防护开着时照样拦截服务停止与策略写入）
                    (rtpStillOn, tamperStillOn) = await DefenderGuard.RefreshProtectionStateAsync(report);
                    if (!rtpStillOn && !tamperStillOn)
                    {
                        Log.Info("DefenderGuard：重新检测通过（实时保护与篡改防护均已关闭），继续应用");
                        return DefenderGuardDecision.Proceed;
                    }

                    Log.Info($"DefenderGuard：重新检测未通过（实时保护仍开={rtpStillOn}，篡改防护仍开={tamperStillOn}）");
                    break; // 仍开启：再弹一轮，文案提示可「仍然继续」

                case ContentDialogResult.Secondary:
                    if (addExclusion)
                    {
                        await DefenderGuard.AddSessionExclusionsAsync(report);
                    }

                    Log.Info("DefenderGuard：用户选择跳过引导继续应用");
                    return DefenderGuardDecision.ProceedAnyway;

                default:
                    Log.Info("DefenderGuard：用户取消应用");
                    return DefenderGuardDecision.Cancel;
            }
        }
    }

    private static string BuildGuardGuidanceText(DefenderPreflightReport report, bool rtpStillOn, bool tamperStillOn)
    {
        var sb = new System.Text.StringBuilder();
        if (report.DefenderPresent)
        {
            var names = string.Join("、", report.SecurityItemNames.Take(6)) + (report.SecurityItemNames.Count > 6 ? " 等" : "");
            sb.AppendLine($"本批优化包含 {report.SecurityItemNames.Count} 项安全防护类条目（{names}）。");
            sb.AppendLine();
            sb.AppendLine("Windows 安全中心的「篡改防护」会拦截任何程序（包括管理员）对 Defender 的修改：被拦截的条目会应用失败，还可能在保护历史里留下拦截记录，甚至隔离本工具的文件。");
            sb.AppendLine();
            sb.AppendLine("要让这些条目顺利应用，请先打开 Windows 安全中心 → 病毒和威胁防护 →「管理设置」，临时关闭「实时保护」和「篡改防护」，完成优化后再恢复。");
            if (report.ThirdPartyAvNames.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("另检测到第三方杀软：" + string.Join("、", report.ThirdPartyAvNames) + "，也请暂时退出。");
            }

            if (rtpStillOn || tamperStillOn)
            {
                sb.AppendLine();
                var which = (rtpStillOn, tamperStillOn) switch
                {
                    (true, true) => "实时保护与篡改防护",
                    (true, false) => "实时保护",
                    _ => "篡改防护",
                };
                sb.Append($"刚重新检测过：{which}仍处于开启状态。确认已关闭后点「我已关闭，重新检测」，或点「仍然继续」跳过引导（相关条目可能失败）。");
            }
        }
        else if (report.ThirdPartyAvNames.Count > 0)
        {
            sb.AppendLine("检测到第三方杀软：" + string.Join("、", report.ThirdPartyAvNames) + "。");
            sb.AppendLine();
            sb.Append("第三方杀软的主动防御可能拦截系统优化写入。建议优化期间暂时退出，结束后再开启。");
        }

        return sb.ToString();
    }

    /// <summary>应用完成后的安全提醒：引导恢复实时保护/篡改防护/杀软。</summary>
    private async Task ShowDefenderRestoreDialogAsync(DefenderPostApplyResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("本轮优化已完成。");
        sb.AppendLine();
        if (result.DefenderLeftOff)
        {
            sb.Append("按本批的「Windows Defender」条目，Defender 将保持关闭；需要恢复系统防护时，可随时用「一键恢复」还原。");
        }
        else
        {
            sb.Append("如果你为优化临时关闭了「实时保护 / 篡改防护」或退出了杀软，建议现在重新开启，恢复系统防护。");
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "优化完成 · 安全提醒",
            Content = sb.ToString(),
            PrimaryButtonText = "打开 Windows 安全中心",
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            OpenWindowsSecurity();
        }
    }

    private static void OpenWindowsSecurity()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("windowsdefender://") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("DefenderGuard：打开 Windows 安全中心失败", ex);
        }
    }
}
