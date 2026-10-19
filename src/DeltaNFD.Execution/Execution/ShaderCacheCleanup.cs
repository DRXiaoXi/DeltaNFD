namespace DeltaNFD.Services;

/// <summary>
/// 固定缓存根的安全清理实现：只清文件、保留目录；跳过链接/重解析项和被占用文件；
/// 根目录/祖先验证在所有删除之前完成，避免通过 junction 把删除带到缓存范围之外。
/// </summary>
internal static class ShaderCacheCleanup
{
    internal static ShaderCacheCleanupResult CleanRoots(
        IEnumerable<string> roots,
        Func<bool>? gameRunning,
        CancellationToken token = default,
        IProgress<string>? progress = null)
    {
        const int maxSkippedDetails = 200;
        var skipped = new List<string>();
        var validRoots = new List<string>();
        var failures = 0;
        var skippedLinks = 0;
        var deletedFiles = 0;
        var freedBytes = 0L;
        var gameStarted = false;
        var cancelled = false;

        bool TokenStopped()
        {
            if (!token.IsCancellationRequested) return false;
            cancelled = true;
            return true;
        }

        bool GameStopped()
        {
            if (gameRunning?.Invoke() != true) return false;
            gameStarted = true;
            return true;
        }

        bool ShouldStop() => TokenStopped() || GameStopped();
        void Skip(string detail)
        {
            if (skipped.Count < maxSkippedDetails) skipped.Add(detail);
        }

        if (ShouldStop()) return Build();
        if (roots is null) return Build();

        // 先验证所有根目录及祖先，再执行第一次删除：任一根本身是链接/不可读/是文件时整体不清理。
        foreach (var raw in roots)
        {
            // 这里只检查取消；游戏启动检查在真正开始遍历/删除前才做，
            // 避免把所有根目录验证都算成“停止”而少删第一个文件。
            if (TokenStopped()) break;
            if (string.IsNullOrWhiteSpace(raw))
            {
                Skip("空缓存路径已忽略");
                continue;
            }

            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            }
            catch (Exception ex)
            {
                Skip($"{raw}：路径无效：{ex.Message}");
                failures++;
                continue;
            }

            if (!Path.IsPathFullyQualified(full))
            {
                Skip($"{full}：不是绝对路径，已拒绝");
                failures++;
                continue;
            }

            if (IsDriveRoot(full))
            {
                Skip($"{full}：拒绝清理磁盘根目录");
                failures++;
                continue;
            }

            if (validRoots.Contains(full, StringComparer.OrdinalIgnoreCase)) continue;

            if (TryFindReparseAncestor(full, out var linkedAncestor))
            {
                Skip($"{full}：路径包含链接/重解析目录（{linkedAncestor}），未清理");
                failures++;
                continue;
            }

            try
            {
                var attributes = File.GetAttributes(full);
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    Skip($"{full}：不是目录，未清理");
                    failures++;
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Skip($"{full}：缓存根是链接/重解析项，未清理");
                    failures++;
                    continue;
                }
                validRoots.Add(full);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex)
            {
                Skip($"{full}：无法读取目录属性：{ex.Message}");
                failures++;
            }
        }

        if (failures == 0 && !gameStarted && !cancelled)
        {
            foreach (var root in validRoots)
            {
                progress?.Report($"正在清理：{root}");
                var folders = new Stack<string>();
                folders.Push(root);
                while (folders.TryPop(out var folder))
                {
                    if (ShouldStop()) break;

                    // 遍历期间目录可能被替换成链接；进入前再核对一次。
                    try
                    {
                        var attributes = File.GetAttributes(folder);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            skippedLinks++;
                            Skip($"{folder}：链接/重解析目录已跳过");
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) == 0)
                        {
                            Skip($"{folder}：不再是目录，已跳过");
                            failures++;
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Skip($"{folder}：无法进入目录：{ex.Message}");
                        failures++;
                        continue;
                    }

                    string[] entries;
                    try
                    {
                        entries = Directory.GetFileSystemEntries(folder);
                    }
                    catch (Exception ex)
                    {
                        Skip($"{folder}：无法枚举目录：{ex.Message}");
                        failures++;
                        continue;
                    }

                    foreach (var entry in entries)
                    {
                        if (ShouldStop()) break;
                        try
                        {
                            var attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                skippedLinks++;
                                Skip($"{entry}：链接/重解析项已跳过");
                                continue;
                            }
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                folders.Push(entry);
                                continue;
                            }

                            long length = 0;
                            try { length = new FileInfo(entry).Length; } catch { }
                            File.Delete(entry);
                            deletedFiles++;
                            freedBytes += length;
                        }
                        catch (FileNotFoundException) { }
                        catch (DirectoryNotFoundException) { }
                        catch (Exception ex)
                        {
                            Skip($"{entry}：{ex.Message}");
                            failures++;
                        }
                    }

                    if (gameStarted || cancelled) break;
                }
                if (gameStarted || cancelled) break;
            }
        }

        return Build();

        ShaderCacheCleanupResult Build()
        {
            var partial = failures > 0 || skippedLinks > 0 || gameStarted || cancelled;
            var freedMb = Math.Round(freedBytes / 1024.0 / 1024.0, 1);
            var message = $"已删除 {deletedFiles} 个缓存文件，释放 {freedMb:0.0} MB。";
            if (gameStarted) message += " 检测到三角洲已启动，已停止剩余清理。";
            else if (cancelled) message += " 清理已取消。";
            else if (failures > 0) message += $" {failures} 项未完成。";
            else if (skippedLinks > 0) message += $" {skippedLinks} 个链接/重解析项已跳过。";
            return new ShaderCacheCleanupResult
            {
                FreedMb = freedMb,
                DeletedFiles = deletedFiles,
                FailedItems = failures,
                SkippedLinks = skippedLinks,
                GameStarted = gameStarted,
                Cancelled = cancelled,
                Partial = partial,
                Message = message,
                Skipped = skipped,
            };
        }
    }

    private static bool IsDriveRoot(string full)
    {
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return false;
        return full.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
            || full.Equals(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryFindReparseAncestor(string path, out string linkedPath)
    {
        linkedPath = "";
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    linkedPath = current;
                    return true;
                }
            }
            catch
            {
                // 无法读取属性的祖先交给对应根/目录的枚举错误如实报告。
            }

            var root = Path.GetPathRoot(current);
            if (!string.IsNullOrEmpty(root) && current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return false;
    }
}
