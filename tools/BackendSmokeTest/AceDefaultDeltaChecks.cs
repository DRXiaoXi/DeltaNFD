using System.Security.Cryptography;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

internal static class AceDefaultDeltaChecks
{
    internal static void Run()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-ace-default-" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        void Cancelled(Action action, string message)
        { try { action(); } catch (OperationCanceledException) { count++; return; } throw new Exception(message); }
        string Write(string relative)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "owned metadata fixture"); return path;
        }
        string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var shared = Path.Combine(root, "shared");
        var delta = Path.Combine(root, "WeGame", "DeltaForce(2001918)");
        var payload = Path.Combine(delta, "DeltaForce");
        var manual = Path.Combine(root, "OtherGame");
        string[] known = [delta];
        IReadOnlyList<AceGameCandidate> Discover(List<string> notes, Func<bool> blocked) =>
            AceDefaultGameInspection.Read(notes, blocked, () => known, () => AppSettingsStore.ReadRequired().AceGameSearchRoots);
        AceCoreCheckResult Inspect() => AceCoreOwnershipInspection.Read(shared, () => false, Discover);
        try
        {
            var core = Write(@"shared\ACE-CORE1.sys");
            Directory.CreateDirectory(Path.Combine(payload, "Content", "Paks"));
            var ace = Write(@"WeGame\DeltaForce(2001918)\DeltaForce\Binaries\Win64\AntiCheatExpert\ACE-Service64.exe");
            Write(@"OtherGame\Binaries\Win64\AntiCheatExpert\ACE-Service64.exe");
            Write(@"UnregisteredOtherGame\Binaries\Win64\AntiCheatExpert\ACE-Service64.exe");
            AppSettingsStore.UpdateRequired(s => { s.GameInstallRoots = [delta]; s.GameRootOverride = delta; s.AceGameSearchRoots = []; });
            var settings = Digest(AppSettingsStore.DefaultPath);
            var aceHash = Digest(ace);
            var result = Inspect();
            Check(result.Games.Count == 1 && result.Games[0].Name == "三角洲行动", "known Delta Force is included without an ACE bookmark");
            Check(result.Games[0].Source == AceDefaultGameInspection.DeltaSource, "default source is visible");
            Check(result.ManuallyAssociatedGameCount == 0 && !result.Games[0].ManuallyAssociated, "default inclusion is not a manual association");
            Check(result.Level == AceCoreCheckLevel.Normal && result.SysCount == 1, "one known Delta installation accounts for one CORE");
            Check(result.Games.All(g => !g.InstallDirectory.Contains("UnregisteredOtherGame")), "other unregistered games remain undiscovered");
            Check(AppSettingsStore.ReadRequired().AceGameSearchRoots.Count == 0 && Digest(AppSettingsStore.DefaultPath) == settings,
                "default inclusion never writes a manual ACE bookmark");

            AppSettingsStore.UpdateRequired(s => s.AceGameSearchRoots = [manual]);
            var secondCore = Write(@"shared\ACE-CORE2.sys");
            result = Inspect();
            Check(result.Games.Count == 2 && result.ManuallyAssociatedGameCount == 1, "known Delta and a different manual game are combined");
            Check(result.Level == AceCoreCheckLevel.Normal, "two confirmed games account for two CORE files");
            File.Delete(secondCore);
            AppSettingsStore.UpdateRequired(s => s.AceGameSearchRoots = [delta.ToUpperInvariant() + "\\"]);
            result = Inspect();
            Check(result.Games.Count == 1 && result.ManuallyAssociatedGameCount == 1, "same-root case/trailing-slash manual duplicate counts once");
            Check(result.Games[0].Source.Contains(AceManualGameInspection.Source), "combined default/manual source is shown");
            AppSettingsStore.UpdateRequired(s => s.AceGameSearchRoots = [payload]);
            result = Inspect();
            Check(result.Games.Count == 1 && result.ManuallyAssociatedGameCount == 1, "wrapper and payload paths sharing ACE folders count once");
            AceGameSearchSettings.Remove(payload);
            result = Inspect();
            Check(result.Games.Count == 1 && result.ManuallyAssociatedGameCount == 0, "removing a manual duplicate preserves default Delta detection");
            Check(File.Exists(ace) && File.Exists(core), "bookmark removal does not delete game or shared files");

            known = [delta, payload, delta.ToUpperInvariant() + "\\"];
            result = Inspect();
            Check(result.Games.Count == 1, "duplicate known wrapper/payload installs deduplicate by ACE directories");
            var secondDelta = Path.Combine(root, "SecondDelta");
            Directory.CreateDirectory(Path.Combine(secondDelta, "Content", "Paks"));
            Write(@"SecondDelta\Binaries\Win64\AntiCheatExpert\ACE-Service64.exe");
            known = [delta, secondDelta];
            result = Inspect();
            Check(result.Games.Count == 2 && result.ManuallyAssociatedGameCount == 0, "multiple distinct recognized Delta installations are retained");
            known = [];
            result = Inspect();
            Check(result.Games.Count == 0 && result.Level == AceCoreCheckLevel.NeedsReview, "no known or manual game does not invent a default installation");
            AppSettingsStore.UpdateRequired(s => s.AceGameSearchRoots = [manual]);
            Check(Inspect().Games.Single().InstallDirectory == manual, "manual games still work with no known Delta");

            var invalid = Path.Combine(root, "NotDelta");
            Write(@"NotDelta\Binaries\Win64\AntiCheatExpert\ACE-Service64.exe");
            known = [Path.Combine(root, "deleted"), invalid, @"\\server\game", "relative"];
            var notes = new List<string>();
            var candidates = Discover(notes, () => false);
            Check(candidates.Count == 1 && candidates[0].ManuallyAssociated, "invalid known paths cannot masquerade as Delta");
            Check(notes.Count == 4, "invalid or stale known paths are reported");
            known = [delta];
            notes.Clear();
            candidates = AceDefaultGameInspection.Read(notes, () => false, () => known, () => throw new IOException("fixture settings failure"));
            Check(candidates.Count == 1 && notes.Any(n => n.Contains("fixture settings failure")), "manual settings failure preserves known Delta and reports the gap");
            notes.Clear();
            candidates = AceDefaultGameInspection.Read(notes, () => false, () => throw new IOException("fixture locator failure"), () => [manual]);
            Check(candidates.Count == 1 && candidates[0].ManuallyAssociated && notes.Any(n => n.Contains("fixture locator failure")), "locator failure preserves manual games and reports the gap");
            notes.Clear();
            candidates = AceDefaultGameInspection.Read(notes, () => false, () => known, () => null);
            Check(candidates.Count == 1 && notes.Any(n => n.Contains("配置为空")), "null manual list does not hide known Delta");
            var providers = 0;
            Cancelled(() => AceDefaultGameInspection.Read([], () => true,
                () => { providers++; return known; }, () => { providers++; return []; }), "gate must run before either provider");
            Check(providers == 0, "game-running gate reads no roots or bookmarks");
            var gameStarted = false;
            Cancelled(() => AceDefaultGameInspection.Read([], () => gameStarted,
                () => { gameStarted = true; return known; }, () => throw new Exception("must not read manual roots")), "game starting during discovery stops further reads");

            var noAce = Path.Combine(root, "DeltaWithoutAce");
            Directory.CreateDirectory(Path.Combine(noAce, "Content", "Paks"));
            known = [noAce];
            AppSettingsStore.UpdateRequired(s => s.AceGameSearchRoots = []);
            result = Inspect();
            Check(result.Games.Count == 0 && result.Notes.Any(n => n.Contains("未计入 ACE 游戏数")), "recognized game without ACE evidence is explicit, not silently counted");
            known = [delta];
            var manualRoots = Enumerable.Range(0, 32).Select(i => Path.Combine(root, "Bookmark" + i)).ToArray();
            foreach (var path in manualRoots) Directory.CreateDirectory(path);
            candidates = AceDefaultGameInspection.Read([], () => false, () => known, () => manualRoots);
            Check(candidates.Count == 33, "default Delta does not consume the 32 manual bookmark slots");
            Check(Digest(ace) == aceHash && File.Exists(core), "all detection checks preserve cache/game/shared payloads");
            Console.WriteLine($"ACE default Delta checks: {count} passed; owned fixtures/injected known roots only, no host game scan or cleanup.");
        }
        finally
        {
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe fixture path");
            Directory.Delete(root, true);
        }
    }
}
