using DeltaNFD.Models;

namespace DeltaNFD.Services;

/// <summary>
/// 显卡优化的演示实现：数据为 mock，仅模拟应用延迟。
/// 该页不在导航中；但绝不能显示硬编码驱动版本（旧版曾写死 566.36，容易与真实驱动不一致）。
/// </summary>
public class MockGpuOptimizer : IGpuOptimizer
{
    public async Task<GpuInfo> GetGpuInfoAsync()
    {
        await Task.Delay(400);
        return new GpuInfo
        {
            Vendor = "NVIDIA",
            Model = "GeForce RTX 4070",
            VramGb = 12,
            DriverVersion = "演示数据（非真实驱动）",
            TemperatureC = 47,
            Usage = 58,
        };
    }

    public async Task ApplySettingsAsync(GpuSettings settings, IProgress<string>? progress = null)
    {
        await Task.Delay(1200);
    }
}
