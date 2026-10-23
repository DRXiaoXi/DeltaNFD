using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class GpuSpoofPage
{
    private readonly IGpuSpoofService _pageSpoofRecovery = ServiceLocator.GpuSpoof;
    private async void CompletePendingRestore_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        ViewModel.IsBusy = true;
        try
        {
            var preview = await GpuSpoofPendingRecovery.PreviewAsync(_pageSpoofRecovery);
            if (preview is null) { ViewModel.ShowResult(OperationResult.Ok("没有临时显卡伪装待还原记录。")); return; }
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "确认完成临时还原收尾？",
                Content = "已核对待还原目标：" + preview.Name + "\n" + preview.Pending.Path
                    + "\n\n当前为原生驱动描述，且没有本工具的型号备份。仅清除失效的待还原记录，不修改显卡型号或驱动。",
                PrimaryButtonText = "确认完成收尾", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner) != ContentDialogResult.Primary) return;
            await GpuSpoofPendingRecovery.CompleteAsync(_pageSpoofRecovery, preview);
            var cleanup = ViewModel.Frame.FrameModeEnabled ? OperationResult.Ok("") : await ViewModel.Frame.DisableLogonAutostartAsync();
            ViewModel.ShowResult(OperationResult.Ok("临时还原记录已完成收尾，可以重新使用显卡伪装。"
                + (cleanup.Success ? "" : "登录任务清理未完成：" + cleanup.Message)));
        }
        catch (Exception ex) { Log.Error("临时显卡还原收尾未完成", ex); ViewModel.ShowResult(OperationResult.Fail(ex.Message)); }
        finally { ViewModel.IsBusy = false; await ViewModel.RefreshAsync(); }
    }
}
