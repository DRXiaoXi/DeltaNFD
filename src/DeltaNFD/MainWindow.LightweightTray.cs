using DeltaNFD.Native;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml.Media;

namespace DeltaNFD;

public sealed partial class MainWindow
{
    private bool _startupChecksCompletedForTray;
    private bool _lightweightTrayRequested;
    private bool _lightweightTrayTransition;

    private bool CanReleaseWinUi()
    {
        if (!_startupChecksCompletedForTray || App.WindowsSessionEnding || App.PendingFileFallback is not null || AppDataPaths.MigrationWarning is not null
            || OfflineModeGuard.BlocksNormalAutomation || SingleInstanceGuard.DeploymentInProgress()
            || _gameVersionChecking || UpdateViewModel.HasCurrentWork || _mcpOperations?.HasActive == true
            || BackendOperationGate.HasStartingBackends || BackendSessions.Snapshot().Any(c => c.HasInvocationInFlight || c.RequiresAttention)
            || BackendUnconfirmedStore.PendingModules().Count > 0
            || ServiceLocator.PluginRuntime.HasInFlightOperations || ServiceLocator.PluginRuntime.RunningPluginIds.Count > 0)
            return false;

        // Keep user confirmations and pickers owned by the current window alive.
        if (ContentFrame.XamlRoot is not { } root || VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count != 0)
            return false;
        if (BackendSessions.Snapshot().Any(c => c.ModuleId == DeltaNFD.Backends.BackendModules.Procman && !c.ExitConfirmed)
            && !BackendProcmanService.Instance.IsConfirmedIdleForTray) return false;

        var settings = AppSettingsStore.ReadRequired();
        return settings.CloseToTrayEnabled && !HasBackgroundWorkToPreserve(settings)
            && !settings.GamePriorityEnabled && !settings.RuntimeGuardEnabled && !settings.OptimalClockEnabled
            && !settings.FramePowerRestorePending && !settings.FrameStartupRecoveryPending && !settings.TempSpoofRestorePending
            && string.IsNullOrWhiteSpace(settings.PendingUpdateVersion)
            && !File.Exists(Path.Combine(AppDataPaths.Root, "frame_power_scheme_snapshot.json"))
            && !File.Exists(Path.Combine(AppDataPaths.Root, "defender-session.json"));
    }

    private async void HideToTray()
    {
        if (_exitRequested || _pluginExitInProgress || _deploymentInProgress || _factoryResetInProgress) return;
        if (_trayIcon is null || !_trayIcon.Show())
        {
            Log.Error("托盘：图标注册失败，无法驻留托盘");
            return;
        }

        var keepVisible = false;
        var navEnabled = NavView.IsEnabled;
        var frameEnabled = ContentFrame.IsEnabled;
        using var stop = new ExitOperationScope();
        try
        {
            if (CanReleaseWinUi())
            {
                _lightweightTrayTransition = _pluginExitInProgress = _lightweightTrayRequested = true;
                NavView.IsEnabled = ContentFrame.IsEnabled = false;
                stop.Hold(ServiceLocator.PluginRuntime.BlockNewOperations());
                stop.Hold(BackendOperationGate.BlockNewOperations());
                stop.Hold(ServiceLocator.PauseGameMonitorConnections());

                // A passive handoff must not cancel a running command or perform recovery on its own.
                if (CanReleaseWinUi())
                {
                    var failures = await BackendLifecycle.PrepareHandoverAsync("转入轻量托盘", idleTrayOnly: true);
                    if (failures.Count != 0)
                    {
                        keepVisible = true;
                        Log.Warn(BackendLifecycle.DescribeBlocker(failures, "转入轻量托盘"));
                    }
                    else if (_lightweightTrayRequested && CanReleaseWinUi())
                    {
                        using var handoff = await LightweightTray.PrepareAsync();
                        if (handoff is not null && _lightweightTrayRequested && CanReleaseWinUi())
                        {
                            // Keep admission closed through exit, then let the new native process take the locks.
                            handoff.Commit();
                            stop.Commit();
                            _committedExitOperations = stop;
                            Log.Info("托盘：后端已排空，退出 WinUI 并交给原生托盘。");
                            ExitAfterPreparedHandover();
                            return;
                        }
                    }
                }
            }
        }
        catch (Exception ex) { Log.Warn("轻量托盘未完成，保留现有进程：" + ex.Message); }
        finally
        {
            if (_lightweightTrayTransition)
            {
                _lightweightTrayTransition = false;
                keepVisible |= !_lightweightTrayRequested;
                _lightweightTrayRequested = false;
                _pluginExitInProgress = false;
                if (!_exitRequested)
                {
                    NavView.IsEnabled = navEnabled;
                    ContentFrame.IsEnabled = frameEnabled;
                }
            }
        }

        if (!keepVisible && !_exitRequested && _trayIcon?.Show() == true) AppWindow.Hide();
    }
}
