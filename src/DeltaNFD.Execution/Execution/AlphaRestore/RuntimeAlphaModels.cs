namespace DeltaNFD.Services.AlphaRestore;

internal sealed record RuntimeUninstallPreview(List<VcRedistEntry> Entries, string Fingerprint, bool AllowNonstandard = false);
internal enum RuntimeRepairMode { FullReinstall = 2 }
internal sealed record RuntimeRepairEnvironmentItem(string Name, string Status, string Detail, string Suggestion = "");
internal sealed record RuntimeRepairEnvironmentReport(IReadOnlyList<RuntimeRepairEnvironmentItem> Items)
{
    public bool CanProceed => Items.Count > 0 && Items.All(i => i.Status is "passed" or "warning");
    public bool HasWarnings => Items.Any(i => i.Status == "warning");
    public string Summary => !CanProceed ? "当前环境不适合开始修复，请先处理未通过或无法确认的项目。"
        : HasWarnings ? "基础环境检查通过，但有提醒；请阅读后再确认修复。" : "基础环境检查通过，可以继续确认修复。";
    public string Details => string.Join("\n\n", Items.Select(i => $"{i.Name}：{(i.Status switch { "passed" => "通过", "warning" => "提醒", "blocked" => "未通过", _ => "无法确认" })}\n{i.Detail}"
        + (i.Suggestion.Length > 0 ? "\n处理建议：" + i.Suggestion : "")))
        + "\n\n预检不能保证安装一定成功，不会为了通过检查自动修改服务、系统权限或关闭安全软件。";
}

/// <summary>一个 Visual C++ 运行库卸载条目（来自卸载表，含卸载机制）。</summary>
internal sealed class VcRedistEntry
{
    public required string DisplayName { get; init; }

    public string DisplayVersion { get; init; } = "";

    /// <summary>x64 / x86（按显示名判断）。</summary>
    public required string Architecture { get; init; }

    /// <summary>卸载表子键名（MSI 条目 = 产品码 GUID）。</summary>
    public required string KeyName { get; init; }

    /// <summary>原始登记类型：Msi / Inf / Bundle / Unknown。支持的条目统一解析为 MSI，不执行包装卸载入口。</summary>
    public required string UninstallKind { get; init; }

    public string UninstallString { get; init; } = "";

    public string QuietUninstallString { get; init; } = "";
}

/// <summary>一项「非最适版本」判定结果（分支版本低于推荐版本或未安装，黄色提醒）。</summary>
internal sealed class VcRedistSuboptimal
{
    /// <summary>分支与架构，如 "2005 (x64)"、"2015-2026 (x86)"。</summary>
    public required string Branch { get; init; }

    /// <summary>推荐（最适）版本。</summary>
    public required string RecommendedVersion { get; init; }

    /// <summary>当前登记/已核对的 MSI 版本（空的原因由 Reason 区分为缺失或无法核对）。</summary>
    public string InstalledVersion { get; init; } = "";

    public string DisplayName { get; init; } = "";

    /// <summary>missing / version-unknown / below-recommended；旧协议缺省为空。</summary>
    public string Reason { get; init; } = "";

    /// <summary>仅推荐版本差异，不作为安装失败或 DLL 损坏的证据。</summary>
    public bool IsRecommendationOnly { get; init; }
}

/// <summary>运行库扫描只读快照。v14 异常是用户确认的三角洲掉帧兼容性策略，不等于 DLL 损坏。</summary>
internal sealed class VcRedistScanReport
{
    /// <summary>异常运行库：显示名匹配 "Visual C++ v14"；保留三角洲掉帧兼容性标记。</summary>
    public required List<VcRedistEntry> Abnormal { get; init; }

    /// <summary>非最适版本条目：各分支（2005~2013 / 2015-2026）低于推荐版本或未安装。</summary>
    public required List<VcRedistSuboptimal> Suboptimal { get; init; }

    /// <summary>系统上全部 Visual C++ 运行库条目（含异常与正常，重装运行库时逐一卸载用）。</summary>
    public required List<VcRedistEntry> All { get; init; }

    /// <summary>是否存在异常条目（v14 运行库）。</summary>
    public bool HasAbnormal => Abnormal.Count > 0;

    /// <summary>是否存在非最适版本条目（低于推荐版本或未安装）。</summary>
    public bool HasSuboptimal => Suboptimal.Count > 0;

    public required string Advice { get; init; }
}

