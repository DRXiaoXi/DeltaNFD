using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeltaNFD.Services;

internal sealed record Vc14Step(int? ExitCode, bool Unknown = false, string Detail = "")
{
    internal bool Success => !Unknown && ExitCode is 0 or 3010;
    internal bool Reboot => ExitCode is 3010 or 1641;
}
internal interface IVc14Environment
{
    Vc14Scan Scan();
    string? Blocker();
    void VerifyBaselines();
    void VerifyInstalled();
    string DiagnosticsDirectory => "";
    void Trace(string stage, object value) { }
    Task<OperationResult> CleanLegacyAsync(string fingerprint, IProgress<string>? progress, CancellationToken token) => Task.FromResult(OperationResult.Ok("未发现旧包装。"));
    Task<Vc14Step> RemoveAsync(Vc14Entry entry, IProgress<string>? progress, CancellationToken token);
    Task<Vc14Step> InstallAsync(string arch, IProgress<string>? progress, CancellationToken token);
}
internal static class Vc14Repair
{
    internal static Vc14Preview Preview(IVc14Environment environment)
    {
        var scan = environment.Scan(); var blocker = environment.Blocker();
        try { environment.VerifyBaselines(); } catch (Exception ex) { blocker ??= ex.Message; }
        if (scan.Issues.Count > 0) blocker ??= string.Join("\n", scan.Issues);
        return new(scan, Vc14Catalog.Fingerprint(scan), blocker is null, blocker ?? "");
    }
    internal static async Task<OperationResult> RunAsync(IVc14Environment environment, string fingerprint, IProgress<string>? progress, CancellationToken token)
    {
        var changed = false; var reboot = false;
        OperationResult Fail(string text, bool unknown = false) => new()
        {
            Success = false, IsPartial = changed && !unknown, OutcomeUnknown = unknown, RequiresReboot = reboot,
            DiagnosticsDirectory = environment.DiagnosticsDirectory,
            Message = text + (changed ? "\n已执行部分运行库操作，请核对结果；不会继续安装或强杀安装器。" : "\n未执行卸载或安装。"),
        };
        try
        {
            token.ThrowIfCancellationRequested();
            var preview = Preview(environment);
            if (!preview.CanRepair) return Fail(preview.Blocker);
            if (fingerprint != preview.Fingerprint) return Fail("14.x 安装清单已变化，请重新预览确认。");
            environment.Trace("confirmed-plan", preview);
            var remaining = preview.Scan.Products.ToList();
            foreach (var entry in preview.Scan.Products.OrderByDescending(p => p.Role == "Additional"))
            {
                token.ThrowIfCancellationRequested();
                var current = environment.Scan();
                if (current.Issues.Count > 0 || current.Legacy.Fingerprint != preview.Scan.Legacy.Fingerprint
                    || Vc14Catalog.Fingerprint(current, false) != Vc14Catalog.Fingerprint(new(remaining, [], []), false))
                    return Fail("卸载期间组件身份或安装清单发生变化，已停止。");
                if (environment.Blocker() is { } blocked) return Fail(blocked);
                progress?.Report("仅卸载 14.x：" + entry.Name);
                changed = true;
                var step = await environment.RemoveAsync(entry, progress, token); reboot |= step.Reboot;
                environment.Trace("remove-result", new { entry, step });
                if (!step.Success || step.Reboot) return Fail("14.x 卸载未完成或需要重启：" + step.Detail, step.Unknown);
                remaining.Remove(entry);
            }
            var removed = environment.Scan();
            if (removed.Issues.Count > 0 || removed.Products.Count > 0) return Fail("仍有 14.x 组件或安装状态无法确认，未启动安装。");
            var installedArchitectures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var arch in new[] { "x64", "x86" })
            {
                token.ThrowIfCancellationRequested();
                var current = environment.Scan();
                if (current.Issues.Count > 0 || current.Legacy.Fingerprint != preview.Scan.Legacy.Fingerprint || current.Products.Count != installedArchitectures.Count * 2
                    || current.Products.Any(p => !installedArchitectures.Contains(p.Architecture) || !Vc14Catalog.SameVersion(p.Version, "14.42.34433.0")))
                    return Fail("安装阶段组件发生变化或前一步组件未确认，已停止后续安装。");
                if (environment.Blocker() is { } blocked) return Fail(blocked);
                environment.VerifyBaselines();
                progress?.Report("安装工具原有的 2015–2022 " + arch + " · 14.42.34433.0");
                changed = true;
                var step = await environment.InstallAsync(arch, progress, token); reboot |= step.Reboot;
                environment.Trace("install-result", new { arch, step });
                if (!step.Success || step.Reboot) return Fail("14.42 安装未完成或需要重启：" + step.Detail, step.Unknown);
                var installed = environment.Scan();
                environment.Trace("install-postcheck", new { arch, installed });
                if (!Vc14InstallPolicy.ArchitectureComplete(installed, arch))
                    return Fail("安装器已返回成功，但 " + arch + " 的 14.42 MSI 组件未装齐，已停止。"
                        + "旧包装登记仍存在时，/install 可能不安装缺失组件，应由原包执行 /repair。请核对日志后再恢复运行库。");
                installedArchitectures.Add(arch);
            }
            var after = environment.Scan();
            var verified = after.Issues.Count == 0 && after.Products.Count == 4 && Vc14Catalog.Families.All(f =>
                after.Products.Count(p => p.Architecture == f.Arch && p.Role == f.Role && Vc14Catalog.SameVersion(p.Version, "14.42.34433.0")) == 1);
            if (!verified) return Fail("安装后未确认 x86/x64 的四个 14.42.34433 组件，不能报告修复成功。");
            environment.VerifyInstalled();
            if (after.Legacy.Entries.Count > 0 || after.Legacy.Issues.Count > 0)
            {
                if (after.Legacy.Fingerprint != preview.Scan.Legacy.Fingerprint) return Fail("旧包装在修复期间发生变化，未自动清理；当前 14.42 已装回，请重新预览旧包装处理。");
                var cleanup = await environment.CleanLegacyAsync(after.Legacy.Fingerprint, progress, token);
                environment.Trace("legacy-cleanup-result", cleanup);
                if (!cleanup.Success) return new OperationResult { Success = false, IsPartial = !cleanup.OutcomeUnknown,
                    OutcomeUnknown = cleanup.OutcomeUnknown, DiagnosticsDirectory = cleanup.DiagnosticsDirectory,
                    Message = "14.42 组件与核心 DLL 已确认，旧包装处理未完成。\n" + cleanup.Message };
                after = environment.Scan();
                environment.VerifyInstalled();
            }
            environment.Trace("verified-after", after);
            return new OperationResult { Success = true, DiagnosticsDirectory = environment.DiagnosticsDirectory,
                Message = "14.x 可再发行 MSI 组件卸载完成，已安装并复检原有 2015–2022 x64/x86 14.42.34433。"
                + (after.HasAbnormal || after.HasWarning ? "\n仍有其他包装登记或兼容性提醒，请核对详情。" : "\n已核验的旧 INF 包装残留处理完成。")
                + "\n未处理 2005–2013、SDK/Debug 或 DirectX；此结果不代表已验证游戏帧率改善。" };
        }
        catch (OperationCanceledException) { return Fail("已取消等待或后续步骤。", changed); }
        catch (Exception ex) { return Fail("14.x 修复已停止：" + ex.Message, changed); }
    }
}

internal sealed class Vc14Environment : IVc14Environment
{
    internal static readonly IReadOnlyDictionary<string, string> Hashes = new Dictionary<string, string>
    {
        ["x64"] = "1821577409C35B2B9505AC833E246376CC68A8262972100444010B57226F0940",
        ["x86"] = "DD1A8BE03398367745A87A5E35BEBDAB00FDAD080CF42AF0C3F20802D08C25D4",
    };
    private readonly string root = AppDataPaths.Root;
    private readonly string logs = Path.Combine(Log.LogDirectory, "vc14-repair-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
    public string DiagnosticsDirectory => logs;
    public void Trace(string stage, object value)
    {
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, stage + "-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(value));
    }
    public Vc14Scan Scan() => Vc14Catalog.Scan() with { Legacy = new Vc14LegacyEnvironment().Detect() };
    public Task<OperationResult> CleanLegacyAsync(string fingerprint, IProgress<string>? progress, CancellationToken token)
        => Task.FromResult(Vc14LegacyCleanup.Run(new Vc14LegacyEnvironment(), fingerprint, progress, token));
    public string? Blocker()
    {
        if (!ElevationHelper.IsElevated) return ElevationHelper.NotElevatedMessage;
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            return "此修复只支持 x64 Windows 的 x86/x64 C++ 组件。";
        var pending = new RuntimeInstallationReview(root).InspectPending(RuntimeInstallationReview.InstallerBusy(), out var ended);
        if (pending is not null || ended) return pending ?? "上次安装结果待核对，请先核对旧版未完成操作。";
        if (RuntimeInstallationReview.InstallerBusy()) return "Windows Installer 仍在执行事务，请结束后重试。";
        if (Vc14Catalog.DependencyIssue(Vc14Catalog.Scan().Products) is { } dependency) return dependency;
        var guard = new RuntimeGuardService().GetStatusAsync().GetAwaiter().GetResult();
        if (guard.IfeoCount > 0 || guard.Ue4PrereqDenied || guard.IfeoRestorePending || guard.Ue4RestorePending || guard.GuardIssue.Length > 0)
            return "请先关闭运行库防护并核对安装器拦截；不会自动关闭防护。";
        return null;
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
    public void VerifyBaselines()
    {
        foreach (var item in Hashes)
        {
            var path = Baseline(item.Key);
            using var stream = File.OpenRead(path);
            if (Convert.ToHexString(SHA256.HashData(stream)) != item.Value || FileVersionInfo.GetVersionInfo(path).FileVersion != "14.42.34433.0")
                throw new IOException("内置 14.42 原包校验失败：" + item.Key);
            // Resolve the registered-bundle maintenance action before any uninstall, and again before launch.
            _ = Vc14InstallPolicy.ReadAction(item.Key);
        }
    }
    public Task<Vc14Step> RemoveAsync(Vc14Entry entry, IProgress<string>? progress, CancellationToken token)
    {
        var current = Vc14Catalog.Read(entry.Code, entry.Architecture, entry.Role, entry.UpgradeCode);
        if (current != entry) throw new IOException("待卸载 MSI 身份已变化。");
        var clients = Vc14Catalog.ApprovedClients(entry);
        var arguments = new List<string> { "/x", Guid.Parse(entry.Code).ToString("B"), "/qn", "/norestart", "/L*V", NewLog(entry.Code) };
        // WiX v3 SplitIgnoredDependents accepts exact, semicolon-separated keys; never IGNOREDEPENDENCIES=ALL.
        if (clients.Count > 0) arguments.Add("IGNOREDEPENDENCIES=" + string.Join(";", clients));
        Trace("verified-uninstall-command", new { entry, clients, arguments });
        return RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"),
            arguments, progress, token);
    }
    public Task<Vc14Step> InstallAsync(string arch, IProgress<string>? progress, CancellationToken token)
    {
        var action = Vc14InstallPolicy.ReadAction(arch);
        var arguments = new[] { action, "/quiet", "/norestart", "/log", NewLog(arch) };
        Trace("verified-install-command", new { arch, action, arguments });
        progress?.Report(arch + (action == "/repair" ? "：原有 14.42 包装仍在，执行原包修复以补回组件。" : "：执行原包安装。"));
        return RunAsync(Baseline(arch), arguments, progress, token);
    }
    private string NewLog(string label)
    {
        Directory.CreateDirectory(logs);
        return Path.Combine(logs, label.Trim('{', '}') + ".log");
    }
    private async Task<Vc14Step> RunAsync(string executable, IReadOnlyList<string> arguments, IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var active = Path.Combine(root, "runtime-repair-active.json");
        Directory.CreateDirectory(root);
        using var owner = Process.GetCurrentProcess();
        var id = Guid.NewGuid().ToString("D");
        object Record(string stage, int pid, long ticks) => new { Id = id, Stage = stage, Pid = pid, StartTicks = ticks, Executable = executable,
            OwnerPid = owner.Id, OwnerStartTicks = owner.StartTime.ToUniversalTime().Ticks, CreatedUtc = DateTimeOffset.UtcNow };
        using (var file = new FileStream(active, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) JsonSerializer.Serialize(file, Record("prepared", 0, 0));
        Process? process = null;
        try
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            process = Process.Start(info) ?? throw new IOException("安装器未启动。");
            var temporary = active + "." + id + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Record("running", process.Id, process.StartTime.ToUniversalTime().Ticks)));
            File.Move(temporary, active, true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
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
            if (RuntimeInstallationReview.InstallerBusy()) return new(process.ExitCode, true, "进程已退出，但 MSI 事务仍忙，记录保留。日志：" + logs);
            var archive = Path.Combine(root, "runtime-repair-history"); Directory.CreateDirectory(archive);
            File.Move(active, Path.Combine(archive, id + "-exit-" + process.ExitCode + ".json"));
            return new(process.ExitCode, false, "退出码 " + process.ExitCode + "；日志：" + logs);
        }
        catch (Exception ex) { return new(null, true, "安装器启动或结果未确认，未强杀进程，记录保留：" + ex.Message + "；日志：" + logs); }
        finally { process?.Dispose(); }
    }
}
