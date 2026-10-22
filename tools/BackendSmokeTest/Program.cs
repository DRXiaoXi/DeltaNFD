using DeltaNFD.Services;

if (args is ["--pso-cleanup-checks"])
{
    try { PsoCacheCleanupChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length is 1 or 2 && args[0] == "--update-response-checks")
{
    try { await UpdateResponseChecks.RunAsync(args.Length == 2 ? args[1] : null); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--vc14-msi-state-checks"])
{
    try { Vc14MsiStateChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--vc14-msi-state-native-readonly"])
{
    try { Vc14MsiStateChecks.ReadNative(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--ccd-model-checks"])
{
    try { CcdModelChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--gpu-spoof-recovery-checks"])
{
    try { await GpuSpoofRecoveryChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--runtime-detection-preference-checks"])
{
    try { await RuntimeDetectionPreferenceChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--vc14-registration-checks"])
{
    try { await Vc14RegistrationChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--vc14-registration-log-replay", var registrationSnapshot, var registryErrorLog])
{
    try { Vc14RegistrationChecks.Replay(registrationSnapshot, registryErrorLog); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--vc14-registration-native-readonly"])
{
    try
    {
        var evidence = Vc14MsiOrphans.ReadAbsence(Guid.NewGuid().ToString("B"));
        if (!evidence.ConfirmedAbsent && ElevationHelper.IsElevated) throw new IOException("Native absence query not confirmed: " + evidence.Detail);
        Console.WriteLine(evidence.ConfirmedAbsent
            ? "Native MSI absence query passed in all requested contexts. Read only; no registry or service writes."
            : "Native MSI query remained unconfirmed in this non-elevated host and correctly denies cleanup: " + evidence.Detail + ". Read only; no registry or service writes.");
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--vc14-ownership-checks", var ownershipRoot])
{
    try { Vc14OwnershipChecks.Run(ownershipRoot); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}
if (args is ["--vc14-dependency-log-replay", var logRoot, var ownershipSource])
{
    try { Vc14DependencyChecks.Replay(logRoot, ownershipSource); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--vc14-dependency-log-replay", var logReplayRoot])
{
    try { Vc14DependencyChecks.Replay(logReplayRoot); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--vc14-file-repair-checks", var fileRepairRoot])
{
    try { await Vc14FileRepairChecks.RunAsync(fileRepairRoot); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--vc14-bundle-identity-checks", var originalRoot])
{
    try { Vc14BundleIdentityChecks.Run(originalRoot); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 3 && args[0] is "--alpha-full-download-live" or "--alpha-full-download-direct")
{
    try { await AlphaUpgradeChecks.RetryFullAsync(args[1], args[2], args[0] == "--alpha-full-download-direct"); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 3 && args[0] == "--alpha-upgrade-live")
{
    try { await AlphaUpgradeChecks.RunAsync(args[1], args[2]); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}
if (args.Length == 3 && args[0] == "--runtime-ui-layout")
{
    try { if (AlphaUpgradeChecks.RuntimeEnabled(args[1]) != bool.Parse(args[2])) throw new Exception("Feature metadata mismatch"); Console.WriteLine("Runtime UI metadata verified: " + args[2]); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length is 1 or 2 && args[0] == "--official-cpp-checks")
{
    try { await OfficialCppChecks.RunAsync(args.Length == 2 ? args[1] : null); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--safe-installer-native-readonly"])
{
    try
    {
        var state = new SafeInstallerWindowsPlatform().Capture();
        Console.WriteLine($"Native Installer read-only query: mode={state.Mode}; exists={state.ServiceExists}; state={state.ServiceState}; identityAvailable={state.ServicePid > 0}. No writes or service control executed.");
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--safe-installer-checks"])
{
    try { await SafeInstallerChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--vc14-staged-checks"])
{
    try { await Vc14StagedChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--advanced-startup-checks"])
{
    try { await AdvancedStartupChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--runtime-file-use-checks"])
{
    try { await RuntimeFileUseChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}
if (args is ["--runtime-file-use-native-readonly"])
{
    try
    {
        var scan = await RuntimeFileUseMaintenance.ScanAsync(default);
        Console.WriteLine($"Native Restart Manager: success={scan.Success}, processes={scan.Processes.Count}, selectable={scan.Processes.Count(p => p.CanClose)}. Read-only; no process closing.");
        if (!scan.Success) throw new Exception(scan.Message);
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--file-update-checks"])
{
    try { await FileUpdateChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--game-power-native-child-checks"])
{
    try { await GamePowerThrottlingChecks.NativeChildAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args is ["--game-power-checks"])
{
    try { GamePowerThrottlingChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 3 && args[0] == "--file-update-layout-checks")
{
    try
    {
        var inventory = DeltaNFD.Updates.FileUpdateRules.Read(File.ReadAllBytes(args[1]), DeltaNFD.Updates.FileUpdateRules.Hash(args[1]));
        if (DeltaNFD.Updates.FileTransaction.Changed(args[2], inventory).Count != 0) throw new InvalidDataException("Generated inventory does not match published layout.");
        Console.WriteLine($"Generated file inventory matches all {inventory.Files.Count} deployed files; strict product validation passed.");
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 3 && args[0] == "--update-server-simulation")
{
    try { await UpdateServerSimulationChecks.RunAsync(args[1], args[2]); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.FirstOrDefault() == "--runtime-alpha-uninstall-checks")
{
    try { await DeltaNFD.Tests.AlphaRestore.RuntimeUnifiedUninstallChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.FirstOrDefault() == "--runtime-highfps-checks")
{
    try { await RuntimeHighFpsChecks.RunAsync(args.Length > 1 ? args[1] : null); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}


if (args.Length is 1 or 2 && args[0] == "--bug-feedback-checks")
{
    try { await BugFeedbackChecks.RunAsync(args.Length == 2 ? args[1] : null); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--vc14-checks") || args.Contains("--vc14-readonly"))
{
    try { if (args.Contains("--vc14-readonly")) Vc14DetectionChecks.ReadOnly(); else Vc14DetectionChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length is 1 or 2 && args[0] == "--runtime-repair-retirement-checks")
{
    try { RuntimeRepairRetirementChecks.Run(args.Length == 2 ? args[1] : null); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--shader-recovery-checks"))
{
    try { ShaderRecoveryChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--nv-shader-cache-checks") || args.Contains("--nv-shader-cache-readonly"))
{
    try { NvShaderCacheChecks.Run(args.Contains("--nv-shader-cache-readonly")); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--memory-mode-checks"))
{
    try { await ProcmanMemoryModeChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--procman-priority-interval-ipc-checks"))
{
    try { await ProcmanPriorityIntervalChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--memory-efficiency-benchmark"))
{
    try { await MemoryEfficiencyBenchmark.RunAsync(args.Contains("--all-memory-levels")); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--runtime-pending-recovery-checks"))
{
    try { await RuntimePendingRecoveryChecks.RunAsync(args.Contains("--with-ipc")); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--runtime-progress-child"))
{
    Console.Write("[== 3.8% ]\r[=== 20.2% ]\r");
    Console.Out.Flush();
    await Task.Delay(6500);
    Console.WriteLine("fixture complete");
    return;
}

if (args.Contains("--procman-soak-child"))
{
    BackgroundSustainedChecks.RunChild(args.Contains("--allocate-memory"));
    return;
}
if (args.Contains("--suppression-sustained-checks"))
{
    try { await BackgroundSustainedChecks.SuppressionAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}
if (args.Contains("--memory-sustained-checks"))
{
    try { await BackgroundSustainedChecks.MemoryAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}
if (args.Contains("--background-native-soak"))
{
    try { await BackgroundSustainedChecks.NativeSoakAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--procman-topology-checks"))
{
    try { ProcmanCpuTopologyChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--procman-topology-ipc-checks"))
{
    try { await ProcmanCpuTopologyChecks.RunIpcAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--ace-virtualization-checks"))
{
    AceVirtualizationChecks.Run();
    return;
}

if (args.Contains("--ace-virtualization-readonly"))
{
    var id = Guid.NewGuid();
    var root = Path.GetFullPath(Path.Combine("_buildcheck", "virtualization-readonly-" + id.ToString("N")));
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
    AppDataPaths.ConfigureIsolatedDataRoot(root, id);
    var result = await AceVirtualizationCheck.RunAsync();
    Console.WriteLine(result.OverallSummary);
    foreach (var item in result.Hardware.Concat(result.System)) Console.WriteLine($"{item.Id}: {item.Status}: {item.Detail}");
    Console.WriteLine("Read-only diagnostics: " + root);
    return;
}

if (args.Contains("--procman-list-checks"))
{
    ProcmanListChecks.Run();
    return;
}

if (args.Contains("--delta-rules-offline-checks"))
{
    try { DeltaRulesOfflineChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--frame-background-checks"))
{
    try { FrameBackgroundChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--factory-reset-checks"))
{
    try { await FactoryResetChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-retirement-checks"))
{
    await OfflineModeRetirementChecks.RunAsync();
    return;
}

if (args.Contains("--procman-memory-checks"))
{
    await ProcmanMemoryChecks.RunAsync();
    return;
}

if (args.Contains("--backend-attention-checks"))
{
    BackendAttentionChecks.Run();
    return;
}

if (args.Length == 2 && args[0] == "--frame-power-readonly-probe")
{
    if (!Guid.TryParse(args[1], out _)) throw new ArgumentException("expected scheme GUID");
    var runner = new Func<string, TimeSpan, Task<(int, string, string)>>(async (command, timeout) =>
    {
        if (!command.StartsWith("/q", StringComparison.Ordinal)) throw new InvalidOperationException("read-only probe");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), command)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(timeout);
        return (process.ExitCode, await stdout, await stderr);
    });
    var service = new FrameTweaksService(Path.Combine(Path.GetTempPath(), "read-only-probe-unused"), runner, () => null);
    typeof(FrameTweaksService).GetField("_nativePowerReads", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(service, true);
    var task = (Task)typeof(FrameTweaksService).GetMethod("QuerySchemeSettingsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(service, [args[1]])!;
    await task;
    var value = (System.Collections.ICollection?)task.GetType().GetProperty("Result")!.GetValue(task);
    Console.WriteLine(value is null ? "READ-ONLY: settings could not be fully verified" : $"READ-ONLY: {value.Count} power settings verified through native AC/DC reads");
    Environment.ExitCode = value is null ? 1 : 0;
    return;
}

if (args.Length == 2 && args[0] == "--backend-startup-checks")
{
    try { await BackendStartupChecks.RunAsync(args[1]); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--dotnet-layout-checks"))
{
    try { await DotNetLayoutChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--external-fix-regression-checks"))
{
    try { await ExternalFixRegressionChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--procman-test-child", StringComparer.Ordinal))
{
    ProcmanCoreChecks.RunChild();
    return;
}

if (args.Contains("--procman-core-checks", StringComparer.Ordinal))
{
    try { ProcmanCoreChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length is 4 or 5 && args[0] == "--deployment-install-fixture")
{
    try { Environment.ExitCode = await DeploymentInstallFixture.RunAsync(args[1], args[2], args[3], args.Length == 5 ? int.Parse(args[4]) : 0); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--update-source-fallback-checks"))
{
    try { await UpdateSourceFallbackChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--update-channel-checks"))
{
    try { await UpdateChannelChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--application-prelaunch-checks"))
{
    try { await ApplicationPreLaunchChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--runtime-guard-recovery-scope-checks"))
{
    try { RuntimeGuardRecoveryScopeChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-import-picker-checks", StringComparer.OrdinalIgnoreCase))
{
    try { PluginImportPickerChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 2 && args[0] == "--deployment-test-child" && int.TryParse(args[1], out var childDelay)
    && childDelay is > 0 and <= 15000)
{
    await Task.Delay(childDelay);
    return;
}

// T5 生命周期检查用的临时测试后端：本进程被复制并重命名为官方后端 EXE 后，
// 必须最先识别，绝不能落入下面的真实只读冒烟路径。
if (Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "")
    .StartsWith("DeltaNFD.Backend.", StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = await BackendSmokeTest.TestBackendFixture.RunAsync(args);
    return;
}

if (args.Contains("--backend-handover-checks", StringComparer.OrdinalIgnoreCase)
    || args.Contains("--deployment-handover-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendHandoverChecks.RunAsync(args.Contains("--deployment-handover-checks", StringComparer.OrdinalIgnoreCase)); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

// T6 模块边界检查的子进程：在临时目录（脱离源码兜底）断言后端缺失时 Resolve 明确失败。
if (args.Contains("--module-boundary-missing-child", StringComparer.OrdinalIgnoreCase))
{
    Environment.ExitCode = ModuleBoundaryChecks.RunMissingChild(args);
    return;
}

// T6：前端/执行库/合同/后端/共享库的依赖与源码边界，以及后端缺失/不兼容的失败模式。
if (args.Contains("--module-boundary-checks", StringComparer.OrdinalIgnoreCase))
{
    try { ModuleBoundaryChecks.Run(args); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

// T6：无头运行时测量（空闲后端工作集/CPU/进程数），不启动 GUI、不做写入。
if (args.Contains("--t6-runtime-measure", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendSmokeTest.T6RuntimeMeasure.RunAsync(args); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--deployment-layout-checks", StringComparer.OrdinalIgnoreCase))
{
    try { DeploymentLayoutChecks.Run(args); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--cpuframe-status-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await CpuFrameStatusChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--system-bx-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendSmokeTest.SystemBxChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--process-maintenance-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await ProcessMaintenanceChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-recovery-boundary-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await OfflineRecoveryBoundaryChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--ace-cleanup-scope-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await AceCleanupScopeChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--ace-core-cleanup-checks", StringComparer.OrdinalIgnoreCase))
{
    try { AceCoreFileCleanupChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--backend-recovery-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendRecoveryChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--backend-reconciliation-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendReconciliationChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 3 && args[0] == "--settings-write-child")
{
    BackendBackupConcurrencyChecks.SettingsChild(args[1], args[2]);
    return;
}

if (args.Length == 3 && args[0] == "--backup-write-child")
{
    BackendBackupConcurrencyChecks.Child(args[1], args[2]);
    return;
}
if (args.Contains("--backend-backup-concurrency-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendBackupConcurrencyChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--ace-cleanup-stage-checks", StringComparer.OrdinalIgnoreCase))
{
    try { AceCleanupStageChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--gpu-spoof-watch-checks", StringComparer.OrdinalIgnoreCase))
{
    try { GpuSpoofWatchChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length is 1 or 2 && args[0] == "--dashboard-shader-health-checks")
{
    try { await DashboardShaderHealthChecks.RunAsync(args.Length == 2 ? args[1] : null); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--dashboard-design-checks", StringComparer.OrdinalIgnoreCase))
{
    try { DashboardDesignChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-navigation-checks", StringComparer.OrdinalIgnoreCase))
{
    try { BackendSmokeTest.PluginNavigationChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-ui-wiring-checks", StringComparer.OrdinalIgnoreCase))
{
    try { PluginUiWiringChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-diagnostics-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await PluginDiagnosticsChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--shader-path-checks", StringComparer.OrdinalIgnoreCase))
{
    try { ShaderCachePathChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-test-delayed-exit", StringComparer.Ordinal))
{
    await Task.Delay(1800);
    return;
}

if (args.Contains("--plugin-integrity-checks", StringComparer.OrdinalIgnoreCase))
{
    try { BackendSmokeTest.PluginIntegrityChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-stability-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await PluginStabilityChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

// 插件运行时测试后端：宿主按规范只传 --dnfd-pipe/--dnfd-session/--dnfd-host-pid 三个标准参数，
// 因此这里按可执行文件名（PluginChild<Mode>.exe）识别，而不是额外命令行开关。
if (Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "")
    .StartsWith("PluginChild", StringComparison.OrdinalIgnoreCase))
{
    BackendSmokeTest.PluginRuntimeChild.Run();
    return;
}

if (args.Contains("--offline-helper-e2e-child", StringComparer.OrdinalIgnoreCase))
{
    OfflineHelperE2EChecks.RunChild();
    return;
}

if (args.Contains("--plugin-adversarial-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendSmokeTest.PluginAdversarialChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-offline-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendSmokeTest.PluginOfflineChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-continuous-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendSmokeTest.PluginContinuousChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-restore-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendSmokeTest.PluginRestoreChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-runtime-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendSmokeTest.PluginRuntimeChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-weaknet-checks", StringComparer.OrdinalIgnoreCase))
{
    try { BackendSmokeTest.PluginWeakNetBoostChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-manager-checks", StringComparer.OrdinalIgnoreCase))
{
    try { BackendSmokeTest.PluginManagerChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-import-checks", StringComparer.OrdinalIgnoreCase))
{
    try { BackendSmokeTest.PluginImportChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-contract-checks", StringComparer.OrdinalIgnoreCase))
{
    try { BackendSmokeTest.PluginContractChecks.Run(args); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-contract-e2e-checks", StringComparer.OrdinalIgnoreCase))
{
    try { BackendSmokeTest.PluginContractChecks.Run(args); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-helper-e2e-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await OfflineHelperE2EChecks.RunAsync(args); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-audit-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await OfflineAuditChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-audit-child", StringComparer.OrdinalIgnoreCase))
{
    await Task.Delay(TimeSpan.FromSeconds(90));
    return;
}

if (args.Contains("--offline-cpu-set-native-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await OfflineCpuSetNativeChecks.RunIsolatedAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}
if (args.Contains("--offline-cpu-set-native-child", StringComparer.OrdinalIgnoreCase))
{
    try { OfflineCpuSetNativeChecks.RunCurrentProcess(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-cpu-set-checks", StringComparer.OrdinalIgnoreCase))
{
    try { OfflineCpuSetCatalogChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-state-checks", StringComparer.OrdinalIgnoreCase))
{
    try { OfflineModeStateChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-cpu-plan-checks", StringComparer.OrdinalIgnoreCase))
{
    try { OfflineCpuSetPlannerChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-task-ownership-checks", StringComparer.OrdinalIgnoreCase))
{
    try { OfflineTaskOwnershipChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-timer-unit-checks", StringComparer.OrdinalIgnoreCase))
{
    try { OfflineTimerResolutionChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--frame-power-safety-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await FramePowerSafetyChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--runtime-installer-fixture", StringComparer.OrdinalIgnoreCase))
{
    var logIndex = Array.FindIndex(args, a => a.Equals("/L*V", StringComparison.OrdinalIgnoreCase));
    var packageIndex = Array.FindIndex(args, a => a.Equals("/i", StringComparison.OrdinalIgnoreCase));
    if (logIndex < 0 || packageIndex < 0) { Environment.ExitCode = 2; return; }
    var path = Path.GetFullPath(args[logIndex + 1]);
    var prefix = Path.Combine(Path.GetTempPath(), "DeltaNFD_RepairChecks_");
    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { Environment.ExitCode = 2; return; }
    var fail = args[packageIndex + 1].Contains("2005", StringComparison.OrdinalIgnoreCase);
    File.WriteAllText(path, fail ? "Error 1935 HRESULT: 0x80070005" : "MainEngineThread is returning 3010", System.Text.Encoding.Unicode);
    Environment.ExitCode = fail ? 1603 : 3010;
    return;
}

if (args.Contains("--frame-feature-checks", StringComparer.OrdinalIgnoreCase))
{
    try { FrameFeatureChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}


if (args.Contains("--delta-force-search-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await DeltaForceSearchChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--runtime-guard-recovery-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await RuntimeGuardRecoveryChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--game-target-test-child"))
{
    using var child = args.Length > 1 ? System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args[1], "--game-target-test-child")
        { UseShellExecute = false, CreateNoWindow = true }) : null;
    if (child is not null) Console.WriteLine(child.Id);
    await Task.Delay(TimeSpan.FromSeconds(60));
    if (child is not null && !child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
    return;
}
if (args.Contains("--game-target-checks"))
{
    try { await GameTargetChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

﻿// 只读冒烟测试：只枚举显卡、读取优化项状态、查询 VBS 运行时。
// 绝不调用 SpoofAsync / RestoreAsync / DisableAsync —— 那些会真实改动系统。
if (args.Contains("--affinity-test-child", StringComparer.OrdinalIgnoreCase))
{
    await Task.Delay(TimeSpan.FromSeconds(60));
    return;
}

if (args.Contains("--affinity-reliability-checks", StringComparer.OrdinalIgnoreCase))
{
    try { AffinityReliabilityChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--appdata-migration-checks", StringComparer.OrdinalIgnoreCase))
{
    try { AppDataMigrationChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--update-checks", StringComparer.OrdinalIgnoreCase))
{
    try { UpdateChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--update-live", StringComparer.OrdinalIgnoreCase))
{
    try { await UpdateChecksLive.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--power-scheme-checks", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        var powerSchemes = await new PowerService().GetSchemesAsync();
        var active = powerSchemes.SingleOrDefault(s => s.IsActive);
        if (active is null)
            throw new Exception("未能从 powercfg 列表识别当前电源计划。");
        Console.WriteLine($"电源计划只读检查通过：{powerSchemes.Count} 条，当前「{active.Name}」（{active.Guid}）");
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--runtime-guard-safety-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await RuntimeGuardSafetyChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--ace-guard-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await AceGuardChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--backend-contract-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendSmokeTest.BackendContractChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--hardware-only", StringComparer.OrdinalIgnoreCase))
{
    await HardwareDetectionChecks.RunAsync();
    return;
}

if (args.Contains("--cpu-plan-checks", StringComparer.OrdinalIgnoreCase))
{
    try { CpuPlanChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--settings-cache-checks", StringComparer.OrdinalIgnoreCase))
{
    try { SettingsCacheChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--recovery-checks", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        OptimizationRecoveryChecks.Run();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("系统优化恢复检查失败：" + ex);
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Contains("--phase1-checks", StringComparer.OrdinalIgnoreCase))
{
    try { SystemOptimizePhaseOneChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--phase1-live", StringComparer.OrdinalIgnoreCase))
{
    await SystemOptimizePhaseOneChecks.RunLiveAsync();
    return;
}

if (args.Contains("--phase23-checks", StringComparer.OrdinalIgnoreCase))
{
    try { SystemOptimizePhaseTwoThreeChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--recovery-checks-no-registry", StringComparer.OrdinalIgnoreCase))
{
    try { OptimizationRecoveryChecks.Run(includeRegistry: false); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

DeltaNFD.Services.Log.Startup();
DeltaNFD.Services.Log.Info("冒烟测试启动");
Console.WriteLine("=== Delta NFD 后端只读冒烟测试 ===");
Console.WriteLine($"管理员权限：{(ElevationHelper.IsElevated ? "是" : "否")}");
Console.WriteLine();

var spoof = new GpuSpoofService();
var adapters = await spoof.GetAdaptersAsync();
Console.WriteLine($"检测到 {adapters.Count} 个显示适配器：");
foreach (var adapter in adapters)
{
    Console.WriteLine($"  · {adapter.DisplayName}");
    Console.WriteLine($"      键：HKLM\\{adapter.RegistryPath}");
    Console.WriteLine($"      DeviceDesc：{adapter.DeviceDesc}");
    Console.WriteLine($"      已伪装：{(adapter.IsMasked ? "是" : "否")}（本工具备份：{(adapter.HasBackup ? "有" : "无")}）");
}

Console.WriteLine();
Console.WriteLine($"预置伪装型号：{string.Join("  ", spoof.PresetNames)}");
Console.WriteLine();

var tweaks = new SystemTweakService();
Console.WriteLine("系统优化项状态（IsOptimized=已关闭该项功能）：");
foreach (var status in await tweaks.GetStatusesAsync())
{
    Console.WriteLine($"  [{(status.IsOptimized ? "已优化" : "未优化")}] {status.DisplayName} —— {status.Detail}");
}

Console.WriteLine();
Console.WriteLine($"VBS/HVCI 运行时：{await tweaks.GetVbsRuntimeSummaryAsync()}");
Console.WriteLine();

// 电源计划（只读查询）
var power = new PowerService();
var schemes = await power.GetSchemesAsync();
Console.WriteLine($"电源计划（{schemes.Count} 个）：");
foreach (var scheme in schemes)
{
    Console.WriteLine($"  [{(scheme.IsActive ? "当前" : "    ")}] {scheme.Name} ({scheme.Guid})");
}
Console.WriteLine();

// 系统清理清单体积扫描（只读，不删除）
var cleanup = new CleanupService();
var targets = await cleanup.ScanAsync();
Console.WriteLine($"清理清单扫描（共 {targets.Sum(t => t.SizeMb) / 1024.0:0.00} GB）：");
foreach (var target in targets)
{
    Console.WriteLine($"  · {target.Name}: {(target.SizeMb >= 1024 ? $"{target.SizeMb / 1024.0:0.00} GB" : $"{target.SizeMb:0.0} MB")}");
}
Console.WriteLine();

// 高危分组状态（只读查询；计划任务状态走 PowerShell 查询，可能需要几秒）
var advanced = new AdvancedTweakService();
var groups = await advanced.GetGroupsAsync();
Console.WriteLine($"高级优化分组（{groups.Count} 组）：");
foreach (var group in groups)
{
    Console.WriteLine($"  [{(group.IsApplied ? "已应用" : "未应用")}] {group.DisplayName}（{group.Risk}）{group.Detail}");
}
Console.WriteLine();

// 运行库保护状态（只读：已装 VC++ 运行库版本 / UE4Prereq 定位 / IFEO 现状）
var guard = new RuntimeGuardService();
var guardStatus = await guard.GetStatusAsync();
Console.WriteLine($"运行库保护状态：IFEO {guardStatus.IfeoCount}/{guardStatus.IfeoTotal}，UE4Prereq {(guardStatus.Ue4PrereqFound ? $"已找到({guardStatus.Ue4PrereqPath})，{(guardStatus.Ue4PrereqDenied ? "已拒绝执行" : "未拦截")}" : "未找到")}");
foreach (var redist in guardStatus.InstalledRedists)
{
    Console.WriteLine($"  · {redist.DisplayName} [{redist.Architecture}] v{redist.Version}");
}
Console.WriteLine();

// 着色器维护状态（只读：驱动体检 + PSOCache 体积 + 健康检测，不删除）
var shader = new ShaderService();
var shaderStatus = await shader.GetStatusAsync();
Console.WriteLine($"着色器维护状态：{(shaderStatus.IsNvidia ? $"N 卡 {shaderStatus.DriverVersion}" : "非 N 卡")}");
Console.WriteLine($"  驱动体检：{shaderStatus.DriverAdvice}");
Console.WriteLine($"  PSOCache：{(shaderStatus.PsoCachePath.Length > 0 ? $"{shaderStatus.PsoCachePath}（{shaderStatus.PsoCacheSizeMb:0.0} MB）" : "未找到")}，游戏运行中={shaderStatus.IsGameRunning}");
var shaderDiag = await shader.DiagnoseAsync();
Console.WriteLine($"  健康检测[{shaderDiag.Level}]：{shaderDiag.Summary}");
foreach (var d in shaderDiag.Details)
{
    Console.WriteLine("      " + d);
}
Console.WriteLine($"      建议：{shaderDiag.Advice}");
Console.WriteLine();

// ACE 组件扫描（只读，不删除）
var ace = new AceService();
var aceScan = await ace.ScanAsync();
Console.WriteLine($"ACE 扫描：{(aceScan.CanClean ? "无阻止进程" : $"被阻断（{string.Join("、", aceScan.BlockingProcesses)}）")} + " +
                  $"服务 {aceScan.Services.Count(s => s.Exists)}/6，目录 {aceScan.Paths.Count(p => p.Exists)}，注册表残留 {aceScan.RegistryKeys.Count}");
var aceCore = await ace.CheckCoreFilesAsync();
Console.WriteLine($"  ACE-CORE 检测[{aceCore.Level}]：{aceCore.Summary}（{aceCore.SysCount} 个）");
foreach (var f in aceCore.Files) { Console.WriteLine("      " + f); }
// CPU 拓扑检测（只读）
var cpu = new CpuTopologyService();
var topology = await cpu.GetTopologyAsync();
Console.WriteLine($"CPU 拓扑：{topology.Vendor} · {topology.CpuName}");
Console.WriteLine($"  物理核心 {topology.PhysicalCores} · 逻辑处理器 {topology.LogicalProcessors} · 处理器组 {topology.GroupCount}");
Console.WriteLine($"  大小核：{(topology.IsHybrid ? $"是（P {topology.PCoreCount} + E {topology.ECoreCount}）" : "否")} · 超线程：{(topology.HasHyperThreading ? "已启用" : "未启用")}");
if (topology.IsAmd)
{
    Console.WriteLine($"  CCD 数：{topology.CcdCount}");
    foreach (var ccd in topology.Ccds)
    {
        Console.WriteLine($"    · CCD{ccd.Index}：{ccd.CoreCount} 核，L3 {ccd.L3CacheMb:0} MB（掩码 0x{ccd.Mask:X}）");
    }

    Console.WriteLine($"  结论：{topology.CcdAdvice}");
}
Console.WriteLine();

// NVIDIA 驱动设置服务（只读：环境 + 三角洲配置的覆盖状态）
var nv = new NvProfileService();
var nvEnv = await nv.GetEnvironmentAsync();
Console.WriteLine($"NVIDIA 驱动设置环境：N卡={(nvEnv.IsNvidia ? nvEnv.GpuName : "无")} · NVAPI={(nvEnv.NvApiAvailable ? "可用" : "不可用")} · nvdrsdb锁定={(nvEnv.DrsFilesLocked ? "是" : "否")}");
if (nvEnv.IsNvidia && nvEnv.NvApiAvailable)
{
    var nvValues = await nv.GetGameSettingValuesAsync();
    if (nvValues is null)
    {
        Console.WriteLine("  三角洲驱动配置读取失败");
    }
    else
    {
        foreach (var def in nv.GetSettingCatalog())
        {
            var state = nvValues.TryGetValue(def.Id, out var s) ? s : (HasOverride: false, Value: 0u);
            Console.WriteLine($"  · {def.Title}（0x{def.Id:X8}）：{(state.HasOverride ? $"已覆盖 0x{state.Value:X}" : "默认")}");
        }
    }
}
Console.WriteLine();
Console.WriteLine("（本次只做了读取，未执行任何伪装/关闭/清理/杀进程操作）");

// DefenderGuard 预检状态（只读：Defender 在位状态 + 第三方杀软清单；批次含安全类条目时的引导依据）
Console.WriteLine();
Console.WriteLine("=== 内存清理（待备列表）原语（只读验证，不执行清理） ===");
{
    var mem = DeltaNFD.Native.MemoryPurge.QueryMemory();
    Console.WriteLine($"  物理内存总量：{DeltaNFD.Native.MemoryPurge.FormatBytes(mem.TotalPhys)} · 可用：{DeltaNFD.Native.MemoryPurge.FormatBytes(mem.AvailPhys)}（{mem.AvailRatio:P1}） · 负载 {mem.LoadPercent}%");
    Console.WriteLine("  （PurgeStandbyList 会真实清空待备内存列表，属写操作，冒烟测试不执行）");
}

Console.WriteLine();
Console.WriteLine("=== DefenderGuard 预检状态（只读） ===");
{
    var guardReport = await DefenderGuard.PreflightAsync(new List<DefenderBatchItem>
    {
        new() { Name = "模拟条目", Ops = "REG HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender DisableAntiSpyware" },
    });
    if (guardReport is null)
    {
        Console.WriteLine("  预检未触发（配置缺失或无命中）");
    }
    else
    {
        Console.WriteLine($"  Defender：{(guardReport.DefenderPresent ? "在" : "无")} · 实时保护：{(guardReport.InitialRealTimeProtectionOn ? "开" : "关")} · 篡改防护：{(guardReport.TamperProtectionOn ? "开" : "关")}");
        Console.WriteLine($"  第三方杀软：{(guardReport.ThirdPartyAvNames.Count > 0 ? string.Join("、", guardReport.ThirdPartyAvNames) : "无")}");
        Console.WriteLine($"  命中条目：{string.Join("、", guardReport.SecurityItemNames)}（需引导={guardReport.NeedsGuidance}）");
    }
}

// 系统定时器分辨率只读验证
Console.WriteLine();
Console.WriteLine("=== 加速系统响应模式原语（只读验证） ===");
Console.WriteLine($"当前系统定时器分辨率：{DeltaNFD.Native.ProcessPerf.QueryTimerResolutionMs():0.###} ms");
// CPU 集（软亲和性）自检：对本测试进程设置默认集 → 读回 → 还原为全部核心
Console.WriteLine("=== CPU 集（软亲和性）原语自检（本进程，结束前还原） ===");
{
    var self = System.Diagnostics.Process.GetCurrentProcess();
    var testMask = 0xFUL; // CPU0–3
    var setOk = DeltaNFD.Native.CpuSets.SetDefaultByMask(self.Handle, testMask);
    var getOk = DeltaNFD.Native.CpuSets.TryGetDefaultMask(self.Handle, out var gotMask);
    Console.WriteLine($"  设置默认集 0x{testMask:X}：{(setOk ? "成功" : "失败")}；读回：{(getOk ? $"0x{gotMask:X}" : "无")}（{(getOk && gotMask == testMask ? "一致 ✓" : "不一致 ✗")}）");
    var restoreMask = (1UL << Environment.ProcessorCount) - 1;
    var restoreOk = DeltaNFD.Native.CpuSets.SetDefaultByMask(self.Handle, restoreMask);
    Console.WriteLine($"  还原为全部核心 0x{restoreMask:X}：{(restoreOk ? "成功" : "失败")}");
}
Console.WriteLine();
Console.WriteLine("（加速响应验证也全部只读，未修改任何电源/进程设置）");
