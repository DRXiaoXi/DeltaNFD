using DeltaNFD.Services;
using Microsoft.UI.Xaml;

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
            if (!status.Complete || !status.NeedsAttention)
            {
                LowMemoryWarning.IsOpen = false;
                return;
            }

            var needs = new List<string>();
            foreach (var action in Enum.GetValues<LowMemoryAction>())
                if (status.Needs(action) == true) needs.Add(action switch
                {
                    LowMemoryAction.SysMain => "SysMain 未启用或未运行", LowMemoryAction.MemoryCompression => "内存压缩已禁用",
                    LowMemoryAction.PageCombining => "分页合并已禁用", _ => "预读取尚未禁用",
                });
            LowMemoryWarning.Message = string.Join("；", needs) + " 请进入引导页核对，完成后重启电脑。";
            LowMemoryWarning.IsOpen = true;
        }
        catch (Exception ex)
        {
            if (!_attached || generation != _timerGeneration) return;
            LowMemoryWarning.IsOpen = false;
            Log.Warn("主页低内存配置检测失败：" + ex.Message);
        }
    }
    private void LowMemoryRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (_attached) Frame.Navigate(typeof(LowMemoryRecoveryPage));
    }
}
