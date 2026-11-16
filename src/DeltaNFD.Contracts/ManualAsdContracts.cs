namespace DeltaNFD.Services;

public enum ManualAsdStage { Idle, Cleaning, Warmup, Activated, NotDetected, Cancelled, Interrupted }

/// <summary>Only an explicitly started, fully cleaned session can authorize the manual size test.</summary>
public sealed record ManualAsdSession
{
    public int Schema { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Owner { get; init; } = "";
    public string DriverKey { get; init; } = "";
    public GameInstallationSelection Selection { get; init; } = new();
    public string SystemRoot { get; init; } = "";
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? CleanedUtc { get; init; }
    public DateTimeOffset? CheckedUtc { get; init; }
    public ManualAsdStage Stage { get; init; }
    public string Message { get; init; } = "";
    public string MatchedFile { get; init; } = "";
    public long MatchedBytes { get; init; }
}

public sealed record ManualAsdContext(bool Supported, string DriverKey, string Owner,
    GameInstallationSelection Selection, string[] PsoPaths, string SystemRoot);

public sealed class ManualAsdStartArgs
{
    public GameInstallationSelection? Selection { get; init; }
    public string ExpectedContextId { get; init; } = "";
}

public static class ManualAsdPolicy
{
    public const int Version = 1;
    public const long ThresholdBytes = 100L * 1024 * 1024;
    public static bool Supported(ShaderGpuDetectionState state, string version) => state == ShaderGpuDetectionState.Nvidia
        && version.Split('.') is [var major, var minor] && int.TryParse(major, out var m) && m >= 621
        && int.TryParse(minor, out var n) && n is >= 0 and <= 99;
}
