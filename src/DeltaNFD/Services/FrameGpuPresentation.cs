using DeltaNFD.Backends;

namespace DeltaNFD.Services;

internal static class FrameGpuPresentation
{
    private static bool ParentFresh(ProcmanStatusSnapshot? parent, DateTimeOffset now) => parent is not null &&
        parent.Module == BackendModules.Procman && parent.UpdatedUtc != default && parent.UpdatedUtc <= now.AddSeconds(2) &&
        now - parent.UpdatedUtc <= TimeSpan.FromSeconds(10);
    private static bool Fresh(ProcmanStatusSnapshot? parent, DateTimeOffset child, DateTimeOffset now) =>
        parent is not null && parent.Module == BackendModules.Procman && parent.UpdatedUtc != default &&
        parent.UpdatedUtc <= now.AddSeconds(2) && now - parent.UpdatedUtc <= TimeSpan.FromSeconds(10) &&
        child != default && child <= now.AddSeconds(2) && now - child <= TimeSpan.FromSeconds(10);
    internal static string Process(bool enabled, bool active, ProcmanStatusSnapshot? parent, DateTimeOffset now, string error)
    {
        var state = parent?.FramePriority;
        var pending = state is { GpuBackupCount: > 0 } && (state.RestorePending || !enabled || !active);
        if (!enabled || !active) return pending ? "GPU 调度优先级恢复仍待核对" : enabled ? "已选择 · 开启帧格并运行目标游戏后生效" : "未启用";
        if (error.Length == 0 && ParentFresh(parent, now) && state?.GpuProtocolVersion != 1) return "当前后端不支持帧格 GPU 调度，请使用完整新版程序";
        if (error.Length > 0 || !Fresh(parent, state?.UpdatedUtc ?? default, now)) return "等待后端更新 GPU 调度状态，未确认生效";
        if (state!.GpuProtocolVersion != 1) return "当前后端不支持帧格 GPU 调度，请使用完整新版程序";
        if (state.HasError || pending) return state.Message;
        return parent!.RulesEngineRunning && state.Maintaining && state.GpuAppliedCount > 0
            ? $"GPU 调度优先级已回读：{state.GpuAppliedCount}/{state.InstanceCount} 个目标实例" : "等待目标游戏启动或后端核对 GPU 调度";
    }
    internal static string Nvidia(bool enabled, bool active, ProcmanStatusSnapshot? parent, DateTimeOffset now, string error)
    {
        var state = parent?.FrameNvidiaGpuShare;
        if (state is not { RestorePending: true } && (!enabled || !active)) return enabled ? "已选择 · 开启帧格并运行目标游戏后检测支持并应用" : "未启用";
        if (error.Length == 0 && ParentFresh(parent, now) && state?.ProtocolVersion != 1) return "当前后端不支持 NVIDIA 比例，请使用完整新版程序";
        if (error.Length > 0 || !Fresh(parent, state?.UpdatedUtc ?? default, now)) return state is { RestorePending: true }
            ? "NVIDIA 比例恢复仍待核对；等待后端更新" : "等待后端核对 NVIDIA 比例，未确认生效";
        if (state!.ProtocolVersion != 1) return "当前后端不支持 NVIDIA 比例，请使用完整新版程序";
        if (state.Maintaining && !parent!.RulesEngineRunning) return "监控已停止，NVIDIA 比例恢复仍待核对";
        return state.Message;
    }
}
