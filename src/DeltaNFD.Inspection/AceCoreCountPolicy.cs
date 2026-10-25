using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

/// <summary>User-defined count policy; never grants deletion authority or changes hash associations.</summary>
internal static class AceCoreCountPolicy
{
    internal static AceCoreCheckLevel Evaluate(int coreCount, int gameCount, int manualGameCount, bool needsReview)
    {
        var excess = coreCount > gameCount;
        if (excess && manualGameCount > 0) return AceCoreCheckLevel.Abnormal;
        if (excess || needsReview) return AceCoreCheckLevel.NeedsReview;
        return coreCount == 0 ? AceCoreCheckLevel.None : AceCoreCheckLevel.Normal;
    }
}
