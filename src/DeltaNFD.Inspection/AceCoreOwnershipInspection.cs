using System.Diagnostics;
using System.Security.Cryptography;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

/// <summary>Bounded, read-only inventory. A filename suffix or a file count is never ownership evidence.</summary>
internal static class AceCoreOwnershipInspection
{
    private sealed record CoreFile(string Path, long Size, string? Hash);
    private sealed class Budget
    {
        internal int Entries = 20000;
        internal long HashBytes = 128L * 1024 * 1024;
        internal void Touch()
        {
            if (--Entries < 0) throw new IOException("达到目录读取上限，部分归属待核对。");
        }
    }

    internal static AceCoreCheckResult Read(string root, Func<bool> blocked,
        Func<List<string>, Func<bool>, IReadOnlyList<AceGameCandidate>>? discover = null)
    {
        if (blocked()) return Blocked();
        var notes = new List<string>();
        var budget = new Budget();
        var watch = Stopwatch.StartNew();
        long lastCheck = 0;
        bool CheckGame()
        {
            if (watch.ElapsedMilliseconds - lastCheck < 100) return false;
            lastCheck = watch.ElapsedMilliseconds;
            return blocked();
        }
        try
        {
            var shared = ReadCoreFiles(root, budget, notes, CheckGame);
            var candidates = (discover ?? AceGameDiscovery.Read)(notes, CheckGame);
            var games = new List<AceGameInstallation>();
            var gameFiles = new List<List<CoreFile>>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (CheckGame()) throw new OperationCanceledException();
                if (budget.Entries < 0) break;
                try
                {
                    var directory = Path.GetFullPath(candidate.Root).TrimEnd(Path.DirectorySeparatorChar);
                    if (!visited.Add(directory) || !AceGameDiscovery.SafeDirectory(directory) || IsContainer(directory)) continue;
                    var paths = AceGameDiscovery.FindAceDirectories(directory, CheckGame, budget.Touch, notes);
                    if (paths.Count == 0) continue;
                    // Registry/launcher records can point at different levels of the same installation.
                    var existing = games.FindIndex(g => g.AceDirectories.Intersect(paths, StringComparer.OrdinalIgnoreCase).Any());
                    if (existing >= 0)
                    {
                        foreach (var path in paths.Except(games[existing].AceDirectories, StringComparer.OrdinalIgnoreCase))
                        {
                            games[existing].AceDirectories.Add(path);
                            gameFiles[existing].AddRange(ReadCoreFiles(path, budget, notes, CheckGame));
                        }
                        continue;
                    }
                    games.Add(new() { Name = candidate.Name, InstallDirectory = directory, Source = candidate.Source, AceDirectories = paths });
                    gameFiles.Add(paths.SelectMany(p => ReadCoreFiles(p, budget, notes, CheckGame)).ToList());
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { notes.Add($"安装位置读取不完整（{candidate.Name}）：{ex.Message}"); }
            }
            if (blocked()) return Blocked(); // Do not publish an inventory after the game has started.
            var associations = shared.Select(file => new AceCoreAssociation
            {
                Path = file.Path,
                Games = file.Hash is null ? [] : games.Where((_, i) => gameFiles[i].Any(f => f.Hash == file.Hash))
                    .Select(g => $"{g.Name}（{g.InstallDirectory}）").ToList(),
                Evidence = file.Hash is null ? "文件未能完整读取，归属待核对。" : "",
            }).Select(a => new AceCoreAssociation
            {
                Path = a.Path, Games = a.Games,
                Evidence = a.Games.Count > 0 ? "与游戏 ACE 目录内驱动副本的 SHA256 完全一致。" +
                    (a.Games.Count > 1 ? "多款游戏使用相同组件，不能确认独占归属。" : "") :
                    a.Evidence.Length > 0 ? a.Evidence : "未找到可匹配的游戏内驱动副本；不代表冗余或异常。",
            }).ToList();
            var unknown = associations.Count(a => a.Games.Count == 0);
            var level = notes.Count > 0 || unknown > 0 ? AceCoreCheckLevel.NeedsReview :
                shared.Count == 0 ? AceCoreCheckLevel.None : AceCoreCheckLevel.Normal;
            return new()
            {
                Level = level, SysCount = shared.Count,
                Files = shared.Select(f => $"· {Path.GetRelativePath(root, f.Path)}（{(f.Size < 0 ? "大小待核对" : $"{f.Size / 1048576.0:0.0} MB")}）").ToList(),
                Games = games, Associations = associations, Notes = notes.Distinct().Take(32).ToList(),
                Summary = $"检测到 {games.Count} 个包含 ACE 组件的游戏安装目录、{shared.Count} 个共享 ACE-CORE 驱动文件。" +
                    (level == AceCoreCheckLevel.NeedsReview ? $"{unknown} 个文件归属待核对或部分信息未读全；未据此判定异常。" :
                        shared.Count > 0 ? "驱动副本已匹配游戏，多款 ACE 游戏可正常共存。" : "未发现共享 ACE-CORE 文件，不代表游戏内没有 ACE。"),
                Advice = "ACE-CORE 文件数量不是异常依据。仅在游戏实际报错时核对对应游戏或使用其启动器修复；不要因多文件而清理其他游戏的组件。",
            };
        }
        catch (OperationCanceledException) { return Blocked(); }
        catch (Exception ex)
        {
            return new() { Level = AceCoreCheckLevel.Failed, SysCount = 0, Files = [],
                Summary = "ACE 游戏与驱动归属检测未完成。", Advice = "请稍后重试，读取失败不代表组件异常。", Notes = [ex.Message] };
        }
    }

    private static bool IsContainer(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        return Path.GetPathRoot(path)?.TrimEnd('\\') == path.TrimEnd('\\') ||
            path.Equals(windows, StringComparison.OrdinalIgnoreCase) || path.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase) ||
            new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                .Any(folder => string.Equals(path, Environment.GetFolderPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    private static List<CoreFile> ReadCoreFiles(string root, Budget budget, List<string> notes, Func<bool> blocked)
    {
        var result = new List<CoreFile>();
        try { _ = File.GetAttributes(root); }
        catch (FileNotFoundException) { return result; }
        catch (DirectoryNotFoundException) { return result; }
        catch (Exception ex) { notes.Add($"组件目录无法读取：{root}（{ex.Message}）"); return result; }
        if (!AceGameDiscovery.SafeDirectory(root)) { notes.Add($"跳过链接或不支持的组件目录：{root}"); return result; }
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0 && budget.Entries >= 0)
        {
            if (blocked()) throw new OperationCanceledException();
            var item = pending.Pop();
            try
            {
                if (!AceGameDiscovery.SafeDirectory(item.Path)) { notes.Add($"跳过链接目录：{item.Path}"); continue; }
                foreach (var path in Directory.EnumerateFileSystemEntries(item.Path))
                {
                    budget.Touch();
                    if (blocked()) throw new OperationCanceledException();
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { notes.Add($"跳过链接：{path}"); continue; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (item.Depth >= 16) notes.Add($"目录层级超过读取上限：{path}");
                        else pending.Push((path, item.Depth + 1));
                    }
                    else if (Path.GetExtension(path).Equals(".sys", StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(path).StartsWith("ACE-CORE", StringComparison.OrdinalIgnoreCase))
                    {
                        long size = -1;
                        string? hash = null;
                        try
                        {
                            var info = new FileInfo(path);
                            size = info.Length;
                            var modified = info.LastWriteTimeUtc;
                            if (size <= 0 || size > 16L * 1024 * 1024 || size > budget.HashBytes) throw new IOException("文件为空或超过驱动读取预算。");
                            budget.HashBytes -= size;
                            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            var buffer = new byte[65536];
                            long readTotal = 0;
                            int read;
                            while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                if (blocked()) throw new OperationCanceledException();
                                if ((readTotal += read) > size) throw new IOException("读取期间文件发生变化。");
                                digest.AppendData(buffer, 0, read);
                            }
                            info.Refresh();
                            if (readTotal != size || info.Length != size || info.LastWriteTimeUtc != modified ||
                                (info.Attributes & FileAttributes.ReparsePoint) != 0 || !AceGameDiscovery.SafeDirectory(item.Path))
                                throw new IOException("读取期间文件或路径发生变化。");
                            hash = Convert.ToHexString(digest.GetHashAndReset());
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { notes.Add($"驱动副本待核对：{path}（{ex.Message}）"); }
                        result.Add(new(path, size, hash));
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { notes.Add($"组件目录读取不完整：{item.Path}（{ex.Message}）"); }
        }
        return result.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static AceCoreCheckResult Blocked() => new()
    {
        Level = AceCoreCheckLevel.Blocked, SysCount = 0, Files = [],
        Summary = "三角洲正在运行，已暂停 ACE 组件与游戏归属检测。", Advice = "退出游戏后重新检测。",
    };
}
