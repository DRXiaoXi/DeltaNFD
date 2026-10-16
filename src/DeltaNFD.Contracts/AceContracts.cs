namespace DeltaNFD.Services;

/// <summary>一个 ACE 相关服务的信息。</summary>
public sealed class AceServiceInfo
{
    public required string Name { get; init; }

    /// <summary>服务注册表键是否存在（存在 = ACE 组件已安装）。</summary>
    public required bool Exists { get; init; }

    /// <summary>启动类型文字（自动/手动/驱动等）。</summary>
    public string StartMode { get; init; } = "未安装";
}

/// <summary>一个 ACE 相关路径的信息。</summary>
public sealed class AcePathInfo
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public required bool Exists { get; init; }

    public double SizeMb { get; init; }
}

/// <summary>ACE 扫描结果（只读快照）。</summary>
public sealed class AceScanResult
{
    /// <summary>
    /// true = 三角洲进程正在运行，已按需求**完全跳过**组件检测：
    /// 未读取 ACE 服务、未遍历 ACE 目录、未查询 ACE 注册表项。
    /// 此时 <see cref="Services"/> / <see cref="Paths"/> / <see cref="RegistryKeys"/> 全为空，
    /// **不代表"本机没装 ACE"**，消费方必须先判断本标志再解读这些列表。
    /// </summary>
    public required bool BlockedByGame { get; init; }

    /// <summary>正在运行的 ACE / 游戏相关进程。</summary>
    public required List<string> RunningProcesses { get; init; }

    /// <summary>阻止清理的进程（sguard64 / 游戏本体）。</summary>
    public required List<string> BlockingProcesses { get; init; }

    public required List<AceServiceInfo> Services { get; init; }

    public required List<AcePathInfo> Paths { get; init; }

    /// <summary>检测到仍然存在的 ACE 注册表项。</summary>
    public required List<string> RegistryKeys { get; init; }

    /// <summary>false = 存在阻止清理的进程，禁止执行。</summary>
    public bool CanClean => BlockingProcesses.Count == 0;

    public bool AnythingFound =>
        Services.Any(s => s.Exists) || Paths.Any(p => p.Exists) || RegistryKeys.Count > 0;
}

/// <summary>ACE 虚拟化环境单项检测的符合度。</summary>
public enum AceVirtStatus
{
    /// <summary>该项满足本工具检测标准。</summary>
    Pass,

    /// <summary>已确认不满足该项标准；不代表可以预测所有反作弊判定。</summary>
    Fail,

    /// <summary>配置或辅助信息提醒，不能据此断定当前有运行冲突。</summary>
    Warn,

    /// <summary>无法检测（需管理员 / 系统接口缺失 / 查询超时）。</summary>
    Unknown,
}

/// <summary>虚拟化环境单项检测结果。</summary>
public sealed class AceVirtCheckItem
{
    /// <summary>稳定标识，如 "hardware.cpu-virt"。</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>"hardware"（BIOS/固件层，需进 BIOS 修改）或 "system"（Windows 系统层，系统内可改）。</summary>
    public required string Category { get; init; }

    public required AceVirtStatus Status { get; init; }

    /// <summary>当前状态简述，如「已启用」「运行中」。未知时说明原因。</summary>
    public required string Detail { get; init; }

    /// <summary>不符合时的修复指引（对应腾讯游戏安全官方设置指引）。</summary>
    public required string Advice { get; init; }
}

/// <summary>
/// ACE 虚拟化环境检测结果（纯只读快照）。分两层：
/// 硬件层检查固件 CPU 虚拟化与当前内核 DMA 保护；系统层区分实际运行与配置提醒。
/// 内核 DMA 保护关闭不能单独证明 IOMMU 关闭；检测结果不是 ACE 通过保证。
/// </summary>
public sealed class AceVirtualizationCheckResult
{
    /// <summary>硬件层检测项（CPU 虚拟化、IOMMU/DMA 保护）。</summary>
    public required List<AceVirtCheckItem> Hardware { get; init; }

    /// <summary>系统层检测项（VBS、内核隔离、hypervisor、Hyper-V 功能、沙盒/WSL2 等）。</summary>
    public required List<AceVirtCheckItem> System { get; init; }

    /// <summary>主板品牌 / BIOS 模式（引导用户进 BIOS 用），来自 msinfo32 同源 CIM 查询。</summary>
    public string BoardInfo { get; init; } = "";

    public bool HasFailure => Hardware.Concat(System).Any(i => i.Status == AceVirtStatus.Fail);

    public bool HasUnknown => Hardware.Count == 0 || System.Count == 0 || Hardware.Concat(System).Any(i => i.Status == AceVirtStatus.Unknown);

    public bool HasWarnings => Hardware.Concat(System).Any(i => i.Status == AceVirtStatus.Warn);

    public bool HasFixableConflict => System.Any(i => i.Status == AceVirtStatus.Fail &&
        i.Id is "system.hypervisor-runtime" or "system.vbs" or "system.hvci");

    public bool AllPass => !HasFailure && !HasUnknown && !HasWarnings;

    public string OverallSummary => HasFailure
        ? "存在已确认未达标项：请查看具体状态与处理指引。"
        : HasUnknown
            ? "部分项目无法确认；读取失败不代表环境不合格，请按单项提示核对。"
            : HasWarnings
                ? "未发现已确认的冲突或未达标项；存在配置提醒，请查看详情。"
                : "本次检测项目均满足本工具的虚拟化兼容性标准；不代表 ACE 通过保证。";
}
public enum AceCoreCheckLevel
{
    /// <summary>未安装 / 未找到 ACE-CORE 文件（无需处理）。</summary>
    None,

    /// <summary>正常（只有 1 个 ACE-CORE 系列 sys 文件）。</summary>
    Normal,

    /// <summary>冗余异常（2 个及以上）：建议一键清除 ACE 后用启动器/WeGame 修复游戏。</summary>
    Abnormal,

    /// <summary>扫描目录失败，不能据此判定 ACE 状态。</summary>
    Failed,

    /// <summary>三角洲进程运行中，已跳过检测（未读取 ACE 目录），不能据此判定 ACE 状态。</summary>
    Blocked,
}

/// <summary>ACE-CORE 冗余检测结果（纯只读）。</summary>
public sealed class AceCoreCheckResult
{
    public required AceCoreCheckLevel Level { get; init; }

    /// <summary>检测到的 ACE-CORE 系列 sys 文件数量。</summary>
    public required int SysCount { get; init; }

    /// <summary>明细（相对路径 + 大小）。</summary>
    public required List<string> Files { get; init; }

    public required string Summary { get; init; }

    public required string Advice { get; init; }
}

/// <summary>
/// ACE（AntiCheatExpert）清理服务：等效 ACE 官方卸载程序，外加清理游戏目录内的 ACE 文件夹。
/// 清除范围：
/// 1) 进程：ACE-Tray / ACE-Service64/32 / ACE-Setup64/32
/// 2) 服务：ACE-BASE / ACE-GAME / ACE-BOOT / ACE-CORE / ACE-SSC-DRV64 / ACE-ADVT（自保护用 SDDL 接管后停止删除）
/// 3) 注册表：已知 ACE 卸载项；共享 AppDataLow 仅清理受限位置、安装路径归属已确认且导出备份已核验的 ACE 子键，绝不删除共享根
/// 4) 文件：%ProgramFiles%\AntiCheatExpert 整树 + {游戏根}\DeltaForce\Binaries\Win64\AntiCheatExpert
/// 被占用文件走 MoveFileEx 延迟到重启后删除。下次启动游戏时 ACE 会自动重新安装。
/// （自 C2 起，执行实现仅存在于 ACE 后端；前端只保留确认弹窗与结果展示。）
/// </summary>
public interface IAceService
{
    /// <summary>扫描 ACE 组件与运行中的 ACE/游戏进程（纯只读）。</summary>
    Task<AceScanResult> ScanAsync();

    /// <summary>一键清除 ACE（游戏或 sguard64 运行时拒绝执行）。</summary>
    Task<OperationResult> CleanAsync(IProgress<string>? progress = null);

    /// <summary>仅删除 C:\Program Files\AntiCheatExpert 及其子目录内 ACE-CORE*.sys；不处理服务、注册表或其他文件。</summary>
    Task<OperationResult> CleanCoreFilesAsync(IProgress<string>? progress = null);

    /// <summary>
    /// ACE-CORE 冗余文件检测（纯只读）：统计 %ProgramFiles%\AntiCheatExpert 下
    /// 文件名含 ACE-CORE 的 .sys 文件——只有 1 个为正常，2 个及以上为冗余异常。
    /// </summary>
    Task<AceCoreCheckResult> CheckCoreFilesAsync();

    /// <summary>
    /// ACE 虚拟化环境检测（纯只读）：硬件层（BIOS 中 VT-x/SVM、VT-d/IOMMU 是否开启）
    /// 与系统层（VBS / 内核隔离 HVCI / hypervisor 启动 / Hyper-V 功能 / 沙盒 / WSL2 是否占用虚拟化）。
    /// 不触碰任何 ACE 组件，游戏运行中也可安全调用。
    /// </summary>
    Task<AceVirtualizationCheckResult> CheckVirtualizationAsync();
}
