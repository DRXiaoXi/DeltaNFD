namespace DeltaNFD.Services;

public static class DpcPendulumSource
{
    public const int ExeBytes = 306928;
    public const string ExeSha256 = "00a7c7b88877bf59dbaa70de734fcd3f287f1eb92caff4571a4c8e67a5dc0aca";
    public const string DriverSha256 = "636519d3293feb6779f089865c8c59a0763e720afefbad46fdb164fc06b9127e";
    public const string ServiceName = "dpclat_driver";
    public const uint Target100ns = 10000;
    public const string Profile = "win10-dpc-exe-v1/";
    public const string AlgorithmProfileSuffix = "/algorithms-v1";
}

public sealed record DpcPendulumConfigureArgs(string ExePath, bool Confirmed);
public sealed record DpcPendulumStatus(bool FileVerified, string ExePath, string Message);

public sealed class DpcPendulumJournal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string BootId { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string Phase { get; set; } = "intent";
    public int Pid { get; set; }
    public long StartTicks { get; set; }
    public int ControllerPid { get; set; }
    public long ControllerStartTicks { get; set; }
    public string ServicePath { get; set; } = "";
}
