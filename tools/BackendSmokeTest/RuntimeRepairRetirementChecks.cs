using System.Reflection;
using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class RuntimeRepairRetirementChecks
{
    internal static void Run(string? layout)
    {
        var count = 0;
        void Check(bool value, string detail) { if (!value) throw new Exception(detail); count++; }
        foreach (var command in new[] { "runtime.repair", "runtime.compatibility.repair", "runtime.redist.uninstall",
            "runtime.redist.uninstall-all", "runtime.redist.preview-all", "runtime.repair.environment",
            "runtime.redist.scan", "runtime.redist.list-v14" })
            Check(!BackendModules.IsAllowed(BackendModules.Runtime, command), "retired command must not be callable: " + command);
        Check(BackendModules.IsAllowed(BackendModules.Runtime, BackendCommands.RuntimeSafeUninstallPreview)
            && BackendModules.IsReadOnly(BackendModules.Runtime, BackendCommands.RuntimeSafeUninstallPreview),
            "safe-mode all-C++ preview is registered read-only");
        Check(BackendModules.IsAllowed(BackendModules.Runtime, BackendCommands.RuntimeSafeUninstall)
            && !BackendModules.IsReadOnly(BackendModules.Runtime, BackendCommands.RuntimeSafeUninstall),
            "safe-mode all-C++ uninstall is registered as mutating");
        var normalPreview = DeltaNFD.Services.AlphaRestore.RuntimeSafeUninstall.PreviewAsync(default).GetAwaiter().GetResult();
        Check(!normalPreview.CanRun && normalPreview.Blocker.Contains("安全模式"), "all-C++ uninstall is unavailable outside safe mode");
        var normalRun = DeltaNFD.Services.AlphaRestore.RuntimeSafeUninstall.RunAsync("", false, null, default).GetAwaiter().GetResult();
        Check(!normalRun.Success && normalRun.Message.Contains("安全模式"), "normal-mode all-C++ run is rejected before any uninstall");
        foreach (var command in new[] { BackendCommands.RuntimeStatus, BackendCommands.RuntimeGuardEnable,
            BackendCommands.RuntimeGuardDisable, BackendCommands.RuntimeGuardSelectMode, BackendCommands.RuntimeGuardCleanupLegacy,
            BackendCommands.RuntimeUe4RepairAcl, BackendCommands.RuntimeReconcile, BackendCommands.RuntimeRecover })
            Check(BackendModules.IsAllowed(BackendModules.Runtime, command), "protection or historical recovery lost: " + command);
        foreach (var method in new[] { "RepairVcRedistAsync", "RepairCompatibilityAsync", "UninstallV14Async",
            "UninstallAllAsync", "PreviewUninstallAllAsync", "CheckRepairEnvironmentAsync", "ScanVcRedistsAsync" })
        {
            Check(typeof(IRuntimeGuardService).GetMethod(method) is null, "retired public service method exists: " + method);
            Check(typeof(RuntimeGuardService).GetMethod(method) is null, "retired implementation method exists: " + method);
        }
        foreach (var name in new[] { "RuntimeRepairArgs", "RuntimeCompatibilityRepairArgs", "RuntimeUninstallAllArgs",
            "RuntimeUninstallPreview", "RuntimeRepairMode", "VcRedistScanReport" })
            Check(typeof(IRuntimeGuardService).Assembly.GetTypes().All(t => t.Name != name), "retired contract remains: " + name);
        Check(typeof(RuntimeInstallationReview).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .All(m => m.Name != "RunAsync"), "historical review must not launch installers");
        if (layout is not null)
        {
            var root = Path.GetFullPath(layout);
            var execution = Assembly.LoadFrom(Path.Combine(root, "Backends", "DeltaNFD.Execution.dll"));
            foreach (var name in new[] { "RuntimeBetaRepair", "RuntimeBulkUninstall", "RuntimeCompatibilityRepair", "RuntimeMsiInstaller",
                "RuntimeStrongRepair", "RuntimeRepairProcessRunner", "RuntimeSystemProbeProcess", "RuntimeRepairEnvironment",
                "RuntimeUnifiedUninstallBackend", "RuntimeUnifiedUninstaller", "RuntimeMsiMetadata", "RuntimePreUninstall" })
                Check(execution.GetType("DeltaNFD.Services." + name) is null, "delivered execution type remains: " + name);
            Check(execution.GetType("DeltaNFD.Services.RuntimeGuardProtection") is not null, "delivered protection removed");
            Check(execution.GetType("DeltaNFD.Services.RuntimeLegacyInstallationReview") is not null, "legacy review removed");
            var unexpected = execution.GetTypes().Where(t => t.Namespace == "DeltaNFD.Services"
                && t.Name.StartsWith("Runtime", StringComparison.Ordinal) && !t.Name.StartsWith("RuntimeGuard", StringComparison.Ordinal)
                && t.Name is not ("RuntimeInstallationReview" or "RuntimeLegacyInstallationReview" or "RuntimeHighFpsMaintenance")).Select(t => t.Name).ToArray();
            Check(unexpected.Length == 0, "unexpected runtime execution types: " + string.Join(", ", unexpected));
            foreach (var asset in new[] { "VCRedistRepair", "VCRedistRepair_unpacked", "DirectXRepair", "DirectXRepair_unpacked",
                "VCRedist2015-2022-x64.exe", "VCRedist2015-2022-x86.exe", "VCRedistRepair.exe", "DotNetRepair" })
            {
                var path = Path.Combine(root, "Assets", asset);
                Check(!File.Exists(path) && !Directory.Exists(path), "retired install resources delivered: " + asset);
            }
            Check(File.Exists(Path.Combine(root, "Views", "RuntimePage.xbf")), "protection page missing from delivered layout");
        }
        Console.WriteLine($"Legacy runtime repair retirement: {count} checks passed. New VC14 workflow is separate; no system changes.");
    }
}
