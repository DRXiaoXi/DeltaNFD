using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

/// <summary>Reuse known Delta Force installs and explicit ACE bookmarks; no general game discovery.</summary>
internal static class AceDefaultGameInspection
{
    internal const string DeltaSource = "软件已识别的三角洲";

    internal static IReadOnlyList<AceGameCandidate> Read(List<string> notes, Func<bool> blocked) =>
        Read(notes, blocked, DeltaForceLocator.FindRoots, () => AppSettingsStore.ReadRequired().AceGameSearchRoots);

    internal static IReadOnlyList<AceGameCandidate> Read(List<string> notes, Func<bool> blocked,
        Func<IEnumerable<string>> knownDeltaRoots, Func<IEnumerable<string>?> manualRoots)
    {
        if (blocked()) throw new OperationCanceledException();
        var candidates = new List<AceGameCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var count = 0;
            foreach (var root in knownDeltaRoots())
            {
                if (blocked()) throw new OperationCanceledException();
                if (++count > AceManualGameInspection.MaxRoots)
                { notes.Add("已识别三角洲目录超过读取上限，部分安装位置待核对。"); break; }
                if (!AceManualGameInspection.ValidateRoot(root, out var directory, out var reason))
                { notes.Add($"已识别三角洲目录待核对（{root}）：{reason}"); continue; }
                if (!DeltaForceLocator.TryGetValidGameRoot(directory, out _))
                { notes.Add($"已识别三角洲目录已失效或缺少游戏内容，未计入 ACE 游戏数：{directory}"); continue; }
                if (seen.Add(directory)) candidates.Add(new("三角洲行动", directory, DeltaSource));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { notes.Add("已识别三角洲目录读取不完整，待核对：" + ex.Message); }

        if (blocked()) throw new OperationCanceledException();
        try
        {
            var manual = AceManualGameInspection.ReadRoots(manualRoots(), notes, blocked);
            AceGameDiscovery.MergeManual(candidates, manual);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { notes.Add("手动 ACE 检索目录配置无法读取，待核对：" + ex.Message); }
        if (blocked()) throw new OperationCanceledException();
        return candidates;
    }
}
