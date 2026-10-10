namespace DeltaNFD.Services.Backends;

internal static class BackendOperationGate
{
    private static readonly object Sync = new();
    private static readonly AsyncLocal<ControlLease?> Control = new();
    private static int _blocks, _starts;
    internal static readonly SemaphoreSlim Handover = new(1, 1);

    internal static IDisposable BlockNewOperations()
    {
        lock (Sync) _blocks++;
        return new Lease(() => { lock (Sync) _blocks--; });
    }

    internal static IDisposable AllowControlOperations()
    {
        var lease = new ControlLease(Control.Value);
        Control.Value = lease;
        return lease;
    }

    internal static void Check()
    {
        lock (Sync)
            if (_blocks > 0 && !HasControl())
                throw new InvalidOperationException("官方后端正在停止或交接，暂不接收新的操作。");
    }

    private static bool HasControl()
    {
        for (var lease = Control.Value; lease is not null; lease = lease.Parent)
            if (Volatile.Read(ref lease.Active) != 0) return true;
        return false;
    }

    private sealed class ControlLease(ControlLease? parent) : IDisposable
    {
        internal ControlLease? Parent { get; } = parent;
        internal int Active = 1;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref Active, 0) == 0) return;
            if (ReferenceEquals(Control.Value, this)) Control.Value = Parent;
        }
    }

    internal static IDisposable EnterStart()
    {
        lock (Sync) { Check(); _starts++; }
        return new Lease(() => { lock (Sync) _starts--; });
    }

    internal static async Task WaitForStartsAsync(CancellationToken token)
    {
        while (true)
        {
            lock (Sync) if (_starts == 0) return;
            await Task.Delay(25, token).ConfigureAwait(false);
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) release(); }
    }
}
