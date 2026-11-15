namespace DeltaNFD.Services;

public sealed record PendulumRegistryValue(bool Exists, int Value);

public sealed record PendulumGlobalRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public PendulumRegistryValue Original { get; init; } = new(false, 0);
    public string WrittenBootId { get; init; } = "";
    public bool TargetEnabled { get; init; }
    public bool Completed { get; set; }
}

public sealed record PendulumGlobalStatus
{
    public PendulumWin10DriverStatus? Win10Driver { get; init; }
    public DpcPendulumStatus? Win10Dpc { get; init; }
    public bool ExperimentalWindows10 { get; init; }
    public string PlatformId { get; init; } = "";
    public bool Supported { get; init; }
    public bool Readable { get; init; }
    public bool Configured { get; init; }
    public bool BootConfirmed { get; init; }
    public bool NeedsRegistration { get; init; }
    public bool PendingRecovery { get; init; }
    public bool RequiresRestart => !ExperimentalWindows10 && Configured && !BootConfirmed;
    public bool Ready => Supported && Readable && (ExperimentalWindows10
        ? Win10Dpc?.FileVerified == true || Win10Driver?.Ready == true : Configured && BootConfirmed) && !PendingRecovery;
    public string BootId { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public string Message { get; init; } = "尚未核对全局生效精度。";
}

/// <summary>Read-only prerequisites. A queued BCD change is not current-boot test mode.</summary>
public sealed record PendulumWin10DriverStatus
{
    public bool Readable { get; init; }
    public bool TestSigningEnabled { get; init; }
    public bool DriverRegistered { get; init; }
    public bool DriverRunning { get; init; }
    public bool DriverIdentityVerified { get; init; }
    public bool Ready => Readable && TestSigningEnabled && DriverRegistered && DriverRunning && DriverIdentityVerified;
    public string Message { get; init; } = "尚未核对测试签名模式和自研驱动。";
}

public sealed record PendulumGlobalArgs(bool Enabled, string Fingerprint);

public sealed record PendulumClockNotice
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset FirstSeenUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public DateTimeOffset? RecoveredUtc { get; init; }
    public string Kind { get; init; } = "";
    public uint Requested100ns { get; init; }
    public uint Observed100ns { get; init; }
    public string Message { get; init; } = "";
}
