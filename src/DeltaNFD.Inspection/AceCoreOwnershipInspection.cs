using System.Diagnostics;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

/// <summary>Bounded metadata-only inventory of ACE games and shared CORE files; no payload hashing.</summary>
internal static class AceCoreOwnershipInspection
{
    private sealed class Budget
    {
        internal int Entries = 20000;
        internal void Touch()
        {
            if (--Entries < 0) throw new IOException("达到目录读取上限，部分数量待核对。");
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
            // A game discovery gap can only increase the number of games, not the CORE count.
            // Preserve its details without downgrading an already proved CORE <= known-games result.
            var coreCountIncomplete = notes.Count > 0;
            var candidates = (discover ?? AceGameDiscovery.Read)(notes, CheckGame);
            var games = new List<AceGameInstallation>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Stopwatch? manualWatch = null;
            foreach (var candidate in candidates)
            {
                if (CheckGame()) throw new OperationCanceledException();
                if (budget.Entries < 0) break;
                try
                {
                    var directory = Path.GetFullPath(candidate.Root).TrimEnd(Path.DirectorySeparatorChar);
                    if (!visited.Add(directory))
                    {
                        if (candidate.ManuallyAssociated)
                        {
                            var duplicate = games.FindIndex(g => g.InstallDirectory.Equals(directory, StringComparison.OrdinalIgnoreCase));
                            if (duplicate >= 0) games[duplicate].ManuallyAssociated = true;
                        }
                        continue;
                    }
                    if (!AceGameDiscovery.SafeDirectory(directory) || IsContainer(directory)) continue;
                    if (candidate.SearchChildren)
                    {
                        manualWatch ??= Stopwatch.StartNew();
                        if (manualWatch.Elapsed >= TimeSpan.FromSeconds(5))
                        { notes.Add("手动目录检索达到累计时间预算，部分游戏目录未读全。"); continue; }
                    }
                    var paths = candidate.SearchChildren
                        ? AceManualGameInspection.FindAceDirectories(directory, CheckGame, budget.Touch, notes,
                            () => manualWatch!.Elapsed >= TimeSpan.FromSeconds(5))
                        : AceGameDiscovery.FindAceDirectories(directory, CheckGame, budget.Touch, notes);
                    if (paths.Count == 0)
                    {
                        if (candidate.SearchChildren) notes.Add($"手动目录未发现可识别的 ACE 组件；可能未安装或超出检索范围，未据此判定异常：{directory}");
                        continue;
                    }
                    // Registry/launcher records can point at different levels of the same installation.
                    var existing = games.FindIndex(g => g.AceDirectories.Intersect(paths, StringComparer.OrdinalIgnoreCase).Any());
                    if (existing >= 0)
                    {
                        if (candidate.ManuallyAssociated) games[existing].ManuallyAssociated = true;
                        foreach (var path in paths.Except(games[existing].AceDirectories, StringComparer.OrdinalIgnoreCase))
                        {
                            games[existing].AceDirectories.Add(path);
                        }
                        continue;
                    }
                    games.Add(new() { Name = candidate.Name, InstallDirectory = directory, Source = candidate.Source,
                        AceDirectories = paths, ManuallyAssociated = candidate.ManuallyAssociated });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { notes.Add($"安装位置读取不完整（{candidate.Name}）：{ex.Message}"); }
            }
            if (blocked()) return Blocked(); // Do not publish an inventory after the game has started.
            var manualCount = games.Count(g => g.ManuallyAssociated);
            var excess = shared.Count > games.Count;
            var level = AceCoreCountPolicy.Evaluate(shared.Count, games.Count, manualCount, coreCountIncomplete);
            var countSummary = $"检测到 {games.Count} 个包含 ACE 组件的游戏安装目录、{shared.Count} 个共享 ACE-CORE 驱动文件；已手动关联 {manualCount} 个游戏。";
            var detail = level == AceCoreCheckLevel.Abnormal
                ? $"手动关联后 CORE 数量仍偏多（{shared.Count} > {games.Count}），按本工具计数规则判定异常。"
                : excess ? $"CORE 数量偏多（{shared.Count} > {games.Count}），尚未完成有效手动关联，请先添加/关联 ACE 游戏。"
                : level == AceCoreCheckLevel.NeedsReview ? $"当前已读 CORE 数量未超出已识别游戏数（{shared.Count} ≤ {games.Count}）；共享 CORE 目录尚未读全，驱动总数待核对。"
                : shared.Count > 0 ? "CORE 数量未超过已识别的 ACE 游戏数量，数量核对正常。" : "未发现共享 ACE-CORE 文件，不代表游戏内没有 ACE。";
            if (level == AceCoreCheckLevel.Abnormal && notes.Count > 0)
                detail += "部分目录或安装信息仍待核对，请查看明细。";
            return new()
            {
                Level = level, SysCount = shared.Count,
                Files = shared.Select(path => "· " + Path.GetRelativePath(root, path)).ToList(),
                Games = games, Notes = notes.Distinct().Take(32).ToList(),
                ManuallyAssociatedGameCount = manualCount,
                Summary = countSummary + detail,
                Advice = excess ? "请确认所有含 ACE 的游戏均已添加/关联，并重新检测。计数提醒不证明多余文件已失效或可删除，目录读取缺口请查看明细。"
                    : coreCountIncomplete ? "共享 CORE 目录未读全，不能确认驱动总数，请查看读取明细后重试。检测不会自动删除组件。"
                    : "按已读完整的共享 CORE 文件数与已确认的 ACE 游戏数量核对，不要求驱动内容匹配；其他游戏检索提示保留在明细，不影响本次数量结果。检测不会自动删除组件。",
            };
        }
        catch (OperationCanceledException) { return Blocked(); }
        catch (Exception ex)
        {
            return new() { Level = AceCoreCheckLevel.Failed, SysCount = 0, Files = [],
                Summary = "ACE 游戏与 CORE 数量检测未完成。", Advice = "请稍后重试，读取失败不代表组件异常。", Notes = [ex.Message] };
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

    private static List<string> ReadCoreFiles(string root, Budget budget, List<string> notes, Func<bool> blocked)
    {
        var result = new List<string>();
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
                        if (!AceGameDiscovery.SafeDirectory(item.Path)) throw new IOException("读取期间目录路径发生变化，数量待核对。");
                        // Enumerating metadata is sufficient for counting; do not open SYS payloads.
                        result.Add(path);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { notes.Add($"组件目录读取不完整：{item.Path}（{ex.Message}）"); }
        }
        return result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static AceCoreCheckResult Blocked() => new()
    {
        Level = AceCoreCheckLevel.Blocked, SysCount = 0, Files = [],
        Summary = "三角洲正在运行，已暂停 ACE 游戏与 CORE 数量检测。", Advice = "退出游戏后重新检测。",
    };
}
