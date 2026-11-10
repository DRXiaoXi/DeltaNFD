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
            var prefetch = PrefetchInspection.Read(mm.ApplicationPreLaunchOn);
            if (prefetch.Disabled is null) errors.Add(prefetch.Detail);
            return new(total, service.Start, running, mm.CompressionOn, mm.PageCombiningOn, prefetch.Disabled, string.Join(" ", errors));
        }
        catch (Exception ex) { return new(total, Error: "低内存配置读取失败：" + ex.Message); }
    }
    internal static bool? EvaluatePrefetch(IReadOnlyList<int?> values, bool? preLaunchOn) =>
        PrefetchInspection.Evaluate(values, preLaunchOn);
}
