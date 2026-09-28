using DeltaNFD.Models;

namespace DeltaNFD.Services;

/// <summary>
/// 显卡优化服务。当前为 Mock 实现，后续接入真实驱动/显卡面板逻辑时替换实现类即可。
/// </summary>
public interface IGpuOptimizer
{
    Task<GpuInfo> GetGpuInfoAsync();

    Task ApplySettingsAsync(GpuSettings settings, IProgress<string>? progress = null);
}
