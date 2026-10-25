using System.Security.Cryptography;
using System.Text.Json;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

internal static class AceManualGameChecks
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-ace-manual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = Path.Combine(root, "settings.json");
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        void Throws<T>(Action action, string message) where T : Exception
        { try { action(); } catch (T) { checks++; return; } throw new Exception(message); }
        string Write(string relative, string text)
        {
            var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path;
        }
        string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        try
        {
            Check(new AppSettings().AceGameSearchRoots.Count == 0 && JsonSerializer.Deserialize<AppSettings>("{}")!.AceGameSearchRoots.Count == 0, "old settings have no implicit manual directories");
            var game = Path.Combine(root, "中文 游戏");
            var payload = Write(@"中文 游戏\Vendor\Client\Plugins\AntiCheatExpert\ACE-CORE-local.sys", "exact-same-core");
            var shared = Path.Combine(root, "shared");
            var driver = Write(@"shared\ACE-CORE100.sys", "exact-same-core");
            Write(@"非 ACE 游戏\AntiCheatExpert\readme.txt", "name alone is insufficient");
            Write(@"非 ACE 游戏\AceCombat.exe", "unrelated game executable name");
            Check(AceManualGameInspection.ValidateRoot(game, out var normalized, out _) && normalized == game, "local Unicode folder validated read-only");
            foreach (var invalid in new[] { "relative\\game", @"\\server\game", @"\\?\C:\game", "", Path.GetPathRoot(root)!, Environment.GetFolderPath(Environment.SpecialFolder.Windows), Path.GetDirectoryName(payload)! })
                Check(!AceManualGameInspection.ValidateRoot(invalid, out _, out _), "invalid/common/component root rejected: " + invalid);
            Check(!AceManualGameInspection.ValidateRoot(payload, out _, out _), "a file is not a game directory");
            Check(!AceManualGameInspection.ValidateRoot(Path.Combine(root, "missing"), out _, out var missing) && missing.Length > 0, "missing/unreadable folder has an explicit reason");
            AppSettingsStore.UpdateRequired(settings, s => { s.SilentStartupEnabled = true; s.FrameModeActive = true; s.TempSpoofRestorePending = true; s.FramePowerRestorePending = true; });
            Check(AceGameSearchSettings.Add(game, settingsPath: settings).Success, "manual bookmark persisted");
            Check(AceGameSearchSettings.Add(game.ToUpperInvariant() + "\\", settingsPath: settings).Success && AceGameSearchSettings.Read(settings).Count == 1, "case/trailing separator duplicate is not added twice");
            var saved = AppSettingsStore.ReadRequired(settings);
            Check(saved.SilentStartupEnabled && saved.FrameModeActive && saved.TempSpoofRestorePending && saved.FramePowerRestorePending, "manual add preserves startup/frame/restoration preferences");
            Check(saved.GameInstallRoots.Count == 0 && saved.GameRootOverride == "", "manual ACE bookmark is separate from Delta/cleanup game roots");
            var notes = new List<string>();
            var manual = AceManualGameInspection.ReadRoots(saved.AceGameSearchRoots, notes, () => false);
            Check(manual.Count == 1 && manual[0].SearchChildren && manual[0].Source == "手动添加", "saved root becomes explicit manual candidate");
            Check(AceGameDiscovery.FindAceDirectories(game, () => false).Count == 0, "fixture was not covered by automatic shallow discovery");
            var before = Directory.GetFiles(Path.Combine(root, "中文 游戏"), "*", SearchOption.AllDirectories).Append(driver).ToDictionary(p => p, Digest);
            var result = AceCoreOwnershipInspection.Read(shared, () => false, (_, _) => manual);
            Check(result.Games.Count == 1 && result.SysCount == 1 && result.Level == AceCoreCheckLevel.Normal, "manual nested ACE directory reaches normal shared-core inventory");
            Check(result.Games[0].InstallDirectory == game && result.Games[0].Source == "手动添加", "manual source and actual root are exposed");
            Check(result.Associations.Single().Games.Count == 1 && result.Associations.Single().Evidence.Contains("SHA256"), "manual folder uses exact hash evidence, no synthetic mapping");
            Check(before.All(p => File.Exists(p.Key) && p.Value == Digest(p.Key)), "inventory does not change game or shared files");
            var candidates = new List<AceGameCandidate> { new("Launcher game name", game.ToUpperInvariant(), "Steam") };
            AceGameDiscovery.MergeManual(candidates, manual);
            Check(candidates.Count == 1 && candidates[0].Name == "Launcher game name" && candidates[0].SearchChildren, "manual merge preserves known launcher name and avoids duplicate game");
            result = AceCoreOwnershipInspection.Read(shared, () => false, (_, _) => [.. candidates, .. manual]);
            Check(result.Games.Count == 1, "component path deduplication still applies");
            var plain = Path.Combine(root, "非 ACE 游戏");
            var plainRoots = AceManualGameInspection.ReadRoots([plain], [], () => false);
            result = AceCoreOwnershipInspection.Read(shared, () => false, (_, _) => plainRoots);
            Check(result.Games.Count == 0 && result.Level == AceCoreCheckLevel.NeedsReview && result.Notes.Any(n => n.Contains("未发现可识别")), "empty/name-only ACE folder has clear uncertainty, not success or abnormality");
            var loose = Path.Combine(root, "LooseGame");
            Write(@"LooseGame\ACE-Service64.exe", "component-name evidence only");
            Write(@"LooseGame\Runtime\ACE-CORE-local.sys", "exact-same-core");
            Write(@"LooseGame\a\b\c\d\e\f\g\ACE-CORE-hidden.sys", "beyond-search-range");
            var hiddenShared = Write(@"shared\ACE-CORE-hidden.sys", "beyond-search-range");
            var looseRoots = AceManualGameInspection.ReadRoots([loose], [], () => false);
            result = AceCoreOwnershipInspection.Read(shared, () => false, (_, _) => looseRoots);
            Check(result.Games.Count == 1 && result.Games[0].AceDirectories.Contains(Path.Combine(loose, "Runtime")), "known component names outside AntiCheatExpert folders are recognized");
            Check(result.Associations.Single(a => a.Path == driver).Games.Count == 1, "loose CORE driver retains exact-hash matching");
            Check(result.Associations.Single(a => a.Path == hiddenShared).Games.Count == 0, "a loose component at game root cannot silently recurse beyond discovery scope");
            File.Delete(hiddenShared);
            var deep = Path.Combine(root, "DeepGame");
            Write(@"DeepGame\1\2\3\4\5\6\7\AntiCheatExpert\ACE-CORE.sys", "too deep");
            notes.Clear();
            Check(AceManualGameInspection.FindAceDirectories(deep, () => false, null, notes).Count == 0 && notes.Any(n => n.Contains("层上限")), "depth cap produces an incomplete reason");
            var broad = Path.Combine(root, "BroadGame"); Directory.CreateDirectory(broad);
            for (var i = 0; i < 2050; i++) Directory.CreateDirectory(Path.Combine(broad, "d" + i));
            notes.Clear();
            Check(AceManualGameInspection.FindAceDirectories(broad, () => false, null, notes).Count == 0 && notes.Any(n => n.Contains("数量或时间上限")), "entry/time budget is explicit");
            notes.Clear();
            Check(AceManualGameInspection.FindAceDirectories(game, () => false, null, notes, () => true).Count == 0 && notes.Any(n => n.Contains("上限")), "shared manual budget stops work before enumerating more roots");
            var calls = 0;
            Throws<OperationCanceledException>(() => AceManualGameInspection.FindAceDirectories(game, () => ++calls >= 2, null, []), "mid-search cancellation must stop directory queries");
            Throws<OperationCanceledException>(() => AceManualGameInspection.ReadRoots([game], [], () => true), "game gate precedes manual root reads");
            result = AceCoreOwnershipInspection.Read(shared, () => true, (_, _) => throw new Exception("must not discover"));
            Check(result.Level == AceCoreCheckLevel.Blocked, "game gate precedes manual inventory");
            notes.Clear();
            Check(AceManualGameInspection.ReadRoots([game, game.ToUpperInvariant(), game + "\\"], notes, () => false).Count == 1, "manual roots normalized and deduplicated");
            Check(AceManualGameInspection.ReadRoots([Path.Combine(root, "deleted")], notes, () => false).Count == 0 && notes.Any(n => n.Contains("待核对")), "deleted saved directory stays explicit, not an abnormal ACE verdict");
            notes.Clear();
            AceManualGameInspection.ReadRoots(Enumerable.Repeat(game, 33), notes, () => false);
            Check(notes.Any(n => n.Contains("32")), "manual configuration enumeration is bounded even for duplicates");
            var digest = Digest(settings);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Throws<OperationCanceledException>(() => AceGameSearchSettings.Add(plain, cancelled.Token, settingsPath: settings), "queued cancellation rejects save");
            Check(Digest(settings) == digest, "cancelled add keeps exact settings bytes");
            Throws<OperationCanceledException>(() => AceGameSearchSettings.Add(plain, canCommit: () => false, settingsPath: settings), "late closed page cannot save");
            Check(Digest(settings) == digest, "late add leaves prior configuration intact");
            Throws<OperationCanceledException>(() => AceGameSearchSettings.Remove(game, canCommit: () => false, settingsPath: settings), "late removal is rejected");
            AceGameSearchSettings.Remove(game, settingsPath: settings);
            Check(AceGameSearchSettings.Read(settings).Count == 0 && File.Exists(payload) && File.Exists(driver), "remove deletes only bookmark, not game/shared files");
            AceGameSearchSettings.Remove(game, settingsPath: settings);
            Check(AceGameSearchSettings.Read(settings).Count == 0, "remove is idempotent");
            AppSettingsStore.UpdateRequired(settings, s => s.AceGameSearchRoots = Enumerable.Range(0, 32).Select(i => Path.Combine(root, "Game" + i)).ToList());
            Throws<IOException>(() => AceGameSearchSettings.Add(game, settingsPath: settings), "save enforces 32-root limit");
            Check(AceGameSearchSettings.Read(settings).Count == 32, "failed limit add preserves saved entries");
            File.WriteAllText(settings, "{corrupt");
            Throws<JsonException>(() => AceGameSearchSettings.Add(game, settingsPath: settings), "corrupt settings cannot be overwritten");
            Check(File.ReadAllText(settings) == "{corrupt", "corrupt evidence retained");
            Console.WriteLine($"ACE manual game checks passed: {checks}; isolated folders/settings only, no real cleanup or player config writes.");
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("DeltaNFD-ace-manual-")) Directory.Delete(full, true);
        }
    }
}
