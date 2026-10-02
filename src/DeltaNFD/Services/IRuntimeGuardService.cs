namespace DeltaNFD.Services;

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

    /// <summary>卸载机制：Msi（msiexec /x）/ Inf（rundll32 advpack）/ Bundle（QuietUninstallString）/ Unknown。</summary>
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
    public bool Ue4RestorePending { get; init; }

    /// <summary>是否找到了三角洲的 UE4 前置包（运行库载体）。</summary>
    public required bool Ue4PrereqFound { get; init; }

    /// <summary>UE4 前置包路径（未找到为空）。</summary>
    public string Ue4PrereqPath { get; init; } = "";

    /// <summary>UE4 前置包是否已被 Deny-Execute ACL 拦截。</summary>
    public required bool Ue4PrereqDenied { get; init; }
}

/// <summary>
/// 运行库保护服务：拦截**所有**运行库安装器（VC++ 全系列 2005~2022、DirectX、UE4/UE5 前置包等），
/// 无论来源是游戏、启动器还是手动安装，一律按文件名 IFEO 劫持 + 关键文件 ACL 拒绝执行。
/// 手动安装运行库前需先关闭拦截。
/// </summary>
public interface IRuntimeGuardService
{
    /// <summary>读取拦截状态与已安装运行库版本（纯只读；含一次计划任务式的进程调用，约 1 秒）。</summary>
    Task<RuntimeGuardStatus> GetStatusAsync();

    /// <summary>开启拦截（IFEO + ACL，写前备份，全部可还原）。</summary>
    Task<OperationResult> EnableAsync();

    /// <summary>关闭拦截（移除 IFEO 与 ACL）。</summary>
    Task<OperationResult> DisableAsync();

    /// <summary>
    /// Visual C++ v14 运行库检测（纯只读）：只检测显示名匹配 "Visual C++ v14 Redistributable" 的条目，
    /// 其余 C++ 运行库不参与检测。All 列表供「重装运行库」逐一卸载使用。
    /// </summary>
    Task<VcRedistScanReport> ScanVcRedistsAsync();

    /// <summary>
    /// 重装运行库：先卸载系统上全部 C++ 运行库（MSI 用 msiexec /x、INF 族用其卸载命令、
    /// Bundle 用静默卸载串），再静默运行内置 AIO Installer.cmd /quiet；
    /// 卸载前做 SHA256 校验，逐包保留结果与详细日志，重装后复检分支版本。约 3–5 分钟。
    /// </summary>
    Task<OperationResult> RepairVcRedistAsync(IProgress<string>? progress = null);

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
}
