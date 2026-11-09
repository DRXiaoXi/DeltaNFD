using DeltaNFD.Inspection;
namespace DeltaNFD.Services;

internal static class LowMemoryHealthCache
{
    private static readonly object Gate = new();
    private static Task<LowMemoryStatus>? _read;
    private static DateTimeOffset _readAt;
    internal static void Invalidate() { lock (Gate) _read = null; }
    public static Task<LowMemoryStatus> ReadAsync(bool force = false)
    {
        lock (Gate)
        {
            if (_read is { IsCompleted: false }) return _read;
            if (_read is null || force || DateTimeOffset.UtcNow - _readAt > TimeSpan.FromSeconds(30))
            { _readAt = DateTimeOffset.UtcNow; _read = LowMemoryInspection.ReadAsync(); }
            return _read;
        }
    }
}
