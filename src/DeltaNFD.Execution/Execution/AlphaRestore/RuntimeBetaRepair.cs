using System.Diagnostics;
using System.Text;

namespace DeltaNFD.Services.AlphaRestore;

internal interface IRuntimeBetaRepairEnvironment
{
    Vc14Baseline Baseline => Vc14Baseline.V144;
    void VerifyInstalled();
    Task<RuntimeStepResult> RecoverBaselineFilesAsync(TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        => Task.FromResult(new RuntimeStepResult(0, "继续实际DLL复检。"));
    void CompletePendingInstall() { }
    Task<OperationResult> VerifyAsync(IProgress<string>? progress, CancellationToken token);
    Task<VcRedistScanReport> ScanAsync(CancellationToken token);
    Task<OperationResult> PrepareV14Async(IProgress<string>? progress, CancellationToken token);
    Task<RuntimeStepResult> InstallAioAsync(TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
    Task<RuntimeStepResult> InstallBaselineAsync(string arch, TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
    Task<RuntimeStepResult> OverwriteCppAsync(RuntimeRepairStep step, TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
    List<RuntimePackageResult> ReadAioResults();
}

/// <summary>全量修复：仅 V14 受控卸载，旧 C++ 逐包使用微软原包安装/修复。</summary>
internal sealed class RuntimeBetaRepair(IRuntimeBetaRepairEnvironment environment, RuntimeRepairDiagnostics diagnostics,
    TimeSpan? totalBudget = null)
{
    internal async Task<OperationResult> RunAsync(IProgress<string>? progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var budget = totalBudget ?? RuntimeRepairCoordinator.TotalBudget;
        deadline.CancelAfter(budget);
        var clock = Stopwatch.StartNew();
        var info = Vc14BaselineInfo.For(environment.Baseline);
        var issues = new List<string>();
        var reboot = false; var executionStarted = false;
        diagnostics.Save("engine.json", new { Engine = "official-cpp-full", SourceCommit = "e9bca41", Budget = budget, Baseline = environment.Baseline });
        TimeSpan Remaining(TimeSpan maximum)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var left = budget - clock.Elapsed;
            if (left <= TimeSpan.Zero) throw new OperationCanceledException(deadline.Token);
            return left < maximum ? left : maximum;
        }
        OperationResult Stop(RuntimeStepResult result, string stage) => new()
        {
            Success = false, IsPartial = executionStarted, OutcomeUnknown = result.OutcomeUnknown, RequiresReboot = reboot,
            Message = stage + (executionStarted ? "未完成，已停止后续步骤；已调用运行库操作，请核对结果。\n" : "前检查未完成；未执行 C++ 卸载和后续安装。\n") + result.Details + "\n诊断目录：" + diagnostics.DirectoryPath,
        };
        try
        {
            var verified = await environment.VerifyAsync(progress, deadline.Token);
            if (!verified.Success) return verified;
            var before = await environment.ScanAsync(deadline.Token);
            diagnostics.Save("before-full-scan.json", before);
            var originalResults = new List<object>();
            foreach (var step in RuntimeCppPlan.CppPlan())
            {
                progress?.Report($"微软 C++ 原包 [{originalResults.Count + 1}/{RuntimeCppPlan.CppPlan().Count}]：{step.Label}");
                executionStarted = true;
                var original = await environment.OverwriteCppAsync(step, Remaining(TimeSpan.FromMinutes(8)), progress, deadline.Token);
                originalResults.Add(new { Step = step, Result = original });
                diagnostics.Save("official-cpp-results.json", originalResults);
                reboot |= original.RequiresReboot;
                if (!original.Success || original.RequiresReboot) return Stop(original, "微软 C++ 原包安装/修复");
            }
            progress?.Report("正在处理 VSTO 和旧 VC/VB 辅助组件（不运行 C++ 2005–2013 或 V14 的 AIO 安装步骤）…");
            var aioTimeout = Remaining(TimeSpan.FromMinutes(20));
            executionStarted = true;
            var aio = await environment.InstallAioAsync(aioTimeout, progress, deadline.Token);
            reboot |= aio.RequiresReboot;
            diagnostics.Save("aio-result.json", aio);
            if (!aio.Success || aio.RequiresReboot) return Stop(aio, "辅助组件安装");
            var packages = new List<RuntimePackageResult>();
            try { packages = environment.ReadAioResults(); }
            catch (Exception ex) { return Stop(new(null, "AIO 逐包结果未确认：" + ex.Message, PostcheckPassed: false), "AIO 复检"); }
            if (packages.Any(p => !p.Success || p.RequiresReboot))
                return Stop(new(null, "AIO 包存在失败或重启请求，未继续基线安装。", PostcheckPassed: false), "AIO 复检");
            // Keep the existing V14 available until every unrelated C++/auxiliary
            // step has completed. Revalidate the confirmed V14 plan here, then
            // install its pinned baseline immediately after controlled removal.
            var v14 = await environment.PrepareV14Async(progress, deadline.Token);
            reboot |= v14.RequiresReboot;
            if (!v14.Success || v14.RequiresReboot)
                return new OperationResult { Success = false, IsPartial = executionStarted || v14.IsPartial,
                    RequiresReboot = reboot, OutcomeUnknown = v14.OutcomeUnknown, DiagnosticsDirectory = v14.DiagnosticsDirectory,
                    Message = "旧版 C++ 和辅助组件步骤已完成；V14 阶段未完成，已停止后续安装。\n" + v14.Message };
            foreach (var arch in new[] { "x64", "x86" })
            {
                progress?.Report("正在安装官方 2015–2022 基线：" + arch);
                var baseline = await environment.InstallBaselineAsync(arch, Remaining(TimeSpan.FromMinutes(5)), progress, deadline.Token);
                reboot |= baseline.RequiresReboot;
                diagnostics.Save("baseline-" + arch + ".json", baseline);
                if (!baseline.Success || baseline.RequiresReboot) return Stop(baseline, "官方基线安装");
            }
            var files = await environment.RecoverBaselineFilesAsync(Remaining(TimeSpan.FromMinutes(20)), progress, deadline.Token);
            reboot |= files.RequiresReboot;
            diagnostics.Save("baseline-file-recovery.json", files);
            if (!files.Success || files.RequiresReboot) return Stop(files, info.Label + " 实际文件恢复");
            environment.VerifyInstalled();
            var after = await environment.ScanAsync(deadline.Token);
            diagnostics.Save("after-scan.json", after);
            var outcome = RuntimeRepairDiagnostics.BuildResult(aio.ExitCode ?? -1, packages, issues, after,
                reboot, diagnostics.DirectoryPath, diagnostics.CollectInstallerEvents());
            if (outcome.Success) environment.CompletePendingInstall();
            diagnostics.Save("summary.json", new { Result = outcome, Packages = packages, Issues = issues });
            return outcome;
        }
        catch (OperationCanceledException)
        {
            return new() { Success = false, RequiresReboot = reboot, OutcomeUnknown = executionStarted && File.Exists(Path.Combine(AppDataPaths.Root, "runtime-repair-active.json")),
                Message = (executionStarted ? "已取消后续步骤或达到总预算；已调用运行库操作，请核对已执行步骤。" : "检查已取消或达到总预算；未执行 C++ 卸载和后续安装。") + "\n诊断目录：" + diagnostics.DirectoryPath };
        }
        catch (Exception ex)
        {
            diagnostics.Save("fatal-error.json", new { Error = ex.ToString() });
            return OperationResult.Fail((executionStarted ? "运行库修复未完成，已调用运行库操作：" : "修复前检查失败，未执行 C++ 卸载和后续安装：") + ex.Message + "\n诊断目录：" + diagnostics.DirectoryPath);
        }
    }
}

internal sealed class RuntimeBetaRepairEnvironment(RuntimeGuardService service, RuntimeRepairDiagnostics diagnostics,
    Vc14Baseline baseline = Vc14Baseline.V144, string expectedV14Fingerprint = "") : IRuntimeBetaRepairEnvironment
{
    public Vc14Baseline Baseline { get; } = baseline;
    public void VerifyInstalled() => new Vc14ReinstallEnvironment(baseline: Baseline).VerifyInstalled();
    public async Task<RuntimeStepResult> RecoverBaselineFilesAsync(TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
    {
        var environment = new Vc14ReinstallEnvironment(baseline: Baseline);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var result = await Vc14FileRecovery.RunAsync(environment, progress, deadline.Token);
        diagnostics.Save("v14-file-recovery.json", new { Result = result, environment.DiagnosticsDirectory });
        return new(result.ExitCode, result.Detail, result.Unknown, PostcheckPassed: result.Success);
    }
    public void CompletePendingInstall()
    {
        var info = Vc14BaselineInfo.For(Baseline);
        var environment = new Vc14ReinstallEnvironment(baseline: Baseline);
        var scan = environment.Scan();
        if (scan.Issues.Count != 0 || scan.Products.Count != 4 || scan.Products.Any(p => !Vc14Catalog.SameVersion(p.Version, info.Version)))
            throw new IOException(info.Label + " 最终组件状态未确认，保留待安装记录。");
        environment.VerifyInstalled();
        environment.CompletePendingInstall();
    }
    private readonly RuntimeRepairProcessRunner _runner = new(AppDataPaths.Root);
    private string Assets => BackendEnvironment.AssetsRoot;
    private string Payload => Path.Combine(Assets, "RuntimeHighFps");
    private string OfficialPayload => Path.Combine(Assets, "RuntimeCppOfficial");

    public async Task<OperationResult> VerifyAsync(IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (new Vc14ReinstallEnvironment(baseline: Baseline).Blocker() is { } blocked) return OperationResult.Fail(blocked);
        var readiness = await service.CheckRepairEnvironmentAsync(token);
        diagnostics.Save("environment-before-uninstall.json", readiness);
        progress?.Report(readiness.Summary);
        if (!readiness.CanProceed) return OperationResult.Fail(readiness.Summary + "\n\n" + readiness.Details);
        var pending = _runner.CheckPending(RuntimeRepairPreflight.InstallerBusy());
        if (pending is not null) return OperationResult.Fail(pending);
        var verified = RuntimeGuardService.VerifyRepairPackage(Assets, Payload, progress);
        if (verified.Success) RuntimeCppOverwriteEnvironment.VerifyCppPayload(OfficialPayload, token, true);
        return verified;
    }
    public Task<VcRedistScanReport> ScanAsync(CancellationToken token) => service.ScanVcRedistsAsync().WaitAsync(token);
    public List<RuntimePackageResult> ReadAioResults() => diagnostics.ReadPackageResults();
    public Task<RuntimeStepResult> OverwriteCppAsync(RuntimeRepairStep step, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        => new RuntimeCppOverwriteEnvironment(diagnostics).RepairCppAsync(step, timeout, progress, token);

    internal static ProcessStartInfo BuildAioCommand(string payload, string diagnosticDirectory)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            $"/d /s /c \"\"{Path.Combine(payload, "Installer.cmd")}\" /quiet\"")
        {
            WorkingDirectory = payload, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        info.Environment["DELTANFD_REPAIR_LOG_DIR"] = diagnosticDirectory;
        info.Environment["DELTANFD_NO_UNINSTALL"] = "1";
        info.Environment["DELTANFD_STOP_FILE"] = Path.Combine(diagnosticDirectory, "stop-requested");
        return info;
    }
    public Task<OperationResult> PrepareV14Async(IProgress<string>? progress, CancellationToken token)
        => PrepareV14Async(new Vc14ReinstallEnvironment(baseline: Baseline), expectedV14Fingerprint, progress, token);

    // Reuse the same wrapper-first executor as the standalone V14 repair, including
    // immediate MSI postchecks, dependency checks and durable continuation records.
    internal static async Task<OperationResult> PrepareV14Async(IVc14ReinstallEnvironment environment,
        string expectedFingerprint, IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (environment.IsSafeMode) return OperationResult.Fail("安全模式仅使用单独卸载入口，完整安装请回正常模式。");
        if (environment.Blocker() is { } blocked) return OperationResult.Fail(blocked);
        var info = Vc14BaselineInfo.For(environment.Baseline);
        var scan = environment.Scan();
        if (scan.Issues.Count > 0) return OperationResult.Fail(string.Join("\n", scan.Issues));
        var plan = environment.Plan(scan);
        if (Vc14Reinstall.Fingerprint(plan, environment.Baseline) != expectedFingerprint)
            return OperationResult.Fail("V14 状态已变化，请重新预览。");
        if (RuntimeHighFpsMaintenance.NeedsV14Uninstall(scan, plan, environment.PendingInstall(), environment.Baseline))
        {
            var preview = Vc14Reinstall.Preview(environment, Vc14RepairMode.UninstallOnly);
            if (!preview.CanRepair) return OperationResult.Fail(preview.Blocker);
            if (Vc14Reinstall.Fingerprint(preview.RemovalPlan, environment.Baseline) != expectedFingerprint)
                return OperationResult.Fail("V14 卸载清单已变化，请重新预览。");
            var result = await Vc14Reinstall.RunAsync(environment, preview.Fingerprint, progress, token, Vc14RepairMode.UninstallOnly);
            if (!result.Success || result.RequiresReboot) return result;
            progress?.Report("V14 卸载及残留复检完成，继续全量修复的安装步骤。");
            return OperationResult.Ok("V14 卸载阶段完成，待安装记录保留。");
        }
        // A proven partial baseline must not be uninstalled on retry. An empty
        // system still needs residue handling and a checkpoint before installation.
        if (scan.Products.Count == 0)
        {
            var pending = environment.PendingInstall() ?? new(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow);
            environment.SavePendingInstall(pending with { UninstallVerified = false, Baseline = environment.Baseline });
            var cleanup = environment.Cleanup(scan, progress, token);
            if (!cleanup.Success) return cleanup;
            environment.SavePendingInstall(pending with { UninstallVerified = true, Baseline = environment.Baseline });
        }
        return OperationResult.Ok("无待卸载项或仅续装已核验的部分基线，继续安装/修复。");
    }
    public async Task<RuntimeStepResult> InstallAioAsync(TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
    {
        using var capture = new CancellationTokenSource();
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        var result = await _runner.RunAsync(BuildAioCommand(Payload, diagnostics.DirectoryPath), "VSTO / 旧 VC/VB 辅助组件 /quiet", timeout, progress, token,
            process =>
            {
                stdout = diagnostics.CaptureOutputAsync(process.StandardOutput, "aio-stdout.log", capture.Token);
                stderr = diagnostics.CaptureOutputAsync(process.StandardError, "aio-stderr.log", capture.Token);
            });
        if (result.OutcomeUnknown) File.WriteAllText(Path.Combine(diagnostics.DirectoryPath, "stop-requested"), "stop");
        capture.CancelAfter(result.OutcomeUnknown ? TimeSpan.Zero : TimeSpan.FromSeconds(5));
        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(6)); }
        catch (Exception ex) { diagnostics.Save("aio-output-warning.json", new { Error = ex.Message }); }
        return result;
    }
    public async Task<RuntimeStepResult> InstallBaselineAsync(string arch, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
    {
        var info = Vc14BaselineInfo.For(Baseline);
        var environment = new Vc14ReinstallEnvironment(baseline: Baseline);
        if (environment.Blocker() is { } blocked) return new(null, blocked, PostcheckPassed: false);
        environment.VerifyBaselines();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var step = await environment.InstallAsync(arch, progress, deadline.Token);
        if (!step.Success || step.Reboot) return new(step.ExitCode, step.Detail, step.Unknown);
        var scan = environment.Scan();
        var complete = scan.Issues.Count == 0 && Vc14Catalog.Families.Where(f => f.Arch == arch).All(f =>
            scan.Products.Count(p => p.Architecture == arch && p.Role == f.Role && Vc14Catalog.SameVersion(p.Version, info.Version)) == 1);
        return new(step.ExitCode, step.Detail + (complete ? "\n" + info.Label + " 组件复检通过。" : "\n" + info.Label + " MSI 组件未装齐，已停止。"), step.Unknown, PostcheckPassed: complete);
    }
}
