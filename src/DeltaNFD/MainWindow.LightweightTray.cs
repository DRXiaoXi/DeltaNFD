using DeltaNFD.Native;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace DeltaNFD;

public sealed partial class MainWindow
{
    private bool _startupChecksCompletedForTray;
    private bool _lightweightTrayRequested;
    private bool _lightweightTrayTransition;
    private DispatcherQueueTimer? _trayWorkingSetTimer;

    private bool HasInFlightWinUiWork() => _gameVersionChecking || UpdateViewModel.HasCurrentWork || _mcpOperations?.HasActive == true
        || BackendOperationGate.HasStartingBackends || BackendSessions.Snapshot().Any(c => c.HasInvocationInFlight || c.RequiresAttention)
        || BackendUnconfirmedStore.PendingModules().Count > 0
        || ServiceLocator.PluginRuntime.HasInFlightOperations || ServiceLocator.PluginRuntime.RunningPluginIds.Count > 0;

    private bool HasWindowPopup() => ContentFrame.XamlRoot is not { } root
        || VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count != 0;

    private bool CanReleaseWinUi()
    {
        if (!_startupChecksCompletedForTray || App.WindowsSessionEnding || App.PendingFileFallback is not null || AppDataPaths.MigrationWarning is not null
            || OfflineModeGuard.BlocksNormalAutomation || SingleInstanceGuard.DeploymentInProgress()
            || HasInFlightWinUiWork() || HasWindowPopup())
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

        if (!keepVisible && !_exitRequested && _trayIcon?.Show() == true)
        {
            FlushWindowPlacement();
            HideWinUiToTray();
        }
    }

    private void HideWinUiToTray()
    {
        var wasVisible = AppWindow.IsVisible;
        AppWindow.Hide();
        if (!wasVisible || AppWindow.IsVisible) return;

        CancelTrayWorkingSetTrim();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMinutes(1);
        timer.IsRepeating = true;
        timer.Tick += TrayWorkingSetTimer_Tick;
        _trayWorkingSetTimer = timer;
        timer.Start();
    }

    private void TrayWorkingSetTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!ReferenceEquals(sender, _trayWorkingSetTimer)) return;
        if (AppWindow.IsVisible || _exitRequested || App.WindowsSessionEnding)
        {
            CancelTrayWorkingSetTrim();
            return;
        }

        try
        {
            if (!_startupChecksCompletedForTray || _closePromptInProgress || _pluginExitInProgress || _deploymentInProgress
                || _factoryResetInProgress || _gpuWatchInFlight || App.PendingFileFallback is not null
                || OfflineModeGuard.BlocksNormalAutomation || SingleInstanceGuard.DeploymentInProgress()
                || HasInFlightWinUiWork() || HasWindowPopup()) return;

            // Stop before calling the native API: at most one attempt per hide, even on error.
            CancelTrayWorkingSetTrim();
            if (CurrentProcessWorkingSet.TryTrim(out var error))
                Log.Info("托盘：已请求修剪前端自身的工作集。");
            else
                Log.Warn($"托盘：前端工作集修剪失败（Win32 {error}）。");
        }
        catch (Exception ex)
        {
            CancelTrayWorkingSetTrim();
            Log.Warn("托盘：工作集修剪检查未完成：" + ex.Message);
        }
    }

    private void CancelTrayWorkingSetTrim()
    {
        if (_trayWorkingSetTimer is not { } timer) return;
        _trayWorkingSetTimer = null;
        timer.Tick -= TrayWorkingSetTimer_Tick;
        timer.Stop();
    }
}
