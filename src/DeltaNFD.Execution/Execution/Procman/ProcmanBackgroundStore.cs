using System.Text.Json;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

internal sealed class ProcmanBackgroundStore(string path)
{
    internal static ProcmanBackgroundConfig Validate(ProcmanBackgroundConfig config)
    {
        if (config.SchemaVersion != 1 || config.MemoryPressurePercent is < 30 or > 95 || config.MemoryBatchLimit is < 1 or > 256
            || config.MemoryCooldownSeconds is < 60 or > 86400
            || config.MemoryMode is not (ProcmanMemoryModes.Normal or ProcmanMemoryModes.Aggressive or ProcmanMemoryModes.DeltaStable)
            || config.ExcludedExecutablePaths is null || config.ExcludedExecutablePaths.Count > 256)
            throw new ArgumentException("后台管理配置无效：内存阈值需为 30–95%，单次回收进程数需为 1–256，回收间隔需为 60–86400 秒，排除项最多 256 个。");
        var suppression = config.Suppression;
        if (suppression is null || suppression.CpuBusyThreshold is < 10 or > 99 || suppression.CpuIdleThreshold is < 5 or > 99
            || suppression.CpuBusyThreshold <= suppression.CpuIdleThreshold || suppression.CpuBusySeconds is < 1 or > 600
            || suppression.CpuIdleSeconds is < 1 or > 600 || suppression.CooldownSeconds is < 0 or > 3600
            || suppression.CandidateCpuFloorPercent is < 0 or > 100 || suppression.MaxAdjustPerRound is < 1 or > 4)
            throw new ArgumentException("后台抑制参数无效；触发阈值需高于恢复阈值。");
        var paths = config.ExcludedExecutablePaths.Select(p =>
        {
            if (string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p) || p.Length > 32760
                || p.IndexOfAny(['*', '?']) >= 0 || !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("排除项必须为完整 EXE 路径，不支持名称通配。");
            return Path.GetFullPath(p);
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return config with { ExcludedExecutablePaths = paths };
    }

    internal ProcmanBackgroundConfig Load()
    {
        try
        {
            try { _ = File.GetAttributes(path); }
            catch (FileNotFoundException) { return new(); }
            catch (DirectoryNotFoundException) { return new(); }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("配置过大。");
            using var document = JsonDocument.Parse(stream);
            var config = Validate(document.RootElement.Deserialize<ProcmanBackgroundConfig>() ?? throw new InvalidDataException("配置为空。"));
            // 仅迁移没有版本标记且整套数值恰好等于旧出厂值的配置，保留用户自定义参数。
            if (document.RootElement.TryGetProperty("Suppression", out var saved)
                && !saved.TryGetProperty("DefaultsVersion", out _)
                && config.Suppression is { CpuBusyThreshold: 80, CpuBusySeconds: 6, CpuIdleThreshold: 60,
                    CpuIdleSeconds: 8, CooldownSeconds: 10, CandidateCpuFloorPercent: 10, MaxAdjustPerRound: 2 })
                config = config with { Suppression = new() };
            return config;
        }
        catch (Exception ex) { throw new InvalidDataException("后台管理配置无法读取，已暂停自动回收；原文件保留。", ex); }
    }

    internal void Save(ProcmanBackgroundConfig config)
    {
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        using var mutex = new Mutex(false, @"Global\DeltaNFD.ProcmanBackground." + digest);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("另一进程正在保存后台管理设置，请稍后重试。");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Never silently replace a corrupt existing configuration.
            _ = Load();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, Validate(config), new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            finally { mutex.ReleaseMutex(); }
        }
    }
}
