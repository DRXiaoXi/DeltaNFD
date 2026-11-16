using DeltaNFD.Inspection;

namespace DeltaNFD.Services;

/// <summary>Only the maintenance backend can start the destructive step. No caller-provided cache paths.</summary>
public static class ManualAsdStart
{
    public static Task<ManualAsdSession> RunAsync(ManualAsdStartArgs args, CancellationToken token) => Task.Run(() =>
    {
        using var operation = GameTargetService.Default.BeginOperation();
        using var scope = GameInstallationScope.Enter(args.Selection, requireChoice: true);
        if (args.Selection is null || !GameInstallationScope.AllowsSharedCleanup)
            throw new InvalidOperationException("手动 ASD 判定需要选择全部安装目录并确认系统共享缓存清理。");
        return Run(ManualAsdStore.Default, ManualAsdInspection.ReadContext, DeltaForceLocator.IsGameRunning,
            (context, cancellation) => ShaderCacheCleanup.CleanMixedRoots(context.PsoPaths, [context.SystemRoot], DeltaForceLocator.IsGameRunning, cancellation), token, args.ExpectedContextId);
    }, token);

    internal static ManualAsdSession Run(ManualAsdStore store, Func<ManualAsdContext> readContext, Func<bool> gameRunning,
        Func<ManualAsdContext, CancellationToken, ShaderCacheCleanupResult> clean, CancellationToken token = default, string? expectedContextId = null)
    {
        using var gate = ShaderCacheOperationGate.Acquire();
        token.ThrowIfCancellationRequested();
        var context = readContext();
        if (expectedContextId is not null && expectedContextId != ManualAsdStore.ContextId(context))
            throw new InvalidOperationException("驱动、用户或缓存目录与确认时不同，未执行清理。请刷新后重试。");
        if (!context.Supported || context.Selection.Roots.Length == 0 || !context.Selection.AllInstallations
            || !context.Selection.SharedCleanupConfirmed)
            throw new InvalidOperationException("手动 ASD 判定仅适用于已确认的 NVIDIA 621 及以上驱动和全部已识别安装目录。");
        if (gameRunning()) throw new InvalidOperationException("请完全退出游戏后再开始清理与判定。");
        var session = new ManualAsdSession { Id = Guid.NewGuid().ToString("N"), Owner = context.Owner, DriverKey = context.DriverKey,
            Selection = context.Selection, SystemRoot = context.SystemRoot, StartedUtc = DateTimeOffset.UtcNow,
            Stage = ManualAsdStage.Cleaning, Message = "正在清理 PSOCache 和 D3DSCache；尚未允许预热判定。" };
        store.Save(session); // Must be durable before the first irreversible deletion.
        try
        {
            var report = clean(context, token);
            token.ThrowIfCancellationRequested();
            if (report.Partial || report.Cancelled || report.GameStarted || report.FailedItems > 0 || report.SkippedLinks > 0
                || !report.VerificationComplete || report.RemainingFiles != 0)
                throw new IOException("缓存清理未完整完成，未进入预热判定。" + report.Message + " " + string.Join("；", report.Skipped.Take(8)));
            if (gameRunning() || !ManualAsdInspection.Matches(session, readContext()))
                throw new IOException("清理期间游戏已启动或驱动/目录发生变化，请退出游戏后重新开始。");
            // Independent empty-baseline read is required even if a cleanup adapter reports success.
            if (HasAnyFile(context.PsoPaths.Concat([context.SystemRoot])))
                throw new IOException("清理后仍有缓存文件或已重新生成，未进入预热判定，请关闭其他图形应用后重试。");
            session = session with { Stage = ManualAsdStage.Warmup, CleanedUtc = DateTimeOffset.UtcNow,
                Message = "第 2 步：缓存已清理。请进入三角洲完成着色器预热，然后返回点击“预热完成”。" };
            store.Save(session); return session;
        }
        catch (Exception ex)
        {
            store.Save(session with { Stage = ManualAsdStage.Interrupted, Message = "本次判定未开始：" + ex.Message });
            throw;
        }
    }
    private static bool HasAnyFile(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            DriverPriorityStore.RejectLinks(root);
            if (!Directory.Exists(root)) continue;
            var pending = new Stack<string>(); pending.Push(root); var count = 0;
            while (pending.TryPop(out var folder))
            {
                if (++count > 4096) throw new IOException("缓存空目录核对达到上限。");
                DriverPriorityStore.RejectLinks(folder);
                foreach (var item in Directory.EnumerateFileSystemEntries(folder))
                {
                    DriverPriorityStore.RejectLinks(item);
                    if ((File.GetAttributes(item) & FileAttributes.Directory) != 0) pending.Push(item); else return true;
                }
            }
        }
        return false;
    }
}
