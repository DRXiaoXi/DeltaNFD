namespace DeltaNFD.Services;

public enum FrameCpuPriority { Idle = 0x40, BelowNormal = 0x4000, Normal = 0x20, AboveNormal = 0x8000, High = 0x80, RealTime = 0x100 }
public enum FrameIoPriority { VeryLow = 0, Low = 1, Normal = 2, High = 3 }
public enum FrameMemoryPriority { VeryLow = 1, Low = 2, Medium = 3, BelowNormal = 4, Normal = 5 }

/// <summary>Only fixed process-priority values; never accepts paths, scripts or arbitrary native classes.</summary>
public sealed record FrameGamePriorityConfig
{
    public FrameCpuPriority Cpu { get; init; } = FrameCpuPriority.AboveNormal;
    public FrameIoPriority Io { get; init; } = FrameIoPriority.Normal;
    public FrameMemoryPriority Memory { get; init; } = FrameMemoryPriority.Normal;
    public bool IsValid => Enum.IsDefined(Cpu) && Enum.IsDefined(Io) && Enum.IsDefined(Memory);
    public static string CpuLabel(FrameCpuPriority value) => value switch
    { FrameCpuPriority.RealTime => "实时", FrameCpuPriority.High => "高", FrameCpuPriority.AboveNormal => "高于正常", FrameCpuPriority.Normal => "正常", FrameCpuPriority.BelowNormal => "低于正常", FrameCpuPriority.Idle => "低", _ => "无效" };
    public static string IoLabel(FrameIoPriority value) => value switch
    { FrameIoPriority.High => "高", FrameIoPriority.Normal => "正常", FrameIoPriority.Low => "低", FrameIoPriority.VeryLow => "非常低", _ => "无效" };
    public static string MemoryLabel(FrameMemoryPriority value) => value switch
    { FrameMemoryPriority.Normal => "正常", FrameMemoryPriority.BelowNormal => "低于正常", FrameMemoryPriority.Medium => "中", FrameMemoryPriority.Low => "低", FrameMemoryPriority.VeryLow => "非常低", _ => "无效" };
}

/// <summary>Actual readback counts, separate from the saved configuration.</summary>
public sealed record FrameGamePrioritySnapshot
{
    public bool Maintaining { get; init; }
    public bool HasError { get; init; }
    public bool RestorePending { get; init; }
    public int BackupCount { get; init; }
    public int InstanceCount { get; init; }
    public int CpuAppliedCount { get; init; }
    public int IoAppliedCount { get; init; }
    public int MemoryAppliedCount { get; init; }
    public string Message { get; init; } = "尚未核对三角洲进程优先级";
    public DateTimeOffset UpdatedUtc { get; init; }
}
