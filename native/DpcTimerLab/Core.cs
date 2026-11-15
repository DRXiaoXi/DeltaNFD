using System.Security.Cryptography;

namespace DeltaNFD.DpcTimerLab;

internal static class Rules
{
    public const string Service = "DeltaNFDDpcTimerLab";
    public const string ExeHash = "00a7c7b88877bf59dbaa70de734fcd3f287f1eb92caff4571a4c8e67a5dc0aca";
    public const string DriverHash = "636519d3293feb6779f089865c8c59a0763e720afefbad46fdb164fc06b9127e";
    public static void Request(uint us)
    {
        if (us is < 500 or > 1000) throw new ArgumentOutOfRangeException(nameof(us), "Request must be 500..1000 whole microseconds.");
    }
    public static void Guest(int major, int build, bool x64, string product, string maker, string model, bool admin, bool snapshot)
    {
        if (!snapshot || !admin || !x64 || major != 10 || build < 17763 || build >= 22000 || product != "WinNT" ||
            !maker.Equals("VMware, Inc.", StringComparison.OrdinalIgnoreCase) ||
            !model.Contains("VMware", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only an elevated Windows 10 x64 1809+ VMware guest with --vm-snapshot is allowed. Host/unknown environments are refused.");
    }
    public static string ImagePath(string path)
    {
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path.Contains('"') || path.Contains('\0') || path.IndexOf(':', 2) >= 0 || !Path.GetFullPath(path).Equals(path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected a canonical local file path, without arguments.");
        return @"\??\" + path;
    }
    public static void Config(uint type, uint start, string path, string target)
    {
        if (type != 1 || start != 3 || !path.Equals(ImagePath(target), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unknown service identity/configuration; no changes made.");
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static byte[] Extract(byte[] exe)
    {
        if (exe.Length != 306928 || Hash(exe) != ExeHash) throw new InvalidDataException("Only the exact original DPC 1.4.0 EXE is accepted.");
        // This resource offset is bound to the complete EXE hash, not a generic PE loader.
        var driver = exe.AsSpan(0x3bd88, 21232).ToArray();
        if (Hash(driver) != DriverHash) throw new InvalidDataException("Embedded x64 driver hash mismatch.");
        return driver;
    }
}

internal interface IDpcDevice : IDisposable
{
    uint Start(uint microseconds);
    uint Current100ns();
}

internal sealed class HoldSession(IDpcDevice device) : IDisposable
{
    private bool attempted, closed;
    private uint requested, granted;
    public (uint RequestedUs, uint GrantedUs, uint Current100ns, string Relation) Begin(uint microseconds)
    {
        Rules.Request(microseconds);
        if (closed) throw new ObjectDisposedException(nameof(HoldSession));
        if (attempted)
        {
            if (requested != microseconds) throw new InvalidOperationException("Changing a request requires a new stopped-driver session.");
        }
        else
        {
            requested = microseconds;
            attempted = true;
            granted = device.Start(microseconds);
            if (granted is 0 or > 1000000) throw new InvalidDataException("Invalid DPC reply; close the handle and stop the dedicated service.");
        }
        return Observe();
    }
    public (uint RequestedUs, uint GrantedUs, uint Current100ns, string Relation) Observe()
    {
        if (!attempted || closed || granted == 0 || granted > 1000000) throw new InvalidOperationException("No verified active exchange.");
        var current = device.Current100ns();
        if (current is 0 or > 10000000) throw new InvalidDataException("Invalid system resolution readback.");
        var target = checked(requested * 10);
        return (requested, granted, current, current == target ? "exact" : current < target ? "finer" : "coarser");
    }
    public void Dispose()
    {
        if (closed) return;
        closed = true;
        // Use the original driver's IRP_MJ_CLEANUP path exactly once. Do not add
        // an extra stop IOCTL: that and cleanup both contain release calls.
        device.Dispose();
    }
}
