namespace DeltaNFD.Services;

public enum Vc14Severity { Normal, Warning, Abnormal }
public sealed record Vc14Entry(string Code, string Name, string Version, string Architecture, string Role, string UpgradeCode);
public sealed record Vc14Notice(string Name, string Version, string Architecture, Vc14Severity Severity, string Detail);
/// <summary>只读诊断信息，不构成 MSI 身份、健康状态或可删除登记的证明。</summary>
public sealed record Vc14MsiPropertyDiagnostic(string Property, uint? ReturnCode, string Result, string Value)
{
    public bool ValueTruncated { get; init; }
}
public sealed record Vc14MsiInstanceDiagnostic(uint Context, string ContextName, string UserContext,
    IReadOnlyList<Vc14MsiPropertyDiagnostic> Properties);
public sealed record Vc14MsiContextDiagnostic(string Scope, uint ContextMask, uint? ReturnCode, string Result,
    bool Complete, IReadOnlyList<Vc14MsiInstanceDiagnostic> Instances, string Detail);
public sealed record Vc14MsiStateDiagnostic(string ProductCode, int StateCode, string StateName, string StateMeaning,
    DateTimeOffset CapturedUtc, IReadOnlyList<Vc14MsiContextDiagnostic> Contexts, string Summary, string Limitations)
{
    public IReadOnlyList<string> Sources { get; init; } = [];
    public IReadOnlyList<int> ObservedStateCodes { get; init; } = [];
}
public sealed record Vc14Scan(IReadOnlyList<Vc14Entry> Products, IReadOnlyList<Vc14Notice> Notices, IReadOnlyList<string> Issues)
{
    public IReadOnlyList<Vc14MsiStateDiagnostic> MsiStateDiagnostics { get; init; } = [];
    public bool MsiStateDiagnosticsTruncated { get; init; }
    // An incomplete scan cannot prove absence. ARP wrappers and Appx notices do not prove installed CRTs.
    public bool IsMissing => Products.Count == 0 && Issues.Count == 0;
    public string AbnormalTitle => IsMissing ? "缺少 C++ 14.x 运行库" : "14.x 兼容性异常：已安装 14.51 或 Visual C++ v14 组件";
    public bool HasAbnormal => IsMissing || Notices.Any(n => n.Severity == Vc14Severity.Abnormal);
    public bool HasWarning => Issues.Count > 0 || Notices.Any(n => n.Severity == Vc14Severity.Warning);
    public IEnumerable<Vc14Notice> GetAbnormalNotices()
    {
        if (IsMissing) yield return new("未检测到已安装的 C++ 14.x 运行库", "未安装", "x64/x86", Vc14Severity.Abnormal,
            "缺少运行库可能导致三角洲无法启动。请手动关闭运行库防护后，在下方预览并安装 14.42 双架构原包，也可使用全量修复。卸载列表残留和 Appx 系统组件包不能替代该运行库。");
        foreach (var notice in Notices.Where(n => n.Severity == Vc14Severity.Abnormal)) yield return notice;
    }
}
public enum Vc14RepairMode { Full, UninstallOnly, InstallOnly, RegistrationCleanup }
public sealed class Vc14RepairArgs
{
    public string Fingerprint { get; set; } = "";
    public Vc14RepairMode Mode { get; set; }
    public bool AllowTemporaryInstaller { get; set; }
}
public sealed record SafeInstallerPreparation(bool Required, bool CanEnable, string Fingerprint, string Detail);
public sealed record Vc14BundleChild(Vc14Entry Product, string DependencyFingerprint, string DependencyArgument);
public sealed record Vc14Continuation(string Id, DateTimeOffset CreatedUtc)
{
    public bool UninstallVerified { get; init; }
}

/// <summary>单个待处理 14.x 登记项：MSI 产品、Burn 包装、登记卸载命令或无法处理项。</summary>
public sealed record Vc14RemovalPlanItem(
    string Key, string Architecture, string Name, string Version, string Kind, string Identity, string Method, string Detail,
    string Role = "", string UpgradeCode = "", string Command = "", string Hive = "", string View = "", string DependencyFingerprint = "",
    string CacheSha256 = "", IReadOnlyList<Vc14BundleChild>? BundleChildren = null)
{
    /// <summary>msi=已核验 MSI；bundle=已核验包装；registry=登记 EXE 命令；inf=固定模板及目标均核验的旧 14.x INF。</summary>
    public bool CanRemove => Kind is "msi" or "bundle" or "registry" or "inf";
}

/// <summary>全量重装预览：完整卸载计划、身份指纹与放行条件。</summary>
public sealed record Vc14ReinstallPreview(Vc14Scan Scan, IReadOnlyList<Vc14RemovalPlanItem> RemovalPlan, string Fingerprint, bool CanRepair, string Blocker)
{
    public SafeInstallerPreparation? TemporaryInstaller { get; init; }
}
