namespace DeltaNFD.Services;

/// <summary>硬截止不等待状态锁或日志 IO；强制退出时依靠写前持久化的原值记录恢复。</summary>
public sealed class OfflineHelperDeadline : IDisposable
{
    private readonly ManualResetEventSlim _finished = new();
    private readonly Thread _thread;
    private int _disposed;

    public OfflineHelperDeadline(TimeSpan timeout, Action? expired = null)
    {
        _thread = new Thread(() =>
        {
            if (!_finished.Wait(timeout)) (expired ?? (() => Environment.Exit(124)))();
        }) { IsBackground = true, Name = "OfflineHelperDeadline" };
        _thread.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _finished.Set();
        _thread.Join();
        _finished.Dispose();
    }
}
