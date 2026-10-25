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
        if (!ViewModel.CanManageManualRoots) return;
        if (!ManualGameAdmissionAvailable())
        { ViewModel.ManualRootsStatusText = "ACE 栏目正在收尾或三角洲运行状态尚未确认，请稍后重新进入。"; return; }
        ViewModel.ManualRootsBusy = true;
        try
        {
            var token = _pageOwner!.QueueToken;
            var owner = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!);
            var folder = Native.FolderPickerDialog.Pick(owner, null);
            if (string.IsNullOrWhiteSpace(folder))
            { ViewModel.ManualRootsStatusText = "已取消选择，未添加目录。"; return; }
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
            if (ManualGameAdmissionAvailable()) ViewModel.ManualRootsStatusText = "目录已保存；检索结果及未完成原因见下方检测明细。";
        }
        catch (OperationCanceledException) { ViewModel.ManualRootsStatusText = "栏目已离开或游戏状态变化，已停止添加目录。"; }
        catch (Exception ex)
        {
            Log.Error("手动添加 ACE 游戏目录失败", ex);
            ViewModel.ManualRootsStatusText = "添加或检索未完成：" + ex.Message;
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
