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
        var roots = new List<string>();
        if (gameCache) roots.AddRange(DeltaForceLocator.FindPsoCachePaths());
        if (nvidiaCache)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            roots.Add(Path.Combine(local, "NVIDIA", "DXCache"));
            roots.Add(Path.Combine(local, "D3DSCache"));
        }
        return CleanRoots(roots, DeltaForceLocator.IsGameRunning, token);
    });

    internal static OperationResult CleanRoots(IEnumerable<string> roots, Func<bool> gameRunning, CancellationToken token = default)
    {
        var report = ShaderCacheCleanup.CleanRoots(roots, gameRunning, token);

        var details = report.Skipped.Take(12).ToList();
        var success = report.FailedItems == 0 && report.SkippedLinks == 0
            && !report.GameStarted && !report.Cancelled;
        var partial = !success && report.DeletedFiles > 0;
        var message = report.Message;
        if (details.Count > 0)
        {
            message += $"\n有 {details.Count} 项未完成/已跳过：\n" + string.Join("\n", details);
        }
        else
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
