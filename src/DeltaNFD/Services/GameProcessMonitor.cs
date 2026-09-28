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

    internal GameProcessMonitor() => _ = RunAsync();

    private async Task RunAsync()
    {
        while (true)
        {
            await Task.Delay(Interval).ConfigureAwait(false);
            try
            {
                var processes = Process.GetProcessesByName(DeltaForceLocator.GameProcessName);
                var id = processes.Length > 0 ? processes[0].Id : (int?)null;
                foreach (var process in processes)
                {
                    process.Dispose();
                }

                void Deliver()
                {
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
