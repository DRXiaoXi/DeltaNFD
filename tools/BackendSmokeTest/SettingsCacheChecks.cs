using DeltaNFD.Services;

internal static class SettingsCacheChecks
{
    internal static void Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DeltaNFD_SettingsCache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var first = Path.Combine(dir, "first.json");
        var second = Path.Combine(dir, "second.json");
        var blocked = Path.Combine(dir, "blocked.json");
        try
        {
            AppSettingsStore.Update(first, s => s.FrameMemoryCleanThresholdPercent = 10);
            AppSettingsStore.Update(second, s => s.FrameMemoryCleanThresholdPercent = 20);
            var snapshot = AppSettingsStore.Read(first);
            snapshot.FrameMemoryCleanThresholdPercent = 99;
            Assert(AppSettingsStore.Read(first).FrameMemoryCleanThresholdPercent == 10, "读取副本不得修改缓存");
            Assert(AppSettingsStore.Read(second).FrameMemoryCleanThresholdPercent == 20, "路径之间不得串用缓存");

            Parallel.For(0, 32, _ => AppSettingsStore.Update(first, s => s.FrameMemoryCleanThresholdPercent++));
            Assert(AppSettingsStore.Read(first).FrameMemoryCleanThresholdPercent == 42, "并发更新不得丢失");

            AppSettings.Save(first, new AppSettings { FrameMemoryCleanThresholdPercent = 73 });
            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddSeconds(2));
            Assert(AppSettingsStore.Read(first).FrameMemoryCleanThresholdPercent == 73, "外部修改应使缓存刷新");

            AppSettingsStore.Update(blocked, s => s.FrameMemoryCleanThresholdPercent = 17);
            Directory.CreateDirectory(blocked + ".tmp");
            AppSettingsStore.Update(blocked, s => s.FrameMemoryCleanThresholdPercent = 88);
            Assert(AppSettingsStore.Read(blocked).FrameMemoryCleanThresholdPercent == 17, "写入失败不得保留未落盘值");
            Console.WriteLine("设置缓存检查通过：副本隔离、路径隔离、并发更新、外部修改、写入失败");
        }
        finally
        {
            foreach (var path in new[] { first, second, blocked })
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
                if (Directory.Exists(path + ".tmp")) Directory.Delete(path + ".tmp");
            }
            Directory.Delete(dir);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
