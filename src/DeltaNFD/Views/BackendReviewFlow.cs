using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

/// <summary>设置页与帧格总开关共享的核对、恢复与证据归档入口。</summary>
internal static class BackendReviewFlow
{
    private static int _reviewing;
    internal static bool IsReviewing => Volatile.Read(ref _reviewing) != 0;
    internal static async Task RunAsync(XamlRoot root, IReadOnlyCollection<string>? modules = null)
    {
        while (await RunOnceAsync(root, modules)) { }
    }
    private static async Task<bool> RunOnceAsync(XamlRoot root, IReadOnlyCollection<string>? modules)
    {
        if (Interlocked.CompareExchange(ref _reviewing, 1, 0) != 0) return false;
        var retry = false;
        using var blocked = BackendOperationGate.BlockNewOperations();
        using var control = BackendOperationGate.AllowControlOperations();
        using var dialogs = await BackendDialogExtensions.EnterReviewAsync();
        try
        {
            var reviews = await BackendLifecycle.PrepareReviewAsync(modules);
            if (reviews.Count == 0)
            {
                await new ContentDialog { XamlRoot = root, Title = "未确认操作", Content = "没有待核对的官方后端记录。", CloseButtonText = "知道了" }.ShowAsync();
                return false;
            }
            foreach (var review in reviews)
            {
                if (review.Module == DeltaNFD.Backends.BackendModules.Runtime && review.Fingerprint.Length == 0
                    && RuntimeCorruptRecordReview.Inspect() is { } corrupt)
                {
                    retry = await ReviewCorruptRuntimeAsync(root, corrupt);
                    if (retry) break;
                    continue;
                }
                // 原进程已确认退出时，做一次只读状态核对，并向玩家提供可用恢复范围。
                ReconcileSnapshot? snapshot = null;
                var snapshotNote = "";
                var readError = "";
                if (review.CanRestore)
                {
                    try { snapshot = await BackendRecovery.ReconcileAsync(review.Module,
                        scope: BackendRecovery.IsRuntimeInstallation(review) ? "repair-review" : null); }
                    catch (Exception ex) { readError = ex.Message; snapshotNote = "\n\n只读状态核对未能完成：" + readError; }
                }
                var scopes = BackendRecovery.RecoveryChoices(review, snapshot).ToList();
                IReadOnlyList<FrameIncompletePowerBackup> powerRecords = [];
                if (review.Module == DeltaNFD.Backends.BackendModules.CpuFrame && scopes.Count > 0)
                {
                    try { powerRecords = await BackendRecovery.ReadPowerReviewAsync(review); }
                    catch (Exception ex) { snapshotNote += "\n旧电源备份未能读取，未开放归档：" + ex.Message; }
                    if (powerRecords.Count > 0) scopes.Add(new RecoveryScope { Id = "power-preserve",
                        Description = "保留当前电源值并归档无法恢复的旧电源备份，再完整复核其他帧格状态", Available = true });
                }
                var canKeep = BackendRecovery.CanKeepCurrentState(review, snapshot);
                var installationReview = BackendRecovery.IsRuntimeInstallation(review);
                var restartCheckpoint = installationReview && scopes.Count == 1 && scopes[0].Id == "repair-restart-checkpoint";
                if (BackendRecovery.IsRuntimeFileReplacement(review))
                    snapshotNote += "\n\n这是系统 DLL 强制替换记录。上方防护状态不代表 DLL 已还原，不能使用防护恢复处理它。请按本次 Vc14ForceReplace 备份目录中的 README/restore.ps1 手动恢复、核对文件与重启排程，再决定是否保留当前状态；不会重新执行替换。";
                if (installationReview)
                    snapshotNote += "\n\n安装/系统修复不能按防护备份回滚。请等待安装进程结束，再点击“核对结束并恢复可用”；会扫描当前组件并归档旧事务，不会重新执行修复。"
                        + "\n若关机中断留下缺少安装器身份的旧记录，可先“建立重启核对点”，保存工作并在方便时使用 Windows 的“重启”，然后回来核对。建立核对点保留全部阻塞，不会自动重启。";
                if (review.Module == DeltaNFD.Backends.BackendModules.Ace && !canKeep)
                    snapshotNote += "\n\nACE 当前状态尚未完成核对，不能归档旧清理记录；请退出游戏并等待旧清理进程结束后重试。";
                if (snapshot is { Complete: true } && scopes.Count == 0)
                    snapshotNote += canKeep ? "\n\n这条记录没有可按备份完成的可信恢复路径。请核对当前状态后再决定是否保留；不会重新执行旧请求。"
                        : "\n\n这条记录尚未提供可用恢复方案，当前不能保留状态并归档；请按上方步骤核对或反馈。";
                var content = new StackPanel { Spacing = 8 };
                AddAdvice(content, BackendRecoveryGuidance.ForReview(review, snapshot, readError));
                var frameAdvice = FrameFailureGuidance.ForReview(review);
                if (frameAdvice.Count > 0)
                    content.Children.Add(new Expander { Header = "帧格处理补充", HorizontalAlignment = HorizontalAlignment.Stretch,
                        Content = new TextBlock { Text = string.Join("\n\n", frameAdvice.Select(advice => advice.Explanation + "\n" + advice.Steps)),
                            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } });
                content.Children.Add(new Expander { Header = "原始记录与技术详情（可展开复制）", HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = new TextBlock { Text = review.Description, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } });
                content.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                    Text = (snapshot is null ? "" : snapshot.Summary + "\n") + snapshotNote +
                        "\n只有在你已核对该模块状态，并确认相关安装/修复程序也已结束后才能处理。" +
                        "「保留当前状态」不还原设置、不重新执行旧请求、不删除原值备份；原始记录会归档。",
                });
                AddDiagnostic(content, review, snapshot, readError);
                RadioButtons? scopePicker = null;
                if (scopes.Count > 1)
                {
                    scopePicker = new RadioButtons { Header = "选择要按备份恢复的范围" };
                    foreach (var scope in scopes) scopePicker.Items.Add($"{scope.Id}：{scope.Description}");
                    scopePicker.SelectedIndex = 0;
                    content.Children.Add(scopePicker);
                }

                var dialog = new ContentDialog
                {
                    XamlRoot = root, Title = "核对操作：" + BackendLifecycle.ModuleDisplayName(review.Module),
                    Content = new ScrollViewer { MaxHeight = 420, Content = content },
                    PrimaryButtonText = scopes.Count > 0 ? restartCheckpoint ? "建立重启核对点"
                        : installationReview ? "核对结束并恢复可用" : powerRecords.Count > 0 ? "处理选中范围" : "按可信备份恢复" : !canKeep ? "刷新并重新核对" : "",
                    SecondaryButtonText = canKeep ? "确认保留当前状态" : "",
                    CloseButtonText = "暂不处理", DefaultButton = scopes.Count > 0 && powerRecords.Count == 0 ? ContentDialogButton.Primary : ContentDialogButton.Close,
                };
                var choice = await dialog.ShowAsync();

                if (choice == ContentDialogResult.Primary && scopes.Count == 0 && !canKeep)
                { retry = true; break; }

                if (choice == ContentDialogResult.Primary && scopes.Count > 0)
                {
                    var scope = scopes[scopePicker?.SelectedIndex is int index && index >= 0 ? index : 0];
                    try
                    {
                        if (scope.Id == "power-preserve" && await new ContentDialog
                        {
                            XamlRoot = root, Title = "保留当前电源值？",
                            Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                                IsTextSelectionEnabled = true, Text = string.Join("\n\n", powerRecords.Select(p => p.Path + "\n" + p.Reason))
                                    + "\n\n仅将列出的无法恢复备份原样归档，保留当前电源值并关闭降低省电延迟；涉及内容快照时也会关闭电源锁定。不会把旧计划值写入新的平衡计划，也不会重建已删除计划。此步骤不算还原成功；随后必须完整恢复、复核其他帧格状态，全部通过才处理待核对记录。" } },
                            PrimaryButtonText = "确认保留并继续核对", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
                        }.ShowAsync() != ContentDialogResult.Primary) continue;
                        var outcome = await BackendRecovery.RestoreAsync(review, scope.Id == "power-preserve" ? "all" : scope.Id,
                            confirmedPower: scope.Id == "power-preserve" ? powerRecords : null);
                        var recoveryChoice = await new ContentDialog
                        {
                            XamlRoot = root, Title = restartCheckpoint && outcome.Success ? "重启核对点已建立"
                                : installationReview ? outcome.Verified ? "安装事务已核对，可重新修复" : "安装事务尚未完成核对"
                                : outcome.Verified ? "恢复已复核通过" : "恢复未通过复核",
                            Content = outcome.Verified || restartCheckpoint && outcome.Success
                                ? outcome.Message + (outcome.Verified ? "\n原始证据已归档；原值备份未删除。" : "\n阻塞与原始证据均已保留，请按提示完成重启核对。")
                                : UnresolvedContent(outcome.Message, review),
                            PrimaryButtonText = outcome.Verified || restartCheckpoint && outcome.Success ? "" : "重新核对",
                            CloseButtonText = "知道了",
                            DefaultButton = ContentDialogButton.Close,
                        }.ShowAsync();
                        retry = !outcome.Verified && recoveryChoice == ContentDialogResult.Primary;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("按备份恢复失败，证据保留", ex);
                        retry = await ShowRetryAsync(root, "未解除阻塞", ex.Message, review);
                    }
                    if (retry) break;
                    continue;
                }

                if (choice != ContentDialogResult.Secondary) continue;
                try
                {
                    var archived = await BackendLifecycle.AcknowledgeCurrentStateAsync(review);
                    await new ContentDialog
                    {
                        XamlRoot = root, Title = "核对记录已归档",
                        Content = "已按你的选择保留当前状态，不表示已还原。原值备份未删除。\n证据：" + archived,
                        CloseButtonText = "知道了",
                    }.ShowAsync();
                }
                catch (Exception ex)
                {
                    Log.Error("后端核对失败，证据保留", ex);
                    retry = await ShowRetryAsync(root, "未解除阻塞", ex.Message, review);
                }
                if (retry) break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("后端核对未能完成，原始证据保留", ex);
            retry = await ShowRetryAsync(root, "核对未能完成", ex.Message);
        }
        finally
        {
            // The review sequence itself explained the remaining blockers; do not immediately duplicate it.
            BackendAttention.Presented(BackendAttention.Snapshot().Where(p => modules is null || modules.Contains(p.Module)));
            Volatile.Write(ref _reviewing, 0);
            BackendAttention.NotifyUnshown();
        }
        return retry;
    }

    private static void AddAdvice(StackPanel content, BackendRecoveryAdvice advice)
    {
        content.Children.Add(new TextBlock { Text = advice.Title, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        content.Children.Add(new TextBlock { Text = advice.Explanation + "\n\n" + advice.Steps,
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
    }

    private static async Task<bool> ReviewCorruptRuntimeAsync(XamlRoot root, RuntimeCorruptRecordReview.State state)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = state.Detail + "\n\n损坏原件会逐字保留。保留当前状态不证明安装成功，也不还原 DLL；独立安装事务、防护与替换备份继续保留。",
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var dialog = new ContentDialog { XamlRoot = root, Title = "运行库操作记录损坏",
            Content = new ScrollViewer { MaxHeight = 420, Content = content },
            PrimaryButtonText = state.CanPrepare ? "建立重启核对点" : state.CanPreserve ? "确认保留当前状态" : "刷新并重新核对",
            CloseButtonText = "暂不处理", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        if (!state.CanPrepare && !state.CanPreserve) return true;
        try
        {
            var message = state.CanPrepare ? RuntimeCorruptRecordReview.Prepare(state)
                : "损坏原件已归档，未认定安装或还原成功。请到运行库页重新检测；独立事务可能仍需核对。\n证据：" + RuntimeCorruptRecordReview.Preserve(state);
            if (!state.CanPrepare) await BackendLifecycle.ReleaseRecoveredSessionsAsync(DeltaNFD.Backends.BackendModules.Runtime);
            await new ContentDialog { XamlRoot = root, Title = state.CanPrepare ? "核对点已建立" : "已保留当前状态",
                Content = message, CloseButtonText = "知道了" }.ShowAsync();
        }
        catch (Exception ex) { return await ShowRetryAsync(root, "未解除阻塞", ex.Message); }
        return false;
    }

    private static void AddDiagnostic(StackPanel content, BackendUnconfirmedStore.Review? review,
        ReconcileSnapshot? snapshot, string error)
    {
        var diagnostic = BackendRecoveryGuidance.Diagnostic(review, snapshot, error);
        content.Children.Add(new Expander { Header = "诊断摘要预览（已处理常见路径和凭据）", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new TextBox { Text = diagnostic, IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, MaxHeight = 220 } });
        var status = new TextBlock { Text = "复制后可粘贴到反馈中；请预览后决定是否发送。", TextWrapping = TextWrapping.Wrap };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(status,
            Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var copy = new Button { Content = "复制诊断摘要" };
        copy.Click += (_, _) =>
        {
            try
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                data.SetText(diagnostic);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                status.Text = "诊断摘要已复制。可关闭此窗口，到“设置 → 反馈 BUG”粘贴并附带本次日志；尚未发送。";
            }
            catch (Exception ex)
            {
                Log.Warn("复制后端诊断摘要失败：" + ex.GetType().Name);
                status.Text = "复制失败。请展开“诊断摘要预览”，选中文字后手动复制；原操作记录仍保留。";
            }
        };
        content.Children.Add(copy);
        content.Children.Add(status);
    }

    private static ScrollViewer UnresolvedContent(string message, BackendUnconfirmedStore.Review? review = null)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = message + "\n原始证据与阻塞仍保留。", TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true });
        AddAdvice(content, review is null ? BackendRecoveryGuidance.ForUnavailable()
            : BackendRecoveryGuidance.ForReview(review, null, message));
        AddDiagnostic(content, review, null, message);
        return new ScrollViewer { MaxHeight = 420, Content = content };
    }

    private static async Task<bool> ShowRetryAsync(XamlRoot root, string title, string message,
        BackendUnconfirmedStore.Review? review = null) =>
        await new ContentDialog
        {
            XamlRoot = root, Title = title,
            Content = UnresolvedContent(message, review),
            PrimaryButtonText = "重新核对", CloseButtonText = "暂不处理", DefaultButton = ContentDialogButton.Close,
        }.ShowAsync() == ContentDialogResult.Primary;
}
