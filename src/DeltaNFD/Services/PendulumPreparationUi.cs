using DeltaNFD.Backends;
using DeltaNFD.Inspection;
using DeltaNFD.Services.Backends;

namespace DeltaNFD.Services;

internal static class PendulumPreparationUi
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static bool IsRunning => Gate.CurrentCount == 0;
    internal static string LastMessage { get; private set; } = "";
    internal static async Task<OperationResult> RunAsync(bool resumeOnly)
    {
        if (!await Gate.WaitAsync(0)) return OperationResult.Fail("钟摆驱动准备正在执行。");
        try
        {
            if (PageBackendSessions.SystemWriteBlocker is { } blocked) return OperationResult.Fail(blocked);
            await using var caller = new BackendCaller(BackendModules.System);
            var response = await caller.InvokeAsync(BackendCommands.SystemPendulumDriverPrepare,
                new PendulumPreparationArgs(resumeOnly, !resumeOnly, PendulumPreparationPackage.Id), null, CancellationToken.None);
            var result = response.Data is { } data ? BackendJson.Deserialize<OperationResult>(data) : OperationResult.Fail(response.Message);
            if (!result.Success || result.RequiresReboot) return result;
            var global = await PendulumGlobalInspection.ReadAsync();
            if (!global.Ready) return OperationResult.Fail(global.Message);
            var settings = AppSettingsStore.Read();
            if (!resumeOnly || settings.OptimalClockScanUtc is null || settings.OptimalClockScanPlatform != global.PlatformId)
            {
                var scan = await BackendProcmanService.Instance.ScanClockAsync(true, settings.OptimalClockPreferredScanMode, CancellationToken.None);
                if (!scan.Success) return OperationResult.Fail("驱动已就绪，但自动检测持有值未完成：" + scan.Message);
                return OperationResult.Ok("自研驱动已自动就绪，并已检测保存持有值；开启帧格后按原逻辑持有。" + scan.Message);
            }
            return result;
        }
        catch (Exception ex) { return OperationResult.Fail("自动准备未完成：" + ex.Message); }
        finally { Gate.Release(); }
    }
    internal static async Task ResumeOnStartupAsync()
    {
        try
        {
            if (!PendulumGlobalInspection.ExperimentalWindows10 || OfflineModeGuard.BlocksNormalAutomation) return;
            var record = await Task.Run(PendulumPreparationInspection.Read);
            if (record is null) return; // installing the app itself is never consent
            var driver = await Task.Run(PendulumWin10DriverInspection.Read);
            if (!driver.TestSigningEnabled) return; // never turn test mode back on automatically
            if (record.Stage == "Ready" && !driver.DriverRegistered) return; // respect explicit uninstall
            var global = await PendulumGlobalInspection.ReadAsync();
            var settings = AppSettingsStore.Read();
            if (record.Stage == "Ready" && driver.Ready && settings.OptimalClockScanUtc is not null && settings.OptimalClockScanPlatform == global.PlatformId) return;
            LastMessage = "正在按上次授权继续准备钟摆驱动与持有值，请稍候…";
            var result = await RunAsync(true);
            LastMessage = result.Message;
            if (result.Success) Log.Info("钟摆驱动自动续办：" + result.Message);
            else Log.Warn("钟摆驱动自动续办：" + result.Message);
        }
        catch (Exception ex) { LastMessage = "钟摆驱动授权记录待核对：" + ex.Message; Log.Warn(LastMessage); }
    }
}
