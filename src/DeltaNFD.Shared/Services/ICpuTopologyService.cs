namespace DeltaNFD.Services;

/// <summary>一个物理核心的信息。</summary>
public sealed class CpuCoreInfo
{
    /// <summary>所属处理器组。</summary>
    public required int Group { get; init; }

    /// <summary>核心掩码（组内位）。</summary>
    public required ulong Mask { get; init; }

    /// <summary>效率分类（0 = 效率核/普通核，>0 = 性能核）。</summary>
    public required int EfficiencyClass { get; init; }

    /// <summary>该核心是否启用了超线程（2 个逻辑处理器）。</summary>
    public required bool Smt { get; init; }

    /// <summary>该核心的逻辑处理器编号（全局编号）。</summary>
    public required List<int> LogicalProcessors { get; init; }
}

/// <summary>一个 CCD（Core Complex Die，优先按处理器 Die 拓扑划分）。</summary>
public sealed class CcdInfo
{
    public required int Index { get; init; }

    /// <summary>所属处理器组。</summary>
    public required int Group { get; init; }

    public required int CoreCount { get; init; }

    public required int LogicalCount { get; init; }

    /// <summary>该 CCD 的亲和性掩码（组内位）。</summary>
    public required ulong Mask { get; init; }

    /// <summary>L3 缓存容量（MB）。</summary>
    public double L3CacheMb { get; init; }
}

public enum CcdDetectionSource
{
    Unknown,
    ProcessorDie,
    L3Fallback,
}

/// <summary>CPU 拓扑检测结果。</summary>
public sealed class CpuTopology
{
    /// <summary>厂商：Intel / AMD / 其他。</summary>
    public required string Vendor { get; init; }

    public required string CpuName { get; init; }

    public required int PhysicalCores { get; init; }

    public required int LogicalProcessors { get; init; }

    /// <summary>处理器组数量。</summary>
    public required int GroupCount { get; init; }

    /// <summary>是否大小核混合架构（存在效率分类 > 0 的核心）。</summary>
    public required bool IsHybrid { get; init; }

    public required int PCoreCount { get; init; }

    public required int ECoreCount { get; init; }

    /// <summary>是否启用超线程（存在 1 核 2 线程的核心）。</summary>
    public required bool HasHyperThreading { get; init; }

    public required bool IsAmd { get; init; }

    /// <summary>AMD：是否多 CCD（检测到 2 个及以上有效处理器域）。</summary>
    public required bool IsAmdMultiCcd { get; init; }

    /// <summary>AMD：CCD 数量；无法判定或非 AMD 为 0。</summary>
    public required int CcdCount { get; init; }

    public CcdDetectionSource CcdDetectionSource { get; init; }

    public required List<CcdInfo> Ccds { get; init; }

    /// <summary>CCD 结构结论文字。</summary>
    public string CcdAdvice { get; init; } = "";

    /// <summary>全部逻辑处理器的亲和掩码。</summary>
    public required ulong AllMask { get; init; }

    /// <summary>性能核掩码（非大小核为 0）。</summary>
    public required ulong PCoreMask { get; init; }

    /// <summary>效率核掩码（非大小核为 0）。</summary>
    public required ulong ECoreMask { get; init; }

    /// <summary>给 UI 的建议文字。</summary>
    public string HybridAdvice { get; init; } = "";
}

/// <summary>
/// CPU 拓扑检测与核心锁定服务。
/// 数据来源：GetLogicalProcessorInformationEx（核心/Die/L3/处理器组）
/// + 注册表 HARDWARE\DESCRIPTION\System\CentralProcessor（厂商/名称）。
/// 核心锁定 = 设置三角洲进程的 ProcessorAffinity。
/// </summary>
public interface ICpuTopologyService
{
    /// <summary>读取完整 CPU 拓扑（纯只读）。</summary>
    Task<CpuTopology> GetTopologyAsync();

    /// <summary>判断三角洲游戏进程当前是否在运行。</summary>
    Task<bool> IsGameRunningAsync();

    /// <summary>
    /// 双CCD专属调度：把三角洲进程锁定到指定 CCD（CCD0 时排除 CPU0 给系统），
    /// 同时把所有其他用户进程推到另一个 CCD，实现游戏独占。
    /// </summary>
    Task<OperationResult> ApplyDualCcdSchedulingAsync(int gameCcdIndex);

    /// <summary>恢复双CCD调度前的默认状态（所有进程回到全部核心）。</summary>
    Task<OperationResult> RevertDualCcdSchedulingAsync();

    /// <summary>
    /// 检测是否存在 bcdedit removememory 内存限制。
    /// 部分"超级优化"工具用此方法把可用内存砍到约 12GB，让游戏以为在低配机运行。
    /// 返回 (是否检测到, 移除的内存 MB, 原始输出)。
    /// </summary>
    Task<(bool Detected, int RemovedMb, string RawOutput)> DetectMemoryLimitAsync();

    /// <summary>一键移除 bcdedit removememory 内存限制（需重启电脑生效）。</summary>
    Task<OperationResult> RemoveMemoryLimitAsync();

    /// <summary>单CCD优化调度：把三角洲进程的 CPU 亲和性排除 CPU0（让出给系统中断/内核）。
    /// 三角洲未运行时登记规则（游戏启动后由轮询自动应用并持续保持），同时强制覆写异类策略。</summary>
    Task<OperationResult> ExcludeCpu0FromGameAsync();

    /// <summary>恢复三角洲进程到全部核心的默认亲和性，并解除单CCD规则与异类策略覆写（游戏未运行也可解除）。</summary>
    Task<OperationResult> RestoreGameFullCoresAsync();
}
