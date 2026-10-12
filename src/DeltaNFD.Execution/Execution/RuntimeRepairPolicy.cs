using System.Diagnostics;

namespace DeltaNFD.Services;

internal sealed record RuntimeServiceState(string Name, int StartType, int State, string Error = "");
internal sealed record RuntimePreflightSnapshot(IReadOnlyList<RuntimeServiceState> Services, bool InstallerBusy,
    bool RebootPending, string Error = "", string WinSxSWarning = "", bool ServicingRebootPending = false)
{
    internal string? Blocker => Error.Length > 0 ? "环境状态无法确认：" + Error
        : Services.Count != 2 ? "安装服务查询不完整，尚未卸载运行库。"
        : Services.FirstOrDefault(s => s.Error.Length > 0 || s.StartType == 4 || s.StartType is not (2 or 3) || s.State is not (1 or 4)) is { } service
            ? service.Error.Length > 0 ? $"服务 {service.Name} 无法查询：{service.Error}" : service.StartType == 4
                ? $"服务 {service.Name} 已禁用；请先确认并恢复服务启动配置，尚未卸载运行库。"
                : $"服务 {service.Name} 的配置/状态无效，未执行运行库操作。"
        : InstallerBusy ? "Windows Installer 正在处理其他事务（可能返回 1618）；等待其结束，不要强杀所有 msiexec。"
        : ServicingRebootPending ? "Windows 系统组件或更新要求重启，请先保存工作并重启后重新检查，尚未卸载运行库。"
        : null; // RebootPending 不再阻塞修复，改为警告
    
    internal string? Warning => (WinSxSWarning.Length > 0 ? "WinSxS 权限警告：" + WinSxSWarning : null)
        ?? (RebootPending ? "Windows 存在待重启记录（可能来自 Windows Update 或其他软件）。若运行库修复失败，建议重启后重试。" : null);
    
    internal static string GetOSDescription()
    {
        var os = Environment.OSVersion;
        return os.Version.Build >= 22000 ? $"Windows 11 Build {os.Version.Build}" : $"Windows 10 Build {os.Version.Build}";
    }
}

internal sealed record RuntimeRepairStep(string Id, string Label, string Branch, string Kind,
    string RelativeFile, VcRedistEntry? UninstallEntry = null);
internal sealed record RuntimeStepResult(int? ExitCode, string Details = "", bool OutcomeUnknown = false,
    bool Skipped = false, bool PostcheckPassed = true, bool RebootPending = false, bool RetainedNewer = false)
{
    internal bool RequiresReboot => RebootPending || ExitCode is 3010 or 1641;
    internal bool Success => !OutcomeUnknown && PostcheckPassed && (Skipped || ExitCode is 0 or 3010 or 1641);
}

internal static class RuntimeRepairPlanner
{
    internal static string[] Targets(VcRedistScanReport scan, RuntimeRepairMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode is RuntimeRepairMode.FullReinstall or RuntimeRepairMode.Legacy2005And2008)
            return (mode == RuntimeRepairMode.Legacy2005And2008 ? new[] { "2005", "2008" }
                : new[] { "2005", "2008", "2010", "2012", "2013", "2026" })
                .SelectMany(y => new[] { y + " (x64)", y + " (x86)" }).ToArray();
        return scan.Suboptimal.Select(s => s.Branch.Replace("2015-2026", "2026", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static List<RuntimeRepairStep> Build(VcRedistScanReport scan, RuntimeRepairMode mode)
    {
        var targets = Targets(scan, mode);
        var steps = new List<RuntimeRepairStep>();
        if (targets.Length == 0) return steps;
        foreach (var entry in UninstallTargets(scan, targets, mode))
            steps.Add(new("uninstall-" + steps.Count, entry.DisplayName, "", "uninstall", "", entry));
        foreach (var target in targets)
        {
            var split = target.Split(' ', 2);
            var year = split[0];
            var arch = split.Length == 2 ? split[1].Trim('(', ')') : "";
            if (!new[] { "2005", "2008", "2010", "2012", "2013", "2026" }.Contains(year)
                || !new[] { "x86", "x64" }.Contains(arch)) throw new InvalidDataException("不支持的修复分支：" + target);
            var names = year switch
            {
                "2005" => new[] { "vcredist.msi" }, "2008" or "2010" => new[] { "vc_red.msi" },
                _ => new[] { "vc_runtimeMinimum_" + arch + ".msi", "vc_runtimeAdditional_" + arch + ".msi" },
            };
            foreach (var file in names) steps.Add(new("install-" + steps.Count, target + " · " + file, target, "msi", Path.Combine(year, arch, file)));
            var inf = year switch { "2005" => "08", "2008" => "09", "2010" => "10", "2012" => "11", "2013" => "12", _ => "14" };
            steps.Add(new("register-" + steps.Count, target + " · 注册安装信息", target, "inf", Path.Combine(year, arch, "vcred" + inf + "_" + arch + ".inf")));
        }
        foreach (var arch in new[] { "x64", "x86" })
            if (targets.Contains("2026 (" + arch + ")"))
                steps.Add(new("baseline-" + arch, "2015–2022 " + arch + " 基线确认", "2026 (" + arch + ")", "exe", "VCRedist2015-2022-" + arch + ".exe"));
        return steps;
    }

    internal static IReadOnlyList<VcRedistEntry> UninstallTargets(VcRedistScanReport scan, string[] targets, RuntimeRepairMode mode)
        => RuntimeBulkUninstall.Preview(scan.All).Entries.Where(e => mode == RuntimeRepairMode.FullReinstall ||
            targets.Contains((new[] { "2005", "2008", "2010", "2012", "2013" }.FirstOrDefault(e.DisplayName.Contains) ?? "2026")
                + " (" + e.Architecture + ")", StringComparer.Ordinal)).ToArray();

    internal static VcRedistScanReport Scope(VcRedistScanReport after, string[] targets, RuntimeRepairMode mode) => new()
    {
        All = after.All, Advice = after.Advice,
        Abnormal = mode == RuntimeRepairMode.FullReinstall || targets.Any(t => t.StartsWith("2026")) ? after.Abnormal : [],
        Suboptimal = after.Suboptimal.Where(s => targets.Contains(s.Branch.Replace("2015-2026", "2026", StringComparison.Ordinal))).ToList(),
    };
}

internal interface IRuntimeRepairEnvironment
{
    Task<IReadOnlyList<RuntimeRepairStep>> PrepareStepsAsync(IReadOnlyList<RuntimeRepairStep> steps, CancellationToken token);
    Task<RuntimePreflightSnapshot> PreflightAsync(CancellationToken token);
    Task<VcRedistScanReport> ScanAsync(CancellationToken token);
    Task<OperationResult> VerifyAsync(IProgress<string>? progress, CancellationToken token);
    Task<RuntimeStepResult> ExecuteAsync(RuntimeRepairStep step, TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
}

internal sealed class RuntimeRepairCoordinator(IRuntimeRepairEnvironment environment, RuntimeRepairDiagnostics diagnostics,
    TimeSpan? budget = null)
{
    internal static readonly TimeSpan TotalBudget = TimeSpan.FromMinutes(26);

    internal async Task<OperationResult> RunAsync(RuntimeRepairMode mode, IProgress<string>? progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(budget ?? TotalBudget);
        var watch = Stopwatch.StartNew();
        var results = new List<object>();
        var packages = new List<RuntimePackageResult>();
        var changed = 0; var reboot = false;
        diagnostics.Save("engine.json", new { SchemaVersion = 2, Engine = "controlled-msi", Mode = mode, Budget = budget ?? TotalBudget });
        try
        {
            if (!Enum.IsDefined(mode)) return OperationResult.Fail("修复模式无效，未操作运行库。");
            var preflight = await environment.PreflightAsync(deadline.Token);
            diagnostics.Save("preflight.json", preflight);
            
            // 如果检测到 Windows Installer 正忙，尝试等待最多 30 秒
            if (preflight.InstallerBusy)
            {
                Log.Info("检测到 Windows Installer 正忙，尝试等待其空闲...");
                progress?.Report("等待其他安装事务结束...");
                if (RuntimeRepairPreflight.WaitForInstallerIdle(TimeSpan.FromSeconds(30)))
                {
                    Log.Info("Windows Installer 已空闲，继续执行。");
                    preflight = await environment.PreflightAsync(deadline.Token);
                    diagnostics.Save("preflight-after-wait.json", preflight);
                }
                else
                {
                    Log.Warn("等待超时，Windows Installer 仍然忙碌。");
                }
            }
            
            if (preflight.Blocker is { } blocker) return Finish(false, blocker);
            
            // WinSxS 权限警告不阻塞执行，但记录到诊断
            if (preflight.Warning is { } warning)
            {
                Log.Warn("运行库修复预检警告：" + warning);
                diagnostics.Save("winsxs-warning.txt", warning);
            }
            var verified = await environment.VerifyAsync(progress, deadline.Token);
            if (!verified.Success) return Finish(false, verified.Message);
            var before = await environment.ScanAsync(deadline.Token);
            diagnostics.Save("before-scan.json", before);
            var targets = RuntimeRepairPlanner.Targets(before, mode);
            
            // 检测 Windows 11 上安装 2005/2008 的兼容性风险
            var legacyWarning = RuntimeRepairPreflight.CheckLegacyRuntimeCompatibility(targets);
            if (legacyWarning.Length > 0)
            {
                Log.Warn("运行库兼容性警告：" + legacyWarning);
                diagnostics.Save("legacy-compat-warning.txt", legacyWarning);
            }
            
            IReadOnlyList<RuntimeRepairStep> steps = RuntimeRepairPlanner.Build(before, mode);
            diagnostics.Save("plan.json", new { Mode = mode, Targets = targets, Steps = steps, LegacyCompatWarning = legacyWarning });
            steps = await environment.PrepareStepsAsync(steps, deadline.Token);
            if (steps.Count == 0) return OperationResult.Skip("没有需要修复的缺失/过旧分支，未卸载或安装任何运行库。");
            var removalVerified = false;
            for (var i = 0; i < steps.Count; i++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (steps[i].Kind != "uninstall" && !removalVerified)
                {
                    var removedScan = await environment.ScanAsync(deadline.Token);
                    diagnostics.Save("after-uninstall-scan.json", removedScan);
                    if (RuntimeRepairPlanner.UninstallTargets(removedScan, targets, mode).Count != 0)
                        return Finish(false, "卸载复检仍发现修复范围内的 C++ 运行库，未启动安装。");
                    removalVerified = true;
                }
                var status = await environment.PreflightAsync(deadline.Token);
                if (status.Blocker is { } reason) return Finish(false, "后续步骤已停止：" + reason);
                
                // 每个步骤前检测并等待 Installer 空闲
                if (status.InstallerBusy)
                {
                    Log.Info($"步骤 {i + 1} 启动前检测到 Installer 忙，等待最多 15 秒。");
                    progress?.Report($"[{i + 1}/{steps.Count}] 等待其他安装事务...");
                    if (!RuntimeRepairPreflight.WaitForInstallerIdle(TimeSpan.FromSeconds(15)))
                    {
                        return Finish(false, $"步骤 {i + 1} 启动前 Windows Installer 持续忙碌，已停止。");
                    }
                    status = await environment.PreflightAsync(deadline.Token);
                    if (status.Blocker is { } blocker2) return Finish(false, "后续步骤已停止：" + blocker2);
                }
                
                var step = steps[i];
                var max = step.Kind == "uninstall" ? RuntimeUnifiedUninstaller.MaximumEntryBudget : TimeSpan.FromMinutes(5);
                var remaining = (budget ?? TotalBudget) - watch.Elapsed;
                if (remaining <= TimeSpan.Zero) return Finish(false, "修复总预算已耗尽，未启动后续步骤。");
                var timeout = remaining < max ? remaining : max;
                var action = step.Kind == "uninstall" ? "卸载" : step.Kind == "inf" ? "登记安装信息" : "安装/修复";
                progress?.Report($"[{i + 1}/{steps.Count}] {action}：{step.Label} · 总耗时 {watch.Elapsed:mm\\:ss}");
                var result = await environment.ExecuteAsync(step, timeout, progress, deadline.Token);
                reboot |= result.RequiresReboot;
                results.Add(new { Step = step, Result = result });
                diagnostics.Save("steps.json", results);
                if (step.Kind == "msi" && result.ExitCode is int code)
                    packages.Add(new(step.RelativeFile, code, step.Id + ".log", result.Details, result.PostcheckPassed));
                diagnostics.Save("package-results.json", packages);
                var baselineConflict = step.Kind == "exe" && RuntimeRepairErrorClassifier.IsVersionConflict(result.ExitCode)
                    && !result.OutcomeUnknown && result.PostcheckPassed;
                if (!result.Success && !baselineConflict)
                {
                    var message = !result.PostcheckPassed && result.ExitCode is 0 or 3010
                        ? "安装器已结束，但产品或关键 DLL 复核未通过：" + result.Details
                            + (result.RequiresReboot ? "\n该组件要求重启，请重启后重新核对；当前未宣称修复成功。" : "")
                        : RuntimeRepairErrorClassifier.Describe(result.ExitCode, result.Details);
                    return Finish(false, step.Label + "：" + message + "\n已停止后续卸载/安装。", result.OutcomeUnknown);
                }
                if (!result.Skipped && !baselineConflict) changed++;
                if (result.ExitCode == 1641) return Finish(false, "安装器已发起重启，未继续启动后续步骤。");
                if (step.Kind == "uninstall" && result.RequiresReboot)
                    return Finish(false, "卸载组件要求重启，未继续卸载或安装；重启后请按保存计划重新检测。");
                // MSI 3010 is aggregated; a newly reported system reboot blocker is checked before the next step.
            }
            var after = await environment.ScanAsync(deadline.Token);
            diagnostics.Save("after-scan.json", after);
            var scoped = RuntimeRepairPlanner.Scope(after, targets, mode);
            var resultAfter = RuntimeRepairDiagnostics.BuildResult(0, packages, [], scoped, reboot, diagnostics.DirectoryPath,
                diagnostics.CollectInstallerEvents());
            var messageAfter = resultAfter.Message;
            if (mode != RuntimeRepairMode.FullReinstall)
                messageAfter = "本次只处理选定分支，未卸载其他运行库。\n" + messageAfter
                    + $"\n全局仍有 {after.Suboptimal.Count} 项版本提醒、{after.Abnormal.Count} 项异常条目；不代表全系列正常。";
            diagnostics.Save("summary.json", new { Result = resultAfter, Mode = mode, Changed = changed, Packages = packages });
            return new OperationResult { Success = resultAfter.Success, RequiresReboot = reboot, Message = messageAfter };
        }
        catch (OperationCanceledException) { return Finish(false, "已取消或达到总时间预算，未启动后续步骤。已有步骤可能改变运行库，需重新检测。"); }
        catch (Exception ex) { Log.Error("运行库受控修复失败", ex); return Finish(false, "修复异常，后续已停止：" + ex.Message); }

        OperationResult Finish(bool success, string message, bool unknown = false)
        {
            diagnostics.CollectInstallerEvents();
            var result = new OperationResult { Success = success, RequiresReboot = reboot, OutcomeUnknown = unknown,
                Message = message + $"\n已确认执行步骤 {changed} 项。诊断目录：{diagnostics.DirectoryPath}"
                    + (diagnostics.Issues.Length == 0 ? "" : "\n诊断采集提醒：" + string.Join("；", diagnostics.Issues.Take(4))) };
            diagnostics.Save("summary.json", new { Result = result, Mode = mode, Steps = results });
            return result;
        }
    }
}

internal static class RuntimeRepairErrorClassifier
{
    internal static bool IsVersionConflict(int? code) => code is 1638 or unchecked((int)0x80070666);

    internal static string Describe(int? code, string details)
    {
        if (code is null) return "结果未确认：" + details;
        if (details.Contains("0x80070422", StringComparison.OrdinalIgnoreCase))
            return "相关服务被禁用/无法启动（0x80070422）；检查 msiserver 与 TrustedInstaller，不要重复全量卸载。";
        if (details.Contains("0x80070005", StringComparison.OrdinalIgnoreCase))
            return "访问被拒绝（0x80070005）；需要 MSI/CBS 日志确定对象，不要批量重置 WinSxS 权限。";
        if (details.Contains("0x80073712", StringComparison.OrdinalIgnoreCase) || details.Contains("0x80073701", StringComparison.OrdinalIgnoreCase))
            return "组件存储损坏或程序集缺失；先核对 CBS 日志及 Windows 组件健康，不自动接管 WinSxS 或删除组件目录。";
        if (code is 1620 or 1619) return "安装包无法打开或格式无效；检查完整解包、路径及安装内容校验。";
        if (code == 1601) return "Windows Installer 服务无法访问；检查服务和系统日志，不反复卸载其他分支。";
        if (code == 1618) return "其他安装事务正在进行（1618）；等待其结束后再检查，不强杀所有 msiexec。";
        if (code is 1612 or 1706) return "原安装源/缓存缺失；需找回对应原安装器，不删除 Installer/Package Cache。";
        if (IsVersionConflict(code)) return "版本冲突（1638 / 0x80070666），不代表安装成功；需结合安装记录和分支版本复检。";
        if (details.Contains("1935", StringComparison.OrdinalIgnoreCase)) return "程序集提交失败（1935），需根据底层 HRESULT 和 CBS 日志定位服务、权限或组件存储问题。";
        if (code == 1603) return "安装器返回通用失败 1603，详细原因见 MSI 日志；不能直接归因于权限。";
        return $"退出码 {code}；{details}";
    }
}
