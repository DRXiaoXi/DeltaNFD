using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;
public sealed partial class TimerRecoveryPage : Page
{
    private readonly PageBackendLease? _owner=PageBackendSessions.Manager.Current;
    private readonly BackendCaller _caller=new(BackendModules.System,PageBackendSessions.Manager.Current);
    private TimerRecoveryStatus? _status;
    private bool _busy;
    public TimerRecoveryPage()
    {
        InitializeComponent();
        Loaded+=async (_,_)=>await RunAsync(RefreshAsync);
    }
    private async Task RunAsync(Func<Task> work)
    {
        if (_busy) return;
        _busy=true;RefreshButton.IsEnabled=RecoverButton.IsEnabled=RestoreDevices.IsEnabled=false;
        try { await work(); }
        catch (Exception ex) { StatusBar.Title="未完成，请重新检测";StatusBar.Message=ex.Message;StatusBar.Severity=InfoBarSeverity.Error; }
        finally
        {
            _busy=false;RefreshButton.IsEnabled=true;
            RecoverButton.IsEnabled=_status?.BcdReadable==true && PageBackendSessions.SystemWriteBlocker is null;
            RestoreDevices.IsEnabled=_status?.DevicesReadable==true && _status.Devices.Any(d=>d.Disabled);
        }
    }
    private async Task RefreshAsync()
    {
        _status=await TimerHealthCache.ReadAsync(true);
        if (!ReferenceEquals(Frame?.Content,this)) return;
        CurrentText.Text="disabledynamictick："+(!_status.BcdReadable ? "不可读" : _status.DynamicTick.Length==0 ? "未配置" : _status.DynamicTick)
            +"\nuseplatformtick："+(!_status.BcdReadable ? "不可读" : _status.PlatformTick.Length==0 ? "未配置" : _status.PlatformTick)
            +"\n"+(_status.DevicesReadable ? (_status.Devices.Count==0 ? "未发现 HPET 设备。" : string.Join("\n",_status.Devices.Select(d=>d.InstanceId+" · "+(d.Disabled ? "已禁用" : "问题码 "+d.ProblemCode)))) : "HPET 设备状态不可读")
            +(_status.Error.Length>0 ? "\n"+_status.Error : "");
        var notice=TimerHealthPresentation.Create(_status);
        StatusBar.Title=notice.Title;
        StatusBar.Severity=notice.IsError ? InfoBarSeverity.Error : notice.IsOpen ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        StatusBar.Message=notice.Message+(PageBackendSessions.SystemWriteBlocker is { } blocker ? "\n"+blocker : "");
    }
    private async void Refresh_Click(object sender,RoutedEventArgs e)=>await RunAsync(RefreshAsync);
    private void Back_Click(object sender,RoutedEventArgs e) { if (!_busy) App.MainWindow?.NavigateByTag("dashboard"); }
    private async void Recover_Click(object sender,RoutedEventArgs e)=>await RunAsync(async ()=>
    {
        if (PageBackendSessions.SystemWriteBlocker is { } blocked) throw new InvalidOperationException(blocked);
        var restoreDevices=RestoreDevices.IsChecked==true;
        await RefreshAsync();
        if (_status?.BcdReadable!=true) throw new InvalidOperationException("无法读取启动配置，未执行恢复。");
        var selected=_status;
        if (await new ContentDialog { XamlRoot=XamlRoot,Title="恢复默认计时器配置？",
            Content="将逐项移除当前启动项的 disabledynamictick 和 useplatformtick 覆盖。"
                +(restoreDevices ? "\n将同时启用当前检测到的已禁用 HPET 设备。" : "\n本次不改变 HPET 设备状态。")
                +"\n操作前保存当前状态；完成后必须手动重启电脑。不会安装运行库、修改其他启动项或自动重启。",
            PrimaryButtonText="确认恢复",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close
        }.ShowWithBackendRecoveryAsync(pageOwner:_owner)!=ContentDialogResult.Primary) return;
        var response=await _caller.InvokeAsync(BackendCommands.SystemTimerRecover,new TimerRecoveryArgs(selected.Fingerprint,restoreDevices),null,CancellationToken.None);
        var result=response.Data is { } data ? BackendJson.Deserialize<OperationResult>(data) : OperationResult.Fail(response.Message);
        ResultText.Text=result.Message;
        await RefreshAsync();
    });
}
