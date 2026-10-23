using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.Backends;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class AcePage : Page
{
    private readonly DeltaNFD.Services.Backends.PageBackendLease? _pageOwner = DeltaNFD.Services.Backends.PageBackendSessions.Manager.Current;
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

    private async void CleanCoreFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupFlowBusy || !ViewModel.CoreCleanEnabled) return;
        _cleanupFlowBusy = true;
        try { await CleanCoreAsync(coreOnly: true); }
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

    private async Task CleanCoreAsync(bool coreOnly = false)
    {
        if (AceCleanupReadiness.RequiresReview)
        {
            await ReviewCleanupAsync();
            if (AceCleanupReadiness.RequiresReview) return;
        }
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = coreOnly ? "仅清理 ACE-CORE SYS 文件？" : "一键清除 ACE？",
            Content = coreOnly
                ? "仅删除 C:\\Program Files\\AntiCheatExpert 及其子目录中，文件名以 ACE-CORE 开头的 .sys 文件。\n\n"
                    + "保留 ACE 服务、注册表、其他文件及游戏目录；不会结束进程或安排重启删除。\n"
                    + "请先完全退出游戏和反作弊守卫。删除不可撤销，完成后请用启动器执行普通修复，再启动游戏。"
                : "将结束 ACE 进程、接管并删除全部 ACE 服务（含自保护驱动）、清理注册表残留、"
                      + "删除安装目录与游戏目录内的 AntiCheatExpert 文件夹。\n\n"
                      + "· 请确认已完全退出游戏（含 sguard64 后台进程）\n"
                      + "· 被占用的文件会在重启电脑后自动删除\n"
                      + "· 下次启动游戏时 ACE 会自动重新安装",
            PrimaryButtonText = coreOnly ? "清理 SYS 文件" : "清除",
            CloseButtonText = "取消",
            DefaultButton = coreOnly ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };

        if (await confirm.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner) != ContentDialogResult.Primary)
        {
            return;
        }

        var result = coreOnly ? await ViewModel.CleanCoreFilesAsync() : await ViewModel.CleanAsync();
        if (result is { Success: false })
        {
            var choice = await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = coreOnly ? "ACE-CORE SYS 清理未完成" : result.IsPartial ? "ACE 清理部分完成" : "ACE 清理未完成",
                Content = result.Message + (result.RequiresReboot ? "\n请先按提示重启，再用游戏启动器执行普通修复。" : ""),
                PrimaryButtonText = AceCleanupReadiness.RequiresReview ? "核对当前状态" : "",
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner, hasReviewAction: AceCleanupReadiness.RequiresReview);
            if (choice == ContentDialogResult.Primary) await ReviewCleanupAsync();
        }
        else if (result is { Success: true })
        {
            // 清除成功：引导用启动器/WeGame 修复游戏（不要深度修复）
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = coreOnly ? "ACE-CORE SYS 清理结果" : "ACE 清理完成",
                Content = result.Message + (coreOnly && result.IsSkipped ? "" : "\n\n"
                          + "下一步（重要）：\n"
                          + "① 打开 WeGame / 游戏启动器\n"
                          + "② 对三角洲行动执行「修复」——普通修复即可，不要使用深度修复\n"
                          + "③ 修复会重新安装干净的 ACE 组件，之后正常启动游戏即可。"),
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Primary,
            }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
        }
    }

    private async Task ShowFailureAsync(Exception ex)
    {
        Log.Error("ACE 清理或核对窗口操作失败", ex);
        try
        {
            await new ContentDialog { XamlRoot = XamlRoot, Title = "ACE 操作未完成",
                Content = ex.Message + "\n若有上次清理记录，请点击「核对上次清理」。", CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
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
        if (await confirm.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner) != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.FixVirtualizationConflictsAsync();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e) => ViewModel.AttachGameState();

    /// <summary>
    /// 页面只摘除订阅，统一栏目会话管理器等待当前请求结束后关闭本页后端。
    /// </summary>
    private void OnPageUnloaded(object sender, RoutedEventArgs e) => ViewModel.DetachGameState();
}
