namespace DeltaNFD.Services;

internal sealed record ShaderCacheCleanupOptions(
    bool ClearReadOnlyFiles = false, bool RemoveEmptyDirectories = false, int SharingViolationRetries = 0)
{
    internal static readonly ShaderCacheCleanupOptions PsoCache = new(true, true, 2);
}

/// <summary>
/// 固定缓存根的安全清理实现：保留根目录；PSOCache 可清理只读文件和空子目录；
/// 根目录/祖先验证在所有删除之前完成，避免通过 junction 把删除带到缓存范围之外。
/// </summary>
internal static class ShaderCacheCleanup
{
    internal static ShaderCacheCleanupResult CleanRoots(
        IEnumerable<string> roots,
        Func<bool>? gameRunning,
        CancellationToken token = default,
        IProgress<string>? progress = null,
        ShaderCacheCleanupOptions? options = null)
    {
        options ??= new();
        const int maxSkippedDetails = 200;
        var skipped = new List<string>();
        var validRoots = new List<string>();
        var failures = 0;
        var skippedLinks = 0;
        var deletedFiles = 0;
        var freedBytes = 0L;
        var gameStarted = false;
        var cancelled = false;
        var remainingFiles = 0;
        var verificationComplete = false;
        var retryWaitMs = 0;
        var failedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedFolders = new List<string>();

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
        void SkipLink(string path)
        {
            if (linkedPaths.Add(path)) { skippedLinks++; Skip($"{path}：链接/重解析项已跳过"); }
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
                            SkipLink(folder);
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
                                SkipLink(entry);
                                continue;
                            }
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                folders.Push(entry);
                                continue;
                            }

                            if (!DeleteFile(entry)) break;
                        }
                        catch (FileNotFoundException) { }
                        catch (DirectoryNotFoundException) { }
                        catch (Exception ex)
                        {
                            failedFiles.Add(entry);
                            Skip($"{entry}：{ex.Message}");
                            failures++;
                        }
                    }

                    visitedFolders.Add(folder);

                    if (gameStarted || cancelled) break;
                }
                if (gameStarted || cancelled) break;
            }

            if (!gameStarted && !cancelled && options.RemoveEmptyDirectories)
            {
                // 只删除已遍历过的空子目录，不递归删除，不删除缓存根。
                foreach (var folder in visitedFolders.AsEnumerable().Reverse())
                {
                    if (validRoots.Contains(folder, StringComparer.OrdinalIgnoreCase)) continue;
                    if (ShouldStop()) break;
                    try
                    {
                        if (TryFindReparseAncestor(folder, out _)) continue;
                        if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder, false);
                    }
                    catch (DirectoryNotFoundException) { }
                    catch (Exception ex) { failures++; Skip($"{folder}：空目录未能删除：{ex.Message}"); }
                }
            }

            if (!gameStarted && !cancelled) VerifyRemaining();
        }

        return Build();

        ShaderCacheCleanupResult Build()
        {
            var partial = failures > 0 || skippedLinks > 0 || gameStarted || cancelled || remainingFiles > 0
                || (!verificationComplete && validRoots.Count > 0);
            var freedMb = Math.Round(freedBytes / 1024.0 / 1024.0, 1);
            var message = $"已删除 {deletedFiles} 个缓存文件，释放 {freedMb:0.0} MB。";
            if (gameStarted) message += " 检测到三角洲已启动，已停止剩余清理。";
            else if (cancelled) message += " 清理已取消。";
            else if (failures > 0) message += $" {failures} 项未完成。";
            else if (skippedLinks > 0) message += $" {skippedLinks} 个链接/重解析项已跳过。";
            message += verificationComplete
                ? $" 已核对剩余 {remainingFiles} 个文件。"
                : " 残留核对未完成，请按未完成原因处理后重试。";
            return new ShaderCacheCleanupResult
            {
                FreedMb = freedMb,
                DeletedFiles = deletedFiles,
                FailedItems = failures,
                SkippedLinks = skippedLinks,
                RemainingFiles = remainingFiles,
                VerificationComplete = verificationComplete,
                GameStarted = gameStarted,
                Cancelled = cancelled,
                Partial = partial,
                Message = message,
                Skipped = skipped,
            };
        }

        bool DeleteFile(string path)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    // 每次重试都复核祖先和文件类型，不能沿重解析路径改属性或删除。
                    if (TryFindReparseAncestor(path, out _))
                        throw new IOException("删除前路径变成链接/重解析项，已拒绝");
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        throw new IOException("删除前文件类型改变，已拒绝");
                    long length = 0;
                    try { length = new FileInfo(path).Length; } catch { }
                    var readOnlyChanged = false;
                    try
                    {
                        if (options.ClearReadOnlyFiles && (attributes & FileAttributes.ReadOnly) != 0)
                        {
                            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                            readOnlyChanged = true;
                        }
                        File.Delete(path);
                        deletedFiles++;
                        freedBytes += length;
                        return true;
                    }
                    finally
                    {
                        // 文件未删除时恢复只读位，不让失败清理留下属性修改。
                        if (readOnlyChanged && File.Exists(path) && !TryFindReparseAncestor(path, out _))
                        {
                            try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly); }
                            catch (Exception ex) { failures++; Skip($"{path}：恢复只读属性失败：{ex.Message}"); }
                        }
                    }
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33
                    && attempt < Math.Clamp(options.SharingViolationRetries, 0, 2) && retryWaitMs < 2000)
                {
                    // 每文件最多重试两次，全批等待累计最多两秒；取消会立即唤醒。
                    if (ShouldStop()) return false;
                    retryWaitMs += 100;
                    if (token.WaitHandle.WaitOne(100)) { cancelled = true; return false; }
                    if (GameStopped()) return false;
                }
            }
        }

        void VerifyRemaining()
        {
            verificationComplete = true;
            foreach (var root in validRoots)
            {
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.TryPop(out var folder))
                {
                    if (ShouldStop()) { verificationComplete = false; return; }
                    try
                    {
                        if (TryFindReparseAncestor(folder, out _))
                        {
                            verificationComplete = false;
                            SkipLink(folder);
                            continue;
                        }
                        foreach (var entry in Directory.GetFileSystemEntries(folder))
                        {
                            if (ShouldStop()) { verificationComplete = false; return; }
                            try
                            {
                                var attributes = File.GetAttributes(entry);
                                if ((attributes & FileAttributes.ReparsePoint) != 0)
                                {
                                    verificationComplete = false;
                                    SkipLink(entry);
                                    continue;
                                }
                                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                                remainingFiles++;
                                if (failedFiles.Add(entry))
                                {
                                    failures++;
                                    Skip($"{entry}：清理后仍存在或重新生成，请重试");
                                }
                            }
                            catch (FileNotFoundException) { }
                            catch (DirectoryNotFoundException) { }
                            catch (Exception ex)
                            {
                                verificationComplete = false;
                                failures++;
                                Skip($"{entry}：无法核对残留：{ex.Message}");
                            }
                        }
                    }
                    catch (DirectoryNotFoundException) { }
                    catch (FileNotFoundException) { }
                    catch (Exception ex)
                    {
                        verificationComplete = false;
                        failures++;
                        Skip($"{folder}：无法核对残留：{ex.Message}");
                    }
                }
            }
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
