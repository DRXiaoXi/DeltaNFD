using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class SettingsPage
{
    private bool _factoryResetBusy;
    private FactoryResetService? _factoryReset;

    private async void FactoryReset_Click(object sender, RoutedEventArgs e)
    {
        if (_factoryResetBusy) return;
        _factoryResetBusy = true;
        FactoryResetButton.IsEnabled = false;
        try
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "恢复工具出厂设置？",
                Content = "请先退出游戏。将备份原配置，停用帧格、CPU 调度、自动内存回收、插件运行和工具自启动，并按已有记录恢复临时修改。\n\n"
                    + "随后重置主题、背景、游戏目录与自动开关，清空进程规则，恢复后台参数默认值。\n"
                    + "系统优化备份、日志与插件文件会保留，已应用的系统优化需在对应页面单独还原。\n\n"
                    + "恢复失败或存在未确认操作时停止重置并保留记录。成功后工具会关闭，重新打开后载入默认配置。",
                PrimaryButtonText = "备份并恢复出厂设置", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary) return;
            App.MainWindow?.SetFactoryResetInteraction(false);
            FactoryResetStatusText.Text = "正在准备恢复出厂设置…";
            _factoryReset = new FactoryResetService();
            var result = await _factoryReset.RunAsync(new Progress<string>(text => FactoryResetStatusText.Text = text));
            FactoryResetStatusText.Text = result.Message;
            App.MainWindow?.SetFactoryResetInteraction(true);
            await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = result.Success ? "出厂设置已恢复" : "恢复出厂设置未完成",
                Content = result.Message + (result.RequiresReboot ? "\n显卡型号已按备份恢复，重启电脑后显示原始型号。" : ""),
                CloseButtonText = result.Success ? "关闭工具" : "知道了",
            }.ShowWithBackendRecoveryAsync();
        }
        catch (Exception ex)
        {
            Log.Error("恢复出厂设置界面操作失败", ex);
            FactoryResetStatusText.Text = "操作未完成：" + ex.Message;
        }
        finally
        {
            if (_factoryReset?.Completed == true) App.MainWindow?.ExitForDeployment();
            else
            {
                App.MainWindow?.SetFactoryResetInteraction(true);
                _factoryResetBusy = false;
                FactoryResetButton.IsEnabled = true;
                RefreshLegacyRecoveryControls();
            }
        }
    }
}
