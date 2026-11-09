using Microsoft.Win32;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

public static class LowMemoryInspection
{
    public static Task<LowMemoryStatus> ReadAsync() => InspectionScheduler.Run(ReadCoreAsync);
    private static async Task<LowMemoryStatus> ReadCoreAsync()
    {
        var total = PhysicalMemoryInspection.InstalledBytes();
        if (total is null or 0) return new(null, Error: "无法读取物理总内存。");
        if (total > LowMemoryStatus.LimitBytes) return new(total);
        try
        {
            var errors = new List<string>();
            var service = SecurityFeatureInspection.ReadService("SysMain");
            bool? running = service.RuntimeState switch { 4 => true, 1 => false, _ => null };
            if (service.Exists != true || service.Start is not (2 or 3 or 4) || running is null)
                errors.Add("SysMain 状态待确认。" + service.Error);
            var mm = await SystemTweakInspection.QueryMmAgentAsync();
            if (mm.CompressionOn is null || mm.PageCombiningOn is null || mm.ApplicationPreLaunchOn is null)
                errors.Add("MMAgent 状态读取未完成。");
            var values = new[]
            {
                ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "EnableApplicationLaunchPrefetching"),
                ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "EnableOperationAPI"),
                ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnablePrefetcher"),
                ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Prelaunch", "Enabled"),
            };
            // Missing overrides use Windows defaults; they do not prove prefetch is disabled.
            bool? disabled = EvaluatePrefetch(values, mm.ApplicationPreLaunchOn);
            return new(total, service.Start, running, mm.CompressionOn, mm.PageCombiningOn, disabled, string.Join(" ", errors));
        }
        catch (Exception ex) { return new(total, Error: "低内存配置读取失败：" + ex.Message); }
    }
    internal static bool? EvaluatePrefetch(IReadOnlyList<int?> values, bool? preLaunchOn) =>
        values.Any(v => v != 0) || preLaunchOn == true ? false : preLaunchOn == false ? true : null;
    private static int? ReadDword(RegistryKey hive, string path, string name)
    {
        using var key = hive.OpenSubKey(path);
        var value = key?.GetValue(name);
        if (value is null) return null;
        if (value is int number) return number;
        throw new InvalidDataException(name + " 不是 DWORD，未按默认值处理。");
    }
}
