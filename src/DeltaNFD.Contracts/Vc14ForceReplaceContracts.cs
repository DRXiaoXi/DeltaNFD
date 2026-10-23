namespace DeltaNFD.Services;

/// <summary>强制替换 14.x 系统 DLL 的逐项预览。Status：ok / replace / absent / blocked。</summary>
public sealed record Vc14ForceReplaceItem(
    string Architecture,
    string File,
    string CurrentVersion,
    string ExpectedVersion,
    string Status,
    string Detail,
    bool InUse,
    bool Downgrade);

/// <summary>
/// 强制替换预览：绕过 Windows Installer，直接用内置官方 14.42.34433 文件覆盖
/// System32/SysWOW64 的 14.x DLL。CanRun=false 时 Blocker 说明原因；Fingerprint 用于执行前复核。
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
    int DowngradeCount)
{
    public string Summary => CanRun
        ? $"待替换 {ReplaceCount} 个，已匹配 {SkipCount} 个" + (DowngradeCount > 0 ? $"，其中 {DowngradeCount} 个当前版本较新（将降级）" : "")
        : Blocker;
}

/// <summary>执行强制替换：必须回传预览指纹。</summary>
public sealed class Vc14ForceReplaceArgs
{
    public string Fingerprint { get; set; } = "";
}
