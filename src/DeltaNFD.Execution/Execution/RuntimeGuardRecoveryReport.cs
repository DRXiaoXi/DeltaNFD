using DeltaNFD.Backends;

namespace DeltaNFD.Services;

public static class RuntimeGuardRecoveryReport
{
    private static bool NeedsOrdinaryRecovery(RuntimeGuardStatus status) => status.IfeoApplied || status.IfeoManagedCount > 0
        || status.IfeoRestorePending || status.Ue4PrereqDenied || status.Ue4RestorePending;

    private static bool Complete(RuntimeGuardStatus status) => status.GuardIssue.Length == 0 && status.IfeoTotal > 0
        && status.IfeoCount >= 0 && status.IfeoManagedCount >= 0 && status.IfeoExternalCount >= 0
        && status.ScopeManagedCount >= 0 && status.ScopeBlockedCount >= 0
        && status.IfeoManagedCount + status.IfeoExternalCount == status.IfeoCount
        && status.ScopeManagedCount <= status.IfeoManagedCount && status.ScopeBlockedCount <= status.IfeoCount
        && status.ScopeManagedCount <= status.ScopeBlockedCount && status.ScopeBlockedCount <= status.IfeoTotal
        && (!status.IfeoApplied || status.ScopeManagedCount == status.IfeoTotal);

    internal static bool VerifiedClosed(RuntimeGuardStatus status) => Complete(status) && !NeedsOrdinaryRecovery(status)
        && status.IfeoCount == 0 && status.IfeoExternalCount == 0 && status.ScopeBlockedCount == 0;

    public static ReconcileSnapshot Build(RuntimeGuardStatus status)
    {
        var complete = Complete(status);
        var available = NeedsOrdinaryRecovery(status);
        var summary = !complete ? "运行库防护状态核对不完整，不能确认已关闭。"
            : status.IfeoApplied ? "运行库防护全部生效，可关闭并复核。"
            : available ? "运行库防护部分生效或有旧残留，可关闭并复核；不是无需恢复。"
            : status.IfeoCount > 0 ? "仍有其他程序的安装器拦截；普通关闭不会强制移除，请在运行库页确认清理。"
            : "未检测到运行库拦截或待恢复残留。";
        var reason = available ? "" : !complete ? "状态不完整，请在运行库页重新核对或重试关闭。"
            : status.IfeoCount > 0 ? "仅有外部拦截，需在运行库页明确确认「强制清理拦截」。"
            : "没有本工具拦截或待恢复残留。";
        return new ReconcileSnapshot
        {
            Module = BackendModules.Runtime, Summary = summary, Complete = complete,
            Items =
            [
                new() { Id = "ifeo", Kind = "guard", State = status.IfeoCount > 0
                    ? $"IFEO 拦截：本工具 {status.IfeoManagedCount} 项、外部 {status.IfeoExternalCount} 项；当前范围 {status.ScopeManagedCount}/{status.IfeoTotal}"
                    : status.IfeoRestorePending ? "IFEO 有待处理记录或归属标记残留" : "未发现 IFEO 拦截",
                    Detail = status.GuardIssue },
                new() { Id = "ue4", Kind = "guard", State = status.Ue4PrereqDenied ? "UE4 旧版拒绝执行权限仍存在"
                    : status.Ue4RestorePending ? "UE4 权限仍有待处理记录" : "未发现 UE4 旧权限残留" },
                new() { Id = "redist", Kind = "redist", State = $"已安装 VC++ 2015-2022 运行库 {status.InstalledRedists.Count} 项" },
            ],
            RecoveryScopes =
            [
                new() { Id = "guard", Description = "关闭本工具运行库拦截并处理旧残留，完成后再次核对",
                    Available = available, Reason = reason,
                    AffectedCount = status.IfeoManagedCount + (status.IfeoRestorePending ? 1 : 0)
                        + (status.Ue4PrereqDenied || status.Ue4RestorePending ? 1 : 0) },
            ],
        };
    }

    public static RecoverOutcome BuildOutcome(OperationResult operation, RuntimeGuardStatus after)
    {
        var snapshot = Build(after);
        var verified = operation.Success && VerifiedClosed(after);
        return new RecoverOutcome
        {
            Scope = "guard", Success = operation.Success, Verified = verified, VerifiedSnapshot = snapshot,
            Message = operation.Message + (verified ? "\n复核通过：未检测到安装器拦截、旧权限残留或待恢复记录。"
                : "\n复核未通过，保留阻塞与原始证据。" + snapshot.Summary),
        };
    }
}
