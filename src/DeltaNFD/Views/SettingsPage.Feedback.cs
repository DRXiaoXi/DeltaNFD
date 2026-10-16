using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class SettingsPage
{
    private bool _feedbackBusy;
    private BugFeedbackPackage? _pendingFeedback;
    private CancellationTokenSource? _feedbackCancellation;

    private async void SendBugFeedback_Click(object sender, RoutedEventArgs e)
    {
        if (_feedbackBusy) return;
        SetFeedbackBusy(true);
        FeedbackResultBar.IsOpen = false;
        FeedbackStatusText.Text = "";
        try
        {
            var form = new BugFeedbackForm();
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "反馈问题", Content = form,
                PrimaryButtonText = "生成预览", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            dialog.PrimaryButtonClick += (_, args) => args.Cancel = !form.ValidateAndFocus();
            while (await dialog.ShowWithBackendRecoveryAsync() == ContentDialogResult.Primary)
            {
                FeedbackStatusText.Text = "正在生成本地反馈预览，尚未发送…";
                var input = form.ReadInput();
                BugFeedbackPackage package;
                try { package = await Task.Run(() => BugFeedbackService.Prepare(input)); }
                catch (Exception ex)
                {
                    Log.Error("生成反馈预览失败", ex);
                    FeedbackStatusText.Text = "反馈预览未生成，尚未发送。请在弹窗中核对原因后重试。";
                    form.ShowPreparationError(ex.Message + "\n可取消勾选附带日志后重试；已填写的内容会保留。");
                    continue;
                }
                var consent = new CheckBox { Content = "我已预览以上内容，同意将其发送给工具作者。", IsChecked = false };
                var preview = new ContentDialog { XamlRoot = XamlRoot, Title = $"确认发送反馈（{package.LogCount} 份日志）", PrimaryButtonText = "发送反馈", CloseButtonText = "返回修改",
                    DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false,
                    Content = new StackPanel { Spacing = 12, Children = {
                        new TextBlock { Text = "接收方：deltaxiaoxi.xin（工具作者）。日志已处理常见用户名路径和凭据，仍可能包含进程名称或其他路径，请预览后决定。不会上传系统文件、配置备份或截图。服务端默认保留 30 天，联系方式仅用于处理此反馈。", TextWrapping = TextWrapping.Wrap },
                        new TextBox { Text = package.Preview, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 300 },
                        new TextBlock { Text = "请预览内容，并勾选下方同意选项后发送。", TextWrapping = TextWrapping.Wrap }, consent } } };
                consent.Checked += (_, _) => preview.IsPrimaryButtonEnabled = true;
                consent.Unchecked += (_, _) => preview.IsPrimaryButtonEnabled = false;
                if (await preview.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary) continue;
                _pendingFeedback = package;
                await UploadFeedbackAsync();
                return;
            }
            FeedbackStatusText.Text = "已取消，未发送新的反馈。";
        }
        catch (Exception ex)
        {
            Log.Error("无法打开或准备反馈", ex);
            ShowFeedbackResult("无法准备反馈", ex.Message + "\n请点击“反馈 BUG”重新打开后再试。", false);
        }
        finally { SetFeedbackBusy(false); }
    }

    private async void RetryBugFeedback_Click(object sender, RoutedEventArgs e)
    {
        if (_feedbackBusy || _pendingFeedback is null) return;
        SetFeedbackBusy(true);
        try { await UploadFeedbackAsync(); }
        finally { SetFeedbackBusy(false); }
    }

    private async Task UploadFeedbackAsync()
    {
        if (_pendingFeedback is null) return;
        using var cancellation = new CancellationTokenSource();
        _feedbackCancellation = cancellation;
        FeedbackResultBar.IsOpen = false;
        CancelFeedbackButton.Visibility = Visibility.Visible;
        FeedbackProgressRing.IsActive = true;
        FeedbackStatusText.Text = $"正在发送反馈（{_pendingFeedback.Archive.Length / 1024.0:F1} KiB），请等待服务器回执…";
        try
        {
            var result = await BugFeedbackService.SubmitAsync(_pendingFeedback, cancellation.Token);
            ShowFeedbackResult(result.Success ? "反馈已收到" : "反馈未完成", result.Message, result.Success);
            if (result.Success) _pendingFeedback = null;
        }
        catch (Exception ex)
        {
            Log.Error("反馈发送未完成", ex);
            ShowFeedbackResult("反馈未完成", "发送时发生异常，尚未确认服务器是否收到。请点击“重试上次反馈”，会沿用同一编号核对。", false);
        }
        finally { _feedbackCancellation = null; CancelFeedbackButton.Visibility = Visibility.Collapsed; FeedbackProgressRing.IsActive = false; }
    }

    private void CancelBugFeedback_Click(object sender, RoutedEventArgs e) => _feedbackCancellation?.Cancel();

    private void ShowFeedbackResult(string title, string message, bool success)
    {
        FeedbackStatusText.Text = "";
        FeedbackResultBar.Title = title;
        FeedbackResultBar.Message = message;
        FeedbackResultBar.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        FeedbackResultBar.IsOpen = true;
        FeedbackResultBar.StartBringIntoView();
    }
    private void SetFeedbackBusy(bool busy)
    {
        _feedbackBusy = busy;
        BugFeedbackButton.IsEnabled = !busy;
        RetryFeedbackButton.IsEnabled = !busy;
        RetryFeedbackButton.Visibility = _pendingFeedback is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
