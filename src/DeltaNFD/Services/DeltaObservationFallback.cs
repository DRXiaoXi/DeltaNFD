namespace DeltaNFD.Services;

/// <summary>One read-only process-name probe while backend observation is unavailable; no executor or write retry.</summary>
internal sealed class DeltaObservationFallback(Func<bool> read, Action<bool?, DateTimeOffset> accept) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _run;
    private int _generation;
    private bool _disposed;
    internal void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!enabled)
            {
                _generation++;
                _run?.Cancel(); _run = null;
                return;
            }
            if (_run is not null) return;
            var cts = _run = new CancellationTokenSource();
            var generation = ++_generation;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var at = DateTimeOffset.UtcNow;
                        bool? running;
                        try { running = read(); } catch { running = null; }
                        lock (_gate)
                        {
                            if (_disposed || generation != _generation || cts.IsCancellationRequested) return;
                        }
                        // State consumers can disable this probe from their notification; never call them under our lock.
                        accept(running, at);
                        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
                finally { cts.Dispose(); }
            });
        }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _generation++; _run?.Cancel(); _run = null; }
    }
}
