namespace DeltaNFD.Services;

/// <summary>用户界面检测偏好；修复执行器不引用此开关。代次阻止关闭前的异步结果重新显示。</summary>
internal sealed class RuntimeDetectionPreference
{
    internal static RuntimeDetectionPreference Shared { get; } = new(
        () => AppSettingsStore.Read().RuntimeDetectionEnabled,
        enabled => AppSettingsStore.Update(s => s.RuntimeDetectionEnabled = enabled));

    private readonly Func<bool> _read;
    private readonly Action<bool> _save;
    private bool _enabled;
    private long _revision;

    internal RuntimeDetectionPreference(Func<bool> read, Action<bool> save)
    {
        _read = read;
        _save = save;
        _enabled = read();
    }

    internal bool Enabled => _enabled;
    internal readonly record struct Snapshot(bool Enabled, long Revision);
    internal Snapshot Capture() => new(_enabled, _revision);
    internal bool IsCurrent(Snapshot snapshot) => snapshot.Enabled && _enabled && snapshot.Revision == _revision;

    // UI callers use the dispatcher thread. Reload also handles settings restored outside this page.
    internal void Reload()
    {
        var enabled = _read();
        if (_enabled == enabled) return;
        _enabled = enabled;
        _revision++;
    }

    internal void SetEnabled(bool enabled)
    {
        _save(enabled);
        Reload();
        if (_enabled != enabled) throw new IOException("无法保存运行库检测设置，请重试。");
    }

    internal async Task<ReadResult<T>> ReadAsync<T>(Func<Task<T>> read)
    {
        Reload();
        var snapshot = Capture();
        if (!snapshot.Enabled) return new(snapshot, default, null);
        try { return new(snapshot, await read(), null); }
        catch (Exception ex) { return new(snapshot, default, ex); }
    }

    internal sealed record ReadResult<T>(Snapshot Snapshot, T? Value, Exception? Error);
}
