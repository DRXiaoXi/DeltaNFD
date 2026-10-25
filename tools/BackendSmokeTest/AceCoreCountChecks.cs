using System.Security.Cryptography;
using System.Text.Json;
using DeltaNFD.Inspection;
using DeltaNFD.Services;
using DeltaNFD.ViewModels;

internal static class AceCoreCountChecks
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-ace-count-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        string Write(string relative, string text)
        { var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path; }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var shared = Path.Combine(root, "shared");
        var candidates = new List<AceGameCandidate>();
        AceCoreCheckResult Read() => AceCoreOwnershipInspection.Read(shared, () => false, (_, _) => candidates);
        try
        {
            foreach (var (cores, games, manualCount, review, expected) in new[]
            {
                (0,0,0,false,AceCoreCheckLevel.None), (1,1,0,false,AceCoreCheckLevel.Normal),
                (6,1,0,false,AceCoreCheckLevel.NeedsReview), (6,1,1,false,AceCoreCheckLevel.Abnormal),
                (6,1,1,true,AceCoreCheckLevel.Abnormal), (6,6,6,false,AceCoreCheckLevel.Normal),
                (6,6,0,false,AceCoreCheckLevel.Normal), (6,6,1,true,AceCoreCheckLevel.NeedsReview),
                (1,2,1,true,AceCoreCheckLevel.NeedsReview), (1,0,0,true,AceCoreCheckLevel.NeedsReview),
            }) Check(AceCoreCountPolicy.Evaluate(cores, games, manualCount, review) == expected, "count policy boundary");
            Check(DashboardHealthPresentation.AceCore(AceCoreCheckLevel.NeedsReview, true) == DashboardHealthState.Warning, "excess pending association is yellow on home");
            Check(DashboardHealthPresentation.AceCore(AceCoreCheckLevel.NeedsReview, false) == DashboardHealthState.Information, "ownership pending with no excess is informational, not yellow");
            Check(DashboardHealthPresentation.Summary(false, DashboardHealthPresentation.AceCore(AceCoreCheckLevel.NeedsReview, false)) == "存在组件提示", "ownership-only information does not inflate warning summary");
            Check(DashboardHealthPresentation.AceCore(AceCoreCheckLevel.Abnormal) == DashboardHealthState.Abnormal, "excess after association is red on home");
            Check(DashboardHealthPresentation.Summary(false, DashboardHealthPresentation.AceCore(AceCoreCheckLevel.NeedsReview, true)) == "1 项提醒", "yellow enters health summary");
            Check(DashboardHealthPresentation.Summary(false, DashboardHealthPresentation.AceCore(AceCoreCheckLevel.Abnormal)) == "1 项异常", "red enters health summary");
            var first = Path.Combine(root, "Game1");
            for (var i = 1; i <= 6; i++) Write($@"shared\ACE-CORE{i}.sys", "payload-" + i);
            Write(@"Game1\Client\Binaries\Win64\AntiCheatExpert\ACE-CORE-local.sys", "payload-1");
            candidates.Add(new("Game1", first, "Steam"));
            var result = Read();
            Check(result.SysCount == 6 && result.Games.Count == 1 && result.ManuallyAssociatedGameCount == 0, "screenshot fixture: one auto game, six drivers");
            Check(result.Level == AceCoreCheckLevel.NeedsReview && result.Summary.Contains("尚未完成有效手动关联"), "screenshot case is yellow before association");
            candidates[0] = candidates[0] with { SearchChildren = true };
            Check(Read().Level == AceCoreCheckLevel.NeedsReview && Read().ManuallyAssociatedGameCount == 0, "recursive discovery alone cannot claim user association");
            var manual = AceManualGameInspection.ReadRoots([first], [], () => false);
            Check(manual.Single().ManuallyAssociated, "manual bookmark produces explicit selection marker");
            AceGameDiscovery.MergeManual(candidates, manual);
            result = Read();
            Check(result.Level == AceCoreCheckLevel.Abnormal && result.ManuallyAssociatedGameCount == 1 && result.HasExcessCoreFiles, "same six drivers turn red after one valid association");
            Check(result.Games.Single().ManuallyAssociated && result.Summary.Contains("6 > 1"), "deduplicated game is explicitly marked associated and count basis shown");
            Check(result.Associations.Count(a => a.Games.Count == 0) == 5, "red count does not invent hash ownership for other drivers");
            Check(result.Advice.Contains("不证明") && !result.Advice.Contains("一键清除"), "red never grants deletion authority");
            var received = JsonSerializer.Deserialize<AceCoreCheckResult>(JsonSerializer.Serialize(result))!;
            Check(received.Level == AceCoreCheckLevel.Abnormal && received.ManuallyAssociatedGameCount == 1
                && received.Games.Single().ManuallyAssociated && received.HasExcessCoreFiles, "backend/frontend JSON retains count and manual evidence");
            var extraCopies = new List<string>();
            for (var i = 2; i <= 6; i++) extraCopies.Add(Write($@"Game1\Client\Binaries\Win64\AntiCheatExpert\ACE-CORE{i}.sys", "payload-" + i));
            result = Read();
            Check(result.Level == AceCoreCheckLevel.Abnormal && result.Associations.All(a => a.Games.Count == 1), "manual one-game excess is red even when every version has a matching payload");
            result = AceCoreOwnershipInspection.Read(shared, () => false, (_, _) => candidates.Select(c => c with { ManuallyAssociated = false }).ToArray());
            Check(result.Level == AceCoreCheckLevel.NeedsReview && result.Associations.All(a => a.Games.Count == 1), "same excess stays yellow before manual association even with all hashes matched");
            foreach (var extra in extraCopies) File.Delete(extra);
            candidates.Add(manual.Single());
            Check(Read().Games.Count == 1 && Read().ManuallyAssociatedGameCount == 1, "same root alias cannot inflate game count");
            candidates.Clear(); candidates.Add(new("Auto", first, "Steam"));
            candidates.Add(manual.Single());
            Check(Read().Level == AceCoreCheckLevel.Abnormal && Read().ManuallyAssociatedGameCount == 1, "manual exact duplicate after auto discovery is still associated once");
            candidates.Clear(); candidates.Add(new("Auto", first, "Steam"));
            candidates.Add(new("Manual child alias", Path.Combine(first, "Client"), "手动添加", true, true));
            Check(Read().Level == AceCoreCheckLevel.Abnormal && Read().Games.Count == 1 && Read().ManuallyAssociatedGameCount == 1, "same component from nested alias counts as one associated game");
            for (var i = 2; i <= 6; i++)
            {
                Write($@"Game{i}\AntiCheatExpert\ACE-CORE-local.sys", "payload-" + i);
                candidates.AddRange(AceManualGameInspection.ReadRoots([Path.Combine(root, "Game" + i)], [], () => false));
            }
            result = Read();
            Check(result.Level == AceCoreCheckLevel.Normal && result.Games.Count == 6 && result.ManuallyAssociatedGameCount == 6, "adding all six games resolves count warning to green");
            Check(result.Associations.All(a => a.Games.Count == 1), "all actual hash associations remain one-to-one in fixture");
            candidates = candidates.Select(c => c with { ManuallyAssociated = false }).ToList();
            Check(Read().Level == AceCoreCheckLevel.Normal, "six automatically detected games and six matched drivers remain normal");
            candidates.Clear();
            var empty = Path.Combine(root, "EmptyManual"); Directory.CreateDirectory(empty);
            candidates.AddRange(AceManualGameInspection.ReadRoots([empty], [], () => false));
            result = Read();
            Check(result.Level == AceCoreCheckLevel.NeedsReview && result.ManuallyAssociatedGameCount == 0, "saved empty directory is not a completed manual association");
            candidates.Clear(); candidates.Add(new("Deleted", Path.Combine(root, "missing"), "手动添加", true, true));
            Check(Read().Level == AceCoreCheckLevel.NeedsReview && Read().ManuallyAssociatedGameCount == 0, "stale manual directory does not turn yellow into red");
            candidates.Clear(); candidates.AddRange(manual);
            result = AceCoreOwnershipInspection.Read(shared, () => false, (notes, _) => { notes.Add("fixture partial scan"); return candidates; });
            Check(result.Level == AceCoreCheckLevel.Abnormal && result.Notes.Contains("fixture partial scan") && result.Summary.Contains("仍待核对"), "count rule does not erase uncertainty evidence or hide it from the summary");
            Check(AceCoreOwnershipInspection.Read(shared, () => true, (_, _) => throw new Exception("must not discover")).Level == AceCoreCheckLevel.Blocked, "game gate wins over all count classification");
            var single = Path.Combine(root, "one-shared");
            var singleFile = Write(@"one-shared\ACE-CORE1.sys", "unmatched-driver");
            result = AceCoreOwnershipInspection.Read(single, () => false, (_, _) => candidates);
            Check(result.SysCount == 1 && result.Games.Count == 1 && result.ManuallyAssociatedGameCount == 1 && !result.HasExcessCoreFiles,
                "reported screenshot fixture: one associated game, one unpaired shared core");
            Check(DashboardHealthPresentation.AceCore(result.Level, result.HasExcessCoreFiles) == DashboardHealthState.Information,
                "one-to-one ownership mismatch never shows a yellow count warning");
            Check(result.Summary.Contains("数量未超出") && !result.Summary.Contains("数量偏多") && result.Associations.Single().Games.Count == 0,
                "count summary stays accurate without inventing ownership");
            File.WriteAllText(singleFile, "payload-1");
            result = AceCoreOwnershipInspection.Read(single, () => false, (_, _) => candidates);
            Check(result.Level == AceCoreCheckLevel.Normal && DashboardHealthPresentation.AceCore(result.Level, result.HasExcessCoreFiles) == DashboardHealthState.Normal,
                "one-to-one verified driver remains green");
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);
            _ = Read();
            Check(before.All(p => File.Exists(p.Key) && p.Value == Hash(p.Key)) && Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == before.Count, "count classification never changes game/shared files");
            Check((int)AceCoreCheckLevel.Abnormal == 2 && (int)AceCoreCheckLevel.NeedsReview == 5, "existing enum numbers remain compatible");
            Console.WriteLine($"ACE manual-association count policy passed: {count}; isolated fixtures, no actual cleanup or player settings writes.");
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("DeltaNFD-ace-count-")) Directory.Delete(full, true);
        }
    }
}
