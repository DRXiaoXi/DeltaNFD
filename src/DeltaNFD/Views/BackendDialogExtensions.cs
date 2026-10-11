using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

/// <summary>所有产品弹窗统一排队；被动结果弹窗直接提供恢复入口，确认弹窗的选择语义不变。</summary>
internal static class BackendDialogExtensions
{
    private static readonly SemaphoreSlim DialogGate = new(1, 1);

    internal static Task<ContentDialogResult> ShowWithBackendRecoveryAsync(this ContentDialog dialog, bool hasReviewAction = false) => ShowAsync(dialog, false, hasReviewAction);

    internal static async Task<IDisposable> EnterReviewAsync()
    {
        await DialogGate.WaitAsync();
        return new ReviewLease();
    }
    private sealed class ReviewLease : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) DialogGate.Release(); }
    }

    private static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, bool unsolicited, bool hasReviewAction = false)
    {
        // Review dialogs have their own sequence. Do not interrupt a recovery confirmation/result.
        while (true)
        {
            while (BackendReviewFlow.IsReviewing) await Task.Delay(100);
            await DialogGate.WaitAsync();
            if (!BackendReviewFlow.IsReviewing) break;
            DialogGate.Release();
        }
        IReadOnlyList<BackendAttentionItem> pending = [];
        bool offersReview = false;
        ContentDialogResult result;
        try
        {
            pending = BackendAttention.Snapshot(onlyUnshown: unsolicited);
            if (unsolicited && (pending.Count == 0 || BackendReviewFlow.IsReviewing)) return ContentDialogResult.None;
            offersReview = pending.Count > 0 && string.IsNullOrEmpty(dialog.PrimaryButtonText)
                && string.IsNullOrEmpty(dialog.SecondaryButtonText) && dialog.XamlRoot is not null;
            if (offersReview)
            {
                var content = new StackPanel { Spacing = 12 };
                var original = dialog.Content;
                dialog.Content = null;
                if (original is ScrollViewer { Content: UIElement inner } viewer)
                { viewer.Content = null; content.Children.Add(inner); }
                else if (original is UIElement element) content.Children.Add(element);
                else content.Children.Add(new TextBlock { Text = original?.ToString() ?? "", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                content.Children.Add(new TextBlock
                {
                    Text = "以下模块的操作结果尚未确认：" + string.Join("、", pending.Select(p => BackendLifecycle.ModuleDisplayName(p.Module)))
                        + "。可直接点击“立即核对与恢复”，先安全停止原后端并核对实际状态，再选择按可信备份恢复或确认保留当前状态。"
                        + "若安装程序仍在运行，请等待其结束；暂不处理会保留证据与阻塞。",
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                });
                dialog.Content = new ScrollViewer { MaxHeight = 440, Content = content };
                dialog.PrimaryButtonText = "立即核对与恢复";
                dialog.DefaultButton = ContentDialogButton.Close;
            }
            // Mark only when the dialog has really opened; a failed ShowAsync must not swallow notification.
            void Opened(ContentDialog _, ContentDialogOpenedEventArgs __)
            { if (offersReview || hasReviewAction) BackendAttention.Presented(pending); }
            dialog.Opened += Opened;
            try { result = await dialog.ShowAsync(); }
            finally { dialog.Opened -= Opened; }
        }
        finally { DialogGate.Release(); }
        if (offersReview && result == ContentDialogResult.Primary)
        {
            await BackendReviewFlow.RunAsync(dialog.XamlRoot, pending.Select(p => p.Module).ToArray());
            // Original passive dialogs had no affirmative action. Never turn recovery into approval/reboot.
            return ContentDialogResult.None;
        }
        return result;
    }

    internal static async Task ShowUnconfirmedAsync(XamlRoot root)
    {
        if (BackendReviewFlow.IsReviewing) return;
        await ShowAsync(new ContentDialog
        {
            XamlRoot = root, Title = "后端操作结果未确认",
            Content = "后台操作发生异常，当前不能认定系统修改或还原已完成。请核对后再继续使用相关功能。",
            CloseButtonText = "暂不处理", DefaultButton = ContentDialogButton.Close,
        }, unsolicited: true);
    }
}
