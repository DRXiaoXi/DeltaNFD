using DeltaNFD.Backends;
using DeltaNFD.Services;

namespace DeltaNFD.Backend.Runtime;

/// <summary>
/// 运行库后端：IFEO / UE4 ACL 防护、事务核对和新版 14.x 定向修复；旧修复命令不注册。
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
            [BackendCommands.RuntimeFileUseScan] = FileUseScanAsync,
            [BackendCommands.RuntimeFileUseClose] = FileUseCloseAsync,
            [BackendCommands.RuntimeHighFpsPreview] = HighFpsPreviewAsync,
            [BackendCommands.RuntimeHighFpsRepair] = HighFpsRepairAsync,
            [BackendCommands.Vc14Scan] = VcScanAsync,
            [BackendCommands.Vc14Preview] = VcPreviewAsync,
            [BackendCommands.Vc14Repair] = VcRepairAsync,
            [BackendCommands.Vc14LegacyClean] = VcLegacyCleanAsync,
            [BackendCommands.RuntimeGuardEnable] = GuardEnableAsync,
            [BackendCommands.RuntimeGuardDisable] = GuardDisableAsync,
            [BackendCommands.RuntimeGuardSelectMode] = GuardSelectModeAsync,
            [BackendCommands.RuntimeGuardCleanupLegacy] = GuardCleanupLegacyAsync,
            [BackendCommands.RuntimeUe4RepairAcl] = Ue4RepairAclAsync,
            [BackendCommands.RuntimeReconcile] = ReconcileAsync,
            [BackendCommands.RuntimeRecover] = RecoverAsync,
        },
    };

    /// <summary>只读状态核对：拦截是否生效、已拦截数量与已安装运行库。</summary>
    private static async Task<BackendResult> ReconcileAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在核对运行库安装事务、防护与当前组件状态…", null, "reconcile");
        var scope = context.ArgsAs<RecoverArgs>().Scope;
        var snapshot = scope == RuntimeLegacyInstallationReview.Scope ? await RuntimeLegacyInstallationReview.InspectAsync() : await BuildSnapshotAsync();
        return BackendResult.Ok("runtime.reconcile", snapshot.Summary, snapshot);
    }

    /// <summary>按可信原值备份关闭拦截并还原；恢复后再次只读复核，复核不通过不报告成功。</summary>
    private static async Task<BackendResult> RecoverAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<RecoverArgs>();
        var scope = (args.Scope ?? "").Trim().ToLowerInvariant();
        if (scope == RuntimeLegacyInstallationReview.Scope)
        {
            context.ReportProgress("正在确认旧安装事务结束并归档核对记录…", null, "recover");
            var reviewed = await RuntimeLegacyInstallationReview.ReviewAsync();
            return BackendResult.Ok("runtime.recover", reviewed.Message, reviewed);
        }
        if (scope == "safe-installer")
        {
            var restored = await Vc14Maintenance.RestoreTemporaryInstallerAsync();
            return BackendResult.Ok(BackendCommands.RuntimeRecover, restored.Message, new RecoverOutcome
            {
                Scope = scope, Success = restored.Success, Verified = restored.Success, Message = restored.Message,
                VerifiedSnapshot = new ReconcileSnapshot { Module = BackendModules.Runtime, Complete = restored.Success,
                    Summary = restored.Message, RecoveryScopes = [new() { Id = scope, Available = !restored.Success, Description = "恢复临时 Installer 设置" }] },
            });
        }
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









    private static async Task<BackendResult> VcScanAsync(BackendCommandContext context) =>
        BackendResult.Ok(BackendCommands.Vc14Scan, "14.x 兼容性检测完成", await Vc14Maintenance.ScanAsync());
    private static async Task<BackendResult> FileUseScanAsync(BackendCommandContext context) =>
        BackendResult.Ok(BackendCommands.RuntimeFileUseScan, "运行库文件占用检测", await RuntimeFileUseMaintenance.ScanAsync(context.CancellationToken, context.ArgsAs<RuntimeFileUseScanArgs>().Scope));
    private static async Task<BackendResult> FileUseCloseAsync(BackendCommandContext context) =>
        BackendResult.Ok(BackendCommands.RuntimeFileUseClose, "运行库占用进程处理完成", await RuntimeFileUseMaintenance.CloseAsync(
            context.ArgsAs<RuntimeFileUseCloseArgs>(), context.CancellationToken));

    private static async Task<BackendResult> HighFpsPreviewAsync(BackendCommandContext context) =>
        BackendResult.Ok(BackendCommands.RuntimeHighFpsPreview, "全量修复预览",
            await RuntimeHighFpsMaintenance.PreviewAsync(context.ArgsAs<RuntimeHighFpsArgs>().StrongMode));

    private static async Task<BackendResult> HighFpsRepairAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<RuntimeHighFpsArgs>();
        return Wrap(BackendCommands.RuntimeHighFpsRepair, await RuntimeHighFpsMaintenance.RepairAsync(
            args.Fingerprint, args.StrongMode, new Progress<string>(s => context.ReportProgress(s, null, "high-fps-repair")), context.CancellationToken));
    }

    private static async Task<BackendResult> VcPreviewAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<Vc14RepairArgs>();
        return BackendResult.Ok(BackendCommands.Vc14Preview, "14.x 阶段预览", await Vc14Maintenance.PreviewAsync(args.Mode, args.AllowTemporaryInstaller));
    }

    private static async Task<BackendResult> VcRepairAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<Vc14RepairArgs>();
        var progress = new Progress<string>(s => context.ReportProgress(s, null, "vc14-repair"));
        return Wrap(BackendCommands.Vc14Repair, await Vc14Maintenance.RepairAsync(args.Fingerprint, progress, context.CancellationToken, args.Mode, args.AllowTemporaryInstaller));
    }

    private static async Task<BackendResult> VcLegacyCleanAsync(BackendCommandContext context) =>
        BackendResult.Failed(BackendCommands.Vc14LegacyClean, "旧包装单独处理已并入 14.x 全量重装，不再单独执行。");

    private static BackendResult Wrap(string code, OperationResult result) =>
        new(result.Success ? BackendResultStatuses.Ok : BackendResultStatuses.Failed, code, result.Message, [],
            result.OutcomeUnknown, System.Text.Json.JsonSerializer.SerializeToElement(result, BackendJson.SerializerOptions));

}
