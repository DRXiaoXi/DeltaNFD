using DeltaNFD.Services;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class GpuSpoofPage : Page
{
    public GpuSpoofViewModel ViewModel { get; } = new();

    public GpuSpoofPage()
    {
        InitializeComponent();
    }

    private async void SpoofButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAdapter is null)
        {
            ViewModel.ShowResult(OperationResult.Fail("请先在列表中选择要伪装的显卡。"));
            return;
        }

        var name = ViewModel.EffectiveName;
        if (string.IsNullOrWhiteSpace(name))
        {
            ViewModel.ShowResult(OperationResult.Fail("请选择预置型号，或输入自定义型号。"));
            return;
        }

        var adapterName = ViewModel.SelectedAdapter.DisplayName;
        var registryPath = ViewModel.SelectedAdapter.RegistryPath;

        if (AppSettingsStore.Read().TempSpoofRestorePending)
        {
            ViewModel.ShowResult(OperationResult.Fail(
                "已有一项临时显卡伪装等待自动还原，请先完成还原后再开始新的显卡伪装。"));
            return;
        }

        // ---------- 临时模式：立即重载 或 重启后自动还原 ----------
        if (ViewModel.IsTemporaryMode)
        {
            if (ViewModel.IsDirectApplyRestart)
            {
                if (ViewModel.Frame.FrameModeEnabled)
                {
                    ViewModel.ShowResult(OperationResult.Fail(
                        "请先退出一键帧格模式，再使用临时重启伪装。帧格模式正在使用同一个登录自启动任务，无法同时安排自动还原。"));
                    return;
                }

                var restartConfirm = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "临时伪装并立即重启？",
                    Content = $"把「{adapterName}」伪装为「{name}」：\n\n"
                              + "· 写入伪装型号并创建登录自启动任务\n"
                              + "· 随后立即重启电脑，重启后本工具自动启动并把注册表改回原始型号\n"
                              + "· Windows 可能要在下一次重启后才刷新已加载设备的显示名称\n\n"
                              + "重启将在 5 秒后强制关闭正在运行的程序，请先保存文件。",
                    PrimaryButtonText = "写入并立即重启",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                };

                if (await restartConfirm.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                var spoofResult = await ViewModel.SpoofSelectedAsync();
                if (spoofResult is { Success: false })
                {
                    ViewModel.ShowResult(spoofResult);
                    return;
                }

                // 非帧格伪装：从帧格选项中移除显卡伪装
                ViewModel.ClearFrameSpoof();

                AppSettingsStore.Update(s =>
                {
                    s.TempSpoofRestorePending = true;
                    s.TempSpoofRestorePath = registryPath;
                });

                var pendingRestore = AppSettingsStore.Read();
                if (!pendingRestore.TempSpoofRestorePending ||
                    !string.Equals(pendingRestore.TempSpoofRestorePath, registryPath, StringComparison.OrdinalIgnoreCase))
                {
                    var rollback = await ViewModel.RestoreByPathAsync(registryPath);
                    ViewModel.ShowResult(OperationResult.Fail(rollback.Success
                        ? "无法保存重启后的自动还原登记，已取消重启并撤销伪装。"
                        : $"无法保存重启后的自动还原登记，未重启；伪装回滚失败：{rollback.Message}。请手动恢复型号。"));
                    return;
                }

                var autostart = await ViewModel.Frame.EnableLogonAutostartAsync();
                if (!autostart.Success)
                {
                    var rollback = await ViewModel.RestoreByPathAsync(registryPath);
                    if (rollback.Success)
                    {
                        AppSettingsStore.Update(s =>
                        {
                            s.TempSpoofRestorePending = false;
                            s.TempSpoofRestorePath = "";
                        });
                        ViewModel.ShowResult(OperationResult.Fail(
                            $"登录自启动任务创建失败，已取消重启并撤销伪装。{autostart.Message}"));
                    }
                    else
                    {
                        ViewModel.ShowResult(OperationResult.Fail(
                            $"登录自启动任务创建失败，未重启。自动还原登记已保留，但伪装回滚也失败：{rollback.Message}。请手动恢复型号或重新启动工具。"));
                    }

                    return;
                }

                var reboot = ViewModel.Frame.RebootNow();
                if (!reboot.Success)
                {
                    ViewModel.ShowResult(OperationResult.Fail(
                        $"伪装和登录自动还原任务已准备好，但自动重启失败：{reboot.Message} 请手动重启电脑。"));
                }

                return;
            }

            // 立即重载显卡（不重启）
            var directConfirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "临时伪装并立即重载？",
                Content = $"把「{adapterName}」伪装为「{name}」并立即重载显卡生效。\n\n"
                          + "· 还原请用本页「恢复原始型号」\n"
                          + "· 重载瞬间屏幕会黑屏数秒；系统会自动恢复\n"
                          + "· 笔记本（显示器由该显卡驱动时）会拒绝重载以避免黑屏风险",
                PrimaryButtonText = "伪装并重载",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await directConfirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var spoofResult2 = await ViewModel.SpoofSelectedAsync();
            if (spoofResult2 is { Success: false })
            {
                ViewModel.ShowResult(spoofResult2);
                return;
            }

            // 非帧格伪装：从帧格选项中移除显卡伪装
            ViewModel.ClearFrameSpoof();

            // 立即重载显卡（复用帧格的黑屏保护闸门）
            var reload = await ViewModel.Frame.ReloadGpuNowAsync(registryPath);
            ViewModel.ShowResult(reload.Success
                ? OperationResult.Ok($"已伪装为「{name}」并立即生效。还原请点「恢复原始型号」。")
                : OperationResult.Fail($"已写入伪装（重启电脑后生效），但立即重载失败：{reload.Message}"));
            return;
        }

        // ---------- 永久模式：直接伪装 ----------
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认伪装",
            Content = $"把「{adapterName}」伪装为「{name}」？\n\n"
                      + "重启电脑后生效，随时可在本页恢复。",
            PrimaryButtonText = "伪装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var permanentResult = await ViewModel.SpoofSelectedAsync();
        if (permanentResult is { Success: true })
        {
            // 永久伪装：从帧格选项中移除显卡伪装
            ViewModel.ClearFrameSpoof();
        }

        ViewModel.ShowResult(permanentResult);
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAdapter is null)
        {
            ViewModel.ShowResult(OperationResult.Fail("请先在列表中选择要恢复的显卡。"));
            return;
        }

        if (!ViewModel.SelectedAdapter.HasBackup)
        {
            ViewModel.ShowResult(OperationResult.Fail(
                "该显卡没有本工具记录的备份（可能未用本工具伪装过），无法恢复。"));
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认恢复",
            Content = $"把「{ViewModel.SelectedAdapter.DisplayName}」恢复为伪装前的原始型号？\n\n"
                      + "重启电脑后生效。",
            PrimaryButtonText = "恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var registryPath = ViewModel.SelectedAdapter.RegistryPath;
        var restore = await ViewModel.RestoreSelectedAsync();
        if (restore.Success)
        {
            var settings = AppSettingsStore.Read();
            if (settings.TempSpoofRestorePending &&
                string.Equals(settings.TempSpoofRestorePath, registryPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!ViewModel.Frame.FrameModeEnabled)
                {
                    await ViewModel.Frame.DisableLogonAutostartAsync();
                }

                AppSettingsStore.Update(s =>
                {
                    s.TempSpoofRestorePending = false;
                    s.TempSpoofRestorePath = "";
                });
            }
        }

        ViewModel.ShowResult(restore);
    }

    private void ResultInfoBar_CloseButtonClick(InfoBar sender, object args)
    {
        ViewModel.IsResultVisible = false;
    }
}
