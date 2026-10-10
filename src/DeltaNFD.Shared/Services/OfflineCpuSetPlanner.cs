using System.Numerics;

namespace DeltaNFD.Services;

public sealed record OfflineCpuSetPlan(ulong TargetMask, ulong OtherProcessMask, string Description)
{
    public bool IsDualCcd => OtherProcessMask != 0;
}

/// <summary>脱机 CPU Sets 规划；只读拓扑与用户保存偏好，不操作进程或系统状态。</summary>
public static class OfflineCpuSetPlanner
{
    public static bool TryCreate(CpuTopology topology, OfflineModePreferences preferences,
        out OfflineCpuSetPlan? plan, out string error)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(preferences);
        plan = null;
        error = "";

        var requestedDualCcd = preferences.DualCcdImmediateEnabled ||
            (preferences.DualCcdArmed && preferences.DualCcdFrameEnabled && preferences.FrameModeWasActive);
        var requestedSingleCcd = !requestedDualCcd && preferences.SingleCcdExcludeCpu0Enabled;
        var requestedGameRule = !requestedDualCcd && !requestedSingleCcd && preferences.GameAffinityRuleEnabled;
        if (!requestedDualCcd && !requestedSingleCcd && !requestedGameRule) return true;

        if (topology.GroupCount != 1 || topology.AllMask == 0 ||
            BitOperations.PopCount(topology.AllMask) != topology.LogicalProcessors)
        {
            error = "CPU 拓扑未知、掩码无效或超出当前单处理器组支持范围，拒绝脱机调度。";
            return false;
        }

        if (requestedDualCcd)
        {
            if (!topology.IsAmdMultiCcd || topology.Ccds.Count != 2 ||
                preferences.DualCcdGameCcdIndex is < 0 or > 1 ||
                topology.Ccds.Any(c => c.Group != 0 || c.Mask == 0) ||
                (topology.Ccds[0].Mask & topology.Ccds[1].Mask) != 0 ||
                (topology.Ccds[0].Mask | topology.Ccds[1].Mask) != topology.AllMask)
            {
                error = "没有可验证的双 CCD 拓扑或所选 CCD 索引无效，拒绝脱机调度。";
                return false;
            }

            var gameMask = topology.Ccds[preferences.DualCcdGameCcdIndex].Mask;
            var otherMask = topology.Ccds[1 - preferences.DualCcdGameCcdIndex].Mask;
            if (preferences.DualCcdGameCcdIndex == 0) gameMask &= ~1UL;
            else otherMask &= ~1UL;
            if (gameMask == 0 || otherMask == 0 || (gameMask & otherMask) != 0 ||
                ((gameMask | otherMask) & ~topology.AllMask) != 0)
            {
                error = "双 CCD CPU Sets 掩码无效，拒绝脱机调度。";
                return false;
            }

            plan = new OfflineCpuSetPlan(gameMask, otherMask,
                $"目标游戏使用 CCD{preferences.DualCcdGameCcdIndex} 的 CPU Sets，当前会话其他进程使用另一 CCD 的 CPU Sets；不承诺独占核心。");
            return true;
        }

        var targetMask = requestedSingleCcd ? topology.AllMask & ~1UL : preferences.GameAffinityRuleMask;
        if (targetMask == 0 || (targetMask & ~topology.AllMask) != 0)
        {
            error = requestedSingleCcd ? "排除 CPU0 后没有可用逻辑处理器。" : "已保存的 CPU 亲和性掩码无效。";
            return false;
        }

        plan = new OfflineCpuSetPlan(targetMask, 0, requestedSingleCcd
            ? "目标游戏使用排除 CPU0 的软 CPU Sets 偏好。"
            : "目标游戏使用已保存的软 CPU Sets 偏好。");
        return true;
    }
}
