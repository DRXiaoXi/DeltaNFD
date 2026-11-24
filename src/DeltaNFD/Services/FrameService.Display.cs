using System.Globalization;
using DeltaNFD.Native;
using DeltaNFD.Services.Backends;

namespace DeltaNFD.Services;

public sealed partial class FrameService
{
    private bool _startingFrame;

    internal (bool Enabled, string Width, string Height) DisplayResolutionOptions { get; private set; } = (false, "", "");

    internal bool DisplayResolutionOptionsEditable =>
        !_frameModeActive && !_startingFrame && !OfflineModeGuard.BlocksNormalAutomation;

    internal void ConfigureDisplayResolution(bool enabled, string width, string height)
    {
        if (DisplayResolutionOptionsEditable) DisplayResolutionOptions = (enabled, width, height);
    }

    private static bool TryReadResolutionPixels(string input, out int value) =>
        int.TryParse(input.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    private static async Task<FrameDisplayReply> ActivateDisplayResolutionAsync(int width, int height,
        Func<int, int, long, Task<bool>> confirm)
    {
        FrameDisplayReply preview;
        try
        {
            using var operation = BackendOperationGate.EnterStart();
            preview = await FrameDisplayService.PreviewAsync(width, height);
        }
        catch (InvalidOperationException ex)
        {
            return new(false, FrameDisplayState.Idle, "分辨率切换未开始。" + ex.Message);
        }
        if (!preview.Success || preview.State != FrameDisplayState.Preview) return preview;

        var keep = false;
        try { keep = await confirm(width, height, preview.Deadline); }
        catch (Exception ex) { Log.Warn("分辨率确认框未能显示：" + ex.Message); }
        return keep ? await FrameDisplayService.KeepAsync(preview.Deadline)
            : await FrameDisplayService.CancelAsync(preview.Deadline);
    }

    private static OperationResult AddDisplayActivationResult(OperationResult activation, FrameDisplayReply display) => new()
    {
        Success = activation.Success,
        IsPartial = activation.IsPartial || display.State != FrameDisplayState.Kept,
        Message = activation.Message + Environment.NewLine + display.Message,
        IsSkipped = activation.IsSkipped,
        OutcomeUnknown = activation.OutcomeUnknown || display.State == FrameDisplayState.RestorePending,
        RequiresLogoff = activation.RequiresLogoff,
        RequiresReboot = activation.RequiresReboot,
        RequiresDisplayConfirm = activation.RequiresDisplayConfirm,
        SuggestShaderMaintenance = activation.SuggestShaderMaintenance,
        DiagnosticsDirectory = activation.DiagnosticsDirectory,
    };
}
