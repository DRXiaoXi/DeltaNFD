namespace DeltaNFD.Services;

public sealed record RuntimeHighFpsPreview(bool CanRepair, string Blocker, string Fingerprint,
    bool StrongMode, IReadOnlyList<string> Entries)
{
    public string V14Fingerprint { get; init; } = "";
    public bool HasUninstallSteps { get; init; }
    public Vc14Baseline Baseline { get; init; } = Vc14Baseline.V144;
}

public sealed class RuntimeHighFpsArgs
{
    /// <summary>Legacy compatibility only: true is explicitly rejected; never falls back to ordinary repair.</summary>
    public bool StrongMode { get; set; }
    public string Fingerprint { get; set; } = "";
    public Vc14Baseline Baseline { get; set; } = Vc14Baseline.V144;
}
