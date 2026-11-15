using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

/// <summary>New custom records carry their admitted image path; old unscoped records stay Delta-only.</summary>
internal static class FrameGameTargetPolicy
{
    internal static bool LegacyDelta(ProcmanProcessIdentity id) => id.Pid > 0 && id.StartTimeUtcTicks > 0
        && Path.IsPathFullyQualified(id.ExecutablePath)
        && Path.GetFileName(id.ExecutablePath).Equals("DeltaForceClient-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase);

    internal static bool CustomBackup(ProcmanProcessIdentity id, string? path)
    {
        try
        {
            return path is not null && id.Pid > 0 && id.StartTimeUtcTicks > 0
                && Path.IsPathFullyQualified(path) && Path.IsPathFullyQualified(id.ExecutablePath)
                && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !(path.Length > 2 && path.IndexOf(':', 2) >= 0)
                && !ProcmanIdentity.IsProtectedPath(path)
                && !new[] { "cmd", "powershell", "pwsh", "DeltaOptimizer" }.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
                && Path.GetFullPath(path).Equals(Path.GetFullPath(id.ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

public sealed partial class ProcmanEngine
{
    internal Func<GameTarget>? FrameTargetForTests { get; set; }
    internal Func<string, string>? FrameCanonicalPathForTests { get; set; }
    private GameTarget FrameTarget => FrameTargetForTests?.Invoke() ?? GameTargetService.Default.Current;
    private string FrameCanonical(string path) => (FrameCanonicalPathForTests ?? GameTargetService.CanonicalPath)(path);

    private bool FrameTargetCurrent(GameTarget target)
    {
        var current = FrameTarget;
        var settings = AppSettingsStore.ReadRequired();
        return current.Generation == target.Generation && current.IsCustom == target.IsCustom
            && string.Equals(current.ExecutablePath, target.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            && settings.CustomGameModeEnabled == target.IsCustom
            && (!target.IsCustom || string.Equals(settings.CustomGameExecutablePath, target.ExecutablePath, StringComparison.OrdinalIgnoreCase));
    }
    private bool IsSelectedFrameTarget(ProcmanProcessIdentity id) => IsSelectedFrameTarget(id, FrameTarget);
    private bool IsSelectedFrameTarget(ProcmanProcessIdentity id, GameTarget target)
    {
        try
        {
            if (!FrameTargetCurrent(target)) return false;
            return !target.IsCustom ? FrameGameTargetPolicy.LegacyDelta(id)
                : FrameGameTargetPolicy.CustomBackup(id, id.ExecutablePath)
                    && FrameCanonical(id.ExecutablePath).Equals(FrameCanonical(target.ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    private string? FrameCustomBackupPath(ProcmanProcessIdentity id) => FrameTarget.IsCustom && IsSelectedFrameTarget(id) ? id.ExecutablePath : null;

    private IReadOnlyList<ProcmanProcessIdentity> ReadFrameGameTargets(GameTarget target,
        Func<IReadOnlyList<ProcmanProcessIdentity>>? injected)
    {
        if (!FrameTargetCurrent(target)) throw new IOException("游戏目标尚未同步，未操作旧目标。");
        if (injected is not null) return injected().Where(id => IsSelectedFrameTarget(id, target)).Distinct().ToArray();
        var result = new List<ProcmanProcessIdentity>();
        var processes = GameTargetService.Default.GetProcesses(forceRefresh: true);
        try
        {
            foreach (var process in processes)
            {
                var id = ProcmanIdentity.TryIdentify(process.Id);
                if (id is null || !IsSelectedFrameTarget(id, target)) throw new IOException($"无法核验目标游戏 PID {process.Id} 的路径、启动时间和目标代次。");
                result.Add(id);
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return result;
    }
}
