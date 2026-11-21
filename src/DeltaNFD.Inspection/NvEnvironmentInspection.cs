using System.Text.RegularExpressions;
using Microsoft.Win32;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;
public static class NvEnvironmentInspection
{
    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string DeviceRoot = @"SYSTEM\CurrentControlSet\Enum\";
    private const string DriverRoot = @"SYSTEM\CurrentControlSet\Control\Class\";
    private const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static Task<NvGpuEnvironmentInfo> ReadAsync() => InspectionScheduler.Run(() =>
    {
        // Reuse the same adapter selection as shader inspection without walking PSO caches.
        var driver = new ShaderInspection().ReadDefaultGameDriver();
        var isNvidia = driver?.State == ShaderGpuDetectionState.Nvidia;
        return new NvGpuEnvironmentInfo
        {
            IsNvidia = isNvidia,
            GpuName = ReadOriginalGpuName(driver, ReadRegistryKey),
            NvApiAvailable = NvShaderInspection.IsAvailable,
            DriverVersion = NvShaderInspection.ReadDriverVersion(),
            DrsFilesExist = DrsFiles().Any(File.Exists),
            DrsFilesLocked = DrsFiles().Any(p => File.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReadOnly) != 0),
            ShaderCache = isNvidia ? NvShaderInspection.ReadShaderCacheSizes() : null,
        };
    });

    // DeviceDesc (and WMI Name derived from it) may be spoofed. Resolve DriverDesc only
    // from the driver bound to the exact PCI instance selected above, not the first NVIDIA driver.
    internal static string ReadOriginalGpuName(ShaderDriverReading? reading,
        Func<string, string[], IReadOnlyDictionary<string, object?>?> readKey)
    {
        if (reading?.State != ShaderGpuDetectionState.Nvidia || reading.PnpDeviceId.Length > 512)
            return "";
        var instance = Regex.Match(reading.PnpDeviceId,
            @"^PCI\\(?<chip>VEN_10DE&DEV_[0-9A-F]{4})(?:&[0-9A-Z_]+)*\\[0-9A-Z&_.-]+\z", IgnoreCase);
        if (!instance.Success)
            return "";

        try
        {
            var device = readKey(DeviceRoot + reading.PnpDeviceId, ["ClassGUID", "Driver", "HardwareID"]);
            if (!string.Equals(device?.GetValueOrDefault("ClassGUID") as string, DisplayClass, StringComparison.OrdinalIgnoreCase)
                || device?.GetValueOrDefault("Driver") is not string driverKey
                || !Regex.IsMatch(driverKey, "^" + Regex.Escape(DisplayClass) + @"\\[0-9]{4}\z", IgnoreCase)
                || device.GetValueOrDefault("HardwareID") is not string[] hardwareIds)
                return "";

            var driver = readKey(DriverRoot + driverKey, ["ProviderName", "MatchingDeviceId", "DriverDesc"]);
            if (driver?.GetValueOrDefault("ProviderName") is not string provider
                || !provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                || driver.GetValueOrDefault("MatchingDeviceId") is not string matching
                || !Regex.IsMatch(matching, @"^PCI\\" + instance.Groups["chip"].Value + @"(?:&[0-9A-Z_]+)*\z", IgnoreCase)
                || !hardwareIds.Contains(matching, StringComparer.OrdinalIgnoreCase)
                || driver.GetValueOrDefault("DriverDesc") is not string name
                || string.IsNullOrWhiteSpace(name) || name.Length > 512
                || name.Any(c => char.IsControl(c) || c is '@' or '%'))
                return "";
            return name.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A model lookup failure must not clear GPU detection or disable driver settings.
            return "";
        }
    }

    private static IReadOnlyDictionary<string, object?>? ReadRegistryKey(string path, string[] valueNames)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
        return key is null ? null : valueNames.ToDictionary(name => name,
            name => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }

    private static IEnumerable<string> DrsFiles() => new[]
    {
        "nvdrsdb0.bin",
        "nvdrsdb1.bin"
    }.Select(name => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NVIDIA Corporation", "Drs", name));
}
