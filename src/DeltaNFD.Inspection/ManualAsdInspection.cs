using System.Diagnostics;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

/// <summary>The directory is scanned only after the player's explicit warmup-complete action.</summary>
public static class ManualAsdInspection
{
    public static ManualAsdContext ReadContext()
    {
        var target = GameTargetService.Default;
        var driver = ShaderInspection.ReadCurrentDriver();
        var inventory = GameInstallationScope.ReadInventory();
        var selection = GameInstallationScope.CurrentSelection ?? GameInstallationScope.Select(inventory, null, true, true);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache");
        var key = ManualAsdStore.Hash($"{driver.State}|{driver.Version}|{driver.Raw}|{driver.Adapter}|{driver.PnpDeviceId}");
        return new(!target.IsCustom && inventory.Length > 0 && ManualAsdPolicy.Supported(driver.State, driver.Version), key,
            ManualAsdStore.Hash(Environment.MachineName + "|" + Environment.UserDomainName + "|" + Environment.UserName),
            selection, DeltaForceLocator.FindPsoCachePaths().ToArray(), root);
    }

    public static bool Matches(ManualAsdSession session, ManualAsdContext current) => current.Supported
        && session.Owner == current.Owner && session.DriverKey == current.DriverKey
        && session.Selection.InventoryId == current.Selection.InventoryId
        && session.Selection.AllInstallations && session.Selection.SharedCleanupConfirmed
        && GameInstallationScope.InventoryId(session.Selection.Roots) == current.Selection.InventoryId
        && Path.GetFullPath(session.SystemRoot).Equals(Path.GetFullPath(current.SystemRoot), StringComparison.OrdinalIgnoreCase);

    public static ManualAsdSession? Read(ManualAsdStore store, Func<ManualAsdContext> context)
    {
        var session = store.Read();
        if (session is null) return null;
        if (!Matches(session, context())) return session with { Stage = ManualAsdStage.Interrupted,
            Message = "驱动、游戏模式或安装目录已变化，请重新开始手动 ASD 判定。" };
        return session;
    }

    public static ManualAsdSession Finish(ManualAsdStore store, string id, Func<ManualAsdContext> context,
        CancellationToken token = default)
    {
        using var gate = ShaderCacheOperationGate.Acquire();
        var session = store.Read() ?? throw new InvalidOperationException("请先开始手动 ASD 判定并完成缓存清理。");
        if (session.Id != id || session.Stage is not (ManualAsdStage.Warmup or ManualAsdStage.NotDetected)
            || session.CleanedUtc is null || !Matches(session, context()))
            throw new InvalidOperationException("判定进度已失效或清理未完成，请重新开始。");
        token.ThrowIfCancellationRequested();
        var match = FindLargeNvph(session.SystemRoot, token);
        token.ThrowIfCancellationRequested();
        if (!Matches(session, context()) || store.Read()?.Id != session.Id)
            throw new InvalidOperationException("判定期间环境或进度已变化，未保存结论。");
        var result = session with
        {
            Stage = match.Bytes > ManualAsdPolicy.ThresholdBytes ? ManualAsdStage.Activated : ManualAsdStage.NotDetected,
            CheckedUtc = DateTimeOffset.UtcNow, MatchedFile = match.Path, MatchedBytes = match.Bytes,
            Message = match.Bytes > ManualAsdPolicy.ThresholdBytes
                ? $"ASD已激活：本次清理后 D3DSCache 出现大于 100MB 的 NVPH（{match.Bytes / 1024.0 / 1024.0:0.00} MB）。"
                : "本次未检测到大于 100MB 的 NVPH，尚未确认 ASD 激活。可继续预热后再次点击“预热完成”。",
        };
        store.Save(result); return result;
    }

    public static ManualAsdSession Cancel(ManualAsdStore store, string id)
    {
        using var gate = ShaderCacheOperationGate.Acquire();
        var session = store.Read() ?? throw new InvalidOperationException("没有正在进行的 ASD 判定。");
        if (session.Id != id || session.Stage == ManualAsdStage.Cleaning) throw new InvalidOperationException("判定进度已变化，请刷新后重试。");
        var cancelled = session with { Stage = ManualAsdStage.Cancelled, Message = "手动 ASD 判定已取消；已清理的缓存不会恢复。" };
        store.Save(cancelled); return cancelled;
    }

    public static (string Path, long Bytes) FindLargeNvph(string root, CancellationToken token = default)
    {
        DriverPriorityStore.RejectLinks(root);
        try { if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) throw new IOException("D3DSCache 不是目录。"); }
        catch (DirectoryNotFoundException) { return ("", 0); }
        catch (FileNotFoundException) { return ("", 0); }
        var folders = new Stack<string>(); folders.Push(root);
        var clock = Stopwatch.StartNew(); var dirs = 0; var files = 0; var errors = new List<string>();
        while (folders.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            if (++dirs > 4096 || clock.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("D3DSCache 检测达到上限，请关闭其他图形应用后重试。");
            try
            {
                DriverPriorityStore.RejectLinks(folder);
                foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                {
                    token.ThrowIfCancellationRequested();
                    if (++files > 50000 || clock.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("D3DSCache 文件过多，本次检测未完成。");
                    try
                    {
                        DriverPriorityStore.RejectLinks(path);
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.Directory) != 0) { folders.Push(path); continue; }
                        if (!Path.GetExtension(path).Equals(".nvph", StringComparison.OrdinalIgnoreCase)) continue;
                        // Only metadata is needed. The game may retain an exclusive content handle after warmup.
                        var length = new FileInfo(path).Length;
                        if (length > ManualAsdPolicy.ThresholdBytes) return (Path.GetRelativePath(root, path), length);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { if (errors.Count < 8) errors.Add(Path.GetFileName(path) + "：" + ex.Message); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { if (errors.Count < 8) errors.Add(Path.GetFileName(folder) + "：" + ex.Message); }
        }
        if (errors.Count > 0) throw new IOException("系统缓存未能完整读取，未作未激活判定：" + string.Join("；", errors));
        return ("", 0);
    }
}
