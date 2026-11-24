namespace DeltaNFD.Services;

public sealed record NvDisplayContainerStatus(string Detail, string? Fingerprint = null, bool Restore = false);
public sealed record NvDisplayContainerChange(bool Restore, string Fingerprint)
{
    public const int ProtocolVersion = 1;
    public const int FileBackupProtocolVersion = 2;
    public const int TelemetryProtocolVersion = 3;
    public const int LocalSystemProtocolVersion = 4;
    public const string RecoveryScope = "nv-display-container";

    public static bool IsFingerprint(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

public sealed record NvDisplayContainerReviewArgs(string Module);
public sealed record NvDisplayContainerRecoveryArgs(string Module, string Fingerprint);

public static class NvTelemetryContainer
{
    public const string ServiceName = "NvTelemetryContainer";
    public const string RecoveryScope = "nv-telemetry-container";
}

public static class NvLocalSystemContainer
{
    public const string ServiceName = "NvContainerLocalSystem";
    public const string RecoveryScope = "nv-local-system-container";
}

public enum NvContainerTarget { Display, LegacyTelemetry, LocalSystem }
