using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace DeltaNFD.Services;

/// <summary>共享的三角洲进程状态采样；所有自动功能复用同一次进程枚举。</summary>
internal sealed class GameProcessMonitor
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();

    internal event Action<bool, int?>? Tick;
    internal bool IsRunning { get; private set; }
    internal int? ProcessId { get; private set; }
    internal IReadOnlyList<GameProcessIdentity> Processes { get; private set; } = [];
    internal long Generation { get; private set; }

    internal GameProcessMonitor()
    {
        GameTargetService.Default.Changed += _ =>
        {
            void Clear()
            {
                IsRunning = false; ProcessId = null; Processes = []; Generation = GameTargetService.Default.Current.Generation;
                foreach (Action<bool, int?> handler in Tick?.GetInvocationList() ?? [])
                    try { handler(false, null); } catch (Exception ex) { Log.Error("目标切换监控刷新失败", ex); }
            }
            if (_dispatcher is null || _dispatcher.HasThreadAccess) Clear(); else _dispatcher.TryEnqueue(Clear);
        };
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            await Task.Delay(Interval).ConfigureAwait(false);
            try
            {
                using var operation = GameTargetService.Default.BeginOperation();
                var generation = GameTargetService.Default.Current.Generation;
                var processes = GameTargetService.Default.GetProcesses(forceRefresh: true);
                var identities = processes.Select(GameTargetService.Default.Identify).OfType<GameProcessIdentity>().ToArray();
                var id = identities.Length > 0 ? identities[0].Id : (int?)null;
                foreach (var process in processes)
                {
                    process.Dispose();
                }

                void Deliver()
                {
                    using var delivery = GameTargetService.Default.BeginOperation();
                    if (GameTargetService.Default.Current.Generation != generation) return;
                    Generation = generation;
                    Processes = identities;
                    ProcessId = id;
                    IsRunning = id.HasValue;
                    foreach (Action<bool, int?> subscriber in Tick?.GetInvocationList().Cast<Action<bool, int?>>() ?? [])
                    {
                        try { subscriber(IsRunning, ProcessId); }
                        catch (Exception ex) { Log.Error("游戏进程监控订阅处理失败", ex); }
                    }
                }

                if (_dispatcher is null || _dispatcher.HasThreadAccess)
                {
                    Deliver();
                }
                else if (!_dispatcher.TryEnqueue(Deliver))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"游戏进程监控查询失败：{ex.Message}");
            }
        }
    }
}
