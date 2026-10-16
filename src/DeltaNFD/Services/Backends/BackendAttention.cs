namespace DeltaNFD.Services.Backends;

internal sealed record BackendAttentionItem(string Module, string Fingerprint);

/// <summary>通知状态与恢复证据分开；提示过不代表操作已恢复或证据已归档。</summary>
internal sealed class BackendAttentionQueue
{
    private readonly object _sync = new();
    private readonly Dictionary<string, BackendAttentionItem> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _presented = new(StringComparer.Ordinal);
    internal bool Report(string module, string fingerprint)
    {
        lock (_sync)
        {
            if (_current.TryGetValue(module, out var old) && old.Fingerprint == fingerprint) return false;
            _current[module] = new(module, fingerprint);
            return !_presented.TryGetValue(module, out var shown) || shown != fingerprint;
        }
    }
    internal IReadOnlyList<BackendAttentionItem> Snapshot(bool onlyUnshown)
    {
        lock (_sync) return _current.Values.Where(item => !onlyUnshown
            || !_presented.TryGetValue(item.Module, out var shown) || shown != item.Fingerprint).ToArray();
    }
    internal void Presented(IEnumerable<BackendAttentionItem> items)
    {
        lock (_sync) foreach (var item in items) _presented[item.Module] = item.Fingerprint;
    }
    internal void Forget(string module)
    {
        lock (_sync) { _current.Remove(module); _presented.Remove(module); }
    }
}

/// <summary>纯 BCL 通知入口；后端读取线程只发信号，不等待 UI，不执行核对或恢复。</summary>
internal static class BackendAttention
{
    private static readonly BackendAttentionQueue Queue = new();
    internal static event Action? Changed;
    internal static void Report(string module)
    {
        try
        {
            var fingerprint = BackendUnconfirmedStore.AttentionFingerprint(module);
            if (fingerprint is null) return;
            if (!Queue.Report(module, fingerprint)) return;
            RaiseChanged();
        }
        catch (Exception ex) { Log.Warn("后端异常通知读取失败：" + ex.Message); }
    }
    internal static void RefreshPersisted()
    {
        foreach (var module in BackendUnconfirmedStore.PendingModules()) Report(module);
    }
    internal static IReadOnlyList<BackendAttentionItem> Snapshot(bool onlyUnshown = false)
    {
        foreach (var item in Queue.Snapshot(false))
            if (BackendUnconfirmedStore.AttentionFingerprint(item.Module) is null) Queue.Forget(item.Module);
        return Queue.Snapshot(onlyUnshown);
    }
    internal static void Presented(IEnumerable<BackendAttentionItem> items) => Queue.Presented(items);
    internal static void NotifyUnshown()
    {
        if (Snapshot(onlyUnshown: true).Count > 0) RaiseChanged();
    }
    private static void RaiseChanged()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch (Exception ex) { Log.Warn("后端异常通知失败：" + ex.Message); }
    }
}
