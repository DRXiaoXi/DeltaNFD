namespace DeltaNFD.Services;

/// <summary>只管理手动安装目录记录；移除记录不删除目录，也不屏蔽自动发现。</summary>
internal static class GameInstallSettings
{
    internal sealed record Entry(string Path, string Source, bool CanRemove);

    internal static IReadOnlyList<Entry> List(IEnumerable<string> automatic, string? settingsPath = null)
    {
        var settings = AppSettingsStore.ReadRequired(settingsPath);
        var sources = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value, string source)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var path = Normalize(value);
            if (!sources.TryGetValue(path, out var labels)) sources[path] = labels = [];
            if (!labels.Contains(source)) labels.Add(source);
        }
        // 无效/已搬走的手动目录仍可见、可移除，不能先用游戏文件校验把它们过滤掉。
        foreach (var path in settings.GameInstallRoots ?? []) Add(path, "手动登记");
        Add(settings.GameRootOverride, "旧手动指定");
        foreach (var path in automatic) Add(path, "自动识别");
        return sources.Select(row => new Entry(row.Key, string.Join(" / ", row.Value),
            row.Value.Any(s => s != "自动识别"))).ToArray();
    }

    internal static void ClearManual(string? settingsPath = null) =>
        AppSettingsStore.UpdateRequired(settingsPath ?? AppSettingsStore.DefaultPath, settings =>
        {
            settings.GameRootOverride = "";
            settings.GameInstallRoots = [];
        });

    internal static void RemoveManual(string path, string? settingsPath = null) =>
        AppSettingsStore.UpdateRequired(settingsPath ?? AppSettingsStore.DefaultPath, settings =>
        {
            var normalized = Normalize(path);
            settings.GameInstallRoots = (settings.GameInstallRoots ?? []).Where(p =>
                !string.Equals(Normalize(p), normalized, StringComparison.OrdinalIgnoreCase)).ToList();
            if (string.Equals(Normalize(settings.GameRootOverride), normalized, StringComparison.OrdinalIgnoreCase))
                settings.GameRootOverride = "";
        });

    private static string Normalize(string? path)
    {
        var value = path?.Trim().Trim('"') ?? "";
        try { return value.Length == 0 ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)); }
        catch { return value; } // 原记录格式错误也必须能单独移除。
    }
}
