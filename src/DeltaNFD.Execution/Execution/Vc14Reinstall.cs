using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>单个安装/卸载步骤结果；3010/1641 视为需要重启。</summary>
public sealed record Vc14StepResult(int? ExitCode, bool Unknown = false, string Detail = "")
{
    public bool Success => !Unknown && ExitCode is 0 or 3010;
    public bool Reboot => ExitCode is 3010 or 1641;
}

public interface IVc14ReinstallEnvironment
{
    Vc14Scan Scan();
    string? Blocker();
    void VerifyBaselines();
    void VerifyInstalled();
    string DiagnosticsDirectory { get; }
    void Trace(string stage, object value);
    Task<Vc14StepResult> RemoveAsync(Vc14RemovalPlanItem item, IProgress<string>? progress, CancellationToken token);
    Task<Vc14StepResult> InstallAsync(string arch, IProgress<string>? progress, CancellationToken token);
}

/// <summary>
/// 全新 14.x 全量重装：按用户 2026-10-15 授权，卸载所有检测到的 x86/x64 C++ 14.x 登记
/// （含未知包装，按其登记的卸载命令执行），再安装内置 2015–2022 14.42.34433 基线并复检。
/// 颜色规则是项目游戏兼容性策略；重装不代表已验证帧率改善。
/// </summary>
public static class Vc14Reinstall
{
    public const string BaselineVersion = "14.42.34433.0";
    internal static readonly IReadOnlyDictionary<string, string> BaselineHashes = new Dictionary<string, string>
    {
        ["x64"] = "1821577409C35B2B9505AC833E246376CC68A8262972100444010B57226F0940",
        ["x86"] = "DD1A8BE03398367745A87A5E35BEBDAB00FDAD080CF42AF0C3F20802D08C25D4",
    };
    // Microsoft 14.42.34433 基线 Burn 包装码；已登记时安装阶段改用原包 /repair 补回组件。
    internal static readonly IReadOnlyDictionary<string, string> BaselineBundles = new Dictionary<string, string>
    {
        ["x64"] = "{804e7d66-ccc2-4c12-84ba-476da31d103d}",
        ["x86"] = "{e7802eac-3305-4da0-9378-e55d1ed05518}",
    };

    /// <summary>枚举全部 14.x 登记（MSI 产品、Burn 包装、仅登记卸载命令的未知条目），生成卸载计划。</summary>
    internal static IReadOnlyList<Vc14RemovalPlanItem> BuildRemovalPlan(Vc14Scan scan)
    {
        var plan = new List<Vc14RemovalPlanItem>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in scan.Products)
        {
            plan.Add(new(product.Code, product.Architecture, product.Name, product.Version, "msi", product.Code,
                "msiexec /x " + product.Code, "已核验 MSI 登记：" + product.Name + " · " + product.Version,
                product.Role, product.UpgradeCode));
            covered.Add(product.Code);
        }
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            foreach (var keyName in root?.GetSubKeyNames() ?? [])
            {
                if (covered.Contains(keyName)) continue;
                using var key = root!.OpenSubKey(keyName);
                var name = key?.GetValue("DisplayName") as string ?? "";
                var version = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("BundleVersion") as string ?? "";
                if (!Vc14Diagnostics.Relevant(name, version)) continue;
                covered.Add(keyName);
                var architecture = name.Contains("arm64", StringComparison.OrdinalIgnoreCase) ? "arm64"
                    : Regex.IsMatch(name, @"\bx64\b", RegexOptions.IgnoreCase) ? "x64" : "x86";
                if (architecture == "arm64")
                {
                    plan.Add(new(keyName, architecture, name, version, "unknown", keyName, "无法处理",
                        "ARM64 组件不在本次 x86/x64 重装范围，保留并说明。"));
                    continue;
                }
                var cache = key?.GetValue("BundleCachePath") as string;
                var uninstall = key?.GetValue("QuietUninstallString") as string ?? key?.GetValue("UninstallString") as string ?? "";
                if (Guid.TryParse(keyName, out _) && Vc14PackageTrust.Verify(cache, version, architecture))
                {
                    plan.Add(new(keyName, architecture, name, version, "bundle", cache!,
                        "Burn 包装静默卸载：" + name, "已核验微软签名包装缓存：" + cache));
                    continue;
                }
                if (Guid.TryParse(keyName, out var code) && BaselineBundles.Values.Any(b => b.Equals(keyName, StringComparison.OrdinalIgnoreCase)))
                {
                    plan.Add(new(keyName, architecture, name, version, "baseline-bundle", code.ToString("B"),
                        "安装阶段由原包 /repair 处理", "基线 14.42 包装登记仍在但缓存缺失或未通过核验；不先卸载，安装阶段用原包修复。"));
                    continue;
                }
                if (Guid.TryParse(keyName, out _) && !string.IsNullOrWhiteSpace(uninstall))
                    plan.Add(new(keyName, architecture, name, string.IsNullOrWhiteSpace(version) ? "未知" : version, "registry",
                        uninstall, "按登记卸载命令执行：" + uninstall,
                        "未知包装：无缓存可核验签名。按用户 2026-10-15 授权（严格全部，含未知）执行其登记的静默卸载命令。"));
                else
                    plan.Add(new(keyName, architecture, name, string.IsNullOrWhiteSpace(version) ? "未知" : version, "unknown",
                        keyName, "无法处理", "无法处理：缺少可执行的卸载命令，且身份未经核验；保留并在结果中说明。"));
            }
        }
        return [.. plan.OrderBy(p => p.Kind switch { "msi" => 0, "bundle" => 1, "registry" => 2, _ => 3 })
            .ThenBy(p => p.Architecture).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    internal static string Fingerprint(IReadOnlyList<Vc14RemovalPlanItem> plan) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan.Select(p =>
            new { p.Key, p.Kind, p.Identity, p.Version, p.Role, p.UpgradeCode }).ToArray()))));

    public static Vc14ReinstallPreview Preview(IVc14ReinstallEnvironment environment)
    {
        var scan = environment.Scan();
        var blocker = environment.Blocker();
        try { environment.VerifyBaselines(); } catch (Exception ex) { blocker ??= ex.Message; }
        if (blocker is null && scan.Issues.Count > 0)
            blocker = "14.x 检测存在问题，暂不能重装：\n" + string.Join("\n", scan.Issues);
        var plan = BuildRemovalPlan(scan);
        if (blocker is null && plan.Count == 0)
            blocker = "未发现任何 C++ 14.x 登记，无需重装。";
        return new(scan, plan, Fingerprint(plan), blocker is null, blocker ?? "");
    }

    public static async Task<OperationResult> RunAsync(IVc14ReinstallEnvironment environment, string fingerprint, IProgress<string>? progress, CancellationToken token)
    {
        var changed = false; var reboot = false; var logs = environment.DiagnosticsDirectory;
        OperationResult Fail(string text, bool unknown = false) => new()
        {
            Success = false, IsPartial = changed && !unknown, OutcomeUnknown = unknown, RequiresReboot = reboot,
            DiagnosticsDirectory = logs,
            Message = text + (changed ? "\n已执行部分卸载或安装，请核对结果；不会继续后续步骤或强杀安装器。" : "\n未执行卸载或安装。"),
        };
        try
        {
            token.ThrowIfCancellationRequested();
            var preview = Preview(environment);
            if (!preview.CanRepair) return Fail(preview.Blocker);
            if (fingerprint != preview.Fingerprint) return Fail("14.x 登记清单已变化，请重新预览确认。");
            environment.Trace("confirmed-plan", new { Count = preview.RemovalPlan.Count, preview.Fingerprint });

            // 阶段一：逐项卸载全部可移除的 14.x 登记（含未知包装）。
            foreach (var item in preview.RemovalPlan)
            {
                token.ThrowIfCancellationRequested();
                if (!item.CanRemove) continue; // unknown / baseline-bundle / arm64：保留并说明
                var current = environment.Scan();
                if (current.Issues.Count > 0) return Fail("检测出现问题，已停止：" + string.Join("\n", current.Issues));
                if (environment.Blocker() is { } blocked) return Fail(blocked);
                progress?.Report("卸载 14.x：" + item.Name + "（" + item.Architecture + "）");
                changed = true;
                var step = await environment.RemoveAsync(item, progress, token);
                reboot |= step.Reboot;
                environment.Trace("remove-result", new { item.Key, item.Kind, step.ExitCode, step.Unknown, step.Detail });
                if (!step.Success || step.Reboot)
                    return Fail("14.x 卸载未完成或需要重启：" + item.Name + "\n" + step.Detail, step.Unknown);
            }

            // 阶段二：确认无 14.x MSI 组件残留后，安装双架构基线。
            var cleared = environment.Scan();
            if (cleared.Products.Count > 0)
                return Fail("仍有 14.x MSI 组件未卸载完成，未启动安装：" + string.Join("; ", cleared.Products.Select(p => p.Name)));
            environment.VerifyBaselines();
            foreach (var arch in new[] { "x64", "x86" })
            {
                token.ThrowIfCancellationRequested();
                if (environment.Blocker() is { } blocked) return Fail(blocked);
                progress?.Report("安装 2015–2022 " + arch + " · " + BaselineVersion);
                changed = true;
                var step = await environment.InstallAsync(arch, progress, token);
                reboot |= step.Reboot;
                environment.Trace("install-result", new { arch, step.ExitCode, step.Unknown, step.Detail });
                if (!step.Success || step.Reboot)
                    return Fail(arch + " 基线安装未完成或需要重启。\n" + step.Detail, step.Unknown);
                var installed = environment.Scan();
                environment.Trace("install-postcheck", new { arch, Products = installed.Products.Count, Issues = installed.Issues });
                if (installed.Issues.Count > 0 || installed.Products.Count(p => p.Architecture == arch) != 2
                    || installed.Products.Any(p => p.Architecture == arch && !Vc14Catalog.SameVersion(p.Version, BaselineVersion)))
                    return Fail("安装器已返回成功，但 " + arch + " 的 14.42 组件未确认装齐，已停止后续安装。"
                        + "若旧包装登记仍存在，安装器可能跳过组件，请核对日志后重试。");
            }

            // 阶段三：最终复检（MSI 登记与系统 DLL 实际版本）。
            var after = environment.Scan();
            var complete = after.Issues.Count == 0 && after.Products.Count == 4 && Vc14Catalog.Families.All(f =>
                after.Products.Count(p => p.Architecture == f.Arch && p.Role == f.Role && Vc14Catalog.SameVersion(p.Version, BaselineVersion)) == 1);
            if (!complete) return Fail("安装后未确认 x64/x86 的四个 14.42 基线组件，不能报告修复成功。");
            environment.VerifyInstalled();
            environment.Trace("verified-after", after);
            var skipped = preview.RemovalPlan.Where(p => !p.CanRemove).ToArray();
            return new OperationResult { Success = true, DiagnosticsDirectory = logs,
                Message = "已卸载全部检测到的可移除 C++ 14.x 登记，并安装/复检 2015–2022 x64/x86 " + BaselineVersion + "。"
                    + (skipped.Length > 0 ? "\n以下登记无法自动处理，已保留：\n" + string.Join("\n", skipped.Select(p => p.Name + " · " + p.Detail)) : "")
                    + (after.HasAbnormal || after.HasWarning ? "\n仍有其他包装登记或兼容性提醒，请核对详情。" : "")
                    + "\n未处理 2005–2013、SDK/Debug、ARM64 或 DirectX；此结果不代表已验证游戏帧率改善。" };
        }
        catch (OperationCanceledException) { return Fail("已取消等待或后续步骤。", changed); }
        catch (Exception ex) { return Fail("14.x 全量重装已停止：" + ex.Message, changed); }
    }
}

internal sealed class Vc14ReinstallEnvironment : IVc14ReinstallEnvironment
{
    private readonly string root = AppDataPaths.Root;
    private readonly string logs = Path.Combine(Log.LogDirectory, "vc14-reinstall-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
    public string DiagnosticsDirectory => logs;
    public void Trace(string stage, object value)
    {
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, stage + "-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(value));
    }
    public Vc14Scan Scan() => Vc14Catalog.Scan();
    public string? Blocker()
    {
        if (!ElevationHelper.IsElevated) return ElevationHelper.NotElevatedMessage;
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            return "此修复只支持 x64 Windows 的 x86/x64 C++ 组件。";
        var pending = new RuntimeInstallationReview(root).InspectPending(RuntimeInstallationReview.InstallerBusy(), out var ended);
        if (pending is not null || ended) return pending ?? "上次安装结果待核对，请先核对未完成操作。";
        if (RuntimeInstallationReview.InstallerBusy()) return "Windows Installer 仍在执行事务，请结束后重试。";
        var guard = new RuntimeGuardService().GetStatusAsync().GetAwaiter().GetResult();
        if (guard.IfeoCount > 0 || guard.Ue4PrereqDenied || guard.IfeoRestorePending || guard.Ue4RestorePending || guard.GuardIssue.Length > 0)
            return "请先关闭运行库防护并核对安装器拦截；不会自动关闭防护。";
        return null;
    }
    public void VerifyBaselines()
    {
        foreach (var item in Vc14Reinstall.BaselineHashes)
        {
            var path = Baseline(item.Key);
            using var stream = File.OpenRead(path);
            if (Convert.ToHexString(SHA256.HashData(stream)) != item.Value
                || FileVersionInfo.GetVersionInfo(path).FileVersion != Vc14Reinstall.BaselineVersion)
                throw new IOException("内置 14.42 原包校验失败：" + item.Key);
        }
    }
    public void VerifyInstalled()
    {
        foreach (var arch in new[] { "x64", "x86" })
        {
            var system = Environment.GetFolderPath(arch == "x64" ? Environment.SpecialFolder.System : Environment.SpecialFolder.SystemX86);
            foreach (var dll in arch == "x64" ? new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" } : new[] { "msvcp140.dll", "vcruntime140.dll" })
            {
                var file = Path.Combine(system, dll);
                var version = FileVersionInfo.GetVersionInfo(file);
                if (version.FileMajorPart != 14 || version.FileMinorPart != 42 || version.FileBuildPart != 34433)
                    throw new IOException(arch + " " + dll + " 实际版本未确认是 14.42.34433，不能报告修复成功。");
            }
        }
    }
    internal static string Baseline(string arch) => Path.Combine(BackendEnvironment.AssetsRoot, "Vc14Baseline14.42", "vc_redist." + arch + ".exe");

    public async Task<Vc14StepResult> RemoveAsync(Vc14RemovalPlanItem item, IProgress<string>? progress, CancellationToken token)
    {
        if (item.Kind == "msi")
        {
            var family = Vc14Catalog.Families.SingleOrDefault(f => f.Arch == item.Architecture
                && f.Upgrade.Equals(item.UpgradeCode, StringComparison.OrdinalIgnoreCase));
            if (family == default) throw new IOException("待卸载 MSI 的升级家族无法确认：" + item.Name);
            var current = Vc14Catalog.Read(item.Key, item.Architecture, family.Role, family.Upgrade)
                ?? throw new IOException("待卸载 MSI 已不在安装状态：" + item.Name);
            if (!Vc14Catalog.SameVersion(current.Version, item.Version))
                throw new IOException("待卸载 MSI 身份已变化：" + item.Name);
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"))
                { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "/x", item.Key, "/qn", "/norestart", "/L*V", NewLog(item.Key.Trim('{', '}')) })
                info.ArgumentList.Add(argument);
            return await RunProcessAsync(info, progress, token, TimeSpan.FromMinutes(5));
        }
        if (item.Kind == "bundle")
        {
            var info = new ProcessStartInfo(item.Identity) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "/uninstall", "/quiet", "/norestart", "/log", NewLog("bundle-" + item.Key.Trim('{', '}')) })
                info.ArgumentList.Add(argument);
            return await RunProcessAsync(info, progress, token, TimeSpan.FromMinutes(10));
        }
        if (item.Kind == "registry")
        {
            // 登记的卸载命令按原样执行：不猜测参数、不静默改写、不强杀。
            var command = item.Identity.Trim();
            var marker = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            var executable = marker >= 0 ? command[..(marker + 4)].Trim('"', ' ') : command;
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                Arguments = marker >= 0 ? command[(marker + 4)..].Trim() : "",
            };
            return await RunProcessAsync(info, progress, token, TimeSpan.FromMinutes(10));
        }
        throw new IOException("不支持的卸载类型：" + item.Kind);
    }

    public Task<Vc14StepResult> InstallAsync(string arch, IProgress<string>? progress, CancellationToken token)
    {
        var action = BaselineBundleRegistered(arch) ? "/repair" : "/install";
        Trace("verified-install-command", new { arch, action });
        progress?.Report(arch + (action == "/repair" ? "：旧 14.42 包装登记仍在，执行原包修复以补回组件。" : "：执行原包安装。"));
        var info = new ProcessStartInfo(Baseline(arch)) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { action, "/quiet", "/norestart", "/log", NewLog("install-" + arch) })
            info.ArgumentList.Add(argument);
        return RunProcessAsync(info, progress, token, TimeSpan.FromMinutes(10));
    }

    private static bool BaselineBundleRegistered(string arch)
    {
        if (!Vc14Reinstall.BaselineBundles.TryGetValue(arch, out var id)) throw new ArgumentException("不支持的原包架构。");
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + id);
            if (key is null) continue;
            var version = key.GetValue("BundleVersion") as string ?? "";
            if (!Vc14Catalog.SameVersion(version, Vc14Reinstall.BaselineVersion))
                throw new IOException("已登记的 14.42 包装版本异常：" + arch + " · " + version);
            return true;
        }
        return false;
    }

    private string NewLog(string label)
    {
        Directory.CreateDirectory(logs);
        var safe = string.Concat(label.TakeWhile(char.IsLetterOrDigit));
        return Path.Combine(logs, (safe.Length > 0 ? safe : "installer") + ".log");
    }

    /// <summary>启动安装/卸载进程并等待退出；全程写入活动事务记录，崩溃后可由核对入口复核，不强杀。</summary>
    private async Task<Vc14StepResult> RunProcessAsync(ProcessStartInfo info, IProgress<string>? progress, CancellationToken token, TimeSpan waitLimit)
    {
        token.ThrowIfCancellationRequested();
        var active = Path.Combine(root, "runtime-repair-active.json");
        Directory.CreateDirectory(root);
        using var owner = Process.GetCurrentProcess();
        var id = Guid.NewGuid().ToString("D");
        object Record(string stage, int pid, long ticks) => new
        {
            Id = id, Stage = stage, Pid = pid, StartTicks = ticks, Executable = info.FileName,
            OwnerPid = owner.Id, OwnerStartTicks = owner.StartTime.ToUniversalTime().Ticks, CreatedUtc = DateTimeOffset.UtcNow,
        };
        using (var file = new FileStream(active, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(file, Record("prepared", 0, 0));
        Process? process = null;
        try
        {
            process = Process.Start(info) ?? throw new IOException("安装器未启动。");
            var temporary = active + "." + id + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Record("running", process.Id, process.StartTime.ToUniversalTime().Ticks)));
            File.Move(temporary, active, true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(waitLimit);
            progress?.Report("正在等待安装事务，PID " + process.Id);
            var wait = process.WaitForExitAsync(timeout.Token);
            while (!wait.IsCompleted)
            {
                var tick = Task.Delay(TimeSpan.FromSeconds(5), timeout.Token);
                if (await Task.WhenAny(wait, tick) == wait) break;
                await tick;
                progress?.Report("安装事务仍运行，正在等待 PID " + process.Id + "；日志：" + logs);
            }
            await wait;
            if (RuntimeInstallationReview.InstallerBusy())
                return new(process.ExitCode, true, "进程已退出，但 MSI 事务仍忙，记录保留。日志：" + logs);
            var archive = Path.Combine(root, "runtime-repair-history");
            Directory.CreateDirectory(archive);
            File.Move(active, Path.Combine(archive, id + "-exit-" + process.ExitCode + ".json"));
            return new(process.ExitCode, false, "退出码 " + process.ExitCode + "；日志：" + logs);
        }
        catch (Exception ex)
        {
            return new(null, true, "安装器启动或结果未确认，未强杀进程，记录保留：" + ex.Message + "；日志：" + logs);
        }
        finally { process?.Dispose(); }
    }
}
