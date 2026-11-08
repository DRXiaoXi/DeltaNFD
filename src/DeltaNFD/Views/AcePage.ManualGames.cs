using DeltaNFD.Inspection;
using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class AcePage
{
    private bool ManualGameAdmissionAvailable() => Volatile.Read(ref _pageLoaded) && _pageOwner is { IsAvailable: true }
        && !OfflineModeGuard.BlocksNormalAutomation && Services.Backends.PageBackendSessions.Manager.DeltaRunning == false;

    private async void AddManualGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanManageManualRoots)
        { ViewModel.ManualRootsStatusText = "ACE 检测或其他操作正在进行，请等待完成后再添加。"; return; }
        if (!ManualGameAdmissionAvailable())
        { ViewModel.ManualRootsStatusText = "ACE 栏目正在收尾或三角洲运行状态尚未确认，请稍后重新进入。"; return; }
        var folder = ViewModel.ManualGamePath.Trim().Trim('"');
        if (folder.Length == 0)
        { ViewModel.ManualRootsStatusText = "请先粘贴具体游戏的安装目录，或点击「浏览」选择，再添加并检索。"; return; }
        ViewModel.ManualRootsBusy = true;
        ViewModel.ManualRootsStatusText = "正在等待并核对所填目录…";
        try
        {
            var token = _pageOwner!.QueueToken;
            using var admission = await _pageOwner.BeginStartAsync(token);
            token.ThrowIfCancellationRequested();
            if (!ManualGameAdmissionAvailable()) return;
            ViewModel.ManualRootsStatusText = "正在核对所选目录…";
            var validation = await InspectionScheduler.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!ManualGameAdmissionAvailable()) throw new OperationCanceledException();
                var valid = AceManualGameInspection.ValidateRoot(folder, out var path, out var reason);
                return (valid, path, reason);
            });
            token.ThrowIfCancellationRequested();
            if (!ManualGameAdmissionAvailable()) return;
            if (!validation.valid) { ViewModel.ManualRootsStatusText = validation.reason; return; }
            var result = await Task.Run(() => AceGameSearchSettings.Add(validation.path, token, ManualGameAdmissionAvailable));
            if (!ManualGameAdmissionAvailable() || token.IsCancellationRequested) return;
            ViewModel.ManualRootsStatusText = result.Message;
            if (!result.Success || !ViewModel.RefreshManualGameRoots()) return;
            await ViewModel.CheckCoreFilesAsync();
            if (ManualGameAdmissionAvailable())
                ViewModel.ManualRootsStatusText = "目录已保存。" + ViewModel.CoreCheckStatusText + " 详情见下方检测明细。";
        }
        catch (OperationCanceledException) { ViewModel.ManualRootsStatusText = "栏目已离开或游戏状态变化，已停止添加目录。"; }
        catch (Exception ex)
        {
            Log.Error("手动添加 ACE 游戏目录失败", ex);
            ViewModel.ManualRootsStatusText = "添加或检索未完成：" + ex.Message;
        }
        finally { ViewModel.ManualRootsBusy = false; }
    }

    private void BrowseManualGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanManageManualRoots || !ManualGameAdmissionAvailable())
        { ViewModel.ManualRootsStatusText = "ACE 当前不可添加目录，请等待检测结束并确认三角洲已退出。"; return; }
        if (App.MainWindow is null)
        { ViewModel.ManualRootsStatusText = "主窗口尚未就绪，可先在输入框中粘贴目录。"; return; }
        ViewModel.ManualRootsBusy = true;
        try
        {
            ViewModel.ManualRootsStatusText = "正在打开文件夹选择器；也可直接粘贴目录。";
            var owner = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            var folder = Native.FolderPickerDialog.Pick(owner, ViewModel.ManualGamePath);
            if (string.IsNullOrWhiteSpace(folder))
            { ViewModel.ManualRootsStatusText = "已取消选择，未添加目录。"; return; }
            if (!ManualGameAdmissionAvailable())
            { ViewModel.ManualRootsStatusText = "栏目或游戏状态已变化，未添加目录。"; return; }
            ViewModel.ManualGamePath = folder;
            ViewModel.ManualRootsStatusText = "已选目录，请点击「添加游戏目录并检索」。";
        }
        catch (Exception ex)
        {
            Log.Error("ACE 游戏目录选择器未能打开", ex);
            ViewModel.ManualRootsStatusText = "文件夹选择器不可用，可直接粘贴目录后添加：" + ex.Message;
        }
        finally { ViewModel.ManualRootsBusy = false; }
    }

    private async void RemoveManualGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string path } || !ViewModel.CanManageManualRoots) return;
        if (!ManualGameAdmissionAvailable())
        { ViewModel.ManualRootsStatusText = "ACE 栏目正在收尾或三角洲运行状态尚未确认，未移除目录。"; return; }
        ViewModel.ManualRootsBusy = true;
        try
        {
            var token = _pageOwner!.QueueToken;
            using var admission = await _pageOwner.BeginStartAsync(token);
            await Task.Run(() => AceGameSearchSettings.Remove(path, token, ManualGameAdmissionAvailable));
            if (!ManualGameAdmissionAvailable() || token.IsCancellationRequested) return;
            if (!ViewModel.RefreshManualGameRoots()) return;
            ViewModel.ManualRootsStatusText = "已从检索列表移除，没有删除游戏文件。";
            await ViewModel.CheckCoreFilesAsync();
        }
        catch (OperationCanceledException) { ViewModel.ManualRootsStatusText = "栏目已离开或游戏状态变化，未继续修改检索目录。"; }
        catch (Exception ex) { ViewModel.ManualRootsStatusText = "移除检索目录失败：" + ex.Message; Log.Error("移除 ACE 手动检索目录失败", ex); }
        finally { ViewModel.ManualRootsBusy = false; }
    }
}
