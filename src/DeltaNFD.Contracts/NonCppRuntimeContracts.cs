namespace DeltaNFD.Services;

/// <summary>Fixed DirectX component inspection. OS-serviced files are diagnosed, never overwritten.</summary>
public sealed record NonCppRuntimeFile(string Name, string Architecture, string Status,
    string Detail, bool NeedsRepair, bool OsManaged)
{
    public string ObservedHash { get; init; } = "";
}

public sealed record NonCppRuntimePreview(bool CanRepair, string Blocker, string Fingerprint,
    IReadOnlyList<NonCppRuntimeFile> Files)
{
    public bool RecoveryPending { get; init; }
    public DateTimeOffset ScannedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class NonCppRuntimeRepairArgs
{
    public string Fingerprint { get; set; } = "";
}
