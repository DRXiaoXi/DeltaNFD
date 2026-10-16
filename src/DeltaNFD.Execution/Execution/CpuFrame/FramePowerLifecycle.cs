namespace DeltaNFD.Services;

/// <summary>电源操作串行化；退出时取消旧代次并等在途操作排空，再进行恢复。</summary>
internal sealed class FramePowerLifecycle
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _session;
    private long _generation;
    public long Generation { get { lock (_stateGate) return _generation; } }

    public void Start()
    {
        lock (_stateGate)
        {
            _session?.Cancel();
            _session?.Cancel();
            _session?.Dispose();
            _session = new CancellationTokenSource();
            _generation++;
        }
    }

    public void Stop()
    {
        lock (_stateGate) { _generation++; _session?.Cancel(); }
    }

    public async Task<T> ExclusiveAsync<T>(Func<Task<T>> operation)
    {
        await _gate.WaitAsync();
        try { return await operation(); }
        finally { _gate.Release(); }
    }

    public Task<T?> CurrentAsync<T>(Func<CancellationToken, Task<T>> operation, bool skipIfBusy = false, long? expectedGeneration = null) where T : class =>
        RunCurrentAsync(operation, skipIfBusy, expectedGeneration);

    private async Task<T?> RunCurrentAsync<T>(Func<CancellationToken, Task<T>> operation, bool skipIfBusy, long? expectedGeneration) where T : class
    {
        long generation;
        CancellationToken token;
        lock (_stateGate)
        {
            if (_session is null || _session.IsCancellationRequested || (expectedGeneration is not null && expectedGeneration != _generation)) return null;
            generation = _generation;
            token = _session.Token;
        }
        if (skipIfBusy) { if (!await _gate.WaitAsync(0)) return null; }
        else await _gate.WaitAsync();
        try
        {
            lock (_stateGate) { if (generation != _generation || token.IsCancellationRequested) return null; }
            try { return await operation(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        }
        finally { _gate.Release(); }
    }
}
