namespace DeltaNFD.Services;

public sealed record NvDisplayContainerStatus(string Detail, string? Fingerprint = null, bool Restore = false);
public sealed record NvDisplayContainerChange(bool Restore, string Fingerprint)
{
    public const int ProtocolVersion = 1;
}
