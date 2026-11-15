using System.Text.Json;
using System.Text.RegularExpressions;
using DeltaNFD.Services;
using Microsoft.Win32;

namespace DeltaNFD.Inspection;

internal sealed record AceGameCandidate(string Name, string Root, string Source, bool SearchChildren = false, bool ManuallyAssociated = false);

/// <summary>Read-only installed locations. No launch commands, drive recursion or name-only ACE allowlist.</summary>
internal static class AceGameDiscovery
{
    internal static IReadOnlyList<AceGameCandidate> Read(List<string> notes, Func<bool> blocked)
    {
        var candidates = new List<AceGameCandidate>();
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steam = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? name, string? root, string source)
        {
            if (blocked()) throw new OperationCanceledException();
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || root.StartsWith(@"\\")) return;
            root = Path.GetFullPath(root).TrimEnd('\\', '/');
            if (locations.Add(root) && candidates.Count < 1024)
                candidates.Add(new(string.IsNullOrWhiteSpace(name) ? Path.GetFileName(root) : name, root, source));
        }
        foreach (var root in DeltaForceLocator.FindRoots()) Add("三角洲行动", root, "三角洲安装记录");
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            if (blocked()) throw new OperationCanceledException();
            try
            {
                using var key = RegistryKey.OpenBaseKey(hive, view);
                foreach (var parent in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\Tencent\WeGame\Games" })
                {
                    using var installed = key.OpenSubKey(parent);
                    if (installed is null) continue;
                    var names = installed.GetSubKeyNames();
                    if (names.Length > 2048) notes.Add("安装记录超过读取上限，部分游戏数量待核对。");
                    foreach (var name in names.Take(2048))
                    {
                        if (blocked()) throw new OperationCanceledException();
                        try
                        {
                            using var entry = installed.OpenSubKey(name);
                            if (entry is null) continue;
                            Add(entry.GetValue("DisplayName") as string ?? entry.GetValue("Name") as string,
                                entry.GetValue("InstallLocation") as string ?? entry.GetValue("InstallPath") as string,
                                parent.Contains("WeGame") ? "WeGame" : "Windows 安装记录");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { notes.Add("部分游戏安装记录无法读取，未据此判断残留。"); }
                    }
                }
                using var valve = key.OpenSubKey(@"SOFTWARE\Valve\Steam");
                var location = valve?.GetValue("SteamPath") as string ?? valve?.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(location)) steam.Add(location);
            }
            catch (OperationCanceledException) { throw; }
            catch { notes.Add("部分游戏安装记录无法读取，未据此判断残留。"); }
        }
        foreach (var candidate in ReadSteam(steam, notes, blocked)) Add(candidate.Name, candidate.Root, candidate.Source);
        var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        foreach (var candidate in ReadEpic(epic, notes, blocked)) Add(candidate.Name, candidate.Root, candidate.Source);
        if (candidates.Count >= 1024) notes.Add("游戏安装位置达到读取上限，部分游戏数量待核对。");
        try
        {
            if (blocked()) throw new OperationCanceledException();
            var manual = AceManualGameInspection.ReadRoots(AppSettingsStore.ReadRequired().AceGameSearchRoots, notes, blocked);
            MergeManual(candidates, manual);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { notes.Add("手动 ACE 检索目录配置无法读取，待核对：" + ex.Message); }
        return candidates.OrderBy(c => c.SearchChildren ? 0 : c.Source == "Windows 安装记录" ? 2 : 1).ToArray();
    }

    internal static void MergeManual(List<AceGameCandidate> candidates, IReadOnlyList<AceGameCandidate> manual)
    {
        foreach (var entry in manual)
        {
            var existing = candidates.FindIndex(c => c.Root.Equals(entry.Root, StringComparison.OrdinalIgnoreCase));
            if (existing < 0) candidates.Add(entry);
            else candidates[existing] = candidates[existing] with { SearchChildren = true, ManuallyAssociated = true,
                Source = candidates[existing].Source + " / " + AceManualGameInspection.Source };
        }
    }

    internal static IReadOnlyList<AceGameCandidate> ReadSteam(IEnumerable<string> roots, List<string> notes, Func<bool> blocked)
    {
        var result = new List<AceGameCandidate>();
        var examined = 0;
        foreach (var steamRoot in roots.Take(8))
        {
            if (blocked()) throw new OperationCanceledException();
            try
            {
                if (!SafeDirectory(steamRoot)) continue;
                var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
                var folders = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (File.Exists(folders))
                    foreach (Match match in Quoted("path").Matches(ReadText(folders))) libraries.Add(Unescape(match.Groups[1].Value));
                if (libraries.Count > 32) notes.Add("Steam 库超过读取上限，部分游戏数量待核对。");
                foreach (var library in libraries.Take(32))
                {
                    if (blocked()) throw new OperationCanceledException();
                    if (!SafeDirectory(library)) continue;
                    var apps = Path.Combine(library, "steamapps");
                    if (!SafeDirectory(apps)) continue;
                    foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf").Take(1025))
                    {
                        if (blocked()) throw new OperationCanceledException();
                        if (++examined > 1024) { notes.Add("Steam 安装清单超过读取上限，部分游戏数量待核对。"); break; }
                        try
                        {
                            var text = ReadText(manifest);
                            var directory = Unescape(Quoted("installdir").Match(text).Groups[1].Value);
                            if (directory.Length == 0 || Path.IsPathRooted(directory)) continue;
                            var common = Path.GetFullPath(Path.Combine(apps, "common"));
                            var root = Path.GetFullPath(Path.Combine(common, directory));
                            if (!root.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                            result.Add(new(Unescape(Quoted("name").Match(text).Groups[1].Value), root, "Steam"));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { notes.Add("部分 Steam 游戏清单无法读取，游戏数量待核对。"); }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { notes.Add("部分 Steam 安装记录无法读取，游戏数量待核对。"); }
        }
        return result;
    }

    internal static IReadOnlyList<AceGameCandidate> ReadEpic(string manifests, List<string> notes, Func<bool> blocked)
    {
        var result = new List<AceGameCandidate>();
        var examined = 0;
        try
        {
            if (blocked()) throw new OperationCanceledException();
            if (SafeDirectory(manifests))
                foreach (var manifest in Directory.EnumerateFiles(manifests, "*.item").Take(513))
                {
                    if (blocked()) throw new OperationCanceledException();
                    if (++examined > 512) { notes.Add("Epic 清单超过读取上限，部分游戏数量待核对。"); break; }
                    try
                    {
                        using var document = JsonDocument.Parse(ReadText(manifest));
                        if (document.RootElement.TryGetProperty("InstallLocation", out var location) && location.ValueKind == JsonValueKind.String)
                            result.Add(new(document.RootElement.TryGetProperty("DisplayName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "Epic 游戏" : "Epic 游戏", location.GetString() ?? "", "Epic"));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { notes.Add("部分 Epic 游戏清单无法读取，游戏数量待核对。"); }
                }
        }
        catch (OperationCanceledException) { throw; }
        catch { notes.Add("部分 Epic 安装记录无法读取，游戏数量待核对。"); }
        return result;
    }

    private static Regex Quoted(string key) => new("\\\"" + key + "\\\"\\s*\\\"((?:\\\\.|[^\\\"\\\\])*)\\\"", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static string Unescape(string value) => value.Replace(@"\\", @"\").Replace("\\\"", "\"");
    private static string ReadText(string path)
    {
        if (!SafeDirectory(Path.GetDirectoryName(path)!) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("跳过链接清单。");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 1024 * 1024) throw new IOException("游戏清单超过读取上限。");
        using var reader = new StreamReader(file);
        var buffer = new char[1024 * 1024 + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return read <= 1024 * 1024 ? new string(buffer, 0, read) : throw new IOException("游戏清单超过读取上限。");
    }

    internal static bool SafeDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(path)) return false;
        for (var parent = new DirectoryInfo(path); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    internal static List<string> FindAceDirectories(string root, Func<bool> blocked, Action? touch = null, List<string>? notes = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Probe(string parent)
        {
            foreach (var relative in new[] { "AntiCheatExpert", @"Binaries\Win64\AntiCheatExpert", @"Binaries\Win32\AntiCheatExpert" })
            {
                if (blocked()) throw new OperationCanceledException();
                touch?.Invoke();
                var path = Path.GetFullPath(Path.Combine(parent, relative));
                if (!SafeDirectory(path)) continue;
                var count = 0;
                foreach (var file in Directory.EnumerateFiles(path).Take(257))
                {
                    if (++count > 256) { notes?.Add($"组件目录条目超过识别上限：{path}"); break; }
                    touch?.Invoke();
                    if (blocked()) throw new OperationCanceledException();
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                    if (Path.GetFileName(file).StartsWith("ACE", StringComparison.OrdinalIgnoreCase) &&
                        Path.GetExtension(file).ToLowerInvariant() is ".exe" or ".dll" or ".sys")
                    { result.Add(path); break; }
                }
            }
        }
        Probe(root);
        var children = 0;
        foreach (var child in Directory.EnumerateDirectories(root).Take(129))
        {
            if (blocked()) throw new OperationCanceledException();
            if (++children > 128) { notes?.Add($"游戏子目录超过识别上限：{root}"); break; }
            touch?.Invoke();
            if (!SafeDirectory(child)) continue;
            var name = Path.GetFileName(child);
            if (name.Equals("Content", StringComparison.OrdinalIgnoreCase) || name.Equals("Saved", StringComparison.OrdinalIgnoreCase)
                || name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) || name.Equals("common", StringComparison.OrdinalIgnoreCase)
                || name.Equals("rail_apps", StringComparison.OrdinalIgnoreCase)) continue;
            Probe(child);
        }
        return result.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
