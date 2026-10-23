namespace DeltaNFD.Services;

/// <summary>
/// 系统垃圾清理的真实实现。清单取自 扩展优化库 CleanUp.json 的安全子集：
/// 只清"内容"不动目录本身；被占用的文件自动跳过。
/// 涉及着色器缓存的清理统一走 <see cref="ShaderCacheCleanup"/> 的固定根安全实现。
/// </summary>
public sealed class CleanupService : ICleanupService
{
    private sealed record Target(string Name, Func<string[]> Paths, string Note = "", string? FilePattern = null);

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static readonly Target[] Targets =
    [
        new("用户临时文件", () => [Path.Combine(LocalAppData, "Temp")],
            "个别正在使用的文件会自动跳过"),
        new("Windows 临时文件", () => [Path.Combine(Environment.SystemDirectory, "..", "Temp")], ""),
        new("Prefetch 预读取", () => [Path.Combine(Environment.SystemDirectory, "..", "Prefetch")],
            "已关闭 Prefetch 时此目录不再增长"),
        new("DirectX / D3DSCache 着色器缓存", () => [Path.Combine(LocalAppData, "D3DSCache")],
            "游戏首次启动会重新编译着色器"),
        new("NVIDIA 着色器缓存", () => [Path.Combine(LocalAppData, "NVIDIA", "DXCache"),
            Path.Combine(LocalAppData, "NVIDIA", "GLCache")],
            "其他游戏首次启动会重新编译着色器"),
        new("AMD 着色器缓存", () => [Path.Combine(LocalAppData, "AMD", "DxCache"),
            Path.Combine(LocalAppData, "AMD", "DxcCache"),
            Path.Combine(LocalAppData, "AMD", "GLCache")],
            "其他游戏首次启动会重新编译着色器"),
        new("Intel 着色器缓存", () => [Path.Combine(LocalAppData, "Intel", "ShaderCache")],
            "存在时清理；其他游戏首次启动会重新编译着色器"),
        new("Windows 更新下载缓存", () => [Path.Combine(Environment.SystemDirectory, "..", "SoftwareDistribution", "Download")],
            "清理后 Windows 更新需重新下载，更新服务运行时部分文件跳过"),
        new("缩略图缓存", () => [Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer")],
            "仅清 thumbcache_*.db，资源管理器会自动重建", FilePattern: "thumbcache_*.db"),
        new("错误报告与转储", () => [
            Path.Combine(LocalAppData, "CrashDumps"),
            Path.Combine(Environment.SystemDirectory, "..", "LiveKernelReports"),
            Path.Combine(Environment.SystemDirectory, "..", "Minidump")], ""),
        new("Windows 错误报告队列", () => [Path.Combine(LocalAppData, "Microsoft", "Windows", "WER")], ""),
        new("INetCache 低权限缓存", () => [Path.Combine(LocalAppData, "Microsoft", "Windows", "INetCache")], ""),
        new("Cryptnet URL 缓存", () => [Path.Combine(LocalAppData, "Microsoft", "CryptnetUrlCache")], ""),
        new("CLR / .NET 使用日志", () => [Path.Combine(LocalAppData, "Microsoft", "CLR_v4.0"),
            Path.Combine(LocalAppData, "Microsoft", "CLR_v4.0_32")], ""),
        new("传输优化文件", () => [Path.Combine(Environment.SystemDirectory, "..", "DeliveryOptimization")], ""),
    ];

    private static bool IsShaderTarget(Target target) =>
        target.Name.Contains("着色器缓存", StringComparison.Ordinal);

    public Task<List<CleanupTarget>> ScanAsync() => Task.Run(() =>
    {
        var result = new List<CleanupTarget>();
        foreach (var target in Targets)
        {
            var paths = target.Paths();
            var sizeMb = paths.Sum(path => GetDirectorySizeMb(path, target.FilePattern));
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
        => Task.Run(() =>
        {
            using var shaderGate = ShaderCacheOperationGate.Acquire();
            var normal = CleanTargets(Targets.Where(t => !IsShaderTarget(t)).ToArray(), progress);
            var shader = CleanShaderCachesCore(progress, CancellationToken.None);
            var skipped = normal.Skipped.Concat(shader.Skipped).ToList();
            if (shader.GameStarted)
                skipped.Add("系统着色器缓存：检测到三角洲启动，已停止剩余清理");
            else if (shader.Cancelled)
                skipped.Add("系统着色器缓存：清理已取消");
            else if (shader.FailedItems > 0)
                skipped.Add($"系统着色器缓存：{shader.FailedItems} 项未完成");
            return (Math.Round(normal.FreedMb + shader.FreedMb, 1), skipped);
        });

    public Task<ShaderCacheCleanupResult> CleanShaderCachesAsync(IProgress<string>? progress = null, CancellationToken token = default)
        // 不让 Task.Run 直接吞掉已取消 token：核心会先返回“已取消”的已知结果，后端才能如实落定。
        => Task.Run(() =>
        {
            using var shaderGate = ShaderCacheOperationGate.Acquire();
            return CleanShaderCachesCore(progress, token);
        });

    private static ShaderCacheCleanupResult CleanShaderCachesCore(IProgress<string>? progress, CancellationToken token)
    {
        var roots = Targets.Where(IsShaderTarget).SelectMany(t => t.Paths()).ToArray();
        return ShaderCacheCleanup.CleanRoots(roots, DeltaForceLocator.IsGameRunning, token, progress);
    }

    private static (double FreedMb, List<string> Skipped) CleanTargets(Target[] targets, IProgress<string>? progress)
    {
        var freedMb = 0.0;
        var skipped = new List<string>();

        foreach (var target in targets)
        {
            foreach (var path in target.Paths())
            {
                if (target.FilePattern is null)
                {
                    // 非着色器目录也走固定根安全实现：不跟随链接、逐项报告失败、0 字节文件也会被清理。
                    progress?.Report($"正在清理：{target.Name}");
                    var report = ShaderCacheCleanup.CleanRoots([path], null, CancellationToken.None);
                    freedMb += report.FreedMb;
                    if (report.Skipped.Count > 0) skipped.AddRange(report.Skipped);
                    if (report.FailedItems > 0)
                        skipped.Add($"{target.Name}：{report.FailedItems} 项未完成");
                    continue;
                }

                var before = GetDirectorySizeMb(path, target.FilePattern);
                if (before <= 0) continue;

                progress?.Report($"正在清理：{target.Name}");
                var failed = CleanMatchingFiles(path, target.FilePattern);
                freedMb += Math.Max(0, before - GetDirectorySizeMb(path, target.FilePattern));
                if (failed > 0)
                {
                    skipped.Add($"{target.Name}：{failed} 个文件被占用，已跳过");
                }
            }
        }

        return (Math.Round(freedMb, 1), skipped);
    }

    // ---------------- 内部 ----------------

    /// <summary>pattern 为空时统计整个目录树；否则只统计该目录顶层匹配的文件（与清理范围一致）。</summary>
    private static double GetDirectorySizeMb(string path, string? pattern = null)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return 0;
            }

            var dirInfo = new DirectoryInfo(path);
            return (pattern is null
                    ? dirInfo.EnumerateFiles("*", SearchOption.AllDirectories)
                    : dirInfo.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
                .Sum(file => { try { return file.Length; } catch { return 0; } }) / 1024.0 / 1024.0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>只删除目录顶层匹配 pattern 的文件（不递归、不动子目录），返回删除失败的文件数。</summary>
    private static int CleanMatchingFiles(string path, string pattern)
    {
        var failed = 0;
        try
        {
            if (!Directory.Exists(path))
            {
                return 0;
            }

            foreach (var file in Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly))
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
        }
        catch
        {
            // 目录访问失败：整体跳过
        }

        return failed;
    }
}
