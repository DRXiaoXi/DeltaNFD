namespace DeltaNFD.Services;

/// <summary>
/// 系统垃圾清理的真实实现。清单取自 扩展优化库 CleanUp.json 的安全子集：
/// 只清"内容"不动目录本身；被占用的文件自动跳过。
/// </summary>
public sealed class CleanupService : ICleanupService
{
    private sealed record Target(string Name, Func<string[]> Paths, string Note = "");

    private static readonly Target[] Targets =
    [
        new("用户临时文件", () => [Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Temp"],
            "个别正在使用的文件会自动跳过"),
        new("Windows 临时文件", () => [Environment.SystemDirectory + "\\..\\Temp"], ""),
        new("Prefetch 预读取", () => [Environment.SystemDirectory + "\\..\\Prefetch"],
            "已关闭 Prefetch 时此目录不再增长"),
        new("DirectX / D3DSCache 着色器缓存", () => [Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\D3DSCache"],
            "游戏首次启动会重新编译着色器"),
        new("DirectX 着色器缓存（NVIDIA/AMD）", () => [Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\NVIDIA\\DXCache",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\AMD\\DxCache"], ""),
        new("Windows 更新下载缓存", () => [Environment.SystemDirectory + "\\..\\SoftwareDistribution\\Download"],
            "清理后 Windows 更新需重新下载，更新服务运行时部分文件跳过"),
        new("缩略图缓存", () => [Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Microsoft\\Windows\\Explorer"],
            "仅清 thumbcache_*.db，资源管理器会自动重建"),
        new("错误报告与转储", () => [
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\CrashDumps",
            Environment.SystemDirectory + "\\..\\LiveKernelReports",
            Environment.SystemDirectory + "\\..\\Minidump"], ""),
        new("Windows 错误报告队列", () => [Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Microsoft\\Windows\\WER"], ""),
        new("INetCache 低权限缓存", () => [Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Microsoft\\Windows\\INetCache"], ""),
        new("Cryptnet URL 缓存", () => [Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Microsoft\\CryptnetUrlCache"], ""),
        new("CLR / .NET 使用日志", () => [Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Microsoft\\CLR_v4.0",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Microsoft\\CLR_v4.0_32"], ""),
        new("传输优化文件", () => [Environment.SystemDirectory + "\\..\\DeliveryOptimization"], ""),
    ];

    public Task<List<CleanupTarget>> ScanAsync() => Task.Run(() =>
    {
        var result = new List<CleanupTarget>();
        foreach (var target in Targets)
        {
            var paths = target.Paths();
            var sizeMb = paths.Sum(path => GetDirectorySizeMb(path));
            result.Add(new CleanupTarget
            {
                Name = target.Name,
                Path = string.Join("；", paths),
                SizeMb = Math.Round(sizeMb, 1),
                Note = target.Note,
            });
        }

        return result;
    });

    public Task<(double FreedMb, List<string> Skipped)> CleanAsync(IProgress<string>? progress = null)
        => Task.Run(() => CleanTargets(Targets, progress));

    public Task<(double FreedMb, List<string> Skipped)> CleanShaderCachesAsync(IProgress<string>? progress = null)
        => Task.Run(() => CleanTargets(
            Targets.Where(t => t.Name.Contains("着色器缓存", StringComparison.Ordinal)).ToArray(),
            progress));

    private static (double FreedMb, List<string> Skipped) CleanTargets(Target[] targets, IProgress<string>? progress)
    {
        var freedMb = 0.0;
        var skipped = new List<string>();

        foreach (var target in targets)
        {
            foreach (var path in target.Paths())
            {
                var before = GetDirectorySizeMb(path);
                if (before <= 0)
                {
                    continue;
                }

                progress?.Report($"正在清理：{target.Name}");
                var failed = CleanDirectoryContents(path);
                freedMb += Math.Max(0, before - GetDirectorySizeMb(path));
                if (failed > 0)
                {
                    skipped.Add($"{target.Name}：{failed} 个文件被占用，已跳过");
                }
            }
        }

        return (Math.Round(freedMb, 1), skipped);
    }

    // ---------------- 内部 ----------------

    private static double GetDirectorySizeMb(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return 0;
            }

            var dirInfo = new DirectoryInfo(path);
            return dirInfo
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => { try { return file.Length; } catch { return 0; } }) / 1024.0 / 1024.0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>清空目录内容（保留目录本身），返回删除失败的文件数。</summary>
    private static int CleanDirectoryContents(string path)
    {
        var failed = 0;
        try
        {
            if (!Directory.Exists(path))
            {
                return 0;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    failed++;
                }
            }

            // 尽力删除空子目录
            foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try
                {
                    Directory.Delete(dir, recursive: false);
                }
                catch
                {
                    // 非空或被占用则保留
                }
            }
        }
        catch
        {
            // 目录访问失败：整体跳过
        }

        return failed;
    }
}
