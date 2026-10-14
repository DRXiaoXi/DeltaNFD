using DeltaNFD.Backends;

namespace DeltaNFD.Services;

/// <summary>核对中断的安装事务并归档活动记录；不回滚安装、不重放修复。</summary>
public static class RuntimeRepairRecovery
{
    public const string Scope = "repair-review";
    private static ReconcileSnapshot TransactionBasis() => new()
    {
        Module = BackendModules.Runtime, Complete = true,
        Summary = "本次核对安装事务与当前 C++ 登记，不修改或还原运行库防护。",
    };
    public static Task<ReconcileSnapshot> InspectAsync()
        => InspectAsync(TransactionBasis(), new RuntimeRepairProcessRunner(AppDataPaths.Root),
            RuntimeRepairPreflight.InstallerBusy, () => new RuntimeGuardService().ScanVcRedistsAsync());
    internal static async Task<ReconcileSnapshot> InspectAsync(ReconcileSnapshot guard, RuntimeRepairProcessRunner runner,
        Func<bool> busy, Func<Task<VcRedistScanReport>> scan)
    {
        var ended = false; string? blocker;
        try
        {
            var installerBusy = busy();
            blocker = runner.InspectPending(installerBusy, out ended);
            if (installerBusy) blocker ??= "Windows Installer 事务仍忙，请等待结束后重新核对。";
        }
        catch (Exception ex) { blocker = "安装事务无法确认：" + ex.Message; }
        VcRedistScanReport? report = null; var scanError = "";
        try { report = await scan(); }
        catch (Exception ex) { scanError = "运行库扫描未完成：" + ex.Message; }
        var available = blocker is null && scanError.Length == 0 && report is not null && guard.Complete;
        var detail = blocker ?? (ended ? "上次进程已退出，结果未知；可核对当前状态并归档。" : "未发现未结束的安装进程记录。");
        var installed = report is null ? scanError
            : $"当前 C++ 登记 {report.All.Count} 项；异常登记 {report.Abnormal.Count} 项；缺失或待更新分支 {report.Suboptimal.Count} 项。";
        return new ReconcileSnapshot
        {
            Module = BackendModules.Runtime, Complete = guard.Complete && report is not null && scanError.Length == 0,
            Summary = detail + "\n" + installed + "\n" + guard.Summary,
            Items = [.. guard.Items, new() { Id = Scope, Kind = "installation-review", State = detail, Detail = installed }],
            RecoveryScopes = [.. guard.RecoveryScopes, new() { Id = Scope, Available = available,
                Description = "核对安装事务已结束，归档记录并恢复可用（不回滚、不重新安装）",
                Reason = available ? "" : blocker ?? (scanError.Length > 0 ? scanError : "运行库防护状态尚未完整核对。"), AffectedCount = 1 }],
        };
    }
    public static Task<RecoverOutcome> ReviewAsync()
        => Task.Run(() =>
        {
            RecoverOutcome? outcome = null;
            var gate = RuntimeGuardProtection.Exclusive(() =>
            {
                outcome = ReviewAsync(new RuntimeRepairProcessRunner(AppDataPaths.Root), RuntimeRepairPreflight.InstallerBusy,
                    () => new RuntimeGuardService().ScanVcRedistsAsync(),
                    () => Task.FromResult(TransactionBasis())).GetAwaiter().GetResult();
                return new OperationResult { Success = outcome.Success, Message = outcome.Message };
            });
            return outcome ?? new RecoverOutcome { Scope = Scope, Success = false, Verified = false, Message = gate.Message };
        });

    internal static async Task<RecoverOutcome> ReviewAsync(RuntimeRepairProcessRunner runner, Func<bool> busy,
        Func<Task<VcRedistScanReport>> scan, Func<Task<ReconcileSnapshot>> guard)
    {
        var snapshot = await InspectAsync(await guard(), runner, busy, scan);
        if (!snapshot.RecoveryScopes.Any(s => s.Id == Scope && s.Available))
            return new() { Scope = Scope, Success = false, Verified = false, VerifiedSnapshot = snapshot,
                Message = "安装事务尚未满足核对条件，记录保留。\n" + snapshot.Summary };
        // 归档前再次确认存活进程和 MSI 事务；CheckPending 只归档已结束的未知记录。
        var pending = busy() ? "Windows Installer 事务仍忙，尚未解除阻塞。" : runner.CheckPending(false);
        if (pending is not null)
            return new() { Scope = Scope, Success = false, Verified = false, VerifiedSnapshot = snapshot, Message = pending };
        var after = await InspectAsync(await guard(), runner, busy, scan);
        var verified = after.Complete && after.RecoveryScopes.Any(s => s.Id == Scope && s.Available);
        return new() { Scope = Scope, Success = verified, Verified = verified, VerifiedSnapshot = after,
            Message = verified ? "已确认旧安装事务结束并扫描当前运行库；活动记录已归档，可解除待核对并重新修复。"
                + "\n这不表示上次修复成功，也不表示组件已回滚。\n" + after.Summary
                : "归档后复检未完成，仍保留模块待核对记录。\n" + after.Summary };
    }
}
