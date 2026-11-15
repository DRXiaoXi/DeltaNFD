using Microsoft.Win32;
using System.Text.Json;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;
public sealed class GpuSpoofReadWatch
{
    internal static bool IsInvalidated(GpuSpoofWatchEntry entry, string? stamp, string? name) => !string.IsNullOrWhiteSpace(stamp) && !string.IsNullOrWhiteSpace(entry.DriverStamp) && name is not null && stamp != entry.DriverStamp;
    private static List<GpuSpoofWatchEntry> Read(string json) => JsonSerializer.Deserialize<List<GpuSpoofWatchEntry>>(json) ?? throw new InvalidDataException("伪装监测记录为空。");
    public static IReadOnlyList<GpuSpoofInvalidation> Check()
    {
        var result = new List<GpuSpoofInvalidation>();
        foreach (var entry in Read(AppSettingsStore.Read().GpuSpoofWatchJson))
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(entry.RegistryPath);
                if (key is null || !string.Equals(key.GetValue("ClassGUID") as string, "{4d36e968-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn("伪装监测：原显卡实例不可确认，未判断为正常或失效：" + entry.RegistryPath);
                    continue;
                }

                var stamp = ReadDriverStamp(entry.RegistryPath);
                var name = key.GetValue("DeviceDesc") as string;
                if (IsInvalidated(entry, stamp, name))
                {
                    result.Add(new(entry, stamp!, name!));
                    Log.Warn("显卡伪装已失效：目标=" + entry.RegistryPath + "；旧驱动=" + entry.DriverStamp + "；新驱动=" + stamp + "；当前名称=" + name);
                }
            }
            catch (Exception ex)
            {
                Log.Error("伪装监测：单个适配器读取失败，继续检查其他显卡", ex);
            }
        }

        return result;
    }

    private static string? ReadDriverStamp(string path)
    {
        using var device = Registry.LocalMachine.OpenSubKey(path);
        var driver = device?.GetValue("Driver") as string;
        if (driver is null || !System.Text.RegularExpressions.Regex.IsMatch(driver, @"^\{4d36e968-e325-11ce-bfc1-08002be10318\}\\[0-9]{4}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return null;
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driver);
        if (key?.GetValue("DriverVersion")is not string version || string.IsNullOrWhiteSpace(version))
            return null;
        return string.Join(" | ", version, key.GetValue("InfPath") as string ?? "", key.GetValue("DriverDate") as string ?? "");
    }
}
