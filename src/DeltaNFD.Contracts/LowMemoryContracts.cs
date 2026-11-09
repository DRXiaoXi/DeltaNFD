using System.Security.Cryptography;
using System.Text;

namespace DeltaNFD.Services;

public enum LowMemoryAction { SysMain, MemoryCompression, PageCombining, Prefetch }
public sealed record LowMemoryApplyArgs(LowMemoryAction Action, string Fingerprint);
public sealed record LowMemoryStatus(ulong? TotalBytes, int? SysMainStart = null, bool? SysMainRunning = null,
    bool? CompressionEnabled = null, bool? CombiningEnabled = null, bool? PrefetchDisabled = null, string Error = "")
{
    public const ulong LimitBytes = 16UL * 1024 * 1024 * 1024;
    public bool IsLowCapacity => TotalBytes is > 0 and <= LimitBytes;
    public bool CapacityKnown => TotalBytes is > 0;
    public bool Complete => CapacityKnown && (!IsLowCapacity || Error.Length == 0
        && SysMainStart is 2 or 3 or 4 && SysMainRunning.HasValue && CompressionEnabled.HasValue
        && CombiningEnabled.HasValue && PrefetchDisabled.HasValue);
    public bool? Needs(LowMemoryAction action) => action switch
    {
        LowMemoryAction.SysMain => SysMainStart == 4 || SysMainRunning == false ? true
            : SysMainStart is 2 or 3 && SysMainRunning == true ? false : null,
        LowMemoryAction.MemoryCompression => CompressionEnabled is { } compression ? !compression : null,
        LowMemoryAction.PageCombining => CombiningEnabled is { } combining ? !combining : null,
        LowMemoryAction.Prefetch => PrefetchDisabled is { } prefetch ? !prefetch : null,
        _ => null,
    };
    public bool NeedsAttention => IsLowCapacity && Enum.GetValues<LowMemoryAction>().Any(a => Needs(a) == true);
    public bool CanCheckTarget(LowMemoryAction action) => Complete || action == LowMemoryAction.SysMain
        && SysMainStart is 2 or 3 or 4 && SysMainRunning.HasValue;
    public bool CanRepairSysMain => IsLowCapacity && CanCheckTarget(LowMemoryAction.SysMain) && Needs(LowMemoryAction.SysMain) == true;
    public bool Ready => IsLowCapacity && Complete && !NeedsAttention;
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{TotalBytes}|{SysMainStart}|{SysMainRunning}|{CompressionEnabled}|{CombiningEnabled}|{PrefetchDisabled}|{Error}")));
}
public static class LowMemoryPolicy
{
    public static bool ShowUpTweak(SystemTweak tweak, ulong? totalBytes) =>
        tweak is not (SystemTweak.HardwareGpuScheduling or SystemTweak.SysMainMemorySupport)
        && (!(totalBytes is > 0 and <= LowMemoryStatus.LimitBytes)
            || tweak is not (SystemTweak.MemoryCompression or SystemTweak.PageCombining));
}
