using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class RuntimePage
{
    // Keep operation gates held until ExitForDeployment terminates this process.
    private IDisposable? _startupHandover;
    private bool _startupExitPending;

    private async Task<bool> ChooseRuntimeEnvironmentAsync(CancellationToken token, Func<Task<bool>> recheck, bool fullRepair = false)
    {
        if (token.IsCancellationRequested) return false;
        if (AdvancedStartupRestart.IsSafeMode) return true;
        var choice = await new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "选择卸载运行库的环境",
            Content = "可以在当前环境处理占用后继续卸载，无需必须进入安全模式。也可选择安全模式，仅在其中执行单独卸载阶段，再回正常模式安装。"
                + "默认安全模式通常不启动 Installer，卸载确认窗口可单独同意临时启用，执行后恢复。\n\n临时启用失败时会停止并报告原因；进入安全模式本身不能解决安装依赖引用或组件损坏。",
            PrimaryButtonText = "在当前环境继续", SecondaryButtonText = "安全模式引导",
            CloseButtonText = "取消修复", DefaultButton = ContentDialogButton.Close,
        }.ShowWithBackendRecoveryAsync();
        if (token.IsCancellationRequested) return false;
        if (choice == ContentDialogResult.Primary) return true;
        ViewModel.RepairStatusText = "已取消本次修复，未执行卸载或安装。";
        if (choice != ContentDialogResult.Secondary) return false;

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(FileUseText("重启后依次选择：\n疑难解答 → 高级选项 → 启动设置 → 重启 → 按 4（或 F4）。\n\n工具只请求本次进入 Windows 高级启动，不会直接选择安全模式。回到登录界面后，使用账户密码登录；PIN 和指纹可能不可用。若设备启用 BitLocker，请提前准备恢复密钥。"));
        body.Children.Add(FileUseText("请先保存工作、退出游戏及其他应用，并手动关闭运行库防护。工具会先停止并交接自己的后台功能，无法确认安全停止时不会请求重启。其他应用仍可能阻止 Windows 重启。\n\n"
            + (fullRepair ? "安全模式下请手动打开工具，点击“预览全量修复”进入 V14 卸载确认；回正常模式后再次点击“预览全量修复”处理全部安装步骤。"
                : "安全模式下请手动打开工具，选择“仅卸载 14.x”，重新预览并确认；卸载后手动回正常模式选择“继续安装 14.42”。")
            + "若需临时启用 Installer，必须另行勾选同意；卸载后先恢复临时设置。不会自动重启、卸载或续跑。若工具无法运行或临时启用失败，请回正常模式核对。"));
        if (fullRepair) body.Children.Add(FileUseText("全量修复：进入安全模式后点击“预览全量修复”，工具会转入 V14 单独卸载确认。结束并恢复临时设置后，手动回正常模式，再点击“预览全量修复”完成微软旧版 C++ 原包和其他组件安装；不会在安全模式执行安装或自动续跑。"));
        var hint = FileUseText("若高级启动不可用，可打开系统恢复设置，使用“高级启动”入口；也可按住 Shift 点击 Windows 的“重启”。");
        var settings = new Button { Content = "打开 Windows 恢复设置" };
        settings.Click += async (_, _) =>
        {
            try
            {
                var opened = await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:recovery"));
                hint.Text = opened ? "已请求打开恢复设置；本工具尚未请求重启。" : "恢复设置无法打开，请按住 Shift 点击 Windows 的“重启”。";
            }
            catch (Exception ex) { hint.Text = "恢复设置未打开：" + ex.Message; }
        };
        body.Children.Add(settings); body.Children.Add(hint);
        var consent = new CheckBox { Content = "我已保存工作，并准备好登录密码及可能需要的恢复密钥" };
        body.Children.Add(consent);
        var guide = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "重启进入高级启动？",
            Content = new ScrollViewer { MaxHeight = 420, Content = body },
            PrimaryButtonText = "立即重启到高级启动", CloseButtonText = "暂不重启",
            DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false,
        };
        consent.Checked += (_, _) => guide.IsPrimaryButtonEnabled = true;
        consent.Unchecked += (_, _) => guide.IsPrimaryButtonEnabled = false;
        if (await guide.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary || token.IsCancellationRequested) return false;
        if (!await recheck() || token.IsCancellationRequested) return false;
        var window = App.MainWindow ?? throw new InvalidOperationException("主窗口不可用，未请求重启。");
        ViewModel.RepairStatusText = "正在停止工具后台功能，准备进入 Windows 高级启动…";
        try
        {
            var result = await AdvancedStartupRestart.RunAsync(consent.IsChecked == true,
                async () =>
                {
                    var permit = await window.PrepareDeploymentAsync(false, "进入高级启动");
                    return new StartupPreparation(permit, permit.Ready, permit.Detail);
                }, AdvancedStartupRestart.RequestWindowsAsync, token);
            ViewModel.RepairStatusText = result.Request.Message;
            Log.Info("运行库安全模式引导：" + result.Request.Message);
            if (result.ShouldExit)
            {
                _startupHandover = result.RetainedLease;
                _startupExitPending = true;
                window.ExitForDeployment();
                return false;
            }
            window.CancelDeploymentPreparation();
            await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "未能进入高级启动",
                Content = result.Request.Message + "\n\n已停止的后台功能需按需重新开启。可重新打开引导使用恢复设置，或按住 Shift 点击 Windows 的“重启”。",
                CloseButtonText = "知道了", DefaultButton = ContentDialogButton.Close,
            }.ShowWithBackendRecoveryAsync();
        }
        catch
        {
            if (!_startupExitPending) window.CancelDeploymentPreparation();
            throw;
        }
        return false;
    }
}
