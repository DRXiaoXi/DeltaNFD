using System.Diagnostics;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

/// <summary>Query-only; never changes a boot setting, device, or recovery record.</summary>
public static class TimerConfigurationInspection
{
    public static string RecordPath => Path.Combine(AppDataPaths.Root, "timer-recovery-latest.json");
    public static Task<TimerRecoveryStatus> ReadAsync() => InspectionScheduler.Run(Read);
    public static TimerRecoveryStatus Read()
    {
        var errors = new List<string>();
        var run = QueryBcd();
        var parsed = ParseBcd(run.ExitCode, run.Output);
        if (!parsed.BcdReadable) errors.Add("无法读取当前启动项（退出码 " + run.ExitCode + "），请以管理员运行工具");
        var devices = new List<TimerDeviceState>();
        var devicesReadable = false;
        var boot = "";
        try
        {
            var scope = new ManagementScope(@"\\.\root\cimv2", new ConnectionOptions { Timeout = TimeSpan.FromSeconds(10) });
            var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10), ReturnImmediately = false };
            using var search = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity"), options);
            using var results = search.Get();
            foreach (ManagementObject device in results)
                using (device)
                {
                    var id = device["PNPDeviceID"] as string ?? "";
                    if (IsHpetDevice(id)) devices.Add(new(id, device["ConfigManagerErrorCode"] is { } code ? Convert.ToInt32(code) : null));
                }
            devicesReadable = devices.All(d => d.ProblemCode is not null);
            if (!devicesReadable) errors.Add("HPET 设备状态不可读");
            using var os = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT LastBootUpTime FROM Win32_OperatingSystem"), options);
            using var systems = os.Get();
            foreach (ManagementObject system in systems)
                using (system) { boot = system["LastBootUpTime"] as string ?? ""; break; }
        }
        catch (Exception ex) { errors.Add("设备或启动时间查询失败：" + ex.Message); }
        var incomplete = false; var restart = false;
        try
        {
            if (File.Exists(RecordPath))
            {
                if ((File.GetAttributes(RecordPath) & FileAttributes.ReparsePoint) != 0 || new FileInfo(RecordPath).Length > 512 * 1024)
                    throw new InvalidDataException("恢复记录大小或文件类型不安全");
                var record = JsonSerializer.Deserialize<TimerRecoveryRecord>(File.ReadAllText(RecordPath)) ?? throw new InvalidDataException("恢复记录为空");
                incomplete = !record.Completed;
                restart = record.Changed && (boot.Length == 0 || record.Before.BootId.Length == 0 || boot == record.Before.BootId);
            }
        }
        catch (Exception ex) { incomplete = true; errors.Add("恢复记录待核对：" + ex.Message); }
        var result = parsed with { Devices = devices.OrderBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase).ToList(), DevicesReadable = devicesReadable,
            BootId = boot, RecoveryIncomplete = incomplete, PendingRestart = restart, Error = string.Join("；", errors) };
        return result with { Fingerprint = Fingerprint(result) };
    }
    public static bool IsHpetDevice(string id) => Regex.IsMatch(id, @"^ACPI\\PNP0103\\[A-Za-z0-9_&.\\-]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static TimerRecoveryStatus ParseBcd(int code, string output)
    {
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return new();
        string Value(string name) => output.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 2 && p[0].Equals(name,StringComparison.OrdinalIgnoreCase)).Select(p=>p[1]).FirstOrDefault() ?? "";
        var dynamicTick = Value("disabledynamictick"); var platformTick = Value("useplatformtick");
        // A nonempty failure/help message must not masquerade as an empty, default boot entry.
        var entry = output.Contains("{current}",StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(output,@"\{[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\}");
        return new() { BcdReadable = entry && TimerRecoveryStatus.IsKnown(dynamicTick) && TimerRecoveryStatus.IsKnown(platformTick),
            DynamicTick = dynamicTick, PlatformTick = platformTick };
    }
    internal static string Fingerprint(TimerRecoveryStatus value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
        value.BcdReadable,value.DynamicTick,value.PlatformTick,value.DevicesReadable,value.Devices,value.BootId,value.RecoveryIncomplete,value.PendingRestart })));
    private static (int ExitCode,string Output) QueryBcd()
    {
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"bcdedit.exe")) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true,RedirectStandardError=true };
            info.ArgumentList.Add("/enum");info.ArgumentList.Add("{current}");
            using var process=Process.Start(info) ?? throw new IOException("未启动 BCDEdit");
            var text=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000)) { try { process.Kill(); } catch { } return (-1,""); }
            Task.WaitAll([text,error],TimeSpan.FromSeconds(3));
            return (process.ExitCode,text.IsCompletedSuccessfully ? text.Result : "");
        }
        catch { return (-1,""); }
    }
}
