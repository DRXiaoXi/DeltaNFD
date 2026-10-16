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
        try
        {
            var title = new TextBox { Header = "问题标题", MaxLength = 100, PlaceholderText = "例如：运行库页点击刷新后没有结果" };
            var area = new ComboBox { Header = "问题所在模块", ItemsSource = BugFeedbackService.Areas, SelectedIndex = BugFeedbackService.Areas.Length - 1, HorizontalAlignment = HorizontalAlignment.Stretch };
            var description = new TextBox { Header = "问题描述与复现步骤", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, MaxLength = 8000,
                PlaceholderText = "请写明：操作了什么、实际出现什么、希望得到什么结果；发生时间或错误码也请填写。" };
            var contact = new TextBox { Header = "联系方式（选填）", MaxLength = 200, PlaceholderText = "QQ 或邮箱，便于补充询问；可以留空" };
            var includeLogs = new CheckBox { Content = "附带最近最多 3 份工具应用日志（每份最多末尾 256 KiB）", IsChecked = true };
            var validation = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var form = new StackPanel { Spacing = 12, Children = { title, area, description, contact, includeLogs, validation } };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "反馈问题", Content = new ScrollViewer { MaxHeight = 470, Content = form },
                PrimaryButtonText = "生成预览", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            BugFeedbackInput Input() => new(title.Text, (string)area.SelectedItem, description.Text, contact.Text, includeLogs.IsChecked == true);
            dialog.PrimaryButtonClick += (_, args) => { validation.Text = BugFeedbackService.Validate(Input()); args.Cancel = validation.Text.Length > 0; };
            while (await dialog.ShowWithBackendRecoveryAsync() == ContentDialogResult.Primary)
            {
                FeedbackStatusText.Text = "正在生成本地反馈预览，尚未发送…";
                var input = Input();
                var package = await Task.Run(() => BugFeedbackService.Prepare(input));
                var consent = new CheckBox { Content = "我已预览以上内容，同意将其发送给工具作者。", IsChecked = false };
                var preview = new ContentDialog { XamlRoot = XamlRoot, Title = $"确认发送反馈（{package.LogCount} 份日志）", PrimaryButtonText = "发送反馈", CloseButtonText = "返回修改",
                    DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false,
                    Content = new StackPanel { Spacing = 12, Children = {
                        new TextBlock { Text = "接收方：deltaxiaoxi.xin（工具作者）。日志已处理常见用户名路径和凭据，仍可能包含进程名称或其他路径，请预览后决定。不会上传系统文件、配置备份或截图。服务端默认保留 30 天，联系方式仅用于处理此反馈。", TextWrapping = TextWrapping.Wrap },
                        new TextBox { Text = package.Preview, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 300 }, consent } } };
                consent.Checked += (_, _) => preview.IsPrimaryButtonEnabled = true;
                consent.Unchecked += (_, _) => preview.IsPrimaryButtonEnabled = false;
                if (await preview.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary) continue;
                _pendingFeedback = package;
                await UploadFeedbackAsync();
                return;
            }
            FeedbackStatusText.Text = "已取消，未发送新的反馈。";
        }
        catch (Exception ex) { FeedbackStatusText.Text = "无法准备反馈：" + ex.Message; }
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
        CancelFeedbackButton.Visibility = Visibility.Visible;
        FeedbackProgressRing.IsActive = true;
        FeedbackStatusText.Text = $"正在发送反馈（{_pendingFeedback.Archive.Length / 1024.0:F1} KiB），请等待服务器回执…";
        try
        {
            var result = await BugFeedbackService.SubmitAsync(_pendingFeedback, cancellation.Token);
            FeedbackStatusText.Text = result.Message;
            if (result.Success) _pendingFeedback = null;
        }
        finally { _feedbackCancellation = null; CancelFeedbackButton.Visibility = Visibility.Collapsed; FeedbackProgressRing.IsActive = false; }
    }

    private void CancelBugFeedback_Click(object sender, RoutedEventArgs e) => _feedbackCancellation?.Cancel();
    private void SetFeedbackBusy(bool busy)
    {
        _feedbackBusy = busy;
        BugFeedbackButton.IsEnabled = !busy;
        RetryFeedbackButton.IsEnabled = !busy;
        RetryFeedbackButton.Visibility = _pendingFeedback is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
