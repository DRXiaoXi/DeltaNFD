using DeltaNFD.Inspection;
using DeltaNFD.Services.TweakDb;

namespace DeltaNFD.Services;

internal static class SystemInspectionCache
{
    private static readonly object Gate = new();
    private static Task<PagefileInfo>? _pagefile;
    private static Task<BxBackupStatus>? _backups;
    private static Task<List<AdvancedTweakGroup>>? _groups;
    private static Task<IReadOnlyList<DriveTypeReport>>? _drives;
    private static readonly Dictionary<string, Task<BxGearInfo?>> Gears = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<List<string>>> Services = new(StringComparer.OrdinalIgnoreCase);
    internal static Task<IReadOnlyList<DriveTypeReport>> Drives() { lock (Gate) return _drives ??= InspectionScheduler.Run(DriveInspector.GetFixedDriveReports); }
    internal static Task<BxGearInfo?> Gear(BxItem item) { lock (Gate) { var key = item.SectionId + "|" + item.Name; if (!Gears.TryGetValue(key, out var read)) Gears[key] = read = new BxInspection().GetGearInfoAsync(item); return read; } }
    internal static Task<List<string>> ResolvedServices(BxServiceGroup group) { lock (Gate) { if (!Services.TryGetValue(group.Id, out var read)) Services[group.Id] = read = new BxInspection().ResolveServiceGroupAsync(group); return read; } }
    internal static void ForgetGear(string section, string name) { lock (Gate) Gears.Remove(section + "|" + name); }
    internal static Task<PagefileInfo> Pagefile() { lock (Gate) return _pagefile ??= new PagefileInspection().GetInfoAsync(); }
    internal static Task<BxBackupStatus> Backups() { lock (Gate) return _backups ??= OptimizationBackupInspection.ReadAsync(); }
    internal static Task<List<AdvancedTweakGroup>> Groups() { lock (Gate) return _groups ??= new AdvancedTweakInspection().GetGroupsAsync(); }
    internal static async Task PreloadAsync()
    {
        var errors = new List<string>();
        foreach (var read in new Func<Task>[] { async () => { await Pagefile(); }, async () => { var b = await Backups(); if (!b.Readable) throw new IOException(b.Error); }, async () => { await Groups(); } })
            try { await read(); } catch (Exception ex) { errors.Add(ex.Message); }
        if (errors.Count > 0) throw new IOException(string.Join("；", errors));
    }
    internal static void Invalidate() { lock (Gate) { _pagefile = null; _backups = null; _groups = null; Gears.Clear(); Services.Clear(); } }
}
