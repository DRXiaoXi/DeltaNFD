using DeltaNFD.Backends;
using DeltaNFD.Services;

namespace DeltaNFD.Backend.Ace;

/// <summary>
/// ACE 后端的命令实现。处理器只做「调用执行库 + 包装结果」，
/// 所有安全检查（管理员、默认游戏模式、游戏/守卫状态复核）都在执行库内部完成。
/// </summary>
internal static class AceCommands
{
    public static readonly BackendHostOptions Options = new()
    {
        Module = BackendModules.Require(BackendModules.Ace),
        Handlers = new Dictionary<string, Func<BackendCommandContext, Task<BackendResult>>>(StringComparer.Ordinal)
        {
            [BackendCommands.AceScan] = ScanAsync,
            [BackendCommands.AceCoreCheck] = CoreCheckAsync,
            [BackendCommands.AceClean] = CleanAsync,
            [BackendCommands.AceReconcile] = ReconcileAsync,
            [BackendCommands.AceVirtCheck] = VirtCheckAsync,
        },
    };

    /// <summary>
    /// 只读状态核对：返回当前实际 ACE 组件状态。ACE 清理不可逆、本工具不保留原值备份，
    /// 因此不提供「按可信备份恢复」范围——只能继续核对或由玩家明确选择保留当前状态。
    /// </summary>
    private static async Task<BackendResult> ReconcileAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在只读核对 ACE 状态…", null, "reconcile");
        var scan = await new AceService().ScanAsync();
        var items = new List<ReconcileItem>();
        foreach (var service in scan.Services)
            items.Add(new ReconcileItem
            {
                Id = service.Name, Kind = "service",
                State = scan.BlockedByGame ? "状态未知（游戏运行中，已跳过读取）" : service.Exists ? service.StartMode : "未安装",
            });
        foreach (var path in scan.Paths)
            items.Add(new ReconcileItem
            {
                Id = path.Name, Kind = "path",
                State = scan.BlockedByGame ? "状态未知（游戏运行中，已跳过读取）" : path.Exists ? "存在" : "不存在",
                Detail = path.Path,
            });
        var summary = scan.BlockedByGame
            ? "三角洲进程运行中：已暂停 ACE 读取，无法确认当前状态。"
            : $"ACE 组件：服务 {scan.Services.Count(s => s.Exists)} 项、目录 {scan.Paths.Count(p => p.Exists)} 项、注册表 {scan.RegistryKeys.Count} 项。";
        var snapshot = new ReconcileSnapshot
        {
            Module = BackendModules.Ace,
            Summary = summary + "ACE 清理不可逆，本工具不保留原值备份；如已执行清理，请用游戏启动器执行一次「修复」重装干净组件。",
            Complete = !scan.BlockedByGame && !GameTargetService.Default.IsCustom,
            Items = items,
            RecoveryScopes =
            [
                new RecoveryScope
                {
                    Id = "none",
                    Description = "ACE 清理无可信备份，无法自动还原",
                    Available = false,
                    Reason = "清理不可逆，设计如此；只能继续核对或保留当前状态。",
                },
            ],
        };
        return BackendResult.Ok("ace.reconcile", snapshot.Summary, snapshot);
    }

    private static async Task<BackendResult> ScanAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在扫描 ACE 组件…", null, "scan");
        var service = new AceService();
        var scan = await service.ScanAsync();
        var message = scan.BlockedByGame
            ? "三角洲进程运行中：已暂停 ACE 检测。"
            : scan.AnythingFound
                ? $"发现 ACE 组件：服务 {scan.Services.Count(s => s.Exists)} 项、目录 {scan.Paths.Count(p => p.Exists)} 项。"
                : "未发现 ACE 组件。";
        return BackendResult.Ok("ace.scan", message, scan);
    }

    private static async Task<BackendResult> CoreCheckAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在检测 ACE-CORE 冗余文件…", null, "core");
        var service = new AceService();
        var core = await service.CheckCoreFilesAsync();
        return BackendResult.Ok("ace.core", core.Summary, core);
    }

    private static async Task<BackendResult> CleanAsync(BackendCommandContext context)
    {
        var service = new AceService();
        var progress = new RelayProgress(context);
        return await service.CleanPhasedAsync(progress, context.CancellationToken);
    }

    /// <summary>虚拟化环境检测（纯只读，不触碰 ACE 组件，游戏运行中也可用）。</summary>
    private static async Task<BackendResult> VirtCheckAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在检测虚拟化环境…", null, "vircheck");
        var result = await new AceService().CheckVirtualizationAsync();
        return BackendResult.Ok("ace.vircheck", result.OverallSummary, result);
    }

    /// <summary>把执行库的文本进度同步转成后端进度帧（保持顺序）。</summary>
    private sealed class RelayProgress(BackendCommandContext context) : IProgress<string>
    {
        public void Report(string value) => context.ReportProgress(value, null, "clean");
    }
}
