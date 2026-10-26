namespace DeltaNFD.Services.Backends;

/// <summary>Failed exit releases admission; a committed exit keeps it closed until process termination.</summary>
internal sealed class ExitOperationScope : IDisposable
{
    private readonly object _gate = new();
    private readonly List<IDisposable> _leases = [];
    private bool _committed, _released;

    internal void Hold(IDisposable? lease)
    {
        if (lease is null) return;
        lock (_gate)
        {
            if (!_released && !_committed) { _leases.Add(lease); return; }
        }
        lease.Dispose();
        throw new InvalidOperationException("退出准入范围已结束。");
    }

    internal void Commit()
    {
        lock (_gate)
        {
            if (_released) throw new InvalidOperationException("退出准入范围已释放。");
            _committed = true;
        }
    }

    internal void Release() => ReleaseCore(force: true);

    private void ReleaseCore(bool force)
    {
        IDisposable[] leases;
        lock (_gate)
        {
            if (_released || _committed && !force) return;
            _released = true;
            leases = _leases.ToArray();
            _leases.Clear();
        }
        // Release every admission even if an unrelated component's lease throws.
        List<Exception>? failures = null;
        for (var i = leases.Length - 1; i >= 0; i--)
            try { leases[i].Dispose(); } catch (Exception ex) { (failures ??= []).Add(ex); }
        if (failures is not null) throw new AggregateException("退出准入释放失败。", failures);
    }

    public void Dispose() => ReleaseCore(force: false);
}
