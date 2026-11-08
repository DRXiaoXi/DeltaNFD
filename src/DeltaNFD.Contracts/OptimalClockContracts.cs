namespace DeltaNFD.Services;

/// <summary>钟摆计划的检测模式。</summary>
public enum OptimalClockScanMode
{
    /// <summary>最低延迟：沿用原算法（三级逼近 + 双赢家 + 头部复测），取负载下典型延迟最低的稳定侧值。</summary>
    LowestLatency = 0,

    /// <summary>稳定延迟：新算法（先测出 2 tick 悬崖，再按实测抖动退让出安全裕量，最后在空闲/标准/重载三条件下验证），
    /// 取「通过验证里最细」的值，宁可牺牲一点中位也要保证任何负载下都不翻回 3 tick。</summary>
    StableLatency = 1,
}

/// <summary>
/// 「钟摆计划」状态快照：由后端权威给出（是否真的全局锁定、实测值、扫描结论），不是用户偏好。
/// Windows 11 常规路径，Windows 10 为须经实测准入的实验兼容路径。
/// </summary>
public sealed class OptimalClockSnapshot
{
    /// <summary>本系统是否可进入检测路径；不表示全局生效已验证。</summary>
    public bool Supported { get; init; }

    /// <summary>用户是否已启用（配置意图）。</summary>
    public bool Enabled { get; init; }

    /// <summary>后端是否正持有该分辨率请求（真实进程存在）。</summary>
    public bool Holding { get; init; }

    /// <summary>请求的定时器分辨率（100ns；1ms = 10000）。</summary>
    public uint Resolution100ns { get; init; }

    /// <summary>内核返回的授予值（100ns）。</summary>
    public uint Granted100ns { get; init; }

    /// <summary>只读查询到的系统当前分辨率（100ns）。</summary>
    public uint Current100ns { get; init; }

    /// <summary>实测确认「不请求的进程也获得该时钟」（全局计时器语义生效）。</summary>
    public bool VerifiedGlobal { get; init; }

    /// <summary>实测：无请求进程的等待粒度中位数（ms）。</summary>
    public double? RequestlessMilliseconds { get; init; }

    /// <summary>实测判定模式：Global / Self / Unknown。</summary>
    public string Mode { get; init; } = "Unknown";

    public bool HasError { get; init; }

    /// <summary>扫描得到「典型延迟最低」的分辨率（100ns）。</summary>
    public uint? BestTypical100ns { get; init; }

    /// <summary>扫描得到「最稳」的分辨率（100ns）。</summary>
    public uint? BestStable100ns { get; init; }

    /// <summary>扫描给出的「实际锁定值」（稳定侧，避开临界边缘双峰；100ns）。</summary>
    public uint? BestLock100ns { get; init; }

    public double? BestTypicalMedianMs { get; init; }
    public double? BestStableMedianMs { get; init; }

    /// <summary>扫描时的「无请求进程」基线（ms）。</summary>
    public double? ScanRequestlessBaselineMs { get; init; }

    /// <summary>扫描时的 CPU 负载线程数（0 = 空闲测量；研究结论要求负载下测量才能选出稳定值）。</summary>
    public int ScanLoadThreads { get; init; }

    /// <summary>产生当前锁定值的检测模式。</summary>
    public OptimalClockScanMode ScanMode { get; init; }

    /// <summary>稳定延迟模式下选定的安全裕量（ms；2 tick 比 1ms 请求多出的余量）。0 = 非稳定模式。</summary>
    public double ScanSafetyMarginMs { get; init; }

    /// <summary>稳定延迟模式下通过验证的条件说明（如「空闲 1.06 / 标准 1.07 / 重载 1.08」）。</summary>
    public string ScanStableConditions { get; init; } = "";

    /// <summary>最近一次扫描完成时间。</summary>
    public DateTimeOffset? ScanUtc { get; init; }

    public string Message { get; init; } = "尚未核对钟摆计划状态";

    public DateTimeOffset UpdatedUtc { get; init; }

    public string DiagnosticHistoryError { get; init; } = "";
    public string DiagnosticLogPath { get; init; } = "";
    public string DiagnosticLogError { get; init; } = "";
    public DateTimeOffset? GlobalVerifiedUtc { get; init; }
    public bool ExperimentalWindows10 { get; init; }
    public List<PendulumClockNotice> Notices { get; init; } = [];
}

/// <summary>「钟摆计划」命令参数。</summary>
public sealed class OptimalClockArgs
{
    /// <summary>锁定分辨率（100ns）；null = 使用已保存的最佳值。</summary>
    public uint? Resolution100ns { get; set; }

    /// <summary>扫描时使用快速档（采样与重复更少）。</summary>
    public bool Quick { get; set; }
    /// <summary>仅运行无 CPU 负载的跨进程能力检测，不保存扫描结果。</summary>
    public bool CompatibilityOnly { get; set; }

    /// <summary>检测模式；默认最低延迟（保持旧行为）。</summary>
    public OptimalClockScanMode ScanMode { get; set; } = OptimalClockScanMode.LowestLatency;

    /// <summary>扫描结果（仅 scan 命令返回）。</summary>
    public OptimalClockScanSummary? Scan { get; set; }
}

/// <summary>扫描结果的 IPC 摘要（完整候选表只保留头部若干项）。</summary>
public sealed class OptimalClockScanSummary
{
    public string Mode { get; set; } = "Unknown";

    /// <summary>本次扫描使用的检测模式。</summary>
    public OptimalClockScanMode ScanMode { get; set; }

    /// <summary>稳定延迟模式选定的安全裕量（ms）。</summary>
    public double SafetyMarginMs { get; set; }

    /// <summary>稳定延迟模式的三条件验证说明。</summary>
    public string StableConditions { get; set; } = "";
    public double? RequestlessBaselineMs { get; set; }
    public uint? BestTypical100ns { get; set; }
    public uint? BestStable100ns { get; set; }
    public double? BestTypicalMedianMs { get; set; }
    public double? BestStableMedianMs { get; set; }
    public int Probes { get; set; }
    public double ElapsedSeconds { get; set; }
    public List<OptimalClockCandidateSummary> Top { get; set; } = [];
}

public sealed class OptimalClockCandidateSummary
{
    public uint Resolution100ns { get; set; }
    public double MedianMs { get; set; }
    public double SpreadMs { get; set; }
    public double Over130Pct { get; set; }
}
