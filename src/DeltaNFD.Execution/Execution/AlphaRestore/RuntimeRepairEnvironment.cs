using System.Diagnostics;

namespace DeltaNFD.Services.AlphaRestore;

internal sealed class RuntimeRepairEnvironment(RuntimeGuardService service, RuntimeRepairDiagnostics diagnostics,
    bool repairInstallerServices = false, bool allowNonstandardUninstall = false)
{
    private readonly RuntimeRepairProcessRunner _runner = new(AppDataPaths.Root);
    private readonly HashSet<string> _retainedBranches = [];
    private string PackageRoot => Path.Combine(BackendEnvironment.AssetsRoot, "RuntimeHighFps");
    private string UninstallPendingRoot => Path.Combine(AppDataPaths.Root,
        allowNonstandardUninstall ? "runtime-unified-uninstall-force-pending" : "runtime-unified-uninstall-pending");

    public Task<RuntimePreflightSnapshot> PreflightAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var snapshot = RuntimeRepairPreflight.Read();
        if (snapshot.Error.Length == 0)
        {
            var pending = _runner.CheckPending(snapshot.InstallerBusy);
            if (pending is not null) snapshot = snapshot with { Error = pending };
        }
        // Preserve the constructor shape for existing callers, but preflight never changes service state.
        _ = repairInstallerServices;
        Log.Info("运行库环境预检：" + System.Text.Json.JsonSerializer.Serialize(snapshot));
        return Task.FromResult(snapshot);
    }

    public async Task<VcRedistScanReport> ScanAsync(CancellationToken token)
        => await service.ScanVcRedistsAsync().WaitAsync(token);

    public async Task<OperationResult> VerifyAsync(IProgress<string>? progress, CancellationToken token)
        => await Task.Run(() => RuntimeGuardService.VerifyRepairPackage(BackendEnvironment.AssetsRoot, PackageRoot, progress), token).WaitAsync(token);

    public Task<IReadOnlyList<VcRedistEntry>> PrepareUninstallBatchAsync(IReadOnlyList<VcRedistEntry> entries, CancellationToken token)
    {
        var uninstaller = new RuntimeUnifiedUninstaller(new RuntimeUnifiedUninstallBackend(diagnostics, allowNonstandardUninstall), diagnostics,
            UninstallPendingRoot);
        return Task.FromResult(uninstaller.PrepareBatch(entries, token));
    }

    public Task<RuntimeStepResult> ExecuteAsync(RuntimeRepairStep step, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
    {
        if (step.Kind != "uninstall" || step.UninstallEntry is null)
            return Task.FromResult(new RuntimeStepResult(null, "此适配器只执行已核验的旧 C++ 卸载。", PostcheckPassed: false));
        return new RuntimeUnifiedUninstaller(new RuntimeUnifiedUninstallBackend(diagnostics, allowNonstandardUninstall), diagnostics,
            UninstallPendingRoot).RunAsync(step.UninstallEntry, step.Id, timeout, progress, token);
    }
}
