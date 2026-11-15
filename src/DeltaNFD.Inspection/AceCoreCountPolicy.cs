using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

/// <summary>Incomplete CORE inventory stays explicit. Missing additional games cannot invalidate a complete non-excess count.</summary>
internal static class AceCoreCountPolicy
{
    internal static AceCoreCheckLevel Evaluate(int coreCount, int gameCount, int manualGameCount, bool coreCountIncomplete)
    {
        var excess = coreCount > gameCount;
        if (excess && manualGameCount > 0) return AceCoreCheckLevel.Abnormal;
        if (excess || coreCountIncomplete) return AceCoreCheckLevel.NeedsReview;
        return coreCount == 0 ? AceCoreCheckLevel.None : AceCoreCheckLevel.Normal;
    }
}
