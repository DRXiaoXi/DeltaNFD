using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;
public sealed partial class DashboardPage
{
    private async Task RefreshLowMemoryAsync(bool force = false)
    {
        var generation = _timerGeneration;
        try
        {
            var status = await LowMemoryHealthCache.ReadAsync(force);
            if (!_attached || generation != _timerGeneration) return;
            LowMemoryWarning.IsOpen = !status.CapacityKnown || status.IsLowCapacity && (!status.Complete || status.NeedsAttention);
            LowMemoryWarning.Severity = InfoBarSeverity.Warning;
            LowMemoryWarning.Title = status.NeedsAttention ? "低内存配置需要调整" : "低内存配置检测未完成";
            var needs = new List<string>();
            foreach (var action in Enum.GetValues<LowMemoryAction>())
                if (status.Needs(action) == true) needs.Add(action switch
                {
                    LowMemoryAction.SysMain => "SysMain 未启用或未运行", LowMemoryAction.MemoryCompression => "内存压缩已禁用",
                    LowMemoryAction.PageCombining => "分页合并已禁用", _ => "预读取尚未禁用",
                });
            LowMemoryWarning.Message = string.Join("；", needs) + (status.Error.Length > 0 ? " " + status.Error : "")
                + (status.IsLowCapacity ? " 请进入引导页核对，完成后重启电脑。" : " 请重新检测物理总内存。");
        }
        catch (Exception ex)
        {
            if (!_attached || generation != _timerGeneration) return;
            LowMemoryWarning.IsOpen = true; LowMemoryWarning.Title = "低内存配置待确认"; LowMemoryWarning.Message = ex.Message;
        }
    }
    private void LowMemoryRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (_attached) Frame.Navigate(typeof(LowMemoryRecoveryPage));
    }
}
