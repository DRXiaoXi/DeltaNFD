namespace DeltaNFD.Services;

/// <summary>
/// 隐藏恢复页的缓存清理：只使用固定缓存根；链接/重解析项会被跳过并报告；
/// 开始前校验所有根，清理过程中持续检查三角洲是否启动。
/// </summary>
public static class ShaderRecoveryCleanup
{
    public static Task<OperationResult> RunAsync(bool gameCache, bool nvidiaCache, CancellationToken token = default) => Task.Run(() =>
    {
        if (!gameCache && !nvidiaCache) return OperationResult.Fail("请至少选择一种缓存。");
        if (gameCache && GameTargetService.Default.IsCustom) return OperationResult.Fail(GameTargetService.DeltaOnlyMessage);
        using var shaderGate = ShaderCacheOperationGate.Acquire();
        var roots = new List<string>();
        if (gameCache) roots.AddRange(DeltaForceLocator.FindPsoCachePaths());
        var driver = gameCache && !nvidiaCache ? ShaderService.ReadNvidiaDriver() : null;
        var system = SystemRoots(gameCache, nvidiaCache, driver,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        return CleanRoots(roots, DeltaForceLocator.IsGameRunning, token, gameCache, system);
    });

    internal static IReadOnlyList<string> SystemRoots(bool gameCache, bool nvidiaCache, ShaderDriverReading? driver, string local)
    {
        var linked = gameCache && driver is not null && ShaderCacheCleanupPolicy.IncludesSystemD3DCache(driver.State, driver.Version);
        if (!nvidiaCache && !linked) return [];
        if (!Path.IsPathFullyQualified(local)) throw new IOException("当前用户系统缓存目录无法确认，未执行清理。");
        var roots = new List<string>();
        if (nvidiaCache) roots.Add(Path.Combine(local, "NVIDIA", "DXCache"));
        roots.Add(Path.Combine(local, "D3DSCache"));
        return roots;
    }

    internal static OperationResult CleanRoots(IEnumerable<string> roots, Func<bool> gameRunning, CancellationToken token = default,
        bool gameCache = false, IEnumerable<string>? systemRoots = null)
    {
        var system = systemRoots?.ToArray();
        var report = system is null ? ShaderCacheCleanup.CleanRoots(roots, gameRunning, token,
            options: gameCache ? ShaderCacheCleanupOptions.PsoCache : null)
            : ShaderCacheCleanup.CleanMixedRoots(roots, system, gameRunning, token);

        var details = report.Skipped.Take(12).ToList();
        var success = !report.Partial;
        var partial = !success && report.DeletedFiles > 0;
        var message = report.Message;
        if (system is { Length: > 0 }) message = "本次包含当前用户系统/驱动缓存：" + string.Join("；", system) + "。\n" + message;
        if (details.Count > 0)
        {
            message += $"\n有 {details.Count} 项未完成/已跳过：\n" + string.Join("\n", details);
        }
        else if (success)
        {
            message += "\n所选缓存清理完成或原本为空。";
        }
        message += "\n请恢复驱动默认，再进入游戏重新预热着色器；缓存删除无法撤销。";

        return new OperationResult
        {
            Success = success,
            IsPartial = partial,
            Message = message,
        };
    }
}
