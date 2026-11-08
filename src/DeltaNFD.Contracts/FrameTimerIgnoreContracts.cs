namespace DeltaNFD.Services;

/// <summary>「让系统忽略三角洲定时器分辨率请求」的实际状态；计数来自回读确认，不是用户偏好。</summary>
public sealed record FrameTimerIgnoreSnapshot
{
    /// <summary>本系统是否支持该位（Windows 11 起；无法确认按不支持处理）。</summary>
    public bool Supported { get; init; }

    /// <summary>是否正在维持（有经核验的三角洲进程已被施加该位）。</summary>
    public bool Maintaining { get; init; }

    public bool HasError { get; init; }

    /// <summary>仍有未恢复的原值记录。</summary>
    public bool RestorePending { get; init; }

    public int BackupCount { get; init; }

    /// <summary>当前匹配到的三角洲主进程数。</summary>
    public int InstanceCount { get; init; }

    /// <summary>已回读确认生效的进程数。</summary>
    public int AppliedCount { get; init; }

    /// <summary>只读观测：当前系统定时器分辨率（ms；不可读为 null）。</summary>
    public double? SystemCurrentMilliseconds { get; init; }

    /// <summary>无请求进程能否获得较细时钟（null = 未核对）；为 false 时拒绝施加忽略位，避免把三角洲压到默认 15.625ms。</summary>
    public bool? LegacyGlobalReachable { get; init; }

    /// <summary>探针实测：无请求进程的等待粒度中位数（ms）。</summary>
    public double? RequestlessGranularityMilliseconds { get; init; }

    /// <summary>本次是否真的施加了忽略位（false 表示因系统时钟过粗而主动跳过）。</summary>
    public bool IgnoreApplied { get; init; }

    public string Message { get; init; } = "尚未核对三角洲定时器请求忽略状态";

    public DateTimeOffset UpdatedUtc { get; init; }
}
