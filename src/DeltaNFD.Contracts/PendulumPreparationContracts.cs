namespace DeltaNFD.Services;

public static class PendulumPreparationPackage
{
    public const string Id = "PendulumTimerV1/alpha0935";
    public const string DriverSha = "9a7e35234617b1d4a86a0dddeb0a819821189b18783028ceecb6db4591f65522";
    public const string CertificateSha = "f17846143ec0d30187488ceaa4c36fed809ab0d710a70e58c9cd4a09d5449fec";
    public const string SetupSha = "ddcd110f2f9665126658a4da2be6e1ed4587f6cee7c321b17093e871c38f4670";
    public const string ManifestSha = "324e0b70c8243a930fc949ef9ee9596c6bffe4279561fc9387bd419c8388f3b0";
    public const string CertificateThumbprint = "C28A9EF423B20DA4A9F0F0F7BD0147C31D0A95FE";
}

public sealed record PendulumPreparationArgs(bool ResumeOnly, bool ConfirmedSensitiveChanges, string PackageId);

/// <summary>Stored only below an administrator-protected directory; never an arbitrary execution request.</summary>
public sealed class PendulumPreparationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PackageId { get; set; } = PendulumPreparationPackage.Id;
    public string Sid { get; set; } = "";
    public string AuthorizedBootId { get; set; } = "";
    public string Stage { get; set; } = "Authorized";
    public bool RootWasPresent { get; set; }
    public bool PublisherWasPresent { get; set; }
    public bool TestModeWasActive { get; set; }
    public bool BootEditAttempted { get; set; }
    public string BootBackupSha { get; set; } = "";
    public bool RootAddAttempted { get; set; }
    public bool PublisherAddAttempted { get; set; }
    public bool RootAddedConfirmed { get; set; }
    public bool PublisherAddedConfirmed { get; set; }
    public string ChildBootId { get; set; } = "";
    public int? ChildPid { get; set; }
    public long ChildStartTicks { get; set; }
    public string ChildKind { get; set; } = "";
    public string LastError { get; set; } = "";
    public DateTimeOffset AuthorizedUtc { get; set; } = DateTimeOffset.UtcNow;
}
