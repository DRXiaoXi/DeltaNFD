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
                // 原进程已确认退出时，做一次只读状态核对，并向玩家提供可用恢复范围。
                ReconcileSnapshot? snapshot = null;
                var snapshotNote = "";
                if (review.CanRestore)
                {
                    try { snapshot = await BackendRecovery.ReconcileAsync(review.Module,
                        scope: BackendRecovery.IsRuntimeInstallation(review) ? "repair-review" : null); }
                    catch (Exception ex) { snapshotNote = "\n\n只读状态核对未能完成：" + ex.Message; }
                }
                var scopes = BackendRecovery.RecoveryChoices(review, snapshot);
                var canKeep = BackendRecovery.CanKeepCurrentState(review, snapshot);
                var installationReview = BackendRecovery.IsRuntimeInstallation(review);
                if (installationReview)
                    snapshotNote += "\n\n安装/系统修复不能按防护备份回滚。请等待安装进程结束，再点击“核对结束并恢复可用”；会扫描当前组件并归档旧事务，不会重新执行修复。";
                if (review.Module == DeltaNFD.Backends.BackendModules.Ace && !canKeep)
                    snapshotNote += "\n\nACE 当前状态尚未完成核对，不能归档旧清理记录；请退出游戏并等待旧清理进程结束后重试。";
                if (snapshot is { Complete: true } && scopes.Count == 0)
                    snapshotNote += "\n\n这条记录没有可按备份完成的可信恢复路径。请核对当前状态后再决定是否保留；不会重新执行旧请求。";
                var content = new StackPanel { Spacing = 8 };
                content.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                    Text = review.Description + "\n\n" + (snapshot is null ? "" : snapshot.Summary + "\n") + snapshotNote +
                        "\n只有在你已核对该模块状态，并确认相关安装/修复程序也已结束后才能处理。" +
                        "「保留当前状态」不还原设置、不重新执行旧请求、不删除原值备份；原始记录会归档。",
                });
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
                    PrimaryButtonText = scopes.Count > 0 ? installationReview ? "核对结束并恢复可用" : "按可信备份恢复" : !canKeep ? "刷新并重新核对" : "",
                    SecondaryButtonText = canKeep ? "确认保留当前状态" : "",
                    CloseButtonText = "暂不处理", DefaultButton = scopes.Count > 0 ? ContentDialogButton.Primary : ContentDialogButton.Close,
                };
                var choice = await dialog.ShowAsync();

                if (choice == ContentDialogResult.Primary && scopes.Count == 0 && !canKeep)
                { retry = true; break; }

                if (choice == ContentDialogResult.Primary && scopes.Count > 0)
                {
                    var scope = scopes[scopePicker?.SelectedIndex is int index && index >= 0 ? index : 0];
                    try
                    {
                        var outcome = await BackendRecovery.RestoreAsync(review, scope.Id);
                        var recoveryChoice = await new ContentDialog
                        {
                            XamlRoot = root, Title = installationReview ? outcome.Verified ? "安装事务已核对，可重新修复" : "安装事务尚未完成核对"
                                : outcome.Verified ? "恢复已复核通过" : "恢复未通过复核",
                            Content = outcome.Message + (outcome.Verified ? "\n原始证据已归档；原值备份未删除。" : "\n阻塞与原始证据均已保留，可稍后重试。"),
                            PrimaryButtonText = outcome.Verified ? "" : "重新核对",
                            CloseButtonText = "知道了",
                            DefaultButton = ContentDialogButton.Close,
                        }.ShowAsync();
                        retry = !outcome.Verified && recoveryChoice == ContentDialogResult.Primary;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("按备份恢复失败，证据保留", ex);
                        retry = await ShowRetryAsync(root, "未解除阻塞", ex.Message);
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
                    retry = await ShowRetryAsync(root, "未解除阻塞", ex.Message);
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

    private static async Task<bool> ShowRetryAsync(XamlRoot root, string title, string message) =>
        await new ContentDialog
        {
            XamlRoot = root, Title = title,
            Content = message + "\n原始证据仍保留。若相关安装程序尚未结束，请先等待；结束后可直接重新核对。",
            PrimaryButtonText = "重新核对", CloseButtonText = "暂不处理", DefaultButton = ContentDialogButton.Close,
        }.ShowAsync() == ContentDialogResult.Primary;
}
