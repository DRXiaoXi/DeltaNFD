using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

public enum ClockEnvironmentAction { RestoreDefaultSource, DisableHpet, RestoreSource, RestoreHpet }
public sealed record ClockEnvironmentArgs(ClockEnvironmentAction Action, string Fingerprint, bool Confirmed);
public sealed record ClockBcdState(string EntryId, string Clock, string Tick, string Inheritance, bool InheritedOverride);
public sealed record HpetDeviceState(string InstanceId, string Identity, string Name, uint Status, uint Problem)
{
    public bool Disabled => Problem == 22;
    public bool CanDisable => Problem == 0 && (Status & 0x2000) != 0;
}
public sealed record ClockEnvironmentSystem(string Machine, string CpuVendor, string BootId,
    ClockBcdState? Bcd, IReadOnlyList<HpetDeviceState> Devices, bool DevicesReadable, string Error);
public sealed record ClockEnvironmentStatus
{
    public ClockEnvironmentSystem? System { get; init; }
    public bool RecordsReadable { get; init; }
    public bool CanDefaultSource { get; init; }
    public bool CanDisableHpet { get; init; }
    public bool CanRestoreSource { get; init; }
    public bool CanRestoreHpet { get; init; }
    public bool SourceBackup { get; init; }
    public bool HpetBackup { get; init; }
    public bool PendingRestart { get; init; }
    public bool Incomplete { get; init; }
    public string Revision { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public string Message { get; init; } = "尚未检测计时源与 HPET 设备。";
}
public static class ClockEnvironmentPolicy
{
    public const int Version = 4;
    public static bool IsBcdKey(string key) => key is "useplatformclock" or "useplatformtick";
    public static bool IsSource(ClockEnvironmentAction action) => action is ClockEnvironmentAction.RestoreDefaultSource or ClockEnvironmentAction.RestoreSource;
    public static bool IsRestore(ClockEnvironmentAction action) => action is ClockEnvironmentAction.RestoreSource or ClockEnvironmentAction.RestoreHpet;
    public static bool ValidDevice(string value) => value.Length <= 512 && Regex.IsMatch(value,
        @"^ACPI\\PNP0103\\[A-Za-z0-9_&.\-]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Digest<T>(T value) => Hash(JsonSerializer.Serialize(value));
    public static bool BcdValue(string value) => value is "" or "yes" or "no";
    public static bool EntryId(string value) => Guid.TryParseExact(value, "B", out _);
}
