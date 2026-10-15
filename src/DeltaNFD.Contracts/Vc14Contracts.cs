namespace DeltaNFD.Services;

public enum Vc14Severity { Normal, Warning, Abnormal }
public sealed record Vc14Entry(string Code, string Name, string Version, string Architecture, string Role, string UpgradeCode);
public sealed record Vc14Notice(string Name, string Version, string Architecture, Vc14Severity Severity, string Detail);
public sealed record Vc14Scan(IReadOnlyList<Vc14Entry> Products, IReadOnlyList<Vc14Notice> Notices, IReadOnlyList<string> Issues)
{
    public bool HasAbnormal => Notices.Any(n => n.Severity == Vc14Severity.Abnormal);
    public bool HasWarning => Issues.Count > 0 || Notices.Any(n => n.Severity == Vc14Severity.Warning);
}
public sealed class Vc14RepairArgs { public string Fingerprint { get; set; } = ""; }

/// <summary>单个待处理 14.x 登记项：MSI 产品、Burn 包装、登记卸载命令或无法处理项。</summary>
public sealed record Vc14RemovalPlanItem(
    string Key, string Architecture, string Name, string Version, string Kind, string Identity, string Method, string Detail,
    string Role = "", string UpgradeCode = "", string Command = "", string Hive = "", string View = "")
{
    /// <summary>msi=已核验 MSI；bundle=已核验包装；registry=登记 EXE 命令；inf=固定模板及目标均核验的旧 14.x INF。</summary>
    public bool CanRemove => Kind is "msi" or "bundle" or "registry" or "inf";
}

/// <summary>全量重装预览：完整卸载计划、身份指纹与放行条件。</summary>
public sealed record Vc14ReinstallPreview(Vc14Scan Scan, IReadOnlyList<Vc14RemovalPlanItem> RemovalPlan, string Fingerprint, bool CanRepair, string Blocker);
