using System.Diagnostics;
using System.Text;

namespace DeltaNFD.Services;

internal interface IRuntimeBetaRepairEnvironment
{
    Task VerifyOverwriteAsync(CancellationToken token) => Task.CompletedTask;
    Task<IReadOnlyList<VcRedistEntry>> PrepareUninstallBatchAsync(IReadOnlyList<VcRedistEntry> entries, CancellationToken token);
    Task<OperationResult> VerifyAsync(IProgress<string>? progress, CancellationToken token);
    Task<VcRedistScanReport> ScanAsync(CancellationToken token);
    Task<RuntimeStepResult> UninstallAsync(VcRedistEntry entry, TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
    Task<RuntimeStepResult> InstallAioAsync(TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
    Task<RuntimeStepResult> InstallBaselineAsync(string arch, TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
    Task<RuntimeStepResult> OverwriteCppAsync(RuntimeRepairStep step, TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
    List<RuntimePackageResult> ReadAioResults();
}

/// <summary>Beta0.9 (72bfbc2): uninstall entries, complete AIO /quiet, official baselines, rescan.</summary>
internal sealed class RuntimeBetaRepair(IRuntimeBetaRepairEnvironment environment, RuntimeRepairDiagnostics diagnostics,
    TimeSpan? totalBudget = null, bool strongRepairMode = false)
{
    internal async Task<OperationResult> RunAsync(IProgress<string>? progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var budget = totalBudget ?? RuntimeRepairCoordinator.TotalBudget;
        deadline.CancelAfter(budget);
        var clock = Stopwatch.StartNew();
        var issues = new List<string>();
        var reboot = false; var executionStarted = false; var dependencyFallback = false;
        diagnostics.Save("engine.json", new { Engine = strongRepairMode ? "strong-cpp-overwrite" : "beta0.9-aio", SourceCommit = "72bfbc2", Budget = budget });
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
            Message = stage + (executionStarted ? "未完成，已停止后续步骤；已调用运行库操作，请核对结果。\n" : "前检查未完成；未执行 C++ 卸载和后续安装。\n") + result.Details + "\n诊断目录：" + diagnostics.DirectoryPath,
        };
        async Task<OperationResult> OverwriteAsync(VcRedistScanReport before)
        {
            await environment.VerifyOverwriteAsync(deadline.Token);
            if (dependencyFallback) diagnostics.Save("engine.json", new { Engine = "dependency-safe-cpp-overwrite",
                StrongRepairMode = strongRepairMode, Budget = budget });
            diagnostics.Save("before-scan.json", before);
            diagnostics.Event("cpp-uninstall-skipped", new { Reason = dependencyFallback ? "shared-dependency-overwrite" : "strong-overwrite", Packages = 18 });
            var results = new List<object>(); var completed = 0; var retained = 0;
            foreach (var step in RuntimeCompatibilityRepair.CppPlan())
            {
                progress?.Report($"C++ 覆盖安装 [{results.Count + 1}/18]：{step.Label}");
                var timeout = Remaining(TimeSpan.FromMinutes(5));
                executionStarted = true;
                var installed = await environment.OverwriteCppAsync(step, timeout, progress, deadline.Token);
                results.Add(new { Step = step, Result = installed }); diagnostics.Save("cpp-overwrite-results.json", results);
                reboot |= installed.RequiresReboot;
                if (installed.Success) { completed++; if (installed.RetainedNewer) retained++; }
                else issues.Add(step.Label + "：" + installed.Details + "；退出码=" + installed.ExitCode);
                if (installed.OutcomeUnknown || installed.ExitCode is 1641 or 1618 || installed.RequiresReboot)
                    return Stop(installed, "C++ 覆盖安装（未执行卸载）");
            }
            var afterOverwrite = await environment.ScanAsync(deadline.Token);
            diagnostics.Save("after-scan.json", afterOverwrite);
            var overwriteResult = new OperationResult
            {
                Success = issues.Count == 0, IsPartial = completed > 0 && issues.Count > 0, RequiresReboot = reboot,
                Message = (issues.Count == 0 ? "C++ 覆盖安装及文件复检完成。" : "C++ 覆盖安装未全部完成。")
                    + (dependencyFallback ? "\n共享依赖已保留；本次自动改为覆盖安装。" : "")
                    + $"\n未执行 C++ 卸载；安装包复检通过：{completed}/18，其中保留并验证较新产品：{retained}。"
                    + (issues.Count > 0 ? "\n" + string.Join("\n", issues.Distinct().Take(12)) : "")
                    + "\n旧包装登记可能仍在列表中；本次结果依据 MSI 产品和关键文件复检。\n诊断目录：" + diagnostics.DirectoryPath,
            };
            diagnostics.Save("summary.json", overwriteResult);
            return overwriteResult;
        }
        try
        {
            var verified = await environment.VerifyAsync(progress, deadline.Token);
            if (!verified.Success) return verified;
            var before = await environment.ScanAsync(deadline.Token);
            if (strongRepairMode) return await OverwriteAsync(before);
            var removal = await RuntimePreUninstall.RunAsync(before, environment.PrepareUninstallBatchAsync,
                environment.UninstallAsync, environment.ScanAsync, Remaining, diagnostics, progress, deadline.Token);
            reboot |= removal.Reboot;
            executionStarted |= removal.ExecutionStarted;
            if (removal.DependencyBlocked && !removal.ExecutionStarted)
            {
                dependencyFallback = true;
                diagnostics.Save("dependency-fallback.json", new { Reason = removal.Stopped!.Details, ExecutionStarted = false });
                progress?.Report("检测到共享运行库依赖；未执行卸载，将保留登记并改为 C++ 覆盖安装…");
                return await OverwriteAsync(before);
            }
            if (removal.Stopped is { } stopped) return Stop(stopped, "运行库卸载");
            progress?.Report("正在运行完整 AIO 安装器，静默重装 2005–2026 全系列…");
            var aioTimeout = Remaining(TimeSpan.FromMinutes(20));
            executionStarted = true;
            var aio = await environment.InstallAioAsync(aioTimeout, progress, deadline.Token);
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
                Message = (executionStarted ? "已取消后续步骤或达到总预算；已调用运行库操作，请核对已执行步骤。" : "检查已取消或达到总预算；未执行 C++ 卸载和后续安装。") + "\n诊断目录：" + diagnostics.DirectoryPath };
        }
        catch (Exception ex)
        {
            diagnostics.Save("fatal-error.json", new { Error = ex.ToString() });
            return OperationResult.Fail((executionStarted ? "运行库修复未完成，已调用运行库操作：" : "修复前检查失败，未执行 C++ 卸载和后续安装：") + ex.Message + "\n诊断目录：" + diagnostics.DirectoryPath);
        }
    }
}

internal sealed class RuntimeBetaRepairEnvironment(RuntimeGuardService service, RuntimeRepairDiagnostics diagnostics, bool strongRepairMode = false)
    : IRuntimeBetaRepairEnvironment
{
    public Task VerifyOverwriteAsync(CancellationToken token)
    {
        RuntimeCompatibilityEnvironment.VerifyCppPayload(Payload, token, true);
        return Task.CompletedTask;
    }
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
        var verified = RuntimeGuardService.VerifyRepairPackage(Assets, Payload, progress);
        if (verified.Success && strongRepairMode) RuntimeCompatibilityEnvironment.VerifyCppPayload(Payload, token, true);
        return verified;
    }
    public Task<VcRedistScanReport> ScanAsync(CancellationToken token) => service.ScanVcRedistsAsync().WaitAsync(token);
    public List<RuntimePackageResult> ReadAioResults() => diagnostics.ReadPackageResults();
    public Task<RuntimeStepResult> OverwriteCppAsync(RuntimeRepairStep step, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        => new RuntimeCompatibilityEnvironment(diagnostics, strongRepairMode: true).RepairCppAsync(step, timeout, progress, token);

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
