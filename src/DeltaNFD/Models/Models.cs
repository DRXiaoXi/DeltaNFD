namespace DeltaNFD.Models;

/// <summary>系统总览信息（仪表盘）。</summary>
public class SystemOverview
{
    public string CpuName { get; set; } = "";
    public string GpuName { get; set; } = "";
    public double CpuUsage { get; set; }
    public double GpuUsage { get; set; }
    public double RamUsedGb { get; set; }
    public double RamTotalGb { get; set; }
    public bool IsGameRunning { get; set; }
    public int PingMs { get; set; }
    public string PowerPlan { get; set; } = "";
}

/// <summary>可清理的后台进程。</summary>
public class ProcessInfo
{
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    public double MemoryMb { get; set; }
    public string Category { get; set; } = "";
}

/// <summary>单个优化项（开关）。</summary>
public class OptimizeItem
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsEnabled { get; set; }
    public bool IsRecommended { get; set; }
}

/// <summary>一轮优化的结果报告。</summary>
public class OptimizeReport
{
    public List<string> AppliedSteps { get; } = new();
    public double ReclaimedMemoryMb { get; set; }
}

/// <summary>显卡信息。</summary>
public class GpuInfo
{
    public string Vendor { get; set; } = "";
    public string Model { get; set; } = "";
    public double VramGb { get; set; }
    public string DriverVersion { get; set; } = "";
    public double TemperatureC { get; set; }
    public double Usage { get; set; }
}

/// <summary>显卡优化设置。</summary>
public class GpuSettings
{
    public string LowLatencyMode { get; set; } = "关闭";
    public string PowerManagementMode { get; set; } = "正常";
    public string ShaderCacheSize { get; set; } = "10 GB";
    public string ThreadedOptimization { get; set; } = "自动";
    public bool VerticalSync { get; set; } = true;
    public int DigitalVibrance { get; set; } = 50;
}
