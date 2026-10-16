namespace DeltaNFD.Services;

/// <summary>Hidden recovery page cleanup: fixed cache roots, no driver settings/database or linked paths.</summary>
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
        var deleted = 0; var failed = new List<string>();
        try
        {
            token.ThrowIfCancellationRequested();
            if (gameRunning()) return OperationResult.Fail("三角洲正在运行，请完全退出后再清理。");
            var targets = roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var root in targets) VerifyPath(root); // Validate all roots before the first deletion.
            foreach (var root in targets)
            {
                if (!Directory.Exists(root)) continue;
                var folders = new Stack<string>(); folders.Push(root);
                while (folders.TryPop(out var folder))
                {
                    token.ThrowIfCancellationRequested();
                    if (gameRunning()) throw new IOException("检测到三角洲启动，已停止剩余清理。");
                    VerifyPath(folder);
                    foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                    {
                        token.ThrowIfCancellationRequested();
                        if (gameRunning()) throw new IOException("检测到三角洲启动，已停止剩余清理。");
                        try
                        {
                            var attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("链接或重解析项已跳过");
                            if ((attributes & FileAttributes.Directory) != 0) { folders.Push(entry); continue; }
                            VerifyPath(entry);
                            File.Delete(entry); deleted++;
                        }
                        catch (Exception ex) { failed.Add(entry + "：" + ex.Message); }
                    }
                }
            }
        }
        catch (Exception ex) { failed.Add(ex.Message); }
        return new OperationResult { Success = failed.Count == 0, IsPartial = deleted > 0 && failed.Count > 0,
            Message = $"已删除 {deleted} 个缓存文件；目录保留。"
                + (failed.Count > 0 ? $"\n有 {failed.Count} 项未完成：\n" + string.Join("\n", failed.Take(12)) : "\n所选缓存清理完成或原本为空。")
                + "\n请恢复驱动默认，再进入游戏重新预热着色器；缓存删除无法撤销。" };
    }

    private static void VerifyPath(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("缓存路径包含链接/重解析目录，未清理：" + current);
    }
}
