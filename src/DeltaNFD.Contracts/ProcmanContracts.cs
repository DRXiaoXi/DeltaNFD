namespace DeltaNFD.Services;

public sealed record ProcmanMemoryCandidate(ProcessIdentityArgs Identity, string Name, long WorkingSetBytes, double CpuPercent);
public sealed record ProcmanMemoryPreview(string Id, DateTimeOffset ExpiresUtc, IReadOnlyList<ProcmanMemoryCandidate> Candidates, string Message)
{
    public string Mode { get; init; } = ProcmanMemoryModes.Normal;
}
public static class ProcmanMemoryModes
{
    public const string Normal = "normal";
    public const string Aggressive = "aggressive";
    public const string DeltaStable = "delta-stable";
}
public sealed class ProcmanMemoryTrimArgs
{
    public string PreviewId { get; set; } = "";
    public List<ProcessIdentityArgs> Processes { get; set; } = [];
}
public sealed record ProcmanMemoryTrimItem(int Pid, string Name, string Status, string Message, long? BeforeBytes = null, long? AfterBytes = null);
public sealed record ProcmanMemoryTelemetry(ulong TotalPhysicalBytes, ulong AvailablePhysicalBytes, uint LoadPercent,
    ulong? CommitBytes = null, ulong? CommitLimitBytes = null);
public sealed record ProcmanMemoryTrimReport(IReadOnlyList<ProcmanMemoryTrimItem> Items, string Message,
    ProcmanMemoryTelemetry? Before = null, ProcmanMemoryTelemetry? After = null);

public sealed record ProcmanBackgroundConfig
{
    public int SchemaVersion { get; init; } = 1;
    public bool AutomaticMemoryEnabled { get; init; }
    public int MemoryPressurePercent { get; init; } = 85;
    /// <summary>手动/自动共用的单次候选上限，默认 5，范围 1–256。</summary>
    public int MemoryBatchLimit { get; init; } = 5;
    /// <summary>自动批次及同一进程重复回收的最短间隔，默认 600 秒，范围 60–86400。</summary>
    public int MemoryCooldownSeconds { get; init; } = 600;
    public string MemoryMode { get; init; } = ProcmanMemoryModes.Normal;
    public List<string> ExcludedExecutablePaths { get; init; } = [];
    public ProcmanSuppressionOptions Suppression { get; init; } = new();
}

public sealed record ProcmanSuppressionOptions
{
    public int DefaultsVersion { get; init; } = 2;
    public int CpuBusyThreshold { get; init; } = 65;
    public int CpuBusySeconds { get; init; } = 3;
    public int CpuIdleThreshold { get; init; } = 50;
    public int CpuIdleSeconds { get; init; } = 6;
    public int CooldownSeconds { get; init; } = 15;
    public int CandidateCpuFloorPercent { get; init; } = 0;
    public int MaxAdjustPerRound { get; init; } = 1;
}
public sealed record ProcmanBackgroundStatus(ProcmanBackgroundConfig Config, bool AutomaticMemoryActive,
    DateTimeOffset? LastCheckUtc, ProcmanMemoryTrimReport? LastReport, string Note = "", string Feedback = "", int EffectiveCandidateCpuFloorPercent = 0,
    int EffectiveMemoryCooldownSeconds = 600);

// ---------------- 进程管理（procman）合同 ----------------
// 设计约束（notes/进程管理与响应性保护待做清单.md）：
// - 进程身份 = PID + 启动时间 UTC ticks + 完整映像路径；写入前必须重新核对身份。
// - 每项能力区分「配置值」与「实际值」：无法回读确认时绝不报告成功。
// - 枚举/详情是只读操作：读取失败不得产生修改记录。
// - 亲和性/CPU 集合按处理器组描述，禁止用组 0 单个 ulong 表示全部 CPU。

/// <summary>进程身份三元组：PID + 启动时间（UTC ticks）+ 完整映像路径。</summary>
public sealed record ProcessIdentityArgs
{
    public int Pid { get; set; }

    /// <summary>枚举时读到的启动时间（UTC ticks）。0 = 未知，此时后端拒绝一切写入。</summary>
    public long StartTimeUtcTicks { get; set; }

    public string ExecutablePath { get; set; } = "";
}

/// <summary>进程管理单页枚举请求：offset/limit 分页 + 可选搜索词（只按进程名过滤）。</summary>
public sealed class ProcmanListArgs
{
    /// <summary>all (legacy), general (excluding Delta Force), or delta.</summary>
    public string Scope { get; set; } = "all";
    public string SortOrder { get; set; } = "memory";
    public int Offset { get; set; }

    /// <summary>页大小上限 500，超出由后端截断并在结果中说明。</summary>
    public int Limit { get; set; } = 200;

    /// <summary>进程名子串过滤（不区分大小写）；空 = 不过滤。不收集命令行。</summary>
    public string? NameFilter { get; set; }
}

/// <summary>进程列表条目：只含只读信息，修改必须另走 set 命令。</summary>
public sealed class ProcmanProcessEntry
{
    public int Pid { get; init; }
    public long StartTimeUtcTicks { get; init; }
    public string Name { get; init; } = "";
    public string ExecutablePath { get; init; } = "";

    /// <summary>近一个采样周期内的 CPU 占用百分比（0-100·单核可>100→按全机归一；未知 null）。</summary>
    public double? CpuPercent { get; init; }

    /// <summary>工作集 MB（未知 null）。</summary>
    public double? WorkingSetMb { get; init; }

    /// <summary>本工具当前对该进程持有的控制属性数（>0 时详情里列出具体属性）。</summary>
    public int OwnedPropertyCount { get; init; }
}

/// <summary>分页枚举结果。</summary>
public sealed class ProcmanListResult
{
    public List<ProcmanProcessEntry> Processes { get; init; } = [];

    /// <summary>过滤后的总进程数（分页用）。</summary>
    public int TotalCount { get; init; }

    /// <summary>枚举期间被拒绝访问而跳过的进程数（如实呈现，不算失败）。</summary>
    public int SkippedInaccessible { get; init; }

    public string Note { get; init; } = "";
}

/// <summary>进程详情查询参数。</summary>
public sealed class ProcmanDetailArgs
{
    public ProcessIdentityArgs Identity { get; set; } = new();
}

/// <summary>单项能力描述：支持状态 + 当前实际值 + 控制方 + 错误原因。</summary>
public sealed class ProcmanCapabilityInfo
{
    /// <summary>能力名：priority / affinity / cpuSet / memoryPriority / ecoQos / priorityBoost / ioPriority / gpuPriority / gpuPreference。</summary>
    public string Capability { get; init; } = "";

    /// <summary>supported = 本机可读写；readOnly = 只能读；unsupported = 本构建系列不开放。</summary>
    public string Support { get; init; } = "";

    /// <summary>当前实际值（能力相关的字符串表示；未知为空）。</summary>
    public string CurrentValue { get; init; } = "";

    /// <summary>当前控制方：manual（当前设置）/ rule:规则名（总是）/ game（游戏模式）/ responsiveness（后台抑制）/ system（无人控制）。</summary>
    public string Owner { get; init; } = "system";

    /// <summary>读取/判断失败原因（成功为空）。有错误时 CurrentValue 不可信。</summary>
    public string Error { get; init; } = "";
}

/// <summary>进程详情结果：身份复核 + 每项能力状态。</summary>
public sealed class ProcmanDetailResult
{
    /// <summary>身份复核是否通过；false 时其余字段为空且 IdentityError 说明原因（已退出/PID 复用/路径不符/访问拒绝）。</summary>
    public bool IdentityOk { get; init; }

    public string IdentityError { get; init; } = "";

    public ProcmanProcessEntry Process { get; init; } = new();

    public List<ProcmanCapabilityInfo> Capabilities { get; init; } = [];

    /// <summary>本工具在此进程上持有的修改记录（属性名 + 原值 + 时间）。</summary>
    public List<ProcmanHeldChange> HeldChanges { get; init; } = [];
}

/// <summary>本工具持有的单条修改记录。</summary>
public sealed class ProcmanHeldChange
{
    public string Capability { get; init; } = "";
    public string OriginalValue { get; init; } = "";
    public string AppliedValue { get; init; } = "";
    public string Owner { get; init; } = "";
    public DateTimeOffset AppliedUtc { get; init; }
}

/// <summary>修改进程属性请求。未提供的能力字段保持不动（null = 不修改）。</summary>
public sealed class ProcmanSetArgs
{
    public ProcessIdentityArgs Identity { get; set; } = new();

    /// <summary>CPU 优先级：Idle/BelowNormal/Normal/AboveNormal/High/RealTime。RealTime 仅手动确认路径允许。</summary>
    public string? CpuPriority { get; set; }

    /// <summary>硬亲和性：按处理器组的掩码列表；null = 不修改，空列表 = 解除全部限制。</summary>
    public List<ProcmanGroupMask>? AffinityMasks { get; set; }

    /// <summary>CPU 集合（软亲和性）：逻辑处理器描述；null = 不修改，空列表 = 清除默认集合。</summary>
    public List<ProcmanGroupMask>? CpuSetMasks { get; set; }

    /// <summary>内存优先级 0-5；null = 不修改。</summary>
    public int? MemoryPriority { get; set; }
    /// <summary>GPU 调度档位 0–4，实时档位不开放；不支持或权限不足则拒绝。</summary>
    public int? GpuPriority { get; set; }

    /// <summary>EcoQoS：true = 启用效率模式（允许降速），false = 禁用；null = 不修改。</summary>
    public bool? EcoQosEnabled { get; set; }

    /// <summary>Windows 动态优先级提升：null = 不修改。注意 API 语义反转（禁用 = 设置 DisableBoost）。</summary>
    public bool? PriorityBoost { get; set; }

    /// <summary>I/O 优先级：VeryLow/Low/Normal/High（Critical 系统保留不开放）；null = 不修改。</summary>
    public string? IoPriority { get; set; }

    /// <summary>GPU 首选项：省电/高性能/Windows 默认（按应用保存，生效可能需重启目标程序）；null = 不修改。</summary>
    public string? GpuPreference { get; set; }

    /// <summary>用户已确认接管（目标属性当前由游戏模式控制时必须为 true）。</summary>
    public bool? ConfirmedTakeover { get; set; }
}

/// <summary>按处理器组的亲和性/CPU 集合掩码。</summary>
public sealed record ProcmanGroupMask(ushort Group, ulong Mask);

/// <summary>逐属性结果：成功/失败分开呈现，部分成功合法。</summary>
public sealed class ProcmanSetResult
{
    /// <summary>身份复核结果；false 时拒绝一切写入。</summary>
    public bool IdentityOk { get; init; }

    public string IdentityError { get; init; } = "";

    public List<ProcmanCapabilityOutcome> Outcomes { get; init; } = [];

    /// <summary>
    /// 是否仍有"结果未知/已保留修改记录待核对"的属性。只有它为 true 时才应建立未确认阻塞；
    /// 全部拒绝、全部回滚成功、受保护零操作等已知结果必须为 false。
    /// </summary>
    public bool RequiresReview { get; init; }
}

/// <summary>单项能力的修改结果。</summary>
public sealed class ProcmanCapabilityOutcome
{
    public string Capability { get; init; } = "";

    /// <summary>applied = 已设置且回读一致；rejected = 前置校验拒绝；failed = 调用或回读失败。</summary>
    public string Status { get; init; } = "";

    public string Message { get; init; } = "";

    /// <summary>回读到的实际值（applied 时非空）。</summary>
    public string ActualValue { get; init; } = "";

    /// <summary>该属性的失败是否留下恢复义务（结果未知/记录已保留）；前置拒绝与已回滚失败为 false。</summary>
    public bool RequiresReview { get; init; }
}

/// <summary>还原属性请求：释放指定或全部本工具持有的修改。</summary>
public sealed class ProcmanResetArgs
{
    public ProcessIdentityArgs Identity { get; set; } = new();

    /// <summary>空 = 还原该进程全部持有属性；否则只还原列出的能力。</summary>
    public List<string>? Capabilities { get; set; }
}

/// <summary>释放接管：恢复接管前状态并交回控制权（与 reset 的区别：交还原控制方继续工作）。</summary>
public sealed class ProcmanReleaseArgs
{
    public ProcessIdentityArgs Identity { get; set; } = new();

    public List<string>? Capabilities { get; set; }
}

/// <summary>进程管理状态快照（空闲状态推送 + status 命令共用）。</summary>
public sealed class ProcmanStatusSnapshot
{
    public string Module { get; init; } = "procman";
    public int BackendPid { get; init; }
    public string BackendPriorityClass { get; init; } = "";

    /// <summary>规则引擎是否在运行。</summary>
    public bool RulesEngineRunning { get; init; }

    /// <summary>后台抑制是否启用（默认关闭）。协议字段名称保留兼容。</summary>
    public bool ResponsivenessEnabled { get; init; }

    /// <summary>当前后台抑制参数，供帧格页重新打开时准确回显；null 表示尚未配置。</summary>
    public ProcmanResponsivenessConfigArgs? ResponsivenessConfig { get; init; }

    /// <summary>当前持有的修改记录总数。</summary>
    public int HeldChangeCount { get; init; }

    /// <summary>待核对（上次会话遗留）的修改记录数。</summary>
    public int UnreconciledCount { get; init; }

    /// <summary>最近一次动作说明。</summary>
    public string LastAction { get; init; } = "";

    /// <summary>最近一次错误说明（无错误为空）。</summary>
    public string LastError { get; init; } = "";

    public DateTimeOffset UpdatedUtc { get; init; }
}

/// <summary>只读核对快照。</summary>
public sealed class ProcmanReconcileResult
{
    /// <summary>逐条核对结论：held 的每条记录 → restored/conflict/missing。</summary>
    public List<ProcmanReconcileEntry> Entries { get; init; } = [];

    public string Summary { get; init; } = "";
}

public sealed class ProcmanReconcileEntry
{
    public int Pid { get; init; }
    public string ExecutablePath { get; init; } = "";
    public string Capability { get; init; } = "";

    /// <summary>restored = 已按记录还原；conflict = 当前值与记录不一致（保留记录待人工）；missing = 进程已退出。</summary>
    public string Status { get; init; } = "";

    public string Message { get; init; } = "";
}

/// <summary>启动时按日志恢复参数。</summary>
public sealed class ProcmanRecoverArgs
{
    /// <summary>空 = 全部；否则只恢复指定能力。</summary>
    public string? Scope { get; set; }
}

/// <summary>处理器组拓扑结果。</summary>
public sealed class ProcmanTopologyResult
{
    public List<ProcmanGroupMask> Groups { get; init; } = [];
    public List<ProcmanLogicalCpu> Processors { get; init; } = [];
    public string ProcessorName { get; init; } = "";
    public string DetectionNote { get; init; } = "";

    /// <summary>
    /// 本机是否支持 CPU 集合组掩码 API（Get/SetProcessDefaultCpuSetMasks，Windows 11 / Server 2022 及以上）。
    /// false 时界面必须禁用 CPU 集合入口，后端拒绝保存 CPU 集合规则；CPU 亲和性不受影响。
    /// </summary>
    public bool CpuSetMasksSupported { get; init; }
}

/// <summary>进程管理独立拓扑；编号仅用于当前机器，未知字段不按逻辑编号推算。</summary>
public sealed class ProcmanLogicalCpu
{
    public ushort Group { get; init; }
    public int Index { get; init; }
    public int? Core { get; set; }
    public int? Thread { get; set; }
    public int ThreadCount { get; set; }
    public uint? CpuSetId { get; set; }
    public byte? EfficiencyClass { get; set; }
    /// <summary>P/E 只由逐核 CPUID 确认；空字符串表示未确认。</summary>
    public string CoreKind { get; set; } = "";
    public int? Package { get; set; }
    public int? Die { get; set; }
    public int? NumaNode { get; set; }
    public int? L3 { get; set; }
    public uint? L3Bytes { get; set; }
    public int? Ccd { get; set; }
}

// ---------------- P2：持久规则 ----------------

/// <summary>规则条目：按完整 EXE 路径（+可选用户身份）匹配，同一目标同一属性仅一条。</summary>
public sealed class ProcmanRuleEntry
{
    /// <summary>完整 EXE 路径（规则身份的一部分；不做名称通配）。</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>能力名（priority/affinity/cpuSet/ecoQos/priorityBoost/…）。</summary>
    public string Capability { get; set; } = "";

    /// <summary>期望值（能力相关格式）。</summary>
    public string Value { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>是否授权未来实例自动接管游戏模式控制（默认否）。</summary>
    public bool AutoTakeover { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class ProcmanRulesListResult
{
    public List<ProcmanRuleEntry> Rules { get; init; } = [];
}

public sealed class ProcmanRulesSaveArgs
{
    public ProcmanRuleEntry Rule { get; set; } = new();
}

public sealed class ProcmanRulesDeleteArgs
{
    public string ExecutablePath { get; set; } = "";
    public string Capability { get; set; } = "";
}

// ---------------- P4：后台抑制 ----------------

public sealed class ProcmanResponsivenessConfigArgs
{
    public bool Enabled { get; set; }

    /// <summary>系统 CPU 使用率阈值（%，默认 35；本项目自行制定的保守值）。</summary>
    public int CpuBusyThreshold { get; set; } = 65;

    /// <summary>持续时长秒（默认 3）。</summary>
    public int CpuBusySeconds { get; set; } = 3;

    /// <summary>恢复阈值（%，默认 50）。</summary>
    public int CpuIdleThreshold { get; set; } = 50;

    /// <summary>低于恢复阈值持续秒（默认 6）。</summary>
    public int CpuIdleSeconds { get; set; } = 6;

    /// <summary>恢复后冷却秒（默认 15；轮内为其一半）。</summary>
    public int CooldownSeconds { get; set; } = 15;

    /// <summary>候选进程最低全机 CPU 占比（%；0 = 根据逻辑 CPU 数自动取 1–5）。</summary>
    public int CandidateCpuFloorPercent { get; set; } = 0;

    /// <summary>每轮最多调整进程数（默认 1）。</summary>
    public int MaxAdjustPerRound { get; set; } = 1;

    /// <summary>兼容旧请求字段；新版固定排除三角洲主进程与系统进程，不再使用此额外排除列表。</summary>
    public List<string> Exclusions { get; set; } = [];
}

public sealed class ProcmanResponsivenessEvent
{
    public DateTimeOffset TimeUtc { get; init; }
    public int Pid { get; init; }
    public string ExecutablePath { get; init; } = "";
    /// <summary>demote = 临时降低优先级；restore = 恢复。</summary>
    public string Action { get; init; } = "";
    public string Reason { get; init; } = "";
}

public sealed class ProcmanResponsivenessEventResult
{
    public List<ProcmanResponsivenessEvent> Events { get; init; } = [];
}

// ---------------- P3 前置：Native 兼容层探针 ----------------

public sealed class ProcmanNativeProbeResult
{
    public int ProcessorGroups { get; init; }
    public List<ProcmanGroupMask> GroupMasks { get; init; } = [];
    public bool IoPrioritySupported { get; init; }
    public bool GpuPrioritySupported { get; init; }
    public string Notes { get; init; } = "";
}
