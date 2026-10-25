using DeltaNFD.Inspection;

namespace DeltaNFD.Services;

/// <summary>Frontend-only bookmarks; never changes game roots or cleanup permissions.</summary>
internal static class AceGameSearchSettings
{
    internal static IReadOnlyList<string> Read(string? settingsPath = null) =>
        (AppSettingsStore.ReadRequired(settingsPath).AceGameSearchRoots ?? throw new IOException("手动检索目录配置为空，请核对设置。"))
        .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(AceManualGameInspection.MaxRoots).ToArray();

    internal static OperationResult Add(string root, CancellationToken token = default, Func<bool>? canCommit = null, string? settingsPath = null)
    {
        if (!AceManualGameInspection.TryNormalizePath(root, out var normalized, out var reason)) return OperationResult.Fail(reason);
        var added = false;
        AppSettingsStore.UpdateRequired(settingsPath ?? AppSettingsStore.DefaultPath, settings =>
        {
            EnsureAllowed(token, canCommit);
            var roots = settings.AceGameSearchRoots ?? throw new IOException("手动检索目录配置为空，未覆盖。" );
            if (roots.Any(p => AceManualGameInspection.TryNormalizePath(p, out var existing, out _) && existing.Equals(normalized, StringComparison.OrdinalIgnoreCase))) return;
            if (roots.Count >= AceManualGameInspection.MaxRoots) throw new IOException($"最多保存 {AceManualGameInspection.MaxRoots} 个检索目录，请先移除不再使用的目录。");
            settings.AceGameSearchRoots = [.. roots, normalized];
            added = true;
        });
        return OperationResult.Ok(added ? "已添加游戏检索目录，正在检测 ACE 组件。" : "该目录已在检索列表中，正在重新检测。");
    }

    internal static void Remove(string root, CancellationToken token = default, Func<bool>? canCommit = null, string? settingsPath = null)
    {
        AppSettingsStore.UpdateRequired(settingsPath ?? AppSettingsStore.DefaultPath, settings =>
        {
            EnsureAllowed(token, canCommit);
            var roots = settings.AceGameSearchRoots ?? throw new IOException("手动检索目录配置为空，未覆盖。");
            settings.AceGameSearchRoots = roots.Where(p => !string.Equals(p, root, StringComparison.OrdinalIgnoreCase)).ToList();
        });
    }

    private static void EnsureAllowed(CancellationToken token, Func<bool>? canCommit)
    {
        token.ThrowIfCancellationRequested();
        if (canCommit is not null && !canCommit()) throw new OperationCanceledException("已离开 ACE 栏目或游戏状态发生变化，未更改检索目录。");
    }
}
