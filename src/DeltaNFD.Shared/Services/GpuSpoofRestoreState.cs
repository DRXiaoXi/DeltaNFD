using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

/// <summary>配置收尾先于备份删除；按目标及任务代次匹配，不能清掉另一项新任务。</summary>
public static class GpuSpoofRestoreState
{
    public sealed record Snapshot(bool Pending, string Path, string Id);
    public static Snapshot Capture(AppSettings settings) => new(settings.TempSpoofRestorePending,
        settings.TempSpoofRestorePath, settings.TempSpoofRestoreId);
    public static bool Same(Snapshot a, Snapshot b) => a.Pending == b.Pending
        && a.Path.Equals(b.Path, StringComparison.OrdinalIgnoreCase) && a.Id == b.Id;
    public static bool IsNativeDescription(string value) => value.Length < 4096 && Regex.IsMatch(value,
        @"^@oem[0-9]+\.inf,%[^;\r\n\\]+%;[^\r\n]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static Snapshot Schedule(string path, string? settingsPath = null)
    {
        var id = Guid.NewGuid().ToString("D");
        AppSettingsStore.UpdateRequired(settingsPath ?? AppSettingsStore.DefaultPath, s =>
        {
            if (s.TempSpoofRestorePending) throw new IOException("已有临时伪装待还原，请先完成还原收尾。");
            s.TempSpoofRestorePending = true; s.TempSpoofRestorePath = path; s.TempSpoofRestoreId = id;
        });
        return new(true, path, id);
    }

    public static void Complete(Snapshot expected, string restoredPath, string? settingsPath = null)
    {
        AppSettingsStore.UpdateRequired(settingsPath ?? AppSettingsStore.DefaultPath, s =>
        {
            var current = Capture(s);
            if (!current.Pending) return; // Repeating an already completed restore is safe.
            if (!current.Path.Equals(restoredPath, StringComparison.OrdinalIgnoreCase))
            {
                if (expected.Pending && expected.Path.Equals(restoredPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("待还原目标已经变化，原记录保留，未完成收尾。");
                return; // Restoring another adapter must not clear this pending task.
            }
            if (!expected.Pending || !Same(expected, current)) throw new IOException("待还原任务已经变化，备份保留，请重新核对。");
            s.TempSpoofRestorePending = false; s.TempSpoofRestorePath = ""; s.TempSpoofRestoreId = "";
        });
    }

    public static bool CanReconcile(Snapshot pending, DisplayAdapterInfo? adapter) => pending.Pending
        && adapter is { HasBackup: false, IsMasked: false }
        && pending.Path.Equals(adapter.RegistryPath, StringComparison.OrdinalIgnoreCase)
        && IsNativeDescription(adapter.DeviceDesc);

    public static void Finish(Action restoreAndVerify, Action completePending, Action removeBackup)
    {
        restoreAndVerify();
        completePending();
        removeBackup();
    }
}
