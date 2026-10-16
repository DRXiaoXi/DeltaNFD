using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.AlphaRestore;
using AlphaService = DeltaNFD.Services.AlphaRestore.RuntimeGuardService;

internal static class RuntimeHighFpsChecks
{
    internal static async Task RunAsync(string? assets = null)
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        Check(BackendModules.IsReadOnly(BackendModules.Runtime, BackendCommands.RuntimeHighFpsPreview), "preview must be read-only");
        Check(BackendModules.RequiresWriteIntent(BackendModules.Runtime, BackendCommands.RuntimeHighFpsRepair), "repair needs write intent");
        Check(BackendModules.IsAllowed(BackendModules.Runtime, BackendCommands.Vc14Repair), "0.9192 V14 repair lost");
        Check(EmergencyPolicyVerifier.CanBlock(BackendCommands.RuntimeHighFpsRepair), "emergency policy must cover restored repair");
        Check(!RuntimeHighFpsMaintenance.NeedsV14Uninstall(new([], [], []), []), "fresh absent runtime proceeds to installation without unnecessary uninstall");
        var installed = new Vc14Entry("{A}", "Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.42", "14.42.34433", "x64", "Minimum", "{U}");
        Check(RuntimeHighFpsMaintenance.NeedsV14Uninstall(new([installed], [], []), []), "installed CRT needs controlled removal before full repair");
        var residue = new Vc14RemovalPlanItem("{B}", "x86", "wrapper", "14.51", "unknown", "", "", "");
        Check(RuntimeHighFpsMaintenance.NeedsV14Uninstall(new([], [], []), [residue]), "unresolved uninstall registration also needs controlled removal");
        var phaseWire = System.Text.Json.JsonSerializer.Deserialize<RuntimeHighFpsPreview>(System.Text.Json.JsonSerializer.Serialize(
            new RuntimeHighFpsPreview(true, "", "confirmed", false, []) { HasUninstallSteps = true }))!;
        Check(phaseWire.HasUninstallSteps && phaseWire.CanRepair, "normal-mode uninstall is allowed by backend protocol");
        var verifiedCheckpoint = new Vc14Continuation(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow) { UninstallVerified = true };
        var baselinePlan = new Vc14RemovalPlanItem("{A}", "x64", installed.Name, installed.Version, "msi", "", "", "");
        Check(!RuntimeHighFpsMaintenance.NeedsV14Uninstall(new([installed], [], []), [baselinePlan], verifiedCheckpoint), "partially installed pinned baseline resumes after verified uninstall");
        Check(RuntimeHighFpsMaintenance.NeedsV14Uninstall(new([installed], [], []), [baselinePlan], verifiedCheckpoint with { UninstallVerified = false }), "checkpoint intent alone cannot prove uninstall completion");
        Check(RuntimeHighFpsMaintenance.NeedsV14Uninstall(new([installed with { Version = "14.51.36247" }], [], []), [baselinePlan], verifiedCheckpoint), "old/new incompatible CRT cannot bypass uninstall with stale checkpoint");
        Check(RuntimeHighFpsMaintenance.NeedsV14Uninstall(new([installed], [], []), [residue], verifiedCheckpoint), "unknown wrapper cannot piggyback on partially installed baseline");
        var intent = RuntimeHighFpsMaintenance.Fingerprint("legacy", "v14-command-a", false);
        Check(intent != RuntimeHighFpsMaintenance.Fingerprint("legacy", "v14-command-b", false)
            && intent != RuntimeHighFpsMaintenance.Fingerprint("legacy", "v14-command-a", true), "confirmation must bind v14 commands and mode");
        var plan = RuntimeCppPlan.CppPlan();
        Check(plan.Count == 10 && plan.All(s => !s.RelativeFile.Contains("2026") && !s.RelativeFile.Contains("2022")), "full plan contains excluded v14 payload");
        Check(plan.Count(s => s.Branch.EndsWith("(x64)")) == 5 && plan.Count(s => s.Branch.EndsWith("(x86)")) == 5, "legacy architecture coverage");
        var repairCommand = RuntimeMsiInstaller.BuildCommand("C:\\fixture\\vc.msi", true, "C:\\fixture\\msi.log", true);
        Check(repairCommand.ArgumentList.Contains("REINSTALL=ALL") && repairCommand.ArgumentList.Contains("REINSTALLMODE=vemus"), "same-product overwrite flags");
        Check(!RuntimeMsiInstaller.BuildCommand("C:\\fixture\\vc.msi", false, "C:\\fixture\\msi.log", true).ArgumentList.Any(a => a.StartsWith("REINSTALL=")), "missing product must use install");

        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-highfps-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var isolationId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), isolationId.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, isolationId);
        try
        {
            async Task<OperationResult> Run(Fake environment) => await new RuntimeBetaRepair(environment,
                new RuntimeRepairDiagnostics(root)).RunAsync(null, default);
            var normal = new Fake { HasLegacy = true };
            Check((await Run(normal)).Success, "normal flow should complete");
            Check(normal.Calls.SequenceEqual(new[] { "verify" }.Concat(Enumerable.Repeat("overwrite", 10)).Concat(new[] { "aio", "v14-remove", "baseline-x64", "baseline-x86", "postcheck" })), "old C++ and auxiliary steps precede contiguous V14 removal/install");
            var removed = await RuntimeHighFpsMaintenance.PreviewAsync(true);
            Check(!removed.CanRepair && removed.Blocker.Contains("已移除") && !removed.StrongMode, "retired strong preview rejected before OS queries");
            var removedRepair = await RuntimeHighFpsMaintenance.RepairAsync("fixture", true, null, default);
            Check(!removedRepair.Success && removedRepair.Message.Contains("已移除"), "legacy strong request must not become ordinary destructive repair");
            var removedInternal = await new AlphaService().RepairVcRedistAsync(strongRepairMode: true);
            Check(!removedInternal.Success && removedInternal.Message.Contains("已移除"), "internal strong entry rejected without system repair");
            foreach (var failure in new[] { new RuntimeStepResult(1603), new RuntimeStepResult(3010), new RuntimeStepResult(null, OutcomeUnknown: true) })
            {
                var stop = new Fake { AioResult = failure };
                var result = await Run(stop);
                Check(!result.Success && !stop.Calls.Contains("baseline-x64") && !stop.Calls.Contains("v14-remove"), "AIO failure/reboot/unknown must stop before V14 removal");
                Check(result.RequiresReboot == failure.RequiresReboot && result.OutcomeUnknown == failure.OutcomeUnknown, "failure state lost");
            }
            var normalCppFailure = new Fake { OverwriteResult = new(1603) };
            Check(!(await Run(normalCppFailure)).Success && normalCppFailure.Calls.Count(c => c == "overwrite") == 1
                && !normalCppFailure.Calls.Contains("aio") && !normalCppFailure.Calls.Contains("v14-remove"), "old C++ failure preserves existing V14");
            var v14Failure = new Fake { V14Result = new(1638) };
            Check(!(await Run(v14Failure)).Success && v14Failure.Calls.Contains("aio") && !v14Failure.Calls.Contains("baseline-x64"), "V14 removal failure stops baseline after older components");
            var baselineFailure = new Fake { BaselineResult = new(1638) };
            Check(!(await Run(baselineFailure)).Success && !baselineFailure.Calls.Contains("baseline-x86"), "baseline conflict must stop");
            var invalidResults = new Fake { BrokenAioResults = true };
            Check(!(await Run(invalidResults)).Success && !invalidResults.Calls.Contains("baseline-x64") && !invalidResults.Calls.Contains("v14-remove"), "missing AIO results preserve V14");
            var badFiles = new Fake { FailPostcheck = true };
            Check(!(await Run(badFiles)).Success, "exit zero cannot replace DLL verification");
            var conflict = new Fake { DependencyConflict = true, HasLegacy = true };
            Check((await Run(conflict)).Success && conflict.Calls.Contains("overwrite") && conflict.Calls.Contains("aio")
                && conflict.Calls.Contains("v14-remove") && !conflict.Calls.Contains("uninstall"), "legacy C++ must use install/repair without old uninstall preflight");

            if (assets is not null)
            {
                assets = Path.GetFullPath(assets);
                var payload = Path.Combine(assets, "RuntimeHighFps");
                Check(AlphaService.VerifyRepairPackage(assets, payload, null).Success, "filtered payload hashes invalid");
                RuntimeCppOverwriteEnvironment.VerifyCppPayload(Path.Combine(assets, "RuntimeCppOfficial"), default, true);
                Check(!Directory.Exists(Path.Combine(payload, "2026")) && !Directory.Exists(Path.Combine(payload, "2022")), "excluded payload delivered");
                var script = File.ReadAllText(Path.Combine(payload, "Installer.cmd")).Replace("\r", "");
                var body = script[(script.IndexOf("\n:process\n", StringComparison.Ordinal) + 1)..];
                Check(body.StartsWith(":process\nrem DeltaNFD:") && body.IndexOf("set _x64install14=0", StringComparison.Ordinal) < body.IndexOf("call :install", StringComparison.Ordinal), "v14 disable flags must run at process label");
                Check(script.Contains("exit !dnfcode!") && script.Contains("DELTANFD_STOP_FILE"), "AIO failure/cancel handling missing");
                Check(!script.Contains("call :AddINF nat %%G") && script.Contains("set _x64install%%G=0") && !script.Contains("call :AddINF nat 14") && !script.Contains("08,09,10,11,12,14) do (\ncall :AddINF"), "2026 INF registration remains");
                Check(!script.Contains("reg delete %_k_%") && script.Contains(":unMSI\nrem C++ removals"), "unchecked duplicate removals restored");
                Check(script.Contains("if defined DELTANFD_NO_UNINSTALL exit /b 0") && script.Contains("if defined DELTANFD_NO_UNINSTALL goto :vbcinstall"), "normal-mode auxiliary phase must skip explicit uninstall and legacy file cleanup");
                Check(RuntimeBetaRepairEnvironment.BuildAioCommand(payload, root).Environment["DELTANFD_NO_UNINSTALL"] == "1", "full repair always disables auxiliary uninstall");
                Check(!script.Contains("start /wait MsiExec.exe /X") && script.Contains("if !dnfcode! equ 1605 goto :eof"), "legacy VC/VB removal must check exit codes; only MSI 1605 is already removed");
            }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"Full runtime repair: {count} checks passed; installers and system repair were mocked. Payload checks only read files/MSI metadata.");
    }

    private sealed class Fake : IRuntimeBetaRepairEnvironment
    {
        internal List<string> Calls = [];
        internal bool HasLegacy, DependencyConflict, BrokenAioResults, FailPostcheck;
        internal RuntimeStepResult AioResult = new(0), V14Result = new(0), BaselineResult = new(0), OverwriteResult = new(0);
        public Task VerifyOverwriteAsync(CancellationToken token) => Task.CompletedTask;
        public void VerifyInstalled() { Calls.Add("postcheck"); if (FailPostcheck) throw new IOException("DLL mismatch"); }
        public Task<OperationResult> VerifyAsync(IProgress<string>? progress, CancellationToken token) { Calls.Add("verify"); return Task.FromResult(OperationResult.Ok("ready")); }
        public Task<VcRedistScanReport> ScanAsync(CancellationToken token) => Task.FromResult(new VcRedistScanReport
        { All = HasLegacy ? [new VcRedistEntry { DisplayName = "Microsoft Visual C++ 2013 Redistributable (x64)", Architecture = "x64", KeyName = "fixture", UninstallKind = "Msi" }] : [], Abnormal = [], Suboptimal = [], Advice = "" });
        public Task<IReadOnlyList<VcRedistEntry>> PrepareUninstallBatchAsync(IReadOnlyList<VcRedistEntry> entries, CancellationToken token)
        { Calls.Add("prepare"); if (DependencyConflict) throw new RuntimeDependencyConflictException("shared"); return Task.FromResult(entries); }
        public Task<RuntimeStepResult> UninstallAsync(VcRedistEntry entry, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        { Calls.Add("uninstall"); HasLegacy = false; return Task.FromResult(new RuntimeStepResult(0)); }
        public Task<OperationResult> PrepareV14Async(IProgress<string>? progress, CancellationToken token) { Calls.Add("v14-remove"); return Task.FromResult(new OperationResult { Success = V14Result.Success, Message = V14Result.Details, OutcomeUnknown = V14Result.OutcomeUnknown, RequiresReboot = V14Result.RequiresReboot }); }
        public Task<RuntimeStepResult> InstallAioAsync(TimeSpan timeout, IProgress<string>? progress, CancellationToken token) { Calls.Add("aio"); return Task.FromResult(AioResult); }
        public Task<RuntimeStepResult> InstallBaselineAsync(string arch, TimeSpan timeout, IProgress<string>? progress, CancellationToken token) { Calls.Add("baseline-" + arch); return Task.FromResult(BaselineResult); }
        public Task<RuntimeStepResult> OverwriteCppAsync(RuntimeRepairStep step, TimeSpan timeout, IProgress<string>? progress, CancellationToken token) { Calls.Add("overwrite"); return Task.FromResult(OverwriteResult); }
        public List<RuntimePackageResult> ReadAioResults() => BrokenAioResults ? throw new IOException("missing") : [];
    }

}
