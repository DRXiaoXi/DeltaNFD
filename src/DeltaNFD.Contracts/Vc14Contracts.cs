namespace DeltaNFD.Services;

public enum Vc14Severity { Normal, Warning, Abnormal }
public sealed record Vc14Entry(string Code, string Name, string Version, string Architecture, string Role, string UpgradeCode);
public sealed record Vc14Notice(string Name, string Version, string Architecture, Vc14Severity Severity, string Detail);
public sealed record Vc14Scan(IReadOnlyList<Vc14Entry> Products, IReadOnlyList<Vc14Notice> Notices, IReadOnlyList<string> Issues)
{
    public Vc14LegacyReport Legacy { get; init; } = new("", [], []);
    public bool HasAbnormal => Notices.Any(n => n.Severity == Vc14Severity.Abnormal);
    public bool HasWarning => Issues.Count > 0 || Legacy.Issues.Count > 0 || Notices.Any(n => n.Severity == Vc14Severity.Warning);
}
public sealed record Vc14LegacyEntry(string Architecture, string Name, string Version, bool CanProcess, string Detail);
public sealed record Vc14LegacyReport(string Fingerprint, IReadOnlyList<Vc14LegacyEntry> Entries, IReadOnlyList<string> Issues);
public sealed record Vc14Preview(Vc14Scan Scan, string Fingerprint, bool CanRepair, string Blocker);
public sealed class Vc14RepairArgs { public string Fingerprint { get; set; } = ""; }
