using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class RuntimePage
{
    private async void CleanupRegistrations_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.GuardCleanupEnabled || ViewModel.RepairIsBusy) return;
        ViewModel.RepairIsBusy = true;
        using var cancellation = new CancellationTokenSource();
        _repairCancellation = cancellation;
        try
        {
            ViewModel.RepairStatusText = "正在核对旧显示登记与 Windows Installer 产品状态…";
            var preview = await ViewModel.PreviewRepairAsync(Vc14RepairMode.RegistrationCleanup);
            if (!preview.CanRepair) { ViewModel.RepairStatusText = preview.Blocker; return; }
            var text = "仅清理以下已确认失效的 C++ 14.x 显示登记，每项先备份再删除。已安装的运行库保留，无需重新卸载或安装，也不会修改系统 DLL、注册表权限或 Installer 组件数据库。\n\n"
                + string.Join("\n\n", preview.RemovalPlan.Select(p => p.Name + "\n" + p.Hive + " / " + p.View + " / " + p.Key))
                + "\n\n无法确认的登记会保留；状态变化时停止并要求重新预览。";
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "确认仅清理失效显示登记？", PrimaryButtonText = "备份并清理", CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
            };
            if (await dialog.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner) != ContentDialogResult.Primary || cancellation.IsCancellationRequested)
            { ViewModel.RepairStatusText = "已取消登记清理。"; return; }
            var result = await ViewModel.RepairAsync(preview.Fingerprint, cancellation.Token, Vc14RepairMode.RegistrationCleanup);
            ViewModel.RepairStatusText = result.Message;
            await new ContentDialog { XamlRoot = XamlRoot, Title = result.Success ? "显示登记清理完成" : "显示登记清理未完成",
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = result.Message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
        }
        catch (Exception ex) { ViewModel.RepairStatusText = "登记清理未完成：" + ex.Message; await ShowRuntimeExceptionAsync(ex); }
        finally { _repairCancellation = null; ViewModel.RepairIsBusy = false; if (!_startupExitPending) await ViewModel.LoadGuardAsync(); }
    }
}
