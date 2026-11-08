namespace DeltaNFD.Services;

public sealed record TimerDeviceState(string InstanceId, int? ProblemCode)
{
    public bool Disabled => ProblemCode == 22;
}
public sealed record TimerRecoveryStatus
{
    public bool BcdReadable { get; init; }
    public string DynamicTick { get; init; } = "";
    public string PlatformTick { get; init; } = "";
    public bool DevicesReadable { get; init; }
    public List<TimerDeviceState> Devices { get; init; } = [];
    public string BootId { get; init; } = "";
    public string Error { get; init; } = "";
    public bool RecoveryIncomplete { get; init; }
    public bool PendingRestart { get; init; }
    public string Fingerprint { get; init; } = "";
    public bool HasOverride => IsEnabled(DynamicTick) || IsEnabled(PlatformTick);
    public bool NeedsAttention => HasOverride || RecoveryIncomplete || PendingRestart || !BcdReadable;
    public string Summary => PendingRestart ? "计时器启动配置已恢复，需重启电脑后重新检测。"
        : RecoveryIncomplete ? "上次计时器恢复未完成，请进入恢复页核对记录。"
        : HasOverride ? "发现计时器启动覆盖，请进入恢复页核对。"
        : !BcdReadable ? "计时器启动配置待核对：" + Error
        : "两项计时器启动覆盖均未开启。";
    public static bool IsEnabled(string value) => value.Trim().ToLowerInvariant() is "yes" or "true" or "1" or "是";
    public static bool IsKnown(string value) => value.Length == 0 || IsEnabled(value) || value.Trim().ToLowerInvariant() is "no" or "false" or "0" or "否";
}
public sealed record TimerRecoveryArgs(string Fingerprint, bool RestoreDisabledDevices);
public sealed record TimerRecoveryRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public TimerRecoveryStatus Before { get; init; } = new();
    public bool Completed { get; set; }
    public bool Changed { get; set; }
    public List<string> Steps { get; init; } = [];
}
