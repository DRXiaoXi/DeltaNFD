namespace DeltaNFD.Services;

/// <summary>物理内存快照（后端只读返回；后端不得在读取失败时返回全零冒充有效值）。</summary>
public sealed class MemoryStatusDto
{
    public ulong TotalPhys { get; set; }
    public ulong AvailPhys { get; set; }
    public uint LoadPercent { get; set; }
    public double AvailRatio { get; set; }
}

/// <summary>待备内存列表清理结果。</summary>
public sealed class MemoryPurgeResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";

    /// <summary>清理前后可用物理内存的变化量（字节；可为负，缓存被立即重用时发生）。</summary>
    public long FreedBytes { get; set; }
}

/// <summary>一键优化（系统维护后端可执行部分）的结果报告。</summary>
public sealed class OptimizeRunReport
{
    public List<string> AppliedSteps { get; set; } = new();
    public double ReclaimedMemoryMb { get; set; }
}
