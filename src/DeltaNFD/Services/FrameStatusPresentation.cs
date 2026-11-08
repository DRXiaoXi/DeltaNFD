namespace DeltaNFD.Services;

internal static class FrameStatusPresentation
{
    internal static string Priority(bool selected, bool active, ProcmanStatusSnapshot? snapshot, DateTimeOffset now, string error)
    {
        var state = snapshot?.FramePriority;
        var pending = state is { RestorePending: true } || state is { BackupCount: > 0 } && (!selected || !active);
        if (error.Length > 0) return pending ? "三角洲优先级恢复仍待核对；后端连接状态未知" : "优先级后端状态待确认，未确认锁定生效";
        if (!pending && (!selected || !active)) return selected ? "已选择 · 等待开启一键帧格模式" : "未启用";
        if (snapshot is null || snapshot.Module != DeltaNFD.Backends.BackendModules.Procman || snapshot.UpdatedUtc == default
            || snapshot.UpdatedUtc > now.AddSeconds(2) || now - snapshot.UpdatedUtc > TimeSpan.FromSeconds(10)
            || state is null || state.UpdatedUtc == default
            || state.UpdatedUtc > now.AddSeconds(2) || now - state.UpdatedUtc > TimeSpan.FromSeconds(10))
            return pending ? "三角洲优先级恢复仍待核对；等待后端更新" : "等待后端核对 CPU、IO 和内存优先级，未确认生效";
        if (pending && !state.RestorePending) return "正在等待后端还原三角洲优先级，尚未确认完成";
        if (!snapshot!.RulesEngineRunning && state.Maintaining) return "监控已停止，三角洲优先级恢复仍待核对";
        return state.Message;
    }
    internal static string TimerIgnore(bool selected, bool active, ProcmanStatusSnapshot? snapshot, DateTimeOffset now, string error)
    {
        var state = snapshot?.FrameTimerIgnore;
        var pending = state is { RestorePending: true } || state is { BackupCount: > 0 } && (!selected || !active);
        var fresh = snapshot is not null && snapshot.Module == DeltaNFD.Backends.BackendModules.Procman
            && snapshot.UpdatedUtc != default && snapshot.UpdatedUtc <= now.AddSeconds(2)
            && now - snapshot.UpdatedUtc <= TimeSpan.FromSeconds(10)
            && state is not null && state.UpdatedUtc != default && state.UpdatedUtc <= now.AddSeconds(2)
            && now - state.UpdatedUtc <= TimeSpan.FromSeconds(10);
        var reading = Reading(fresh && error.Length == 0 ? state!.SystemCurrentMilliseconds : null);
        string WithReading(string message) => message + reading;
        if (error.Length > 0)
            return WithReading(pending ? "三角洲定时器请求忽略状态恢复仍待核对；后端连接状态未知" : "进程管理后端状态待确认，未确认该功能生效");
        if (!pending && fresh && state is { Supported: false })
            return WithReading(selected ? "本系统不支持该功能（仅 Windows 11 起）" : "未启用（本系统不支持该功能）");
        if (!pending && (!selected || !active)) return WithReading(selected ? "已选择 · 等待开启一键帧格模式" : "未启用");
        if (!fresh)
            return WithReading(pending ? "三角洲定时器请求忽略状态恢复仍待核对；等待后端更新" : "等待后端核对三角洲定时器请求忽略状态，未确认生效");
        if (pending && !state!.RestorePending) return WithReading("正在等待后端恢复三角洲定时器请求忽略状态，尚未确认完成");
        if (!snapshot!.RulesEngineRunning && state!.Maintaining) return WithReading("监控已停止，三角洲定时器请求忽略状态恢复仍待核对");
        if (state!.InstanceCount > 0 && !state.RestorePending)
        {
            if (state.IgnoreApplied)
                return WithReading("已忽略三角洲的精度请求，三角洲使用系统当前最佳时钟");
            if (state.LegacyGlobalReachable == false)
                return WithReading("已跳过忽略位：本系统按进程计时，忽略会让三角洲回落到 15.625ms；三角洲保留自身精度");
            if (state.LegacyGlobalReachable is null)
                return WithReading("系统时钟可达性未确认，未施加忽略位");
        }
        return WithReading(state!.Message);
    }

    /// <summary>只读展示：当前系统定时器分辨率实际读数（不改写、不申请、不释放）。</summary>
    private static string Reading(double? milliseconds) => milliseconds is { } value && double.IsFinite(value) && value > 0
        ? $"（当前系统定时器读数 {value:0.###} ms）" : "（当前系统定时器读数不可用）";

    internal static string Dwm(bool selected, bool active, CpuFrameStatusSnapshot? snapshot, DateTimeOffset now, string error)
    {
        if (!selected) return "未启用";
        if (!active) return "已选择 · 开启一键帧格模式后持续监测";
        if (!GameObservationState.Fresh(snapshot, now) || !snapshot!.MonitorRunning || error.Length > 0)
            return "DWM 后端状态待确认，未确认守护生效";
        var dwm = snapshot.Dwm;
        if (dwm.UpdatedUtc == default || dwm.UpdatedUtc > now.AddSeconds(2) || now - dwm.UpdatedUtc > TimeSpan.FromSeconds(10))
            return "DWM 采样状态待确认，等待后端更新";
        return dwm.Message + (dwm.CpuCorePercent is { } cpu ? $" · CPU {cpu:F1}%（单核口径）" : "")
            + (dwm.WorkingSetMiB is { } memory ? $" · 工作集 {memory:F0} MiB" : "");
    }

    internal static string Execution(CpuFrameStatusSnapshot? snapshot, DateTimeOffset now, string error) =>
        GameObservationState.Fresh(snapshot, now) && snapshot!.MonitorRunning ? "帧格监控运行中"
        : error.Length > 0 ? "帧格已配置 · 自动执行未启动：" + (error.Length > 160 ? error[..160] + "…" : error)
        : "帧格已配置 · 自动执行尚未确认";
    internal static string Game(bool? running, string target) => running switch
    {
        true => $" · {target}运行中", false => $" · 等待 {target} 启动", _ => " · 游戏运行状态待确认",
    };
    internal static string Power(bool enabled, string target, CpuFrameStatusSnapshot? snapshot, DateTimeOffset now) =>
        !enabled ? "" : GameObservationState.Fresh(snapshot, now) && snapshot!.PowerLockActive
            ? string.IsNullOrWhiteSpace(target) ? " · 电源计划锁定已生效" : " · 电源计划锁定已生效（指定计划）"
            : " · 电源锁定已配置，尚未确认生效";
}
