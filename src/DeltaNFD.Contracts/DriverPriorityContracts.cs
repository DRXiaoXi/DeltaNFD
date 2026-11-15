using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaNFD.Services;

public readonly record struct InterruptDword(bool Exists, uint Value = 0);
public sealed record DriverInterruptSettings(InterruptDword Msi, InterruptDword MessageLimit, InterruptDword Priority)
{
    public InterruptDword Get(int slot) => slot switch { 0 => Msi, 1 => MessageLimit, 2 => Priority, _ => throw new ArgumentOutOfRangeException(nameof(slot)) };
    public DriverInterruptSettings Set(int slot, InterruptDword value) => slot switch
    { 0 => this with { Msi = value }, 1 => this with { MessageLimit = value }, 2 => this with { Priority = value }, _ => throw new ArgumentOutOfRangeException(nameof(slot)) };
}
public sealed record DriverPriorityDevice(string InstanceId, string Name, string ClassName, string DriverVersion,
    string DriverKey, string Identity, uint? SupportedModes, uint? MaximumMessages, bool Started,
    DriverInterruptSettings? Settings, IReadOnlyList<int> Irqs, string Detail = "");
public sealed record DriverPriorityItem(DriverPriorityDevice Device, string Fingerprint, bool HasBackup,
    bool CanApply, bool CanRestore, string BackupDetail);
public sealed record DriverPriorityReport(IReadOnlyList<DriverPriorityItem> Items, bool Complete, int BackupCount,
    IReadOnlyList<string> Errors);
public enum DriverPriorityAction { Apply = 0, Restore = 1, KeepCurrent = 2 }
public sealed record DriverPriorityArgs(string InstanceId, string Fingerprint, DriverPriorityAction Action,
    bool Confirmed = false, bool? MsiEnabled = null, bool SetMessageLimit = false, uint? MessageLimit = null, int? Priority = null);

public static class DriverPriorityPolicy
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static bool ValidId(string? id)
    {
        if (id is null || id.Length > 512) return false;
        var parts = id.Split('\\');
        return parts.Length == 3 && parts[0].Equals("PCI", StringComparison.OrdinalIgnoreCase)
            && parts.Skip(1).All(p => p.Length is > 0 and <= 256 && p is not ("." or "..")
                && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '&' or '-' or '.' or '{' or '}'));
    }
    public static void Validate(DriverInterruptSettings value)
    {
        for (var i = 0; i < 3; i++)
        {
            var slot = value.Get(i);
            if (!slot.Exists && slot.Value != 0 || slot.Value > (i == 0 ? 1u : i == 1 ? 2048u : 3u))
                throw new InvalidDataException("中断设置的数据类型或数值不在可核对范围。");
        }
    }
    public static DriverInterruptSettings Desired(DriverPriorityDevice device, DriverPriorityArgs args)
    {
        var value = device.Settings ?? throw new IOException("当前设置读取不完整。");
        Validate(value);
        if (args.MsiEnabled is { } enabled)
        {
            if (device.SupportedModes is not { } modes || (modes & 6) == 0 || !enabled && (modes & 1) == 0)
                throw new InvalidOperationException("设备未确认支持所选中断模式，未更改MSI开关。");
            value = value with { Msi = new(true, enabled ? 1u : 0u) };
        }
        if (!args.SetMessageLimit && args.MessageLimit is not null) throw new InvalidDataException("消息上限参数缺少修改标记。");
        if (args.SetMessageLimit)
        {
            if (args.MessageLimit is { } limit && limit > 0)
            {
                if (device.SupportedModes is not { } modes || (modes & 6) == 0
                    || device.MaximumMessages is not > 0 || limit > Math.Min(device.MaximumMessages.Value, 2048u)
                    || (modes & 4) == 0 && limit is not (1 or 2 or 4 or 8 or 16))
                    throw new InvalidOperationException("消息上限超出已确认能力；MSI仅支持1/2/4/8/16，MSI-X须不超过设备上限和2048。");
                value = value with { MessageLimit = new(true, limit) };
            }
            else value = value with { MessageLimit = default };
        }
        if (args.Priority is { } priority)
        {
            if (priority is < 0 or > 3) throw new InvalidDataException("中断优先级只能是默认、低、正常或高。");
            value = value with { Priority = priority == 0 ? default : new(true, (uint)priority) };
        }
        return value;
    }
    public static string SnapshotFingerprint(DriverPriorityDevice device, string backupDigest) => Hash(
        device.InstanceId.ToUpperInvariant() + "|" + device.Identity + "|" + device.Started + "|" + JsonSerializer.Serialize(device.Settings) + "|" + backupDigest);
}
