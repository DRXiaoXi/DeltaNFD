using DeltaNFD.Inspection;
namespace DeltaNFD.Services;

/// <summary>One coalesced query per application start, plus explicit user refreshes. No backend or polling.</summary>
internal static class TimerHealthCache
{
    private static readonly object Gate=new();
    private static Task<TimerRecoveryStatus>? _read;
    public static Task<TimerRecoveryStatus> ReadAsync(bool force=false)
    {
        lock (Gate)
        {
            if (_read is { IsCompleted:false }) return _read;
            if (_read is null || force) _read=TimerConfigurationInspection.ReadAsync();
            return _read;
        }
    }
}
