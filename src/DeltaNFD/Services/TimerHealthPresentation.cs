namespace DeltaNFD.Services;

/// <summary>Shared, query-only presentation for the home warning and the recovery page.</summary>
internal static class TimerHealthPresentation
{
    internal sealed record Notice(bool IsOpen, bool IsError, string Title, string Message);

    public static Notice Create(TimerRecoveryStatus status)
    {
        var enabled = new List<string>();
        // Untrusted or incomplete BCDEdit output must not be presented as confirmed configuration.
        if (status.BcdReadable)
        {
            if (TimerRecoveryStatus.IsEnabled(status.PlatformTick)) enabled.Add("useplatformtick（强制平台计时）");
            if (TimerRecoveryStatus.IsEnabled(status.DynamicTick)) enabled.Add("disabledynamictick（禁用动态计时）");
        }
        var messages = new List<string>();
        if (enabled.Count > 0)
            messages.Add("检测到已开启：" + string.Join("、", enabled) + "。这些启动覆盖可能影响游戏帧率与流畅度；请进入恢复页关闭（移除覆盖，恢复 Windows 默认）。");
        if (!status.BcdReadable) messages.Add("useplatformtick 和 disabledynamictick 的状态尚未确认，不能判定为开启或关闭。");
        if (status.RecoveryIncomplete) messages.Add("上次恢复记录未完成，请进入恢复页核对。");
        if (status.PendingRestart) messages.Add("恢复记录提示仍需手动重启电脑；当前检测结果以上述配置为准。");
        if (status.Error.Length > 0) messages.Add("检测说明：" + status.Error);
        if (enabled.Count > 0)
            messages.Add("恢复完成后需要手动重启电脑；工具不会自动重启。");
        var attention = enabled.Count > 0 || !status.BcdReadable
            || status.RecoveryIncomplete || status.PendingRestart;
        return new(attention, enabled.Count > 0 || status.RecoveryIncomplete,
            enabled.Count > 0 ? "启动计时器选项已开启" : status.RecoveryIncomplete ? "计时器恢复记录待核对"
            : !status.BcdReadable ? "计时器状态待核对"
            : status.PendingRestart ? "恢复后请重启电脑" : "计时器配置检测正常",
            messages.Count > 0 ? string.Join("\n", messages) : "两项启动计时器选项均未开启。");
    }
}
