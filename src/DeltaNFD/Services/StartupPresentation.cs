namespace DeltaNFD.Services;

/// <summary>Only a marked logon launch may hide the window. Manual launches always reveal it.</summary>
internal sealed class StartupPresentation
{
    internal const string LogonArgument = "--autostart";
    private TaskCompletionSource? _visible;
    internal bool IsHidden => _visible is { Task.IsCompleted: false };

    internal static bool IsLogonLaunch(IReadOnlyList<string> commandLine) =>
        commandLine.Count == 2 && commandLine[1] == LogonArgument;

    internal static bool ShouldStartHidden(IReadOnlyList<string> commandLine, bool silentEnabled, bool blocked) =>
        IsLogonLaunch(commandLine) && silentEnabled && !blocked;

    internal void Present(bool silent, Func<bool> ensureTray, Action activate, Action hide)
    {
        // Register the escape route before hiding, including when CloseToTray is disabled.
        var hideWindow = false;
        try { hideWindow = silent && ensureTray(); }
        catch (Exception ex) { Log.Warn("静默启动：托盘不可用，改为显示窗口：" + ex.Message); }
        if (hideWindow) _visible = new(TaskCreationOptions.RunContinuationsAsynchronously);
        activate();
        if (hideWindow && IsHidden) hide();
    }

    // Optional startup dialogs wait for a tray click/manual launch; recovery alerts can reveal immediately.
    internal Task WaitForVisibleAsync() => _visible?.Task ?? Task.CompletedTask;
    internal void Reveal() => _visible?.TrySetResult();
}
