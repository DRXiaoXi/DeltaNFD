using DeltaNFD.Models;

namespace DeltaNFD.Services;

/// <summary>
/// 系统/帧率优化服务。当前为 Mock 实现，后续接入真实逻辑时替换实现类即可。
/// </summary>
public interface ISystemOptimizer
{
    /// <summary>一键优化包含的步骤总数，供界面计算进度百分比。</summary>
    int FullOptimizeStepCount { get; }

    Task<SystemOverview> GetSystemOverviewAsync();

    Task<List<OptimizeItem>> GetOptimizeItemsAsync();

    Task<List<ProcessInfo>> GetCleanableProcessesAsync();

    /// <summary>执行一键优化，通过 progress 上报每一步状态文本。</summary>
    Task<OptimizeReport> RunFullOptimizeAsync(IProgress<string>? progress, CancellationToken ct = default);

    /// <summary>结束指定进程（模拟），返回释放的内存 MB。</summary>
    Task<double> KillProcessAsync(ProcessInfo process);
}
