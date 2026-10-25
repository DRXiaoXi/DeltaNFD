using System.Diagnostics;

namespace DeltaNFD.Inspection;

/// <summary>Bounded queries of explicitly selected game folders. Never grants cleanup ownership.</summary>
internal static class AceManualGameInspection
{
    internal const int MaxRoots = 32;
    internal const string Source = "手动添加";
    private const int MaxDepth = 6;
    private const int MaxDirectories = 2048;
    private static readonly HashSet<string> KnownComponentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACE-BASE.sys", "ACE-BOOT.sys", "ACE-GAME.sys", "ACE-SSC-DRV64.sys", "ACE-BASE.dll",
        "ACE-Service64.exe", "ACE-Service32.exe", "ACE-Setup64.exe", "ACE-Setup32.exe", "ACE-Tray.exe",
    };

    internal static bool TryNormalizePath(string? path, out string normalized, out string reason)
    {
        normalized = "";
        reason = "请选择本地游戏安装目录。";
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || !Path.IsPathFullyQualified(path)
                || path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0) return false;
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var full = normalized;
            if (normalized.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(normalized)!), StringComparison.OrdinalIgnoreCase)
                || new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                    Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                    Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData,
                    Environment.SpecialFolder.ApplicationData }.Select(Environment.GetFolderPath)
                    .Where(p => p.Length > 0).Any(p => full.Equals(Path.TrimEndingDirectorySeparator(p), StringComparison.OrdinalIgnoreCase)))
            {
                reason = "请选择具体游戏的安装目录，不能选择磁盘根目录或系统公共目录。";
                return false;
            }
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if ((windows.Length > 0 && normalized.StartsWith(windows.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                || Path.GetFileName(normalized).Equals("AntiCheatExpert", StringComparison.OrdinalIgnoreCase))
            {
                reason = "请选择游戏安装目录，而非 Windows 或 AntiCheatExpert 组件目录。";
                return false;
            }
            return true;
        }
        catch (Exception ex) { reason = "目录路径无效：" + ex.Message; return false; }
    }

    internal static bool ValidateRoot(string? path, out string normalized, out string reason)
    {
        if (!TryNormalizePath(path, out normalized, out reason)) return false;
        try
        {
            if (new DriveInfo(Path.GetPathRoot(normalized)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
            { reason = "仅支持本地磁盘上的游戏目录，不检索网络映射盘。"; return false; }
            if (!AceGameDiscovery.SafeDirectory(normalized))
            { reason = "目录不存在、不可访问或含有链接，请选择实际游戏目录。"; return false; }
            reason = "";
            return true;
        }
        catch (Exception ex) { reason = "无法核对游戏目录：" + ex.Message; return false; }
    }

    internal static IReadOnlyList<AceGameCandidate> ReadRoots(IEnumerable<string>? roots, List<string> notes, Func<bool> blocked)
    {
        var result = new List<AceGameCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (roots is null) { notes.Add("手动 ACE 检索目录配置为空，待核对。"); return result; }
        var count = 0;
        foreach (var root in roots)
        {
            if (blocked()) throw new OperationCanceledException();
            if (++count > MaxRoots) { notes.Add($"手动检索目录超过 {MaxRoots} 个，超出部分未扫描。"); break; }
            if (!ValidateRoot(root, out var directory, out var reason))
            { notes.Add($"手动检索目录待核对（{root}）：{reason}"); continue; }
            if (seen.Add(directory)) result.Add(new(Path.GetFileName(directory), directory, Source, SearchChildren: true, ManuallyAssociated: true));
        }
        return result;
    }

    internal static List<string> FindAceDirectories(string root, Func<bool> blocked, Action? touch, List<string>? notes, Func<bool>? totalBudgetExpired = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var watch = Stopwatch.StartNew();
        var entries = 0;
        bool Budget()
        {
            if (blocked()) throw new OperationCanceledException();
            touch?.Invoke();
            if (++entries <= MaxDirectories && watch.Elapsed < TimeSpan.FromSeconds(3) && totalBudgetExpired?.Invoke() != true) return true;
            notes?.Add($"手动目录检索达到数量或时间上限，结果可能不完整：{root}");
            return false;
        }
        while (pending.TryDequeue(out var item))
        {
            if (!Budget()) break;
            try
            {
                if (!AceGameDiscovery.SafeDirectory(item.Path)) { notes?.Add($"手动检索跳过链接或不可访问目录：{item.Path}"); continue; }
                var componentFolder = Path.GetFileName(item.Path).Equals("AntiCheatExpert", StringComparison.OrdinalIgnoreCase);
                var files = 0;
                foreach (var file in Directory.EnumerateFiles(item.Path, "ACE*").Take(257))
                {
                    if (!Budget()) return result.ToList();
                    if (++files > 256) { notes?.Add($"ACE 组件目录条目超过识别上限：{item.Path}"); break; }
                    var name = Path.GetFileName(file);
                    var knownName = name.StartsWith("ACE-CORE", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(name).Equals(".sys", StringComparison.OrdinalIgnoreCase)
                        || KnownComponentNames.Contains(name);
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0
                        && (knownName || componentFolder && Path.GetExtension(file).ToLowerInvariant() is ".exe" or ".dll" or ".sys"))
                    { result.Add(item.Path); break; }
                }
                // For known component folders, existing inventory handles payload subdirectories.
                if (componentFolder) continue;
                foreach (var child in Directory.EnumerateDirectories(item.Path))
                {
                    if (!Budget()) return result.ToList();
                    var name = Path.GetFileName(child);
                    if (name.Equals("Content", StringComparison.OrdinalIgnoreCase) || name.Equals("Saved", StringComparison.OrdinalIgnoreCase)
                        || name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    if (item.Depth >= MaxDepth)
                    { notes?.Add($"手动目录检索达到 {MaxDepth} 层上限，部分位置未读取：{root}"); break; }
                    if (!AceGameDiscovery.SafeDirectory(child)) { notes?.Add($"手动检索跳过链接或不可访问目录：{child}"); continue; }
                    pending.Enqueue((child, item.Depth + 1));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { notes?.Add($"手动目录检索不完整（{item.Path}）：{ex.Message}"); }
        }
        return result.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
