namespace DeltaNFD.Services;

public enum RuntimeGuardMode { BasicV14, Full }

public sealed record RuntimeUninstallPreview(List<VcRedistEntry> Entries, string Fingerprint);
public sealed class RuntimeUninstallAllArgs { public string Fingerprint { get; set; } = ""; }
public enum RuntimeRepairMode { MissingOrOutdated, Legacy2005And2008, FullReinstall }
public sealed record RuntimeRepairEnvironmentItem(string Name, string Status, string Detail, string Suggestion = "");
public sealed record RuntimeRepairEnvironmentReport(IReadOnlyList<RuntimeRepairEnvironmentItem> Items)
{
    public bool CanProceed => Items.Count > 0 && Items.All(i => i.Status is "passed" or "warning");
    public bool HasWarnings => Items.Any(i => i.Status == "warning");
    public string Summary => !CanProceed ? "当前环境不适合开始修复，请先处理未通过或无法确认的项目。"
        : HasWarnings ? "基础环境检查通过，但有提醒；请阅读后再确认修复。" : "基础环境检查通过，可以继续确认修复。";
    public string Details => string.Join("\n\n", Items.Select(i => $"{i.Name}：{(i.Status switch { "passed" => "通过", "warning" => "提醒", "blocked" => "未通过", _ => "无法确认" })}\n{i.Detail}"
        + (i.Suggestion.Length > 0 ? "\n处理建议：" + i.Suggestion : "")))
        + "\n\n预检不能保证安装一定成功，不会为了通过检查自动修改服务、系统权限或关闭安全软件。";
}
public sealed record RuntimeGuardLegacyEntry(string Name, string Fingerprint);

/// <summary>已安装的 VC++ 2015-2022 运行库信息。</summary>
public sealed class VcRedistInfo
{
    public required string DisplayName { get; init; }

    public required string Version { get; init; }

    /// <summary>x64 / x86。</summary>
    public required string Architecture { get; init; }
}

/// <summary>一个 Visual C++ 运行库卸载条目（来自卸载表，含卸载机制）。</summary>
public sealed class VcRedistEntry
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
public sealed class VcRedistSuboptimal
{
    /// <summary>分支与架构，如 "2005 (x64)"、"2015-2026 (x86)"。</summary>
    public required string Branch { get; init; }

    /// <summary>推荐（最适）版本。</summary>
    public required string RecommendedVersion { get; init; }

    /// <summary>当前已安装版本（空 = 未安装）。</summary>
    public string InstalledVersion { get; init; } = "";

    public string DisplayName { get; init; } = "";
}

/// <summary>Visual C++ v14 运行库扫描结果（只读快照）。v14 为不完善版本，非伪装包。</summary>
public sealed class VcRedistScanReport
{
    /// <summary>异常运行库：显示名匹配 "Visual C++ v14 Redistributable"（三角洲强制安装的不完善版本）。</summary>
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

/// <summary>运行库保护的整体状态（只读快照，供 UI 展示）。</summary>
public sealed class RuntimeGuardStatus
{
    /// <summary>本机已安装的 VC++ 2015-2022 运行库。</summary>
    public required List<VcRedistInfo> InstalledRedists { get; init; }

    /// <summary>拦截是否生效（全部 IFEO 键已写入）。</summary>
    public required bool IfeoApplied { get; init; }

    /// <summary>IFEO 已拦截的安装器名数量 / 名单总数。</summary>
    public required int IfeoCount { get; init; }

    public required int IfeoTotal { get; init; }
    public int IfeoManagedCount { get; init; }
    public int IfeoExternalCount { get; init; }
    public RuntimeGuardMode Mode { get; init; }
    public int ScopeManagedCount { get; init; }
    public int ScopeBlockedCount { get; init; }
    public IReadOnlyList<RuntimeGuardLegacyEntry> LegacyIfeo { get; init; } = [];
    public bool IfeoRestorePending { get; init; }
    public bool Ue4RestorePending { get; init; }
    public bool CanRepairLegacyUe4Acl { get; init; }
    /// <summary>读取过程中的非致命问题（状态文件损坏、权限无法读取等），为空表示无。</summary>
    public string GuardIssue { get; init; } = "";

    /// <summary>是否找到了三角洲的 UE4 前置包（运行库载体）。</summary>
    public required bool Ue4PrereqFound { get; init; }

    /// <summary>UE4 前置包路径（未找到为空）。</summary>
    public string Ue4PrereqPath { get; init; } = "";

    /// <summary>UE4 前置包上是否残留旧版本写入的 Everyone 拒绝执行（新版本只用 IFEO，关闭/强制清理时剥离）。</summary>
    public required bool Ue4PrereqDenied { get; init; }
}

/// <summary>
/// 运行库保护服务：基础 V14 三项 IFEO，完全防护十一项 IFEO（含 UE4 前置包）。
/// 无论来源是游戏、启动器还是手动安装，均按安装器文件名生效。
/// 手动安装运行库前需先关闭拦截。
/// </summary>
public interface IRuntimeGuardService
{
    Task<RuntimeRepairEnvironmentReport> CheckRepairEnvironmentAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new RuntimeRepairEnvironmentReport([new("环境检查", "unknown", "后端尚未提供环境检查，请更新工具。") ]));
    /// <summary>读取拦截状态与已安装运行库版本（纯只读；含一次计划任务式的进程调用，约 1 秒）。</summary>
    Task<RuntimeGuardStatus> GetStatusAsync();

    /// <summary>按已保存的范围开启拦截，写前备份并复核。</summary>
    Task<OperationResult> EnableAsync();

    /// <summary>关闭拦截：逐项移除本工具各版本写入的 IFEO 与旧版 UE4 拒绝执行；独立工具及其他程序的拦截不动。</summary>
    Task<OperationResult> DisableAsync();
    /// <summary>仅在拦截与恢复义务已清空后保存范围，不自动启用。</summary>
    Task<OperationResult> SelectModeAsync(RuntimeGuardMode mode);
    /// <summary>强制清理（玩家确认后调用）：不依赖记录，移除指定安装器名（空列表 = 全部）上的所有拦截并重置防护记录，快照写入日志。</summary>
    Task<OperationResult> CleanupLegacyIfeoAsync(IReadOnlyList<RuntimeGuardLegacyEntry> confirmedEntries);
    Task<OperationResult> RepairLegacyUe4AclAsync(string expectedPath);

    /// <summary>
    /// 检测各运行库分支的安装记录/版本和异常 v14 条目。All 供高级全量重装使用，
    /// 不等于运行库 DLL 完整性验证。
    /// </summary>
    Task<VcRedistScanReport> ScanVcRedistsAsync();

    /// <summary>
    /// Beta0.9 完整 AIO 修复：校验资源、卸载条目、完整 AIO、官方基线、复检。
    /// 模式字段保留协议兼容，仅接受 FullReinstall；旧局部请求明确拒绝，不扩大卸载范围。
    /// </summary>
    Task<OperationResult> RepairVcRedistAsync(IProgress<string>? progress = null,
        RuntimeRepairMode mode = RuntimeRepairMode.FullReinstall, CancellationToken cancellationToken = default,
        bool strongRepairMode = false);

    /// <summary>兼容性修复：先共享高帧性修复的受控 C++ 卸载阶段，再修复 DirectX 旧组件并安装 C++，最后复检。</summary>
    Task<OperationResult> RepairCompatibilityAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        bool strongRepairMode = false);

    /// <summary>
    /// 枚举「v14 命名」运行库条目（纯只读）：显示名包含「Visual C++ v14 Redistributable」的
    /// Burn 包 / INF 注册项。年份命名的条目（2015/2017/2019/2022/2026 等）不算在内。
    /// </summary>
    Task<List<VcRedistEntry>> GetV14EntriesAsync();

    /// <summary>
    /// 单独卸载名字带「v14」的运行库：只动 v14 命名条目（Bundle/INF），其余分支与年份命名条目
    /// （含 2026）一律不碰。逐条静默卸载；IFEO 防护处于开启状态时拒绝执行（卸载器自身会被拦截）。
    /// </summary>
    Task<OperationResult> UninstallV14Async(IProgress<string>? progress = null);
    /// <summary>只读预览全部微软 C++ 可再发行运行库；不包括 SDK/Debug/VSTO。指纹用于确认后复核。</summary>
    Task<RuntimeUninstallPreview> PreviewUninstallAllAsync() => throw new NotSupportedException("后端未提供全量卸载预览。");
    /// <summary>按已确认清单执行受控卸载，清单变化则拒绝；不强杀安装事务。</summary>
    Task<OperationResult> UninstallAllAsync(string fingerprint, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => Task.FromResult(OperationResult.Fail("后端未提供全量卸载。"));
}
