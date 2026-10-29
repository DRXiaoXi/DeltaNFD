namespace DeltaNFD.Services;

internal static class FrameStatusPresentation
{
    internal static string Timer(bool selected, bool active, CpuFrameStatusSnapshot? snapshot, DateTimeOffset now, string error)
    {
        var timer = snapshot?.Timer;
        var pending = timer is { ReleasePending: true } or { OwnershipUnknown: true }
            || timer?.RequestHeld == true && (!selected || !active)
            || snapshot?.TimerActive == true && (timer is null || timer.UpdatedUtc == default);
        if (error.Length > 0)
            return pending ? "计时器释放仍待核对；后端连接状态未知" : "计时器后端状态待确认，未确认生效或请求已释放";
        if (!pending && (!selected || !active)) return selected ? "已选择 · 等待开启帧格，尚未持有计时器请求" : "未启用";
        if (!GameObservationState.Fresh(snapshot, now) || error.Length > 0)
            return pending ? "计时器释放仍待核对；后端连接状态未知" : "计时器后端状态待确认，未确认精度维持生效";
        if (timer is null || timer.UpdatedUtc == default || timer.UpdatedUtc > now.AddSeconds(2) || now - timer.UpdatedUtc > TimeSpan.FromSeconds(10))
            return pending ? "计时器释放结果待核对；状态尚未更新" : "等待后端核对计时器请求与实际读数";
        if (pending && !timer.ReleasePending && !timer.OwnershipUnknown)
            return "选项已关闭，正在等待后端释放计时器请求";
        if (timer.RequestHeld && !snapshot!.MonitorRunning && !timer.ReleasePending)
            return "监控已停止，计时器仍持有请求，释放待核对";
        return timer.Message;
    }

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
