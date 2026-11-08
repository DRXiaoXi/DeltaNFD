using DeltaNFD.Services.Backends;
using DeltaNFD.Backends;

namespace DeltaNFD.Services;

internal sealed class FactoryResetService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private DeploymentHandover? _permit;
    private IDisposable? _backendBlock, _pluginBlock, _settingsBlock;
    internal bool Completed { get; private set; }

    internal async Task<OperationResult> RunAsync(IProgress<string>? progress = null)
    {
        if (!await Gate.WaitAsync(0)) return OperationResult.Fail("另一个恢复出厂设置操作正在运行。");
        var files = new FactoryResetConfiguration(AppDataPaths.Root);
        try
        {
            if (!ElevationHelper.IsElevated) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
            if (GameTargetService.Default.IsRunning() || DeltaForceLocator.IsGameRunning())
                return OperationResult.Fail("请先退出游戏，再恢复出厂设置。");
            _backendBlock = BackendOperationGate.BlockNewOperations();
            _pluginBlock = ServiceLocator.PluginRuntime.BlockNewOperations();
            _settingsBlock = AppSettingsStore.BlockWritesForReset(AppSettingsStore.DefaultPath);
            using var control = BackendOperationGate.AllowControlOperations();
            progress?.Report("正在备份工具配置和恢复记录…");
            files.Backup();
            var previous = AppSettingsStore.Read();
            progress?.Report("正在暂停自动功能，停止并恢复执行中的修改…");
            files.PauseAutomation();
            var requiresReboot = false;
            await files.CommitAfterRecoveryAsync(async () =>
            {
                _permit = await DeploymentHandover.PrepareAsync(uninstall: true);
                if (!_permit.Ready || _permit.ExitCode != 0)
                    throw new InvalidOperationException("临时状态或历史操作尚未完整核对，未重置配置。\n" + _permit.Detail);
                if (OfflineModeGuard.BlocksNormalAutomation) throw new InvalidOperationException("旧版脱机恢复记录仍未收敛，未重置配置。");
                if (previous.TempSpoofRestorePending || previous.GpuSpoofEnabled)
                {
                    progress?.Report("正在按可信备份恢复显卡型号…");
                    var caller = new BackendCaller(BackendModules.Gpu);
                    try
                    {
                        var response = await caller.InvokeReconcileAsync(BackendCommands.GpuRecover, new RecoverArgs { Scope = "all" }, null, default);
                        var recovery = BackendResults.RequireData<RecoverOutcome>(response, "显卡型号恢复");
                        if (!recovery.Success || !recovery.Verified || recovery.VerifiedSnapshot is not { Complete: true, Module: BackendModules.Gpu })
                            throw new IOException("显卡原值恢复未确认：" + recovery.Message);
                        requiresReboot = true;
                    }
                    finally { if (!await caller.ResetAsync()) throw new IOException("显卡恢复执行者尚未退出，未重置配置。"); }
                }
                progress?.Report("正在关闭工具的登录自启动…");
                var normalTask = await ServiceLocator.AppControl.SetAutoStartAsync(false);
                var frameTask = await ServiceLocator.Frame.DisableLogonAutostartAsync();
                if (!normalTask.Success || !frameTask.Success) throw new IOException(normalTask.Message + "\n" + frameTask.Message);
                if (GameTargetService.Default.IsRunning() || DeltaForceLocator.IsGameRunning())
                    throw new InvalidOperationException("游戏在恢复过程中启动，已停止配置重置。");
                progress?.Report("正在写入并核对默认配置…");
                return OperationResult.Ok("临时修改与自启动已恢复并确认安全停止。");
            });
            Completed = true;
            Log.Info("工具恢复出厂设置完成，原配置备份：" + files.BackupDirectory);
            return OperationResult.Ok("工具配置已恢复出厂设置，进程规则已清空，自动功能与自启动已关闭。\n"
                + "系统优化备份、日志、插件文件和恢复证据均保留。请关闭后重新打开工具，以载入默认配置。\n原配置备份：" + files.BackupDirectory,
                requiresReboot: requiresReboot);
        }
        catch (Exception ex)
        {
            Log.Error("恢复出厂设置未完成，原配置与恢复证据保留", ex);
            return OperationResult.Fail("恢复出厂设置未完成：" + ex.Message + "\n自动功能可能已暂停；这不代表全部恢复完成。"
                + (files.BackupDirectory.Length > 0 ? "\n原配置备份：" + files.BackupDirectory : ""));
        }
        finally
        {
            if (!Completed)
            {
                _permit?.Dispose(); _permit = null;
                _pluginBlock?.Dispose(); _pluginBlock = null;
                _backendBlock?.Dispose(); _backendBlock = null;
                _settingsBlock?.Dispose(); _settingsBlock = null;
            }
            Gate.Release();
        }
    }
}
