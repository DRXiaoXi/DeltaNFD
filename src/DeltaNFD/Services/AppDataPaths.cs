namespace DeltaNFD.Services;

/// <summary>Delta NFD per-user data root, including one-time migration from the former product folder.</summary>
public static class AppDataPaths
{
    public const string DirectoryName = "Delta NFD";
    private const string LegacyDirectoryName = "DeltaOptimizer";
    private static readonly Lazy<string> RootPath = new(ResolveRoot, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Root => RootPath.Value;
    public static string? MigrationWarning { get; private set; }

    public static string RemapLegacyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path ?? "";

        try
        {
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyDirectoryName);
            var prefix = legacy.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return path;

            var relative = Path.GetRelativePath(legacy, path);
            return Path.Combine(Root, relative);
        }
        catch
        {
            return path;
        }
    }

    private static string ResolveRoot()
    {
        var root = ResolveRootAt(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), out var warning);
        MigrationWarning = warning;
        return root;
    }

    internal static string ResolveRootAt(string appData, out string? warning)
    {
        warning = null;
        var current = Path.Combine(appData, DirectoryName);
        var legacy = Path.Combine(appData, LegacyDirectoryName);

        try
        {
            if (Directory.Exists(legacy))
            {
                if (!Directory.Exists(current))
                {
                    var staging = current + ".migrating-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        CopyMissingFiles(legacy, staging);
                        Directory.Move(staging, current);
                    }
                    catch (Exception ex)
                    {
                        var fullStaging = Path.GetFullPath(staging);
                        var rootPrefix = Path.GetFullPath(appData).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (fullStaging.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            try { if (Directory.Exists(fullStaging)) Directory.Delete(fullStaging, recursive: true); }
                            catch { /* 保留现场，不影响旧数据回退。 */ }
                        }
                        warning = $"旧版数据迁移未完成（{ex.Message}），已继续使用旧版数据目录。";
                        return legacy;
                    }
                }
                else
                {
                    var currentHadSettings = File.Exists(Path.Combine(current, "settings.json"));
                    try
                    {
                        CopyMissingFiles(legacy, current);
                    }
                    catch (Exception ex)
                    {
                        warning = $"旧版数据未能完整复制（{ex.Message}）。" +
                            (currentHadSettings ? "将继续使用现有新版数据，请核对旧版备份。" : "已回退使用旧版数据目录。");
                        return currentHadSettings ? current : legacy;
                    }

                    if (currentHadSettings)
                    {
                        try
                        {
                            var oldSettings = Path.Combine(legacy, "settings.json");
                            var newSettings = Path.Combine(current, "settings.json");
                            if (File.Exists(oldSettings) &&
                                File.GetLastWriteTimeUtc(oldSettings) > File.GetLastWriteTimeUtc(newSettings))
                                warning = "检测到旧版设置比新版设置更新；已保留新版设置，请核对两份 settings.json。";
                        }
                        catch (Exception ex) { warning = $"无法核对旧版设置更新时间（{ex.Message}），请手动检查。"; }
                    }
                }
            }
            else Directory.CreateDirectory(current);
        }
        catch (Exception ex)
        {
            warning = $"应用数据目录迁移失败（{ex.Message}）。";
            return Directory.Exists(legacy) ? legacy : current;
        }

        return current;
    }

    /// <summary>Merge missing files only; never overwrite data already written under the new name.</summary>
    private static void CopyMissingFiles(string sourceRoot, string destinationRoot)
    {
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((sourceRoot, destinationRoot));

        while (pending.Count > 0)
        {
            var (source, destination) = pending.Pop();
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.EnumerateFiles(source))
            {
                var target = Path.Combine(destination, Path.GetFileName(file));
                if (!File.Exists(target))
                    File.Copy(file, target, overwrite: false);
            }

            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    continue;

                pending.Push((directory, Path.Combine(destination, Path.GetFileName(directory))));
            }
        }
    }
}
