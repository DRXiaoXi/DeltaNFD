using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeltaNFD.Services;

internal sealed record DirectXPayloadFile(string Path, long Length, string Sha256);
internal sealed record DirectXProbe(string Name, string Architecture, string Version, string Sha256, string Cab);
internal sealed record DirectXPayload(int SchemaVersion, string Origin, string RedistSha256,
    IReadOnlyList<DirectXPayloadFile> Files, IReadOnlyList<DirectXProbe> Probes);
internal sealed record DirectXProbeResult(string Name, string Architecture, bool Passed, string Detail);

internal interface IRuntimeCompatibilityEnvironment
{
    Task<OperationResult> VerifyAsync(IProgress<string>? progress, CancellationToken token);
    Task<IReadOnlyList<DirectXProbeResult>> ProbeAsync(CancellationToken token);
    Task<RuntimeStepResult> InstallDirectXAsync(TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
    Task<RuntimeStepResult> RepairCppAsync(RuntimeRepairStep step, TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
    Task<VcRedistScanReport> ScanCppAsync(CancellationToken token);
    Task<RuntimeStepResult> UninstallCppAsync(VcRedistEntry entry, TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
}

internal sealed class RuntimeCompatibilityRepair(IRuntimeCompatibilityEnvironment environment,
    RuntimeRepairDiagnostics diagnostics, TimeSpan? totalBudget = null)
{
    internal static readonly TimeSpan TotalBudget = TimeSpan.FromMinutes(80);
    internal static IReadOnlyList<RuntimeRepairStep> CppPlan() => RuntimeRepairPlanner.Build(
        new VcRedistScanReport { All = [], Abnormal = [], Suboptimal = [], Advice = "" }, RuntimeRepairMode.FullReinstall)
        .Where(s => s.Kind == "msi").ToArray();

    internal async Task<OperationResult> RunAsync(IProgress<string>? progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var budget = totalBudget ?? TotalBudget;
        deadline.CancelAfter(budget);
        var clock = Stopwatch.StartNew();
        var issues = new List<string>(); var results = new List<object>();
        var completed = 0; var reboot = false; var dxPassed = 0; var dxTotal = 0;
        var removed = 0;
        var dxVerified = false;
        TimeSpan Remaining(TimeSpan cap)
        {
            deadline.Token.ThrowIfCancellationRequested(); var left = budget - clock.Elapsed;
            if (left <= TimeSpan.Zero) throw new OperationCanceledException(deadline.Token);
            return left < cap ? left : cap;
        }
        OperationResult Finish(bool unknown = false)
        {
            var result = new OperationResult
            {
                Success = issues.Count == 0 && !unknown, IsPartial = (completed > 0 || removed > 0) && (issues.Count > 0 || unknown),
                OutcomeUnknown = unknown, RequiresReboot = reboot,
                Message = (issues.Count == 0 && !unknown ? "兼容性修复及组件复检完成。" : "兼容性修复未全部完成。")
                    + (dxVerified ? $"\nDirectX 旧组件：{dxPassed}/{dxTotal} 项复检通过。" : "\nDirectX 尚未完成安装后复检。")
                    + $"\n修复前已处理 C++ 卸载条目：{removed}；安装包复检通过：{completed}。"
                    + (issues.Count > 0 ? "\n" + string.Join("\n", issues.Distinct().Take(12)) : "")
                    + (reboot ? "\n本次组件安装要求重启，请重启后重新检测。" : "")
                    + "\n诊断目录：" + diagnostics.DirectoryPath,
            };
            diagnostics.Save("summary.json", result); return result;
        }
        try
        {
            diagnostics.Save("engine.json", new { Engine = "compatibility-directx-cpp", Budget = budget });
            var ready = await environment.VerifyAsync(progress, deadline.Token);
            if (!ready.Success) return ready;
            var before = await environment.ProbeAsync(deadline.Token);
            diagnostics.Save("directx-before.json", before);
            if (before.Count == 0) { issues.Add("DirectX 检测清单为空，未开始安装。"); return Finish(); }
            var cppBefore = await environment.ScanCppAsync(deadline.Token);
            var removal = await RuntimePreUninstall.RunAsync(cppBefore, environment.UninstallCppAsync, Remaining, diagnostics, progress, deadline.Token);
            removed = removal.Completed; reboot |= removal.Reboot;
            if (removal.Stopped is { } stopped)
            { issues.Add("修复前运行库卸载未全部完成，未启动 DirectX/C++ 安装：" + stopped.Details); return Finish(stopped.OutcomeUnknown); }
            if (before.Any(p => !p.Passed))
            {
                progress?.Report("正在安装／修复微软 DirectX 旧版游戏组件…");
                var dx = await environment.InstallDirectXAsync(Remaining(TimeSpan.FromMinutes(10)), progress, deadline.Token);
                reboot |= dx.RequiresReboot;
                diagnostics.Save("directx-install.json", dx);
                if (dx.OutcomeUnknown || dx.ExitCode is 1641 or 1618 || dx.RequiresReboot)
                { issues.Add("DirectX 执行尚未安全收尾：" + dx.Details); return Finish(dx.OutcomeUnknown); }
                if (!dx.Success) issues.Add("DirectX 安装器未成功：" + dx.ExitCode + "；" + dx.Details);
            }
            var after = await environment.ProbeAsync(deadline.Token);
            dxTotal = before.Count; dxPassed = after.Count(p => p.Passed);
            dxVerified = true;
            diagnostics.Save("directx-after.json", after);
            issues.AddRange(after.Where(p => !p.Passed).Select(p => $"DirectX {p.Architecture}/{p.Name}：{p.Detail}"));
            if (!after.Select(p => p.Architecture + ":" + p.Name).Order().SequenceEqual(before.Select(p => p.Architecture + ":" + p.Name).Order()))
                issues.Add("DirectX 复检项目不完整。");
            var plan = CppPlan();
            diagnostics.Save("cpp-plan.json", plan);
            var stepIndex = 0;
            foreach (var step in plan)
            {
                progress?.Report($"兼容性 C++ 修复 [{++stepIndex}/{plan.Count}]：" + step.Label);
                var result = await environment.RepairCppAsync(step, Remaining(TimeSpan.FromMinutes(5)), progress, deadline.Token);
                results.Add(new { Step = step, Result = result }); diagnostics.Save("cpp-results.json", results);
                reboot |= result.RequiresReboot;
                if (result.Success) completed++; else issues.Add(step.Label + "：" + result.Details + "；退出码=" + result.ExitCode);
                if (result.OutcomeUnknown || result.ExitCode is 1641 or 1618 || result.RequiresReboot)
                { issues.Add("安装事务需要等待或已发起重启，后续 C++ 步骤未完成。"); return Finish(result.OutcomeUnknown); }
            }
            return Finish();
        }
        catch (OperationCanceledException) { issues.Add("已停止后续步骤，未把取消当成恢复成功。"); return Finish(); }
        catch (Exception ex) { issues.Add("修复异常：" + ex.Message); return Finish(); }
    }
}

internal sealed class RuntimeCompatibilityEnvironment(RuntimeRepairDiagnostics diagnostics, bool strongRepairMode = false) : IRuntimeCompatibilityEnvironment
{
    private const string ManifestHash = "4F47FEC83E08A38738CC8C72D020BB0CF3749223A5E95279BD246E2259CAB301";
    private readonly RuntimeRepairProcessRunner _runner = new(AppDataPaths.Root);
    private DirectXPayload? _payload;
    private string DirectXRoot => Path.Combine(BackendEnvironment.AssetsRoot, "DirectXRepair");
    private string CppRoot => Path.Combine(BackendEnvironment.AssetsRoot, "VCRedistRepair");
    public Task<VcRedistScanReport> ScanCppAsync(CancellationToken token) => new RuntimeGuardService().ScanVcRedistsAsync().WaitAsync(token);
    public async Task<RuntimeStepResult> UninstallCppAsync(VcRedistEntry entry, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
    {
        var environment = new RuntimeRepairEnvironment(new RuntimeGuardService(), diagnostics, repairInstallerServices: false);
        if (await new RuntimeGuardService().RepairProtectionBlockerAsync() is { } protection) return new(-1, protection);
        if ((await environment.PreflightAsync(token)).Blocker is { } blocker) return new(-1, blocker);
        return await environment.ExecuteAsync(new("compat-uninstall-" + Guid.NewGuid().ToString("N"), entry.DisplayName, "", "uninstall", "", entry), timeout, progress, token);
    }

    public Task<OperationResult> VerifyAsync(IProgress<string>? progress, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (RuntimeRepairPreflight.Read().Blocker is { } blocker) return Task.FromResult(OperationResult.Fail(blocker));
            var pending = _runner.CheckPending(RuntimeRepairPreflight.InstallerBusy());
            if (pending is not null) return Task.FromResult(OperationResult.Fail(pending));
            var cpp = RuntimeGuardService.VerifyRepairPackage(BackendEnvironment.AssetsRoot, CppRoot, progress);
            if (!cpp.Success) return Task.FromResult(cpp);
            foreach (var step in RuntimeCompatibilityRepair.CppPlan())
            {
                token.ThrowIfCancellationRequested();
                var msiPath = Path.Combine(CppRoot, step.RelativeFile);
                var package = RuntimeMsiMetadata.ReadPackage(msiPath);
                RuntimeMsiInstaller.ValidatePackage(package, step);
                foreach (var assembly in package.Files.Select(f => f.Assembly).Where(a => a is not null).Distinct()) RuntimeSxsResolver.Validate(assembly!);
                if (strongRepairMode) _ = RuntimeMsiInstallBackend.WithSourceHashes(package, Path.GetDirectoryName(msiPath)!);
            }
            _payload = ReadVerifiedPayload(DirectXRoot, token);
            progress?.Report("DirectX 与 C++ 安装资源 SHA256 校验通过。");
            return Task.FromResult(OperationResult.Ok("资源校验通过。"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Task.FromResult(OperationResult.Fail("兼容性修复预检失败：" + ex.Message)); }
    }
    internal static DirectXPayload ReadVerifiedPayload(string root, CancellationToken token)
    {
        var manifestPath = Path.Combine(root, "payload-manifest.json");
        if (!File.Exists(manifestPath) || !Hash(manifestPath).Equals(ManifestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("微软 DirectX 资源清单校验失败。");
        var payload = JsonSerializer.Deserialize<DirectXPayload>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("DirectX 清单为空。");
        if (payload.SchemaVersion != 1 || payload.Files.Count != 157 || payload.Probes.Count != 182)
            throw new InvalidDataException("DirectX 清单不完整。");
        foreach (var file in payload.Files)
        {
            token.ThrowIfCancellationRequested();
            if (Path.GetFileName(file.Path) != file.Path) throw new InvalidDataException("资源路径无效。");
            var path = Path.Combine(root, file.Path);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                || new FileInfo(path).Length != file.Length || !Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("DirectX 资源校验失败：" + file.Path);
        }
        return payload;
    }
    public Task<IReadOnlyList<DirectXProbeResult>> ProbeAsync(CancellationToken token)
    {
        if (_payload is null) throw new InvalidOperationException("尚未验证 DirectX 资源。");
        IReadOnlyList<DirectXProbeResult> results = _payload.Probes.Select(p =>
        {
            token.ThrowIfCancellationRequested();
            var directory = p.Architecture == "x64" ? Environment.SystemDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64");
            return Probe(p, directory);
        }).ToArray();
        return Task.FromResult(results);
    }
    internal static DirectXProbeResult Probe(DirectXProbe probe, string directory)
    {
        try
        {
            if (Path.GetFileName(probe.Name) != probe.Name || probe.Architecture is not ("x86" or "x64")) throw new InvalidDataException("检测项目无效。");
            var path = Path.Combine(directory, probe.Name);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5a4d) throw new IOException("DLL 格式异常。");
            stream.Position = 0x3c; var offset = reader.ReadInt32();
            if (offset < 64 || offset > stream.Length - 6) throw new IOException("PE 头异常。");
            stream.Position = offset;
            if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != (probe.Architecture == "x64" ? 0x8664 : 0x14c)) throw new IOException("DLL 架构不符。");
            var vi = FileVersionInfo.GetVersionInfo(path);
            var actual = new Version(vi.FileMajorPart, vi.FileMinorPart, vi.FileBuildPart, vi.FilePrivatePart);
            var expected = Version.Parse(probe.Version);
            if (actual < expected) throw new IOException($"版本 {actual} 低于 {expected}。");
            if (actual == expected && !Hash(path).Equals(probe.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("同版本 DLL 哈希与微软安装资源不一致。");
            return new(probe.Name, probe.Architecture, true, actual > expected ? "较新版本保留。" : "版本、架构和哈希一致。");
        }
        catch (Exception ex) { return new(probe.Name, probe.Architecture, false, ex.Message); }
    }
    internal static ProcessStartInfo DirectXCommand(string root) => new(Path.Combine(root, "DXSETUP.exe"), "/silent")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
    public async Task<RuntimeStepResult> InstallDirectXAsync(TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
    {
        if (await new RuntimeGuardService().RepairProtectionBlockerAsync() is { } protection) return new(-1, protection);
        if (RuntimeRepairPreflight.Read().Blocker is { } blocker) return new(-1, blocker);
        return await _runner.RunAsync(DirectXCommand(DirectXRoot), "微软 DirectX 旧版组件", timeout, progress, token);
    }
    public async Task<RuntimeStepResult> RepairCppAsync(RuntimeRepairStep step, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
    {
        try
        {
            if (await new RuntimeGuardService().RepairProtectionBlockerAsync() is { } protection) return new(-1, protection);
            if (RuntimeRepairPreflight.Read().Blocker is { } blocker) return new(-1, blocker);
            var path = Path.Combine(CppRoot, step.RelativeFile);
            var backend = new RuntimeMsiInstallBackend(_runner, verifiedSourceRoot: strongRepairMode ? Path.GetDirectoryName(path) : null);
            var package = RuntimeMsiMetadata.ReadPackage(path);
            foreach (var product in RuntimeMsiMetadata.RelatedProducts(package.UpgradeCode))
            {
                token.ThrowIfCancellationRequested();
                if (product.Equals(package.Product, StringComparison.OrdinalIgnoreCase)) continue;
                var installed = RuntimeMsiMetadata.ReadInstalledProduct(product);
                if (installed.State != 5 || !Version.TryParse(installed.Version, out var version) || Normalize(version) <= Normalize(Version.Parse(package.Version))) continue;
                var cached = RuntimeMsiMetadata.ReadInstalledPackage(product);
                if (!CanRetainRelatedPackage(package, cached)) continue;
                var verified = backend.VerifyFiles(cached);
                diagnostics.Save(step.Id + "-newer-related.json", new { Package = package, Installed = cached, Verification = verified });
                return new(0, "保留同一升级系列的较新 C++ 产品；" + verified.Message,
                    Skipped: verified.Success, PostcheckPassed: verified.Success, RetainedNewer: verified.Success);
            }
            return await new RuntimeMsiInstaller(backend, diagnostics).RunAsync(path, step,
                Path.Combine(diagnostics.DirectoryPath, step.Id + ".log"), timeout, progress, token,
                skipHealthyInstalled: !strongRepairMode, forceSameVersion: strongRepairMode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(null, "C++ 产品身份或升级关系无法确认，未强制覆盖：" + ex.Message, PostcheckPassed: false); }
    }
    internal static bool CanRetainRelatedPackage(RuntimeMsiPackage source, RuntimeMsiPackage installed) =>
        Guid.TryParse(source.UpgradeCode, out var family) && Guid.TryParse(installed.UpgradeCode, out var other) && family == other
        && source.Architecture == installed.Architecture && installed.Publisher.Equals(source.Publisher, StringComparison.OrdinalIgnoreCase)
        && installed.Name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase) && !installed.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase)
        && Version.TryParse(installed.Version, out var version) && Normalize(version) > Normalize(Version.Parse(source.Version)) && installed.Files.Count > 0;
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
