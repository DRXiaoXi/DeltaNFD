using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

/// <summary>设置页与帧格总开关共享的核对、恢复与证据归档入口。</summary>
internal static class BackendReviewFlow
{
    private static int _reviewing;
    internal static async Task RunAsync(XamlRoot root, IReadOnlyCollection<string>? modules = null)
    {
        if (Interlocked.CompareExchange(ref _reviewing, 1, 0) != 0) return;
        using var blocked = BackendOperationGate.BlockNewOperations();
        using var control = BackendOperationGate.AllowControlOperations();
        try
        {
            var reviews = await BackendLifecycle.PrepareReviewAsync(modules);
            if (reviews.Count == 0)
            {
                await new ContentDialog { XamlRoot = root, Title = "未确认操作", Content = "没有待核对的官方后端记录。", CloseButtonText = "知道了" }.ShowAsync();
                return;
            }
            foreach (var review in reviews)
            {
                // 原进程已确认退出时，做一次只读状态核对，并向玩家提供可用恢复范围。
                ReconcileSnapshot? snapshot = null;
                var snapshotNote = "";
                if (review.CanRestore)
                {
                    try { snapshot = await BackendRecovery.ReconcileAsync(review.Module); }
                    catch (Exception ex) { snapshotNote = "\n\n只读状态核对未能完成：" + ex.Message; }
                }
                var scopes = BackendRecovery.RecoveryChoices(review, snapshot);
                var canKeep = BackendRecovery.CanKeepCurrentState(review, snapshot);
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
                    PrimaryButtonText = scopes.Count > 0 ? "按可信备份恢复" : "",
                    SecondaryButtonText = canKeep ? "确认保留当前状态" : "",
                    CloseButtonText = "暂不处理", DefaultButton = scopes.Count > 0 ? ContentDialogButton.Primary : ContentDialogButton.Close,
                };
                var choice = await dialog.ShowAsync();

                if (choice == ContentDialogResult.Primary && scopes.Count > 0)
                {
                    var scope = scopes[scopePicker?.SelectedIndex is int index && index >= 0 ? index : 0];
                    try
                    {
                        var outcome = await BackendRecovery.RestoreAsync(review, scope.Id);
                        await new ContentDialog
                        {
                            XamlRoot = root, Title = outcome.Verified ? "恢复已复核通过" : "恢复未通过复核",
                            Content = outcome.Message + (outcome.Verified ? "\n原始证据已归档；原值备份未删除。" : "\n阻塞与原始证据均已保留，可稍后重试。"),
                            CloseButtonText = "知道了",
                        }.ShowAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("按备份恢复失败，证据保留", ex);
                        await new ContentDialog { XamlRoot = root, Title = "未解除阻塞", Content = ex.Message, CloseButtonText = "知道了" }.ShowAsync();
                    }
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
                    await new ContentDialog { XamlRoot = root, Title = "未解除阻塞", Content = ex.Message, CloseButtonText = "知道了" }.ShowAsync();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("后端核对未能完成，原始证据保留", ex);
            await new ContentDialog { XamlRoot = root, Title = "核对未能完成",
                Content = ex.Message + "\n旧记录仍保留，请稍后重试。", CloseButtonText = "知道了" }.ShowAsync();
        }
        finally { Volatile.Write(ref _reviewing, 0); }
    }
}
