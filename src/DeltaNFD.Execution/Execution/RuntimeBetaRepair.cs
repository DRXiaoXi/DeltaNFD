using System.Diagnostics;
using System.Text;

namespace DeltaNFD.Services;

internal interface IRuntimeBetaRepairEnvironment
{
    Task<IReadOnlyList<VcRedistEntry>> PrepareUninstallBatchAsync(IReadOnlyList<VcRedistEntry> entries, CancellationToken token);
    Task<OperationResult> VerifyAsync(IProgress<string>? progress, CancellationToken token);
    Task<VcRedistScanReport> ScanAsync(CancellationToken token);
    Task<RuntimeStepResult> UninstallAsync(VcRedistEntry entry, TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
    Task<RuntimeStepResult> InstallAioAsync(TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
    Task<RuntimeStepResult> InstallBaselineAsync(string arch, TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
    List<RuntimePackageResult> ReadAioResults();
}

/// <summary>Beta0.9 (72bfbc2): uninstall entries, complete AIO /quiet, official baselines, rescan.</summary>
internal sealed class RuntimeBetaRepair(IRuntimeBetaRepairEnvironment environment, RuntimeRepairDiagnostics diagnostics,
    TimeSpan? totalBudget = null)
{
    internal async Task<OperationResult> RunAsync(IProgress<string>? progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var budget = totalBudget ?? RuntimeRepairCoordinator.TotalBudget;
        deadline.CancelAfter(budget);
        var clock = Stopwatch.StartNew();
        var issues = new List<string>();
        var reboot = false;
        diagnostics.Save("engine.json", new { Engine = "beta0.9-aio", SourceCommit = "72bfbc2", Budget = budget });
        TimeSpan Remaining(TimeSpan maximum)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var left = budget - clock.Elapsed;
            if (left <= TimeSpan.Zero) throw new OperationCanceledException(deadline.Token);
            return left < maximum ? left : maximum;
        }
        OperationResult Stop(RuntimeStepResult result, string stage) => new()
        {
            Success = false, OutcomeUnknown = result.OutcomeUnknown, RequiresReboot = reboot,
            Message = stage + "未完成，已停止后续步骤；已有步骤可能改变运行库。\n" + result.Details + "\n诊断目录：" + diagnostics.DirectoryPath,
        };
        try
        {
            var verified = await environment.VerifyAsync(progress, deadline.Token);
            if (!verified.Success) return verified;
            var before = await environment.ScanAsync(deadline.Token);
            var removal = await RuntimePreUninstall.RunAsync(before, environment.PrepareUninstallBatchAsync,
                environment.UninstallAsync, environment.ScanAsync, Remaining, diagnostics, progress, deadline.Token);
            reboot |= removal.Reboot;
            if (removal.Stopped is { } stopped) return Stop(stopped, "运行库卸载");
            progress?.Report("正在运行完整 AIO 安装器，静默重装 2005–2026 全系列…");
            var aio = await environment.InstallAioAsync(Remaining(TimeSpan.FromMinutes(20)), progress, deadline.Token);
            reboot |= aio.RequiresReboot;
            diagnostics.Save("aio-result.json", aio);
            if (aio.OutcomeUnknown || aio.ExitCode == 1641 || aio.ExitCode is null) return Stop(aio, "AIO 重装");
            var packages = new List<RuntimePackageResult>();
            try { packages = environment.ReadAioResults(); }
            catch (Exception ex) { issues.Add("AIO 逐包结果未确认：" + ex.Message); }
            foreach (var arch in new[] { "x64", "x86" })
            {
                progress?.Report("正在安装官方 2015–2022 基线：" + arch);
                var baseline = await environment.InstallBaselineAsync(arch, Remaining(TimeSpan.FromMinutes(5)), progress, deadline.Token);
                reboot |= baseline.RequiresReboot;
                diagnostics.Save("baseline-" + arch + ".json", baseline);
                if (baseline.OutcomeUnknown || baseline.ExitCode == 1641) return Stop(baseline, "官方基线安装");
                // Beta accepts the newer-version baseline conflict only together with final branch verification.
                if (!baseline.Success && !RuntimeRepairErrorClassifier.IsVersionConflict(baseline.ExitCode))
                    issues.Add("官方基线 " + arch + "：" + baseline.Details + "；退出码=" + baseline.ExitCode);
            }
            var after = await environment.ScanAsync(deadline.Token);
            diagnostics.Save("after-scan.json", after);
            var outcome = RuntimeRepairDiagnostics.BuildResult(aio.ExitCode.Value, packages, issues, after,
                reboot, diagnostics.DirectoryPath, diagnostics.CollectInstallerEvents());
            diagnostics.Save("summary.json", new { Result = outcome, Packages = packages, Issues = issues });
            return outcome;
        }
        catch (OperationCanceledException)
        {
            return new() { Success = false, RequiresReboot = reboot,
                Message = "已取消后续步骤或达到总预算，已有运行库可能被卸载；请核对当前状态。\n诊断目录：" + diagnostics.DirectoryPath };
        }
        catch (Exception ex)
        {
            diagnostics.Save("fatal-error.json", new { Error = ex.ToString() });
            return OperationResult.Fail("Beta0.9 运行库修复未完成：" + ex.Message + "\n诊断目录：" + diagnostics.DirectoryPath);
        }
    }
}

internal sealed class RuntimeBetaRepairEnvironment(RuntimeGuardService service, RuntimeRepairDiagnostics diagnostics)
    : IRuntimeBetaRepairEnvironment
{
    public Task<IReadOnlyList<VcRedistEntry>> PrepareUninstallBatchAsync(IReadOnlyList<VcRedistEntry> entries, CancellationToken token)
        => new RuntimeRepairEnvironment(service, diagnostics).PrepareUninstallBatchAsync(entries, token);
    private readonly RuntimeRepairProcessRunner _runner = new(AppDataPaths.Root);
    private string Assets => BackendEnvironment.AssetsRoot;
    private string Payload => Path.Combine(Assets, "VCRedistRepair");

    public async Task<OperationResult> VerifyAsync(IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var readiness = await service.CheckRepairEnvironmentAsync(token);
        diagnostics.Save("environment-before-uninstall.json", readiness);
        progress?.Report(readiness.Summary);
        if (!readiness.CanProceed) return OperationResult.Fail(readiness.Summary + "\n\n" + readiness.Details);
        var pending = _runner.CheckPending(RuntimeRepairPreflight.InstallerBusy());
        if (pending is not null) return OperationResult.Fail(pending);
        return RuntimeGuardService.VerifyRepairPackage(Assets, Payload, progress);
    }
    public Task<VcRedistScanReport> ScanAsync(CancellationToken token) => service.ScanVcRedistsAsync().WaitAsync(token);
    public List<RuntimePackageResult> ReadAioResults() => diagnostics.ReadPackageResults();

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
        return info;
    }
    public async Task<RuntimeStepResult> InstallAioAsync(TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
    {
        using var capture = new CancellationTokenSource();
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        var result = await _runner.RunAsync(BuildAioCommand(Payload, diagnostics.DirectoryPath), "完整 AIO /quiet", timeout, progress, token,
            process =>
            {
                stdout = diagnostics.CaptureOutputAsync(process.StandardOutput, "aio-stdout.log", capture.Token);
                stderr = diagnostics.CaptureOutputAsync(process.StandardError, "aio-stderr.log", capture.Token);
            });
        capture.CancelAfter(result.OutcomeUnknown ? TimeSpan.Zero : TimeSpan.FromSeconds(5));
        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(6)); }
        catch (Exception ex) { diagnostics.Save("aio-output-warning.json", new { Error = ex.Message }); }
        return result;
    }
    public Task<RuntimeStepResult> InstallBaselineAsync(string arch, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
    {
        if (arch is not ("x64" or "x86")) throw new InvalidDataException("未知基线架构。");
        var info = new ProcessStartInfo(Path.Combine(Assets, "VCRedist2015-2022-" + arch + ".exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/install", "/quiet", "/norestart", "/log", Path.Combine(diagnostics.DirectoryPath, "baseline-" + arch + ".log") }) info.ArgumentList.Add(arg);
        return _runner.RunAsync(info, "官方基线 " + arch, timeout, progress, token);
    }
    internal static ProcessStartInfo BuildUninstallCommand(VcRedistEntry entry, string logPath)
    {
        if (entry.UninstallKind == "Msi")
        {
            if (!RuntimeUninstallCommand.TryCreate(entry, logPath, out var plan, out var error)) throw new InvalidDataException(error);
            return plan!.CreateStartInfo();
        }
        if (entry.UninstallKind is not ("Inf" or "Bundle")) throw new InvalidDataException("未知卸载机制。");
        var command = entry.UninstallKind == "Bundle" && !string.IsNullOrWhiteSpace(entry.QuietUninstallString)
            ? entry.QuietUninstallString : entry.UninstallString;
        var args = RuntimeUninstallCommand.Split(command);
        if (args.Length == 0) throw new InvalidDataException("没有登记的卸载入口。");
        var exe = args[0];
        if (entry.UninstallKind == "Inf")
        {
            // The same registered wrapper as Beta, using its quiet Unattend section.
            var inf = RuntimeUninstallCommand.GetInfPath(entry);
            return new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "rundll32.exe"),
                $"advpack.dll,LaunchINFSection \"{inf}\",Unattend,1,N") { UseShellExecute = false, CreateNoWindow = true };
        }
        if (!Path.IsPathFullyQualified(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("包装卸载入口不是绝对 EXE 路径。");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args.Skip(1)) start.ArgumentList.Add(arg);
        return start;
    }
    public async Task<RuntimeStepResult> UninstallAsync(VcRedistEntry entry, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
    {
        try
        {
            var guarded = new RuntimeRepairEnvironment(service, diagnostics, repairInstallerServices: false);
            if (await service.RepairProtectionBlockerAsync() is { } protection) return new(-1, protection);
            if ((await guarded.PreflightAsync(token)).Blocker is { } blocker) return new(-1, blocker);
            return await guarded.ExecuteAsync(new("repair-uninstall-" + Guid.NewGuid().ToString("N"), entry.DisplayName, "", "uninstall", "", entry), timeout, progress, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(null, "卸载入口未执行：" + ex.Message); }
    }
}
