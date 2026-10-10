using DeltaNFD.Native;

namespace DeltaNFD.Services;

public enum OfflineModeStatus
{
    Off,
    Preparing,
    Ready,
    WaitingForGame,
    Applying,
    Kept,
    RestorePending,
    Failed,
}

public enum OfflineCpuSetApi
{
    Windows10Ids,
    Windows11Masks,
}

public enum OfflineCpuSetRecordStatus
{
    Pending,
    Applied,
    RestoreFailed,
}

/// <summary>进入脱机模式前需要保留、退出时恢复的普通模式偏好。</summary>
public sealed class OfflineModePreferences
{
    public bool FrameModeWasActive { get; set; }
    public bool DwmRestartOnGameStart { get; set; }
    public bool FramePowerLockEnabled { get; set; }
    public string FramePowerLockTargetGuid { get; set; } = "";
    public bool GamePriorityEnabled { get; set; }
    public bool GameAffinityRuleEnabled { get; set; }
    public ulong GameAffinityRuleMask { get; set; }
    public bool SingleCcdExcludeCpu0Enabled { get; set; }
    public bool DualCcdImmediateEnabled { get; set; }
    public bool DualCcdArmed { get; set; }
    public bool DualCcdFrameEnabled { get; set; }
    public int DualCcdGameCcdIndex { get; set; }
    public bool AutoStartTaskWasPresent { get; set; }
    public bool AutoStartEnabled { get; set; }
    public bool FrameAutostartTaskWasPresent { get; set; }
    public bool FrameAutostartEnabled { get; set; }
    public bool CloseToTrayEnabled { get; set; }

    public OfflineModePreferences Copy() => (OfflineModePreferences)MemberwiseClone();
}

/// <summary>一个进程在脱机助手中修改的默认 CPU Set 原值与身份。</summary>
public sealed class OfflineCpuSetChange
{
    public int ProcessId { get; set; }
    public long StartTimeUtcTicks { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string Purpose { get; set; } = "";
    public OfflineCpuSetApi Api { get; set; }
    public bool OriginalAssignmentWasSet { get; set; }
    public List<uint> OriginalCpuSetIds { get; set; } = [];
    public List<CpuSetGroupMask> OriginalMasks { get; set; } = [];
    public bool AppliedAssignmentWasSet { get; set; }
    public List<uint> AppliedCpuSetIds { get; set; } = [];
    public List<CpuSetGroupMask> AppliedMasks { get; set; } = [];
    public OfflineCpuSetRecordStatus Status { get; set; }
    public string Error { get; set; } = "";

    public OfflineCpuSetChange Copy() => new()
    {
        ProcessId = ProcessId,
        StartTimeUtcTicks = StartTimeUtcTicks,
        ExecutablePath = ExecutablePath,
        Purpose = Purpose,
        Api = Api,
        OriginalAssignmentWasSet = OriginalAssignmentWasSet,
        OriginalCpuSetIds = [.. OriginalCpuSetIds],
        OriginalMasks = [.. OriginalMasks],
        AppliedAssignmentWasSet = AppliedAssignmentWasSet,
        AppliedCpuSetIds = [.. AppliedCpuSetIds],
        AppliedMasks = [.. AppliedMasks],
        Status = Status,
        Error = Error,
    };
}

/// <summary>脱机模式状态及其独立的偏好/CPU Sets 恢复记录。</summary>
public sealed class OfflineModeState
{
    public int SchemaVersion { get; set; } = 1;
    public bool OfflineModeEnabled { get; set; }
    public OfflineModeStatus Status { get; set; } = OfflineModeStatus.Off;
    public bool CancelRequested { get; set; }
    public OfflineModePreferences? SavedPreferences { get; set; }
    public OfflineModePreferences? PreviousNormalPreferences { get; set; }
    public List<OfflineCpuSetChange> CpuSetChanges { get; set; } = [];
    public int? HelperProcessId { get; set; }
    public long? HelperStartTimeUtcTicks { get; set; }
    public bool HelperOperationCompleted { get; set; }
    public Guid HelperRunId { get; set; }
    public OfflineModeStatus HelperCompletionStatus { get; set; } = OfflineModeStatus.Ready;
    public DateTimeOffset UpdatedUtc { get; set; }
    public string Message { get; set; } = "";

    /// <summary>状态不明或部分转换时也阻止普通自动功能重启。</summary>
    public bool BlocksNormalAutomation => OfflineModeEnabled || CancelRequested || SavedPreferences is not null ||
        CpuSetChanges.Count != 0 || HelperProcessId.HasValue ||
        Status is OfflineModeStatus.Preparing or OfflineModeStatus.WaitingForGame or OfflineModeStatus.Applying or
            OfflineModeStatus.RestorePending or OfflineModeStatus.Failed;

    public OfflineModeState Copy() => new()
    {
        SchemaVersion = SchemaVersion,
        OfflineModeEnabled = OfflineModeEnabled,
        Status = Status,
        CancelRequested = CancelRequested,
        SavedPreferences = SavedPreferences?.Copy(),
        PreviousNormalPreferences = PreviousNormalPreferences?.Copy(),
        CpuSetChanges = CpuSetChanges.Select(x => x.Copy()).ToList(),
        HelperProcessId = HelperProcessId,
        HelperStartTimeUtcTicks = HelperStartTimeUtcTicks,
        HelperOperationCompleted = HelperOperationCompleted,
        HelperRunId = HelperRunId,
        HelperCompletionStatus = HelperCompletionStatus,
        UpdatedUtc = UpdatedUtc,
        Message = Message,
    };
}
