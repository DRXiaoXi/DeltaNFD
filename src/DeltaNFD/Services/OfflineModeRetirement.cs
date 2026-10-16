namespace DeltaNFD.Services;

/// <summary>Removed feature: retain only draining and restoration of existing records.</summary>
public static class OfflineModeRetirement
{
    private static readonly SemaphoreSlim RecoveryGate = new(1, 1);
    public static string LastError { get; private set; } = "";
    public static bool NeedsRecovery => LastError.Length > 0 || OfflineModeGuard.BlocksNormalAutomation;

    public static async Task<OperationResult> RecoverAsync(CancellationToken token = default)
    {
        await RecoveryGate.WaitAsync(token);
        try
        {
            var coordinator = new OfflineModeCoordinator();
            if (!OfflineModeStateStore.Default.TryRead(out _, out var error)) return Failed(error);
            var plugins = await ServiceLocator.PluginRuntime.DrainOfflinePluginsAsync(token);
            if (plugins.Count > 0) return Failed("旧版插件尚未安全停止：" + string.Join("；", plugins));
            var result = await coordinator.PrepareForRemovalAsync(restoreCpuSets: true, token);
            if (!result.Success) return Failed(result.Message);
            AppSettingsStore.Update(s => s.AllowPluginsInOfflineMode = false);
            if (OfflineModeGuard.BlocksNormalAutomation) return Failed("旧版恢复尚未完成，原记录保留。");
            LastError = "";
            return OperationResult.Ok("旧版恢复已完成；脱机模式已移除，未重新开启自动功能。");
        }
        catch (Exception ex) { Log.Error("旧版脱机恢复未完成", ex); return Failed(ex.Message); }
        finally { RecoveryGate.Release(); }
    }

    private static OperationResult Failed(string message)
    {
        LastError = message;
        return OperationResult.Fail(message);
    }
}
