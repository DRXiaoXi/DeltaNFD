using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

/// <summary>
/// 一键帧格模式「开启 / 退出」的共享 UI 流程：
/// 确认对话框（含"不重启生效"的风险警告）→ 调用服务激活/退出 → 询问是否立即重启 → 展示结果。
/// 开关的回拨由调用方负责（调用方持有 suppress 标志）。
/// </summary>
internal static class FrameModeFlow
{
    /// <summary>生成开启确认文案（按当前帧格配置区分；不安全的目标已在预检阶段被拦截）。</summary>
    public static string BuildActivateConfirmText(IFrameService frame)
    {
        if (frame.GpuSpoofEnabled && frame.IsGpuSpoofInFrame)
        {
            if (frame.GpuSpoofApplyMode == GpuSpoofApplyMode.Reboot)
            {
                return "开启后立即执行：\n" +
                       $"· 显卡伪装为「{frame.GpuSpoofFakeName}」，重启电脑后生效\n" +
                       "· 创建登录自启动任务，重启后本程序自动运行并保持帧格模式开启\n" +
                       "· 其余功能（电源锁定 / 加速响应 / DWM 监控）按帧格页各自开关执行";
            }

            var text = "开启后立即执行：\n" +
                       $"· 显卡伪装为「{frame.GpuSpoofFakeName}」\n" +
                       "· 禁用并重新启用目标显卡，使伪装立即生效\n\n" +
                       "⚠ 重载显卡时屏幕会闪烁或短暂黑屏，正在运行的游戏和 3D 应用可能崩溃；" +
                       "已加保险：45 秒后系统自动重新启用显卡。";

            return text;
        }

        return "开启后将启用已配置的帧格功能（各功能按帧格页开关执行，不强制开启 DWM 监控）。";
    }

    /// <summary>生成退出确认文案（按当前帧格配置区分）。</summary>
    public static async Task<string> BuildDeactivateConfirmTextAsync(IFrameService frame)
    {
        if (frame.IsGpuSpoofInFrame && frame.GpuSpoofApplyMode == GpuSpoofApplyMode.DeviceRestart)
        {
            var safe = await frame.IsPathDrivingDisplayAsync(frame.GpuSpoofRegistryPath) == false;
            return safe
                ? "退出后将还原显卡原始型号并重载显卡立即生效（屏幕会闪烁/短暂黑屏，45 秒后系统自动重新启用显卡兜底）。"
                : "退出后将还原显卡原始型号并重载显卡立即生效（台式机放宽限制）。" +
                  "45 秒后系统自动重新启用显卡兜底；重载后需在 1 分钟内确认显示器亮屏，超时未确认自动重启。";
        }

        if (frame.IsGpuSpoofInFrame)
        {
            return "退出后将还原显卡原始型号、移除登录自启动任务，并需要重启电脑完成还原。";
        }

        return "退出后停止帧格专属功能（电源锁定与加速响应停止，开关设置保留）；「DWM 监控」为独立开关，保持你的设置不变。";
    }

    /// <summary>总开关拨动后的完整流程（调用方负责 suppress 与开关回拨）。</summary>
    public static async Task RunToggleAsync(IFrameService frame, ToggleSwitch toggle)
    {
        var root = toggle.XamlRoot;
        if (root is null)
        {
            return;
        }

        if (toggle.IsOn)
        {
            await RunActivateAsync(root, frame);
        }
        else
        {
            await RunDeactivateAsync(root, frame);
        }
    }

    public static async Task RunActivateAsync(XamlRoot root, IFrameService frame)
    {
        // 硬拦截预检：目标显卡正在驱动显示器（或无法确认）时禁止重载——
        // 禁用正在输出画面的显卡会造成无法恢复的黑屏（内屏直连 dGPU 的机型尤其如此）。
        if (frame.GpuSpoofEnabled && frame.IsGpuSpoofInFrame &&
            frame.GpuSpoofApplyMode == GpuSpoofApplyMode.DeviceRestart &&
            await frame.IsPathDrivingDisplayAsync(frame.GpuSpoofRegistryPath) != false)
        {
            await ShowMessageAsync(root, "无法使用「不重启生效」",
                "目标显卡正在驱动你的显示器（或无法确认显示器归属），重载显卡会造成无法恢复的黑屏。\n\n" +
                "请改用「重启生效」模式：伪装写入后重启电脑即可生效，不会黑屏。");
            return;
        }

        if (!await ConfirmAsync(root, "开启一键帧格模式？", BuildActivateConfirmText(frame)))
        {
            return;
        }

        var result = await frame.ActivateFrameModeAsync();
        if (!result.Success)
        {
            try
            {
                var incomplete = await frame.GetIncompletePowerBackupsAsync();
                if (incomplete.Count > 0)
                {
                    var details = string.Join("\n", incomplete.Select(x => x.Path + "\n" + x.Reason));
                    var confirm = new ContentDialog
                    {
                        XamlRoot = root, Title = "旧电源备份缺少原值",
                        Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = details + "\n\n无法恢复丢失的原值，继续重试不会补回备份。是否保留当前 USB/PCIe 电源值，并将这些旧记录移至证据目录？\n这不是恢复默认值或还原成功，不会写入电源设置。\n同时关闭“降低省电延迟”，再尝试开启其他已选择的帧格功能。原 JSON 保留用于后续排查；其他有效备份仍按原值恢复。",
                        } },
                        PrimaryButtonText = "保留当前值并继续", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
                    };
                    if (await confirm.ShowAsync() == ContentDialogResult.Primary)
                    {
                        var preserved = await frame.PreserveCurrentPowerValuesAsync(incomplete);
                        result = preserved.Success ? await frame.ActivateFrameModeAsync() : preserved;
                        if (preserved.Success) result = new OperationResult
                        {
                            Success = result.Success, Message = preserved.Message + "\n降低省电延迟已关闭。\n" + result.Message,
                            RequiresReboot = result.RequiresReboot, RequiresLogoff = result.RequiresLogoff,
                            RequiresDisplayConfirm = result.RequiresDisplayConfirm, IsSkipped = result.IsSkipped,
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("帧格旧备份预检失败", ex);
                result = OperationResult.Fail(result.Message + "\n旧备份预检失败，未隔离记录：" + ex.Message);
            }
        }
        if (!result.Success)
        {
            await ShowMessageAsync(root, "开启失败", result.Message);
            return;
        }

        if (result.RequiresDisplayConfirm)
        {
            // 台式机 + 目标显卡正在驱动显示器：45 秒自动恢复进程已启动，要求 1 分钟内确认亮屏
            await RunDisplayConfirmCountdownAsync(root, frame);
            return;
        }

        if (result.RequiresReboot)
        {
            await AskRebootAsync(root, frame, "显卡已伪装", result.Message);
        }
        else
        {
            await ShowMessageAsync(root, "帧格模式已开启", result.Message);
        }
    }

    public static async Task RunDeactivateAsync(XamlRoot root, IFrameService frame)
    {
        if (!await ConfirmAsync(root, "退出一键帧格模式？", await BuildDeactivateConfirmTextAsync(frame)))
        {
            return;
        }

        var result = await frame.DeactivateFrameModeAsync();
        if (!result.Success)
        {
            await ShowMessageAsync(root, "退出失败", result.Message);
            return;
        }

        if (result.RequiresDisplayConfirm)
        {
            await RunDisplayConfirmCountdownAsync(root, frame);
            return;
        }

        if (result.RequiresReboot)
        {
            await AskRebootAsync(root, frame, "显卡已还原", result.Message);
        }
        else
        {
            await ShowMessageAsync(root, "已退出帧格模式", result.Message);
        }
    }

    /// <summary>
    /// 台式机重载显卡后的 1 分钟亮屏确认倒计时：
    /// 确认亮屏 → 完成（撤销兜底）；倒计时归零未确认 → 自动重启电脑恢复显示。
    /// </summary>
    private static async Task RunDisplayConfirmCountdownAsync(XamlRoot root, IFrameService frame)
    {
        var remaining = 60;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "请确认显示器处于亮屏状态",
            Content = "显卡已重载并生效。\n\n" +
                      "如果显示器亮屏正常，请点击下方按钮完成确认；\n" +
                      "如果屏幕黑屏且倒计时结束仍未亮起，电脑将自动重启以恢复显示。",
            PrimaryButtonText = $"显示器亮屏正常（{remaining} 秒）",
            SecondaryButtonText = "黑屏，立即重启",
            DefaultButton = ContentDialogButton.Primary,
        };

        var dispatcher = ((FrameworkElement?)root.Content)?.DispatcherQueue
            ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining > 0)
            {
                dialog.PrimaryButtonText = $"显示器亮屏正常（{remaining} 秒）";
                return;
            }

            timer.Stop();
            dialog.Hide();
        };
        timer.Start();

        var result = await dialog.ShowAsync();
        timer.Stop();

        if (result == ContentDialogResult.Primary)
        {
            // 亮屏确认完成；45 秒兜底进程随后执行 Enable-PnpDevice 时对已启用设备是空操作
            await ShowMessageAsync(root, "确认完成", "显示器状态正常，帧格模式运行中。");
            return;
        }

        // 倒计时超时或用户选择立即重启
        frame.RebootNow();
    }

    private static Task<bool> ConfirmAsync(XamlRoot root, string title, string content)
        => new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = content,
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        }.ShowAsync().AsTask().ContinueWith(t => t.Result == ContentDialogResult.Primary);

    private static async Task AskRebootAsync(XamlRoot root, IFrameService frame, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = message + "\n\n是否立即重启电脑？（未保存的文件会被强制关闭）",
            PrimaryButtonText = "立即重启",
            CloseButtonText = "稍后自己重启",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            frame.RebootNow();
        }
    }

    private static Task ShowMessageAsync(XamlRoot root, string title, string message)
        => new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = message,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync().AsTask();
}
