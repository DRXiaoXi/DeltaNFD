using Microsoft.UI.Dispatching;

namespace DeltaNFD.Services;

/// <summary>
/// 共享的三角洲进程状态订阅：唯一权威是 CPU/帧格后端的两秒监控。
/// 前端不再自行枚举目标进程，而是订阅后端状态快照并把「游戏是否运行 / PID」派发回 UI 线程。
/// 后端缺失或脱机时不启动后端（保持零进程默认模式）。
/// </summary>
internal sealed class GameProcessMonitor : IDisposable
{
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly CancellationTokenSource _cts = new();

    internal event Action<bool, int?>? Tick;
    internal bool IsRunning { get; private set; }
    internal int? ProcessId { get; private set; }

    internal GameProcessMonitor()
    {
        GameTargetService.Default.Changed += OnTargetChanged;
        _ = RunAsync();
    }

    private void OnTargetChanged(GameTarget target) => Deliver(false, null);

    private async Task RunAsync()
    {
        // 零进程脱机/恢复流程：不启动后端监控（普通自动化已暂停）。
        if (OfflineModeGuard.BlocksNormalAutomation) return;

        var service = Backends.BackendCpuFrameService.Instance;
        service.StatusUpdated += OnStatusUpdated;
        try
        {
            await service.EnsureMonitorAsync(_cts.Token);
            await Task.Delay(Timeout.Infinite, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("CPU/帧格状态监控启动失败（不自行建立第二份进程监控）：" + ex.Message);
        }
        finally
        {
            service.StatusUpdated -= OnStatusUpdated;
        }
    }

    private void OnStatusUpdated(CpuFrameStatusSnapshot snapshot) => Deliver(snapshot.GameRunning, snapshot.GamePid);

    private void Deliver(bool running, int? pid)
    {
        void Apply()
        {
            IsRunning = running;
            ProcessId = pid;
            foreach (Action<bool, int?> handler in Tick?.GetInvocationList() ?? [])
            {
                try { handler(running, pid); }
                catch (Exception ex) { Log.Error("游戏进程监控订阅处理失败", ex); }
            }
        }

        if (_dispatcher is null || _dispatcher.HasThreadAccess) Apply();
        else _dispatcher.TryEnqueue(Apply);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        GameTargetService.Default.Changed -= OnTargetChanged;
    }
}
