using System.Diagnostics;

namespace DeltaNFD.Services;

internal sealed class RuntimeRepairEnvironment(RuntimeGuardService service, RuntimeRepairDiagnostics diagnostics) : IRuntimeRepairEnvironment
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
        // 自动尝试修复安装服务状态问题
        if (snapshot.Blocker is not null && snapshot.Services.Any(s => s.StartType == 4 || (s.State is < 1 or > 4)))
        {
            Log.Info("检测到安装服务状态异常，尝试自动修复。");
            var repairResult = RuntimeRepairPreflight.TryRepairInstallerService();
            if (repairResult.Success)
            {
                snapshot = RuntimeRepairPreflight.Read();
                if (snapshot.Error.Length == 0)
                {
                    var pending = _runner.CheckPending(snapshot.InstallerBusy);
                    if (pending is not null) snapshot = snapshot with { Error = pending };
                }
                Log.Info("安装服务状态已自动修复，重新预检：" + System.Text.Json.JsonSerializer.Serialize(snapshot));
            }
            else
            {
                Log.Warn("安装服务状态自动修复失败：" + repairResult.Message);
            }
        }
        Log.Info("运行库环境预检：" + System.Text.Json.JsonSerializer.Serialize(snapshot));
        return Task.FromResult(snapshot);
    }

    public async Task<VcRedistScanReport> ScanAsync(CancellationToken token)
        => await service.ScanVcRedistsAsync().WaitAsync(token);

    public async Task<OperationResult> VerifyAsync(IProgress<string>? progress, CancellationToken token)
        => await Task.Run(() => RuntimeGuardService.VerifyRepairPackage(BackendEnvironment.AssetsRoot, PackageRoot, progress), token).WaitAsync(token);

    public Task PrepareStepsAsync(IReadOnlyList<RuntimeRepairStep> steps, CancellationToken token)
    {
        var uninstaller = new RuntimeUnifiedUninstaller(new RuntimeUnifiedUninstallBackend(diagnostics), diagnostics,
            Path.Combine(AppDataPaths.Root, "runtime-unified-uninstall-pending"));
        foreach (var step in steps.Where(s => s.Kind == "uninstall"))
        {
            token.ThrowIfCancellationRequested();
            var plan = uninstaller.Prepare(step.UninstallEntry ?? throw new InvalidDataException("缺少卸载条目。"));
            diagnostics.Save(step.Id + "-unified-plan.json", plan);
        }
        return Task.CompletedTask;
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
