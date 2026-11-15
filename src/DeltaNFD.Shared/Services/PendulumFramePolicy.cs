namespace DeltaNFD.Services;

/// <summary>Shared preparation contract; a saved scan alone is not current readiness.</summary>
internal static class PendulumFramePolicy
{
    internal static uint Resolution(AppSettings settings) => settings.OptimalClockUseCustomResolution
        ? settings.OptimalClockCustomResolution100ns : settings.OptimalClockResolution100ns;
    internal static string CustomSource(PendulumGlobalStatus global) => global.ExperimentalWindows10
        ? global.Win10Dpc is not null && global.PlatformId.Length > 0 ? global.PlatformId + "/custom-v1" : ""
        : "win11-custom-v1/" + Environment.OSVersion.Version;
    internal static bool Prepared(PendulumGlobalStatus global, AppSettings settings)
    {
        if (!settings.OptimalClockUseCustomResolution) return AutomaticPrepared(global, settings);
        var source = CustomSource(global);
        return global.Ready && source.Length > 0 && settings.OptimalClockCustomVerifiedUtc is not null
            && settings.OptimalClockCustomPlatform == source && settings.OptimalClockCustomResolution100ns >= 5000
            && settings.OptimalClockCustomResolution100ns <= (global.ExperimentalWindows10 ? 10000u : 156250u);
    }
    internal static bool AutomaticPrepared(PendulumGlobalStatus global, AppSettings settings)
    {
        if (!global.Ready || settings.OptimalClockScanUtc is null) return false;
        var target = settings.OptimalClockResolution100ns;
        if (!global.ExperimentalWindows10)
            return settings.OptimalClockScanPlatform.Length == 0 && target is >= 5100 and <= 156250;
        if (global.PlatformId.Length == 0) return false;
        var profile = settings.OptimalClockScanPlatform;
        if (global.Win10Dpc is not null)
            return profile == global.PlatformId && target == DpcPendulumSource.Target100ns
                || profile == global.PlatformId + DpcPendulumSource.AlgorithmProfileSuffix && target is >= 5100 and <= 10000;
        return global.Win10Driver?.Ready == true && profile == global.PlatformId && target is >= 5000 and <= 10000;
    }
    internal static bool AutomaticOptimumPrepared(PendulumGlobalStatus global, AppSettings settings)
        => AutomaticPrepared(global, settings)
            && (settings.OptimalClockBestTypical100ns > 0 || settings.OptimalClockBestStable100ns > 0)
            && (!global.ExperimentalWindows10 || global.Win10Dpc is not null
                && settings.OptimalClockScanPlatform == global.PlatformId + DpcPendulumSource.AlgorithmProfileSuffix);
}
