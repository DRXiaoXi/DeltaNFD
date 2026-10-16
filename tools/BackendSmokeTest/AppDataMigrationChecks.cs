using DeltaNFD.Services;

internal static class AppDataMigrationChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_MigrationChecks_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "first");
            var old = Path.Combine(first, "DeltaOptimizer");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "settings.json"), "old settings");
            File.WriteAllText(Path.Combine(old, "backups.json"), "old backup");
            var current = AppDataPaths.ResolveRootAt(first, out var warning);
            Check(current == Path.Combine(first, AppDataPaths.DirectoryName) && warning is null,
                "First migration did not select the new directory");
            Check(File.ReadAllText(Path.Combine(current, "settings.json")) == "old settings" &&
                  File.ReadAllText(Path.Combine(current, "backups.json")) == "old backup" &&
                  File.Exists(Path.Combine(old, "settings.json")), "First migration did not preserve data");

            var merge = Path.Combine(root, "merge");
            old = Path.Combine(merge, "DeltaOptimizer");
            current = Path.Combine(merge, AppDataPaths.DirectoryName);
            Directory.CreateDirectory(old);
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(old, "settings.json"), "old settings");
            File.WriteAllText(Path.Combine(old, "backups.json"), "old backup");
            File.WriteAllText(Path.Combine(current, "settings.json"), "new settings");
            Check(AppDataPaths.ResolveRootAt(merge, out warning) == current && warning is null,
                "Existing new directory was not selected");
            Check(File.ReadAllText(Path.Combine(current, "settings.json")) == "new settings" &&
                  File.ReadAllText(Path.Combine(current, "backups.json")) == "old backup",
                "Merge overwrote new settings or missed an old backup");

            File.SetLastWriteTimeUtc(Path.Combine(old, "settings.json"), DateTime.UtcNow.AddMinutes(2));
            Check(AppDataPaths.ResolveRootAt(merge, out warning) == current && warning is not null &&
                  File.ReadAllText(Path.Combine(current, "settings.json")) == "new settings",
                "Newer legacy settings were silently ignored or overwrote the new settings");

            var blocked = Path.Combine(root, "blocked");
            old = Path.Combine(blocked, "DeltaOptimizer");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "settings.json"), "old settings");
            File.WriteAllText(Path.Combine(blocked, AppDataPaths.DirectoryName), "not a directory");
            Check(AppDataPaths.ResolveRootAt(blocked, out warning) == old && warning is not null,
                "Failed migration did not fall back to legacy data with a warning");
            Check(Directory.GetDirectories(blocked, AppDataPaths.DirectoryName + ".migrating-*").Length == 0 &&
                  File.ReadAllText(Path.Combine(old, "settings.json")) == "old settings",
                "Failed migration left a staging directory or altered legacy settings");

            Console.WriteLine("应用数据迁移检查通过：首次复制、保留新版设置、旧版更新提示、失败回退旧目录");
        }
        finally
        {
            var fullRoot = Path.GetFullPath(root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(fullRoot, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
