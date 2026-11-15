namespace DeltaNFD.Services;

public enum ClockValueAction { SaveCustom = 0, UseAutomatic = 1, TestCustomSleep = 2, TestAutomaticSleep = 3 }
public sealed class ClockValueArgs
{
    public ClockValueAction Action { get; set; }
    public uint? Resolution100ns { get; set; }
}
public sealed record ClockSleepResult
{
    public bool Custom { get; init; }
    public bool TargetVerified { get; init; }
    public bool ReleaseConfirmed { get; init; }
    public uint Requested100ns { get; init; }
    public uint Granted100ns { get; init; }
    public uint Current100ns { get; init; }
    public uint? Before100ns { get; init; }
    public uint? After100ns { get; init; }
    public int Samples { get; init; }
    public double? MedianMs { get; init; }
    public double? MeanMs { get; init; }
    public double? P95Ms { get; init; }
    public double? MinMs { get; init; }
    public double? MaxMs { get; init; }
    public double? Over130Pct { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }
    public string Message { get; init; } = "";
}
