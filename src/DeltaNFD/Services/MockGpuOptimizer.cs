using DeltaNFD.Models;

namespace DeltaNFD.Services;

/// <summary>显卡优化的演示实现：数据为 mock，仅模拟应用延迟。</summary>
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
            DriverVersion = "566.36",
            TemperatureC = 47,
            Usage = 58,
        };
    }

    public async Task ApplySettingsAsync(GpuSettings settings, IProgress<string>? progress = null)
    {
        await Task.Delay(1200);
    }
}
