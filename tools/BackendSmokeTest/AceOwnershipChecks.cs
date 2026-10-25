using System.Security.Cryptography;
using System.Text.Json;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

internal static class AceOwnershipChecks
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-ace-ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        string Write(string relative, string value)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
            return path;
        }
        var shared = Path.Combine(root, "shared");
        var candidates = new List<AceGameCandidate>();
        AceCoreCheckResult Read() => AceCoreOwnershipInspection.Read(shared, () => false, (_, _) => candidates);
        try
        {
            Check(Read().Level == AceCoreCheckLevel.None, "Missing shared root is not an error");
            var g1 = Path.Combine(root, "DeltaGame");
            var g2 = Path.Combine(root, "ArenaGame");
            candidates.Add(new("三角洲行动", g1, "WeGame"));
            candidates.Add(new("暗区突围：无限", g2, "Steam"));
            Write(@"DeltaGame\DeltaForce\Binaries\Win64\AntiCheatExpert\ACE-CORE-local.sys", "game-one-v1");
            Write(@"ArenaGame\ABInfinite\Binaries\Win64\AntiCheatExpert\ACE-CORE-local.sys", "game-two-v2");
            var a = Write(@"shared\ACE-CORE100.sys", "game-one-v1");
            Write(@"shared\version\ACE-CORE200.SYS", "game-two-v2");
            Write(@"shared\ACE-BASE.sys", "ignored");
            Write(@"shared\xACE-CORE.sys", "ignored");
            Write(@"shared\ACE-CORE.dll", "ignored");
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            var result = Read();
            Check(result.Level == AceCoreCheckLevel.Normal, "Two games / two drivers coexist normally");
            Check(result.Games.Count == 2 && result.SysCount == 2, "Count installations and exact core SYS names");
            Check(result.Associations.All(x => x.Games.Count == 1), "Every core has exact game payload evidence");
            Check(result.Associations.Single(x => x.Path == a).Games.Single().Contains("三角洲行动"), "Names/suffixes need not match to verify payload");
            Check(result.Games.All(x => x.AceDirectories.Count == 1 && x.InstallDirectory.Length > 0), "Expose confirmed component directories");
            Check(result.Notes.Count == 0, "Complete fixtures have no invented uncertainty");
            Check(before.All(x => File.Exists(x.Key) && x.Value == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x.Key)))) &&
                Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == before.Count, "Inventory neither writes nor removes files");

            candidates.Add(new("Alias", Path.Combine(g1, "DeltaForce"), "Windows 安装记录"));
            candidates.Add(new("Case alias", g2.ToUpperInvariant(), "Epic"));
            Check(Read().Games.Count == 2, "Registry/launcher aliases are deduplicated by component path");
            var unknown = Write(@"shared\ACE-CORE999.sys", "not-in-installed-games");
            result = Read();
            Check(result.Level == AceCoreCheckLevel.NeedsReview && result.SysCount == 3, "More files than games cannot imply abnormality");
            Check(result.Associations.Single(x => x.Path == unknown).Games.Count == 0, "Unmatched core stays unassigned");
            Check(!result.Advice.Contains("一键清除"), "No cleanup recommendation from file count");
            File.Delete(unknown);
            candidates.Clear();
            result = Read();
            Check(result.Level == AceCoreCheckLevel.NeedsReview && result.Games.Count == 0, "Missing install metadata does not mean redundant files");
            Check(result.Associations.All(x => x.Games.Count == 0), "No synthetic one-to-one pairing by order/count");
            candidates.Add(new("三角洲行动", g1, "WeGame"));
            Write(@"DeltaGame\DeltaForce\Binaries\Win64\AntiCheatExpert\ACE-CORE-v2.sys", "game-two-v2");
            result = Read();
            Check(result.Level == AceCoreCheckLevel.NeedsReview && result.Games.Count == 1, "One game with multiple cores is yellow until manual association, even when hashes match");
            candidates.Add(new("暗区突围：无限", g2, "Steam"));
            result = Read();
            Check(result.Associations.Any(x => x.Games.Count == 2 && x.Evidence.Contains("不能确认独占")), "Shared identical payload is not falsely attributed exclusively");

            using (var locked = new FileStream(a, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                result = Read();
                Check(result.Level == AceCoreCheckLevel.NeedsReview && result.SysCount == 2, "Locked driver preserves count and uncertainty");
                Check(result.Associations.Single(x => x.Path == a).Games.Count == 0, "Locked driver is never matched by name");
            }
            var big = Write(@"shared\ACE-CORE-big.sys", "");
            using (var file = File.OpenWrite(big)) file.SetLength(17 * 1024 * 1024);
            Check(Read().Level == AceCoreCheckLevel.NeedsReview, "Oversized driver is bounded and pending review");
            File.Delete(big);
            var blocked = AceCoreOwnershipInspection.Read(shared, () => true, (_, _) => throw new Exception("Must not discover"));
            Check(blocked.Level == AceCoreCheckLevel.Blocked && blocked.Files.Count == 0 && blocked.Games.Count == 0, "Game gate precedes all inventory");
            var started = false;
            blocked = AceCoreOwnershipInspection.Read(shared, () => started, (_, _) => { started = true; return candidates; });
            Check(blocked.Level == AceCoreCheckLevel.Blocked && blocked.Associations.Count == 0, "Game starting during scan discards partial inventory");
            result = AceCoreOwnershipInspection.Read(shared, () => false, (notes, _) => { notes.Add("incomplete fixture"); return candidates; });
            Check(result.Level == AceCoreCheckLevel.NeedsReview && result.Notes.Contains("incomplete fixture"), "Partial metadata is explicit, not an abnormal verdict");
            candidates.Add(new("Non-ACE", Path.Combine(root, "NonAce"), "Epic"));
            Write(@"NonAce\AntiCheatExpert\readme.txt", "empty folder name is insufficient");
            Check(Read().Games.Count == 2, "Folder name alone is insufficient to identify an ACE game");
            var package = JsonSerializer.Deserialize<AceCoreCheckResult>(JsonSerializer.Serialize(Read()))!;
            Check(package.Games.Count == 2 && package.Associations.Count == 2, "Additive ownership contract roundtrips");
            Check((int)AceCoreCheckLevel.Blocked == 4 && (int)AceCoreCheckLevel.Abnormal == 2, "Old enum values remain compatible");

            var steam = Path.Combine(root, "Steam");
            var library = Path.Combine(root, "Steam library");
            string Vdf(string s) => s.Replace("\\", "\\\\");
            Write(@"Steam\steamapps\libraryfolders.vdf", $"\"libraryfolders\" {{ \"1\" {{ \"path\" \"{Vdf(library)}\" }} }}");
            Write(@"Steam library\steamapps\appmanifest_1.acf", "\"AppState\" { \"name\" \"ACE Fixture\" \"installdir\" \"Game Fixture\" }");
            Write(@"Steam library\steamapps\appmanifest_2.acf", "\"name\" \"Escape\" \"installdir\" \"../../outside\"");
            var notes = new List<string>();
            var steamGames = AceGameDiscovery.ReadSteam([steam], notes, () => false);
            Check(steamGames.Count == 1 && steamGames[0].Name == "ACE Fixture", "Parse actual Steam VDF/ACF layout including escaped library path");
            Check(steamGames[0].Root == Path.Combine(library, "steamapps", "common", "Game Fixture"), "Steam install path stays inside library common");
            Write(@"Epic manifests\valid.item", JsonSerializer.Serialize(new { DisplayName = "Epic ACE Fixture", InstallLocation = g2 }));
            Write(@"Epic manifests\broken.item", "{broken");
            var epicGames = AceGameDiscovery.ReadEpic(Path.Combine(root, "Epic manifests"), notes, () => false);
            Check(epicGames.Count == 1 && epicGames[0].Name == "Epic ACE Fixture", "One malformed Epic manifest does not hide other games");
            Check(notes.Count > 0, "Malformed manifest reports partial inventory");
            try { AceGameDiscovery.ReadSteam([steam], [], () => true); throw new Exception("Expected pause"); }
            catch (OperationCanceledException) { checks++; }

            var deep = Path.Combine(shared, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("nested", 18)));
            Directory.CreateDirectory(deep);
            result = Read();
            Check(result.Level == AceCoreCheckLevel.NeedsReview && result.Notes.Any(n => n.Contains("层级")), "Directory depth limit stays explicit");
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            var link = Path.Combine(shared, "linked");
            var linked = false;
            try { Directory.CreateSymbolicLink(link, outside); linked = true; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Console.WriteLine("SKIP: OS denied creating a synthetic directory link."); }
            if (linked)
            {
                Write(@"outside\ACE-CORE100.sys", "outside-only");
                result = Read();
                Check(result.SysCount == 2 && result.Notes.Any(n => n.Contains("链接")), "Never traverse a component link to an outside file");
                Directory.Delete(link);
            }

            Console.WriteLine($"ACE ownership checks passed: {checks}; synthetic games only, no real ACE cleanup or settings writes.");
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("DeltaNFD-ace-ownership-"))
                Directory.Delete(full, true);
        }
    }
}
