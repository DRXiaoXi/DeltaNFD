using DeltaNFD.Backends;

namespace DeltaNFD.Services;

internal sealed class AceCleanupGuard(Func<bool> isCustom, Func<bool> isElevated, Func<IReadOnlyList<string>> blockers)
{
    public BackendResult? Check(string stage, List<BackendResultItem> done, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            var message = $"阶段 {stage} 已取消，已完成 {done.Count} 项；不表示已还原。";
            return done.Count == 0 ? BackendResult.Cancelled("ace.cancelled", message)
                : BackendResult.PartialResult("ace.cancelled", message, done.ToArray());
        }
        BackendResult Stop(string code, string message) => done.Count == 0
            ? BackendResult.Failed(code, message)
            : BackendResult.PartialResult(code, message, done.ToArray());
        try
        {
            if (isCustom()) return Stop("ace.custom-target", GameTargetService.DeltaOnlyMessage);
            if (!isElevated()) return Stop("ace.not-elevated", ElevationHelper.NotElevatedMessage);
            var active = blockers();
            if (active.Count > 0) return Stop("ace.blocked", $"阶段 {stage} 检测到游戏/守卫（{string.Join("、", active)}），停止后续清理；已完成 {done.Count} 项。");
            return null;
        }
        catch (Exception ex) { return Stop("ace.state-unknown", $"阶段 {stage} 状态无法确认，未继续清理；已完成 {done.Count} 项：{ex.Message}"); }
    }
}
