using System.ComponentModel;
using DeltaNFD.Models;

namespace DeltaNFD.Services;

/// <summary>
/// 游戏进程优化服务：
/// - 三角洲进程出现时自动提升进程优先级（扩展优化库 游戏模式同款）
/// - 真实后台进程扫描 / 结束
/// - 工作集内存整理（释放被缓存占用的物理内存）
/// </summary>
public interface IGameProcessService : INotifyPropertyChanged
{
    /// <summary>三角洲进程当前是否在运行。</summary>
    bool IsGameRunning { get; }

    /// <summary>游戏进程优先级提升（开启后三角洲启动时自动设为 High）。</summary>
    bool GamePriorityEnabled { get; set; }

    /// <summary>最近一次自动提权的状态文字。</summary>
    string StatusText { get; }

    /// <summary>扫描可清理的后台进程（排除系统关键进程与本程序）。</summary>
    Task<List<ProcessInfo>> GetScannableProcessesAsync();

    /// <summary>结束指定进程（真实 Kill）。</summary>
    Task<OperationResult> KillProcessAsync(ProcessInfo process);

    /// <summary>结束一批进程，返回成功结束的数量与释放内存。</summary>
    Task<(int Killed, double FreedMb)> KillManyAsync(IEnumerable<ProcessInfo> processes, IProgress<string>? progress = null);

    /// <summary>对给定进程做工作集裁剪（内存整理），返回释放的内存 MB。</summary>
    Task<double> TrimWorkingSetAsync(IEnumerable<ProcessInfo> processes, IProgress<string>? progress = null);
}
