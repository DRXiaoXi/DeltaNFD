using Microsoft.Win32;
using System.Text.Json;

namespace DeltaNFD.Services;

/// <summary>显卡伪装失效监测（执行实现；记录类型见合同程序集）。</summary>
public static class GpuSpoofWatch
{
    /// <summary>
    /// 判定失效：驱动印记变化即视为需要提醒，不再要求名称先被还原。
    /// 驱动更新后 Enum 键里的 DeviceDesc 是保留伪装名还是被驱动重写，行为并不确定；
    /// 只要驱动变了，就无法再证明那个字符串仍是「生效中的伪装」，因此一律提醒，
    /// 名称是否被还原只影响 UI 文案（已由 GpuSpoofInvalidation 带 CurrentName 供前端区分）。
    /// </summary>
    internal static bool IsInvalidated(GpuSpoofWatchEntry entry, string? stamp, string? name) =>
        !string.IsNullOrWhiteSpace(stamp) && !string.IsNullOrWhiteSpace(entry.DriverStamp) && name is not null &&
        stamp != entry.DriverStamp;

    private static List<GpuSpoofWatchEntry> Read(string json) =>
        JsonSerializer.Deserialize<List<GpuSpoofWatchEntry>>(json) ?? throw new InvalidDataException("伪装监测记录为空。");

    public static void Record(string path, string expected)
    {
        var stamp = ReadDriverStamp(path) ?? "";
        AppSettingsStore.Update(s =>
        {
            var entries = Read(s.GpuSpoofWatchJson);
            entries.RemoveAll(e => e.RegistryPath.Equals(path, StringComparison.OrdinalIgnoreCase));
            entries.Add(new(path, expected, stamp));
            s.GpuSpoofWatchJson = JsonSerializer.Serialize(entries);
        });
        Log.Info("显卡伪装监测：已登记目标=" + path + "；驱动=" + stamp + "；期望名称=" + expected);
    }

    public static void Remove(string path) => AppSettingsStore.Update(s =>
    {
        var entries = Read(s.GpuSpoofWatchJson);
        entries.RemoveAll(e => e.RegistryPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        s.GpuSpoofWatchJson = JsonSerializer.Serialize(entries);
    });

    public static IReadOnlyList<GpuSpoofInvalidation> Check() => DeltaNFD.Inspection.GpuSpoofReadWatch.Check();

    private static string? ReadDriverStamp(string path)
    {
        using var device = Registry.LocalMachine.OpenSubKey(path);
        var driver = device?.GetValue("Driver") as string;
        if (driver is null || !System.Text.RegularExpressions.Regex.IsMatch(driver,
            @"^\{4d36e968-e325-11ce-bfc1-08002be10318\}\\[0-9]{4}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return null;
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driver);
        if (key?.GetValue("DriverVersion") is not string version || string.IsNullOrWhiteSpace(version)) return null;
        return string.Join(" | ", version, key.GetValue("InfPath") as string ?? "", key.GetValue("DriverDate") as string ?? "");
    }
}
