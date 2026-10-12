using System.Diagnostics;

namespace DeltaNFD.Services;

internal sealed class RuntimeRepairEnvironment(RuntimeGuardService service, RuntimeRepairDiagnostics diagnostics, bool repairInstallerServices = false) : IRuntimeRepairEnvironment
{
    private readonly RuntimeRepairProcessRunner _runner = new(AppDataPaths.Root);
    private readonly HashSet<string> _retainedBranches = [];
    private string PackageRoot => Path.Combine(BackendEnvironment.AssetsRoot, "VCRedistRepair");

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
        var uninstaller = new RuntimeUnifiedUninstaller(new RuntimeUnifiedUninstallBackend(diagnostics), diagnostics,
            Path.Combine(AppDataPaths.Root, "runtime-unified-uninstall-pending"));
        return Task.FromResult(uninstaller.PrepareBatch(entries, token));
    }

    public async Task<IReadOnlyList<RuntimeRepairStep>> PrepareStepsAsync(IReadOnlyList<RuntimeRepairStep> steps, CancellationToken token)
    {
        var entries = await PrepareUninstallBatchAsync(steps.Where(s => s.Kind == "uninstall")
            .Select(s => s.UninstallEntry ?? throw new InvalidDataException("缺少卸载条目。")).ToArray(), token);
        return entries.Select((e, i) => new RuntimeRepairStep("uninstall-" + i, e.DisplayName, "", "uninstall", "", e))
            .Concat(steps.Where(s => s.Kind != "uninstall")).ToArray();
    }

    public async Task<RuntimeStepResult> ExecuteAsync(RuntimeRepairStep step, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
    {
        ProcessStartInfo info;
        var logFile = Path.Combine(diagnostics.DirectoryPath, step.Id + ".log");
        if (step.Kind == "uninstall")
        {
            if (step.UninstallEntry is null) return new(-1, "缺少卸载条目");
            return await new RuntimeUnifiedUninstaller(new RuntimeUnifiedUninstallBackend(diagnostics), diagnostics,
                Path.Combine(AppDataPaths.Root, "runtime-unified-uninstall-pending"))
                .RunAsync(step.UninstallEntry, step.Id, timeout, progress, token);
        }
        else if (step.Kind == "msi")
        {
            var msiPath = ResolvePayload(step.RelativeFile);
            var msi = new RuntimeMsiInstaller(new RuntimeMsiInstallBackend(_runner), diagnostics);
            var installed = await msi.RunAsync(msiPath, step, logFile, timeout, progress, token);
            if (installed.RetainedNewer) _retainedBranches.Add(step.Branch);
            return installed;
        }
        else if (step.Kind == "inf")
        {
            if (_retainedBranches.Contains(step.Branch))
                return new(0, "保留较新 MSI 的登记信息，未用旧 INF 包装版本覆盖。", Skipped: true, RetainedNewer: true);
            var path = ResolvePayload(step.RelativeFile);
            var products = RuntimeUninstallCommand.ReadQuietInfProducts(path);
            if (products.Length == 0 || products.Any(p => RuntimeGuardService.QueryMsiProductState(p) != 5))
                return new(-1, "关联 MSI 产品未确认安装成功，未登记 INF 包装版本。");
            // Same quiet registration as the verified AIO, without invoking its uninstall logic.
            info = new(Path.Combine(Environment.SystemDirectory, "rundll32.exe")) { UseShellExecute = false, CreateNoWindow = true,
                Arguments = $"advpack.dll,LaunchINFSectionEx \"{path}\",Install,,12,N" };
        }
        else if (step.Kind == "exe")
        {
            if (!new[] { "VCRedist2015-2022-x64.exe", "VCRedist2015-2022-x86.exe" }.Contains(step.RelativeFile))
                return new(-1, "不支持的基线安装器");
            info = new(Path.Combine(BackendEnvironment.AssetsRoot, step.RelativeFile)) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "/install", "/quiet", "/norestart", "/log", logFile }) info.ArgumentList.Add(arg);
        }
        else return new(-1, "未知受控安装步骤");
        Log.Info($"运行库步骤启动：id={step.Id}；kind={step.Kind}；file={info.FileName}；args={info.Arguments} {string.Join(" | ", info.ArgumentList)}；日志={logFile}；上限={timeout}");
        var result = await _runner.RunAsync(info, step.Label, timeout, progress, token);
        var details = result.Details;
        try { if (File.Exists(logFile)) details += "\n" + diagnostics.ReadErrorDetails(logFile); }
        catch (Exception ex)
        {
            Log.Warn("运行库步骤日志读取失败：" + ex.Message);
            details += "\n步骤日志无法读取：" + ex.Message;
        }
        result = result with { Details = details };
        return result;
    }

    private string ResolvePayload(string relative)
    {
        var root = Path.GetFullPath(PackageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new IOException("安装资源缺失或路径无效：" + relative);
        return path;
    }
}
