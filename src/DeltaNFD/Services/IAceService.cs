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

/// <summary>ACE-CORE 冗余检测结论等级。</summary>
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
/// 3) 注册表：卸载项 Uninstall\{141E7813-...}、HKLM\SOFTWARE\AppDataLow、HKU\.DEFAULT\SOFTWARE\AppDataLow
/// 4) 文件：%ProgramFiles%\AntiCheatExpert 整树 + {游戏根}\DeltaForce\Binaries\Win64\AntiCheatExpert
/// 被占用文件走 MoveFileEx 延迟到重启后删除。下次启动游戏时 ACE 会自动重新安装。
/// </summary>
public interface IAceService
{
    /// <summary>扫描 ACE 组件与运行中的 ACE/游戏进程（纯只读）。</summary>
    Task<AceScanResult> ScanAsync();

    /// <summary>一键清除 ACE（游戏或 sguard64 运行时拒绝执行）。</summary>
    Task<OperationResult> CleanAsync(IProgress<string>? progress = null);

    /// <summary>
    /// ACE-CORE 冗余文件检测（纯只读）：统计 %ProgramFiles%\AntiCheatExpert 下
    /// 文件名含 ACE-CORE 的 .sys 文件——只有 1 个为正常，2 个及以上为冗余异常。
    /// </summary>
    Task<AceCoreCheckResult> CheckCoreFilesAsync();
}
