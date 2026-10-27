namespace DeltaNFD.Services;

// The read-only smoke executable does not compile the WinUI service locator.
internal static class ServiceLocator
{
    // Actual view-model tests supply an isolated guard. Other checks cannot start runtime writes through this stub.
    internal static Func<IRuntimeGuardService> RuntimeGuardFactory { get; set; } =
        () => throw new InvalidOperationException("Runtime view-model guard fixture is not configured.");
    private static readonly Lazy<IRuntimeGuardService> GuardLazy = new(() => RuntimeGuardFactory());
    internal static IRuntimeGuardService Guard => Backends.PageBackendSessions.Service(
        GuardLazy, () => RuntimeGuardFactory(), "runtime");
    internal static IPowerService Power { get; } = new PowerService();
    internal static GameTargetService GameTarget => GameTargetService.Default;
    internal static OfflineFrameStub Frame { get; } = new();
    internal static OfflineAppControlStub AppControl { get; } = new();
    internal static OfflineGameProcessStub GameProcess { get; } = new();
    internal static ICpuTopologyService Cpu { get; } = new Backends.BackendCpuTopologyService();
    private static readonly Lazy<PluginManagerService> PluginsLazy = new(() =>
        new PluginManagerService(Path.Combine(AppDataPaths.Root, "deployment-test-plugins")));
    internal static PluginManagerService Plugins => PluginsLazy.Value;
    private static readonly Lazy<Plugins.PluginRuntimeService> RuntimeLazy = new(() =>
        new Plugins.PluginRuntimeService(Plugins.Backups, Plugins.Handoffs, Plugins.DataRoot, Plugins.Authorizations, Plugins));
    internal static Plugins.PluginRuntimeService PluginRuntime => RuntimeLazy.Value;
}

// Only UI/OS boundaries are replaced. OfflineModeCoordinator and all IPC/restore locks are production code.
internal sealed class OfflineFrameStub
{
    internal bool FrameModeActive => AppSettingsStore.Read().FrameModeActive;
    internal bool DwmRestartOnGameStart
    {
        get => AppSettingsStore.Read().DwmRestartOnGameStart;
        set => AppSettingsStore.Update(s => s.DwmRestartOnGameStart = value);
    }
    internal void RefreshDualCcdArmState() { }
    internal Task<OperationResult> DeactivateFrameModeAsync()
    {
        AppSettingsStore.Update(s => s.FrameModeActive = false);
        return Task.FromResult(OperationResult.Ok("隔离帧格已停用。"));
    }
}

internal sealed class OfflineAppControlStub
{
    internal bool CloseToTrayEnabled => AppSettingsStore.Read().CloseToTrayEnabled;
    internal void SetCloseToTray(bool enabled) => AppSettingsStore.Update(s => s.CloseToTrayEnabled = enabled);
}

internal sealed class OfflineGameProcessStub
{
    internal bool GamePriorityEnabled
    {
        get => AppSettingsStore.Read().GamePriorityEnabled;
        set => AppSettingsStore.Update(s => s.GamePriorityEnabled = value);
    }
    internal async Task<OperationResult> DisableGamePriorityForOfflineAsync()
    {
        var result = await Backends.BackendCpuFrameService.Instance.ApplyPriorityAsync(false);
        if (result.Success) GamePriorityEnabled = false;
        return result;
    }
}
