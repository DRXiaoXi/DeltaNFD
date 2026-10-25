namespace DeltaNFD.Services;

internal static class FrameStatusPresentation
{
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
