using DeltaNFD.Backends;
using DeltaNFD.Services;

namespace DeltaNFD.Backend.Runtime;

/// <summary>
/// 运行库后端的命令实现：IFEO / UE4 ACL 保护、VC++ 运行库扫描、重装与卸载。
/// 权限与拦截状态复核在执行库内完成；处理器只做调用与结果包装。
/// </summary>
internal static class RuntimeCommands
{
    public static readonly BackendHostOptions Options = new()
    {
        Module = BackendModules.Require(BackendModules.Runtime),
        Handlers = new Dictionary<string, Func<BackendCommandContext, Task<BackendResult>>>(StringComparer.Ordinal)
        {
            [BackendCommands.RuntimeStatus] = StatusAsync,
            [BackendCommands.RuntimeGuardEnable] = GuardEnableAsync,
            [BackendCommands.RuntimeGuardDisable] = GuardDisableAsync,
            [BackendCommands.RuntimeGuardSelectMode] = GuardSelectModeAsync,
            [BackendCommands.RuntimeGuardCleanupLegacy] = GuardCleanupLegacyAsync,
            [BackendCommands.RuntimeUe4RepairAcl] = Ue4RepairAclAsync,
            [BackendCommands.RuntimeRedistScan] = RedistScanAsync,
            [BackendCommands.RuntimeRedistListV14] = RedistListV14Async,
            [BackendCommands.RuntimeRepair] = RepairAsync,
            [BackendCommands.RuntimeCompatibilityRepair] = CompatibilityRepairAsync,
            [BackendCommands.RuntimeRedistUninstall] = RedistUninstallAsync,
            [BackendCommands.RuntimeRedistPreviewAll] = PreviewAllAsync,
            [BackendCommands.RuntimeRedistUninstallAll] = UninstallAllAsync,
            [BackendCommands.RuntimeReconcile] = ReconcileAsync,
            [BackendCommands.RuntimeRecover] = RecoverAsync,
        },
    };

    /// <summary>只读状态核对：拦截是否生效、已拦截数量与已安装运行库。</summary>
    private static async Task<BackendResult> ReconcileAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在只读核对运行库防护状态…", null, "reconcile");
        var snapshot = await BuildSnapshotAsync();
        return BackendResult.Ok("runtime.reconcile", snapshot.Summary, snapshot);
    }

    /// <summary>按可信原值备份关闭拦截并还原；恢复后再次只读复核，复核不通过不报告成功。</summary>
    private static async Task<BackendResult> RecoverAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<RecoverArgs>();
        var scope = (args.Scope ?? "").Trim().ToLowerInvariant();
        if (scope != "guard")
            return BackendResult.Failed("runtime.recover", $"未知恢复范围“{args.Scope}”，已拒绝执行。");

        context.ReportProgress("正在关闭运行库拦截并还原原值…", null, "recover");
        var service = new RuntimeGuardService();
        var result = await service.DisableAsync();
        var outcome = RuntimeGuardRecoveryReport.BuildOutcome(result, await service.GetStatusAsync());
        return BackendResult.Ok("runtime.recover", outcome.Message, outcome);
    }

    private static async Task<ReconcileSnapshot> BuildSnapshotAsync()
    {
        return RuntimeGuardRecoveryReport.Build(await new RuntimeGuardService().GetStatusAsync());
    }

    private static async Task<BackendResult> StatusAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在读取运行库防护状态…", null, "status");
        var status = await new RuntimeGuardService().GetStatusAsync();
        return BackendResult.Ok("runtime.status", RuntimeGuardRecoveryReport.Build(status).Summary, status);
    }

    private static async Task<BackendResult> GuardEnableAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在开启运行库拦截…", null, "guard-enable");
        return Wrap("runtime.guard.enable", await new RuntimeGuardService().EnableAsync());
    }

    private static async Task<BackendResult> GuardDisableAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在关闭运行库拦截…", null, "guard-disable");
        return Wrap("runtime.guard.disable", await new RuntimeGuardService().DisableAsync());
    }

    private static async Task<BackendResult> GuardSelectModeAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<RuntimeSelectModeArgs>();
        return Wrap("runtime.guard.select-mode", await new RuntimeGuardService().SelectModeAsync(args.Mode));
    }

    private static async Task<BackendResult> GuardCleanupLegacyAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<RuntimeCleanupLegacyArgs>();
        context.ReportProgress("正在强制清理运行库安装器拦截…", null, "cleanup-legacy");
        return Wrap("runtime.guard.cleanup-legacy", await new RuntimeGuardService().CleanupLegacyIfeoAsync(args.Entries));
    }

    private static async Task<BackendResult> Ue4RepairAclAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<RuntimeUe4RepairArgs>();
        if (string.IsNullOrWhiteSpace(args.ExpectedPath))
            return BackendResult.Failed("runtime.ue4.repair-acl", "缺少 expectedPath。");
        context.ReportProgress("正在修复 UE4 前置包 ACL…", null, "ue4-acl");
        return Wrap("runtime.ue4.repair-acl", await new RuntimeGuardService().RepairLegacyUe4AclAsync(args.ExpectedPath));
    }

    private static async Task<BackendResult> RedistScanAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在扫描 VC++ 运行库…", null, "redist-scan");
        var report = await new RuntimeGuardService().ScanVcRedistsAsync();
        return BackendResult.Ok("runtime.redist.scan", report.Advice, report);
    }

    private static async Task<BackendResult> RedistListV14Async(BackendCommandContext context)
    {
        var entries = await new RuntimeGuardService().GetV14EntriesAsync();
        return BackendResult.Ok("runtime.redist.list-v14", $"发现 {entries.Count} 个 v14 命名条目。", entries);
    }

    private static async Task<BackendResult> RepairAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<RuntimeRepairArgs>();
        if (!Enum.IsDefined(args.Mode)) return BackendResult.Failed(context.Command, "修复模式无效，未执行。");
        var progress = new RelayProgress(context);
        return Wrap("runtime.repair", await new RuntimeGuardService().RepairVcRedistAsync(progress, args.Mode, context.CancellationToken, args.StrongRepairMode));
    }

    private static async Task<BackendResult> RedistUninstallAsync(BackendCommandContext context)
    {
        var progress = new RelayProgress(context);
        return Wrap("runtime.redist.uninstall", await new RuntimeGuardService().UninstallV14Async(progress));
    }

    private static async Task<BackendResult> PreviewAllAsync(BackendCommandContext context)
    {
        var preview = await new RuntimeGuardService().PreviewUninstallAllAsync();
        return BackendResult.Ok(context.Command, $"全量卸载候选 {preview.Entries.Count} 项。", preview);
    }

    private static async Task<BackendResult> UninstallAllAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<RuntimeUninstallAllArgs>();
        return Wrap(context.Command, await new RuntimeGuardService().UninstallAllAsync(args.Fingerprint, new RelayProgress(context), context.CancellationToken));
    }

    private static async Task<BackendResult> CompatibilityRepairAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<RuntimeCompatibilityRepairArgs>();
        return Wrap(context.Command, await new RuntimeGuardService().RepairCompatibilityAsync(
            new RelayProgress(context), context.CancellationToken, args.StrongRepairMode));
    }

    private static BackendResult Wrap(string code, OperationResult result) =>
        new(result.Success ? BackendResultStatuses.Ok : BackendResultStatuses.Failed, code, result.Message, [],
            result.OutcomeUnknown, System.Text.Json.JsonSerializer.SerializeToElement(result, BackendJson.SerializerOptions));

    private sealed class RelayProgress(BackendCommandContext context) : IProgress<string>
    {
        public void Report(string value) => context.ReportProgress(value, null, "runtime");
    }
}
