using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeltaNFD.Services;

public sealed record DisplayInterruptAdapter(string InstanceId, string Name, string DriverVersion, string DriverKey,
    string Identity, bool Started, bool Readable, uint? Priority, string Detail = "");

public sealed record DisplayInterruptPriorityItem(DisplayInterruptAdapter Adapter, string Fingerprint, bool HasBackup,
    bool CanApply, bool CanRestore, string Detail);

public sealed record DisplayInterruptPriorityReport(IReadOnlyList<DisplayInterruptPriorityItem> Items, bool Complete,
    int BackupCount, string Message);

public sealed record DisplayInterruptPriorityArgs(string InstanceId, string Fingerprint, bool Restore, bool Confirmed);

public static class DisplayInterruptPriorityPolicy
{
    public const string RecoveryScope = "display-interrupt-priority";
    public const uint High = 3;

    public static bool ValidId(string? id)
    {
        if (id is null || id.Length > 512) return false;
        var parts = id.Split('\\');
        return parts.Length == 3 && parts[0].Equals("PCI", StringComparison.OrdinalIgnoreCase)
            && parts[1].StartsWith("VEN_", StringComparison.OrdinalIgnoreCase)
            && parts.Skip(1).All(part => part.Length is > 0 and <= 256 && part is not ("." or "..")
                && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '&' or '-' or '.' or '{' or '}'));
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static string Fingerprint(DisplayInterruptAdapter adapter, string? backupDigest) => Hash(
        JsonSerializer.Serialize(new { adapter.InstanceId, adapter.Identity, adapter.Started, adapter.Readable, adapter.Priority, backupDigest }));
}
