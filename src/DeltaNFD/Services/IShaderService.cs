namespace DeltaNFD.Services;

/// <summary>N 卡驱动适配结论。</summary>
public enum ShaderDriverState
{
    /// <summary>未检测到 N 卡（或驱动信息读取失败）。</summary>
    NotApplicable,

    /// <summary>驱动过老（低于 572.83）：着色器渲染使用旧方法。</summary>
    TooOld,

    /// <summary>问题驱动系列（591 / 610 / 616）：着色器文件可能缺失。</summary>
    ProblemSeries,

    /// <summary>驱动状态正常。</summary>
    Ok,
}

public enum ShaderGpuDetectionState
{
    Unknown,
    NotPresent,
    Nvidia,
}

/// <summary>着色器维护状态（只读快照）。</summary>
public sealed class ShaderStatus
{
    /// <summary>是否检测到 N 卡。</summary>
    public required bool IsNvidia { get; init; }

    public ShaderGpuDetectionState GpuDetectionState { get; init; }

    /// <summary>游戏惯用格式驱动版本（如 "572.83"）；读取失败为空。</summary>
    public string DriverVersion { get; init; } = "";

    public required ShaderDriverState DriverState { get; init; }

    /// <summary>给玩家看的驱动体检结论（含建议）。</summary>
    public required string DriverAdvice { get; init; }

    /// <summary>PSOCache 目录路径（未找到为空）。</summary>
    public string PsoCachePath { get; init; } = "";

    /// <summary>PSOCache 体积（MB）；目录不存在为 0。</summary>
    public double PsoCacheSizeMb { get; init; }

    /// <summary>三角洲游戏进程是否在运行（运行中无法清理）。</summary>
    public required bool IsGameRunning { get; init; }
}

/// <summary>着色器健康检测结论等级。</summary>
public enum ShaderDiagLevel
{
    /// <summary>判定不适用（非 N 卡 / 驱动过老 / 未找到缓存目录），拒绝下结论。</summary>
    NotApplicable,

    /// <summary>信息性提示（尚未生成缓存 / 尚未预热等，无异常）。</summary>
    Info,

    /// <summary>结构正常。</summary>
    Normal,

    /// <summary>着色器异常（驱动非问题系列）：清理旧着色器 + 重新预热。</summary>
    Abnormal,

    /// <summary>着色器异常且驱动属问题系列；617.14 与 591/610/616 共用问题驱动判定，更高版本按大 NVPH 缺失判定。</summary>
    AbnormalDriverIssue,
}

/// <summary>着色器健康检测结果（纯只读）。</summary>
public sealed class ShaderDiagnosis
{
    public required ShaderDiagLevel Level { get; init; }

    /// <summary>一句话结论。</summary>
    public required string Summary { get; init; }

    /// <summary>判定明细（GameVer 文件夹清单、NVPH 文件大小等）。</summary>
    public required List<string> Details { get; init; }

    /// <summary>引导文案（下一步做什么）。</summary>
    public required string Advice { get; init; }
}

/// <summary>
/// 着色器维护服务：N 卡驱动版本体检（问题驱动 / 过老驱动提醒）+ 三角洲 PSOCache 旧着色器清理
/// + PSOCache 健康检测（AMD / Intel / NVIDIA 通用的 GameVer 临时计数判定，以及 NVIDIA 专用的
/// NVPH 结构判定）。
/// PSOCache 位于 {游戏根}\DeltaForce\Saved\PSOCache，游戏运行时被锁定，需退出游戏后清理。
/// </summary>
public interface IShaderService
{
    /// <summary>读取驱动体检结论与 PSOCache 状态（纯只读）。</summary>
    Task<ShaderStatus> GetStatusAsync();

    /// <summary>一键清除 PSOCache 旧着色器文件（游戏运行中会拒绝执行；清除后首次进图需重新编译着色器）。</summary>
    Task<OperationResult> ClearPsoCacheAsync();

    /// <summary>
    /// PSOCache 健康检测（纯只读），判定优先级：
    /// ① GameVer 达到 2 个及以上 → AMD / Intel / NVIDIA 通用异常判据；
    /// ② 单版本下 SM6\DXCache（缺少目录时回退 SM5\DXCache）的 NVPH 文件缺失 / 0KB 损坏判定（仅 N 卡且驱动 ≥572.83 时下结论）；
    ///    617.14 与 591/610/616 共用问题驱动判定；更高版本若 ≥256MB NVPH 少于两个，也判为驱动异常。
    /// </summary>
    Task<ShaderDiagnosis> DiagnoseAsync();
}
