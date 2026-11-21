namespace DeltaNFD.Services;

internal static class FrameSessionState
{
    internal static void BeginLaunch(AppSettings settings)
    {
        if (!settings.FrameModeActive) return;
        settings.FrameResumeOnAppStart ??= true;
        settings.FrameStartupRecoveryPending = true;
        settings.FrameModeActive = false;
    }

    internal static void BeginShutdown(AppSettings settings)
    {
        if (!settings.FrameModeActive) return;
        settings.FrameStartupRecoveryPending = true;
        settings.FrameModeActive = false;
    }

    internal static void CompleteExit(AppSettings settings) => settings.FrameModeActive = false;

    internal static bool CanResume(AppSettings settings) => settings.FrameResumeOnAppStart == true
        && !settings.FrameStartupRecoveryPending && !settings.FrameModeActive;

    internal static void UserSetActive(AppSettings settings, bool active)
    {
        settings.FrameModeActive = active;
        settings.FrameResumeOnAppStart = active;
    }
}
