namespace DeltaNFD.Services;

/// <summary>Bounded session-end wait. Cancellation never cancels or replays an admitted recovery.</summary>
internal sealed class SessionEndDrain(
    Func<Task<IReadOnlyList<string>>> drain,
    Func<IDisposable> blockNewOperations,
    Action<string> report) : IDisposable
{
    private readonly object _gate = new();
    private Task<IReadOnlyList<string>>? _task;
    private IDisposable? _block;
    private bool _cancelled;
    private bool _disposed;

    internal bool Succeeded
    {
        get { lock (_gate) return _task is { IsCompletedSuccessfully: true } task && task.Result.Count == 0; }
    }

    internal bool Query(TimeSpan budget)
    {
        Task<IReadOnlyList<string>> task;
        lock (_gate)
        {
            if (_disposed) return false;
            _cancelled = false;
            _block ??= blockNewOperations();
            // Native messages can arrive on the UI thread. Run recovery without its context.
            task = _task ??= Task.Run(drain);
        }
        try
        {
            if (!task.Wait(budget))
            {
                report("Windows 会话结束：收尾仍在进行，未认定恢复完成；原操作证据保留。");
                return false;
            }
            var pending = task.GetAwaiter().GetResult();
            report(pending.Count == 0 ? "Windows 会话结束：官方后端已确认收尾。"
                : "Windows 会话结束：仍需核对，证据保留：" + string.Join("、", pending));
            return pending.Count == 0;
        }
        catch (Exception ex)
        {
            report("Windows 会话结束：收尾未确认，证据保留：" + ex.GetBaseException().Message);
            return false;
        }
    }

    internal void Cancel()
    {
        lock (_gate)
        {
            _cancelled = true;
            if (_task is null) { Release(); return; }
            var task = _task;
            _ = task.ContinueWith(_ =>
            {
                lock (_gate)
                {
                    // A new query may have reused the ongoing drain. Do not reopen its gate.
                    if (!_cancelled || !ReferenceEquals(_task, task)) return;
                    Release();
                    _task = null;
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void Release() { _block?.Dispose(); _block = null; }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        Cancel();
    }
}
