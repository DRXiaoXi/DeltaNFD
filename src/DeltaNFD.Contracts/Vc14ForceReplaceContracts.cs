namespace DeltaNFD.Services;

/// <summary>
/// 强制替换 14.x 系统 DLL 的逐项预览。Status：ok（已是目标版本且权限正常）/
/// replace（需替换）/ acl（内容已匹配但普通用户不可读，只需修复权限）/
/// absent（缺失，强制替换不创建）/ blocked（无法检查或链接）。
/// AclBroken 表示普通用户读不到（权限被改坏）；Locked 表示管理员组无写权限
/// （通常为 TrustedInstaller 所有，替换前需临时接管所有权）。
/// </summary>
public sealed record Vc14ForceReplaceItem(
    string Architecture,
    string File,
    string CurrentVersion,
    string ExpectedVersion,
    string Status,
    string Detail,
    bool InUse,
    bool Downgrade,
    bool AclBroken = false,
    bool Locked = false);

/// <summary>
/// 强制替换预览：绕过 Windows Installer，直接用所选内置官方 14.44 文件覆盖
/// System32/SysWOW64 的 14.x DLL，并修复被改坏的权限、按需临时接管文件属主
/// 后恢复原 ACL。CanRun=false 时 Blocker 说明原因；Fingerprint 用于执行前复核。
/// </summary>
public sealed record Vc14ForceReplacePreview(
    bool CanRun,
    string Blocker,
    string Fingerprint,
    IReadOnlyList<Vc14ForceReplaceItem> Items,
    string PayloadVersion,
    int ReplaceCount,
    int SkipCount,
    int AbsentCount,
    int DowngradeCount,
    int AclRepairCount = 0,
    int LockedCount = 0)
{
    public Vc14Baseline Baseline { get; init; } = Vc14Baseline.V144;
    public string Summary => CanRun
        ? $"目标 {PayloadVersion}：待替换 {ReplaceCount} 个，仅修复权限 {AclRepairCount} 个，已匹配 {SkipCount} 个"
            + (DowngradeCount > 0 ? $"，其中 {DowngradeCount} 个当前版本较新（将降级）" : "")
            + (LockedCount > 0 ? $"，{LockedCount} 个需临时接管文件所有权" : "")
        : Blocker;
}

/// <summary>执行强制替换：必须回传预览指纹与同一基线。</summary>
public sealed class Vc14ForceReplaceArgs
{
    public string Fingerprint { get; set; } = "";
    public Vc14Baseline Baseline { get; set; } = Vc14Baseline.V144;
}
