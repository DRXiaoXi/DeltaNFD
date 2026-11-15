namespace DeltaNFD.Services;

public sealed record NvidiaGpuShareInfo
{
    public bool Supported { get; init; }
    public bool HagsEnabled { get; init; } = true;
    public string HardwareKey { get; init; } = "";
    public int Current { get; init; }
    public int Default { get; init; }
    public int Minimum { get; init; }
    public int Maximum { get; init; }
    public int Step { get; init; }
    public uint PresetMask { get; init; }
    public bool Allows(int percent) => Supported && percent is >= 0 and <= 100 &&
        ((percent switch { 0 => PresetMask & 1, 10 => PresetMask & 2, 20 => PresetMask & 4, 50 => PresetMask & 8, _ => 0 }) != 0 ||
        (Step > 0 && Minimum >= 0 && Maximum <= 100 && Minimum <= Maximum && percent >= Minimum && percent <= Maximum && (percent - Minimum) % Step == 0));
}

public sealed record FrameNvidiaGpuShareSnapshot
{
    public int ProtocolVersion { get; init; }
    public bool Maintaining { get; init; }
    public bool HasError { get; init; }
    public bool RestorePending { get; init; }
    public NvidiaGpuShareInfo? Info { get; init; }
    public string Message { get; init; } = "尚未检测 NVIDIA 前后台比例";
    public DateTimeOffset UpdatedUtc { get; init; }
}
