using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.Backends;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class AcePage : Page
{
    private bool _cleanupFlowBusy;
    public AceViewModel ViewModel { get; } = new();

    public AcePage()
    {
        InitializeComponent();

        // 导航每次新建页面实例 → 订阅必须随 Loaded/Unloaded 成对挂/摘，
        // 否则会从长生命周期的游戏进程服务上泄漏（与 LabPage 同一套约定）。
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;

        _ = ViewModel.LoadAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private async void CleanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupFlowBusy || ViewModel.IsCleaning) return;
        _cleanupFlowBusy = true;
        try { await CleanCoreAsync(); }
        catch (Exception ex) { await ShowFailureAsync(ex); }
        finally { _cleanupFlowBusy = false; ViewModel.RefreshCleanupReview(); }
    }

    private async void ReviewCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupFlowBusy || ViewModel.IsCleaning) return;
        _cleanupFlowBusy = true;
        try { await ReviewCleanupAsync(); }
        catch (Exception ex) { await ShowFailureAsync(ex); }
        finally { _cleanupFlowBusy = false; ViewModel.RefreshCleanupReview(); }
    }

    private async Task ReviewCleanupAsync()
    {
        await BackendReviewFlow.RunAsync(XamlRoot, [BackendModules.Ace]);
        await ViewModel.LoadAsync();
    }

    private async Task CleanCoreAsync()
    {
        if (AceCleanupReadiness.RequiresReview)
        {
            await ReviewCleanupAsync();
            if (AceCleanupReadiness.RequiresReview) return;
        }
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "一键清除 ACE？",
            Content = "将结束 ACE 进程、接管并删除全部 ACE 服务（含自保护驱动）、清理注册表残留、"
                      + "删除安装目录与游戏目录内的 AntiCheatExpert 文件夹。\n\n"
                      + "· 请确认已完全退出游戏（含 sguard64 后台进程）\n"
                      + "· 被占用的文件会在重启电脑后自动删除\n"
                      + "· 下次启动游戏时 ACE 会自动重新安装",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirm.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await ViewModel.CleanAsync();
        if (result is { Success: false })
        {
            var choice = await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = result.IsPartial ? "ACE 清理部分完成" : "ACE 清理未完成",
                Content = result.Message + (result.RequiresReboot ? "\n请先按提示重启，再用游戏启动器执行普通修复。" : ""),
                PrimaryButtonText = AceCleanupReadiness.RequiresReview ? "核对当前状态" : "",
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            }.ShowWithBackendRecoveryAsync(hasReviewAction: AceCleanupReadiness.RequiresReview);
            if (choice == ContentDialogResult.Primary) await ReviewCleanupAsync();
        }
        else if (result is { Success: true })
        {
            // 清除成功：引导用启动器/WeGame 修复游戏（不要深度修复）
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "ACE 清理完成",
                Content = result.Message + "\n\n"
                          + "下一步（重要）：\n"
                          + "① 打开 WeGame / 游戏启动器\n"
                          + "② 对三角洲行动执行「修复」——普通修复即可，不要使用深度修复\n"
                          + "③ 修复会重新安装干净的 ACE 组件，之后正常启动游戏即可。",
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Primary,
            }.ShowWithBackendRecoveryAsync();
        }
    }

    private async Task ShowFailureAsync(Exception ex)
    {
        Log.Error("ACE 清理或核对窗口操作失败", ex);
        try
        {
            await new ContentDialog { XamlRoot = XamlRoot, Title = "ACE 操作未完成",
                Content = ex.Message + "\n若有上次清理记录，请点击「核对上次清理」。", CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync();
        }
        catch (Exception dialogError) { Log.Error("ACE 失败提示无法显示", dialogError); }
    }

    private async void CheckCoreButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CheckCoreFilesAsync();
    }

    private async void CheckVirtButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CheckVirtualizationAsync();
    }

    private async void FixVirtButton_Click(object sender, RoutedEventArgs e)
    {
        // 修复涉及 bcdedit/DISM 改系统，必须先确认。
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "一键修复系统层虚拟化冲突？",
            Content = "将执行「关闭 Hyper-V / VBS（含内核隔离）」完整流程：\n"
                      + "· bcdedit 关闭 hypervisor 开机启动\n"
                      + "· DISM 禁用 Hyper-V / 虚拟机平台相关功能\n"
                      + "· HvHost / vmms 服务改为手动启动\n"
                      + "· 注册表关闭 VBS / 内存完整性 / Credential Guard\n\n"
                      + "修改前会自动备份原值，可在「系统优化」页还原。重启电脑后生效；"
                      + "重启后 WSL2、虚拟机、Windows 沙盒将不可用。",
            PrimaryButtonText = "修复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await confirm.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.FixVirtualizationConflictsAsync();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e) => ViewModel.AttachGameState();

    /// <summary>
    /// 离开页面：摘订阅，并尝试关闭 ACE 后端进程（空闲才关；
    /// 清理进行中或结果未知时由 <see cref="BackendAceService.StopBackendIfIdleAsync"/> 自行拒绝）。
    /// 下次进页由 LoadAsync 按需重新拉起。
    /// </summary>
    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.DetachGameState();
        if (ServiceLocator.Ace is BackendAceService ace)
        {
            _ = ace.StopBackendIfIdleAsync();
        }
    }
}
