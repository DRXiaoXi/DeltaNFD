namespace DeltaNFD.Services;

/// <summary>帧格旧电源备份中不完整（缺原值/损坏）的一条记录；仅用于「保留当前值」的人工确认流程。</summary>
public sealed record FrameIncompletePowerBackup(string FileName, string Path, string Fingerprint, string Reason);

/// <summary>
/// CPU/帧格后端的实际生效状态快照（T4）。由现有状态心跳生成，经状态帧推送给前端；
/// 独立只读游戏观测不要求启动会执行优化的监控循环。
/// 前端只据此展示「实际生效状态」，绝不把偏好当作生效状态，也不再自行建立第二份进程监控。
/// 所有时间/代次/身份字段都来自后端对目标的实时复核。
/// </summary>
public sealed class CpuFrameStatusSnapshot
{
    /// <summary>模块 id（固定 cpuframe）。</summary>
    public string Module { get; init; } = "cpuframe";

    /// <summary>后端模块版本（三段数字），供前端核对。</summary>
    public string ModuleVersion { get; init; } = "";

    /// <summary>会执行优化的两秒自动监控是否正在运行；独立只读观测不依赖它。</summary>
    public bool MonitorRunning { get; init; }

    /// <summary>当前目标代次（目标切换会自增；操作前后端会复核代次）。</summary>
    public long TargetGeneration { get; init; }

    /// <summary>当前目标显示名。</summary>
    public string TargetDisplayName { get; init; } = "";

    /// <summary>是否自定义目标（true 时按可执行文件绝对路径匹配，绝不只按进程名）。</summary>
    public bool IsCustomTarget { get; init; }

    /// <summary>自定义目标的绝对可执行文件路径（非自定义为空）。</summary>
    public string TargetExecutablePath { get; init; } = "";

    /// <summary>目标游戏当前是否在运行。</summary>
    public bool GameRunning { get; init; }

    /// <summary>Independent Delta Force presence; null means no trustworthy observation, irrespective of selected target.</summary>
    public bool? DeltaForceRunning { get; init; }
    public DateTimeOffset? DeltaForceObservedUtc { get; init; }
    public string DeltaForceObservationError { get; init; } = "";

    /// <summary>当前游戏进程 PID（未运行时为 null）。</summary>
    public int? GamePid { get; init; }

    /// <summary>游戏进程启动时间 UTC ticks（身份复核用；未知为 0）。</summary>
    public long GameStartTimeUtcTicks { get; init; }

    /// <summary>游戏进程映像绝对路径（身份复核用；无法读取时为空）。</summary>
    public string GameExecutablePath { get; init; } = "";

    /// <summary>当前匹配到的目标实例数（多开时 &gt; 1）。</summary>
    public int GameInstanceCount { get; init; }

    /// <summary>自动优先级偏好是否启用。</summary>
    public bool PriorityEnabled { get; init; }

    /// <summary>实际已应用的优先级（如 High）；未应用为空。</summary>
    public string PriorityApplied { get; init; } = "";

    /// <summary>实际已提升优先级的实例数。</summary>
    public int PriorityAppliedCount { get; init; }

    /// <summary>硬亲和性规则偏好是否启用。</summary>
    public bool AffinityRuleEnabled { get; init; }

    /// <summary>硬亲和性规则掩码（未启用为 0）。</summary>
    public ulong AffinityRuleMask { get; init; }

    /// <summary>双 CCD 专属调度是否实际生效（后端拥有此运行态，前端只读镜像）。</summary>
    public bool DualCcdSchedulingActive { get; init; }

    /// <summary>单 CCD 排除 CPU0 是否实际生效。</summary>
    public bool SingleCcdExcludeActive { get; init; }

    /// <summary>双 CCD 调度绑定的游戏 CCD 下标（未生效为 -1）。</summary>
    public int DualCcdGameIndex { get; init; } = -1;

    /// <summary>系统定时器高分辨率是否实际已请求。</summary>
    public bool TimerActive { get; init; }
    public int GamePowerAppliedCount { get; init; }
    public int GamePowerRestorePending { get; init; }
    public bool GamePowerHasError { get; init; }
    public string GamePowerStatus { get; init; } = "";

    /// <summary>最近一次内存清理的实际状态文字。</summary>
    public string MemoryCleanStatus { get; init; } = "";

    /// <summary>本轮监控是否已因游戏启动重启过 DWM。</summary>
    public bool DwmRestartedThisSession { get; init; }

    /// <summary>帧格临时优化（前台加速/降低省电延迟）是否实际已应用。</summary>
    public bool TempOptimizationApplied { get; init; }

    /// <summary>帧格电源锁定是否实际生效。</summary>
    public bool PowerLockActive { get; init; }

    /// <summary>已锁定的电源计划 GUID（未锁定为空）。</summary>
    public string PowerLockedSchemeGuid { get; init; } = "";

    /// <summary>最近一次动作的说明（供前端状态文字）。</summary>
    public string LastAction { get; init; } = "";

    /// <summary>最近一次错误的如实说明（无错误为空）。</summary>
    public string LastError { get; init; } = "";

    /// <summary>快照生成时间（UTC）。</summary>
    public DateTimeOffset UpdatedUtc { get; init; }
}

/// <summary>开关类命令参数（监控 / 优先级 / 计时器）。</summary>
public sealed class CpuFrameToggleArgs
{
    public bool Enabled { get; set; }
}

/// <summary>硬亲和性规则参数：掩码 0 = 解除规则。</summary>
public sealed class CpuFrameAffinityArgs
{
    public ulong Mask { get; set; }
}

/// <summary>双 CCD 调度参数。</summary>
public sealed class CpuFrameCcdArgs
{
    public int GameCcdIndex { get; set; }
}

/// <summary>帧格电源锁定参数。</summary>
public sealed class CpuFramePowerLockArgs
{
    public bool Enabled { get; set; }

    /// <summary>锁定的目标计划 GUID；空 = 锁定开启时的当前计划（不切换）。</summary>
    public string TargetGuid { get; set; } = "";
}

/// <summary>帧格临时优化参数（前台加速 + 降低省电延迟）。</summary>
public sealed class CpuFrameTempArgs
{
    public bool ForegroundResponsiveness { get; set; }

    public bool ForegroundPriority { get; set; }

    public bool NicPowerOff { get; set; }

    public bool UsbSuspendOff { get; set; }

    public bool PcieAspmOff { get; set; }

    /// <summary>机箱形态确认为笔记本时，网卡省电全禁 / 关闭 PCIe 省电由后端跳过。</summary>
    public bool LaptopExcluded { get; set; }
}

/// <summary>加速响应的期望状态；CoreParking 仅用于拒绝已退休功能。</summary>
public sealed class CpuFrameBoostArgs
{
    /// <summary>退休协议字段；true 时拒绝整个请求。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool CoreParking { get; set; }

    /// <summary>游戏进程禁用执行速度节能，仍受帧格与保存偏好约束。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool EcoQos { get; set; }

    public bool Timer { get; set; }
}

/// <summary>亲和性原值能否安全还原的参数（脱机切换前复核归属）。</summary>
public sealed class CpuFrameAffinityCheckArgs
{
    public ulong Mask { get; set; }
}

/// <summary>亲和性原值还原可行性结果。</summary>
public sealed class CpuFrameAffinityCheckResult
{
    public bool Ok { get; init; }

    public string Reason { get; init; } = "";
}

/// <summary>是否仍有待还原的亲和性记录。</summary>
public sealed class CpuFramePendingResult
{
    public bool Pending { get; init; }
}

/// <summary>bcdedit removememory 内存限制检测结果。</summary>
public sealed class CpuFrameMemoryLimitResult
{
    public bool Detected { get; init; }

    public int RemovedMb { get; init; }

    public string RawOutput { get; init; } = "";
}

/// <summary>按用户确认归档旧帧格电源备份的参数（记录逐字保留，不猜原值）。</summary>
public sealed class CpuFramePreservePowerArgs
{
    public List<FrameIncompletePowerBackup> Confirmed { get; set; } = [];
}
