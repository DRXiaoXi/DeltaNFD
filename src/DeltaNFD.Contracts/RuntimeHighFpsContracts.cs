namespace DeltaNFD.Services;

public sealed record RuntimeHighFpsPreview(bool CanRepair, string Blocker, string Fingerprint,
    bool StrongMode, IReadOnlyList<string> Entries)
{
    public string V14Fingerprint { get; init; } = "";
    public bool HasUninstallSteps { get; init; }
}

public sealed class RuntimeHighFpsArgs
{
    /// <summary>Legacy compatibility only: true is explicitly rejected; never falls back to ordinary repair.</summary>
    public bool StrongMode { get; set; }
    public string Fingerprint { get; set; } = "";
}
