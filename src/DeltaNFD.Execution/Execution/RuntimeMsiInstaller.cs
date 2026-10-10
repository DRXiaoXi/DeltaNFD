using System.Diagnostics;

namespace DeltaNFD.Services;

internal sealed record RuntimeMsiFile(string Component, string KeyFileName, string FileName, Version Version)
{
    public string Sha256 { get; init; } = "";
}
internal sealed record RuntimeMsiPackage(string Product, string Name, string Publisher, string Version,
    string Architecture, IReadOnlyList<RuntimeMsiFile> Files)
{
    public string UpgradeCode { get; init; } = "";
}

internal interface IRuntimeMsiInstallBackend
{
    RuntimeMsiPackage ReadPackage(string path);
    RuntimeMsiProduct ReadProduct(string product);
    OperationResult VerifyFiles(RuntimeMsiPackage package);
    Task<RuntimeStepResult> RunAsync(ProcessStartInfo info, string label, TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
}

/// <summary>All repair modes share identity-based install/repair/retain decisions, not a UI-mode flag.</summary>
internal sealed class RuntimeMsiInstaller(IRuntimeMsiInstallBackend backend, RuntimeRepairDiagnostics diagnostics)
{
    internal async Task<RuntimeStepResult> RunAsync(string path, RuntimeRepairStep step, string logPath,
        TimeSpan timeout, IProgress<string>? progress, CancellationToken token, bool skipHealthyInstalled = false,
        bool forceSameVersion = false)
    {
        var clock = Stopwatch.StartNew();
        var attempts = new List<object>();
        RuntimeStepResult? previous = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var package = backend.ReadPackage(path);
            ValidatePackage(package, step);
            var installed = backend.ReadProduct(package.Product);
            var action = Decide(package, installed);
            if (skipHealthyInstalled && action == "repair" && Normalize(Version.Parse(installed.Version)) >= Normalize(Version.Parse(package.Version)))
            {
                var currentFiles = backend.VerifyFiles(package);
                if (currentFiles.Success) return new(0, "已有同一版本且关键 DLL 复核通过，未重复安装。", Skipped: true);
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                token.ThrowIfCancellationRequested();
                diagnostics.Save(step.Id + "-msi-decision.json", new { Package = package, Installed = installed, Action = action });
                if (action == "retain-newer")
                {
                    var verified = backend.VerifyFiles(package);
                    return new(0, "已安装较新产品，保留版本；" + verified.Message,
                        Skipped: verified.Success, PostcheckPassed: verified.Success, RetainedNewer: verified.Success);
                }
                var remaining = timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) return new(null, "MSI 步骤预算已耗尽，未启动新的尝试。", PostcheckPassed: false);
                var attemptLog = attempt == 0 ? logPath : Path.ChangeExtension(logPath, ".retry.log");
                var info = BuildCommand(path, action == "repair", attemptLog, forceSameVersion);
                Log.Info($"MSI 受控执行：{step.Label}；action={action}；product={package.Product}；日志={attemptLog}；剩余预算={remaining}");
                progress?.Report(step.Label + (action == "repair" ? "：修复已安装产品。" : "：安装缺失产品。"));
                var result = await backend.RunAsync(info, step.Label, remaining, progress, token);
                previous = result;
                var details = result.Details;
                try { if (File.Exists(attemptLog)) details += "\n" + diagnostics.ReadErrorDetails(attemptLog); }
                catch (Exception ex) { details += "\n步骤日志无法读取：" + ex.Message; Log.Warn(details); }
                attempts.Add(new { Attempt = attempt + 1, Action = action, Command = info.ArgumentList.ToArray(), Log = attemptLog, Result = result, Details = details });
                diagnostics.Save(step.Id + "-msi-attempts.json", attempts);
                result = result with { Details = details };
                if (result.OutcomeUnknown) return result;
                if (token.IsCancellationRequested)
                    return result with { PostcheckPassed = false, Details = details + "\n已取消后续复核；安装已结束，尚未确认文件状态。" };
                if (RuntimeRepairErrorClassifier.IsVersionConflict(result.ExitCode) && attempt == 0 && action == "install")
                {
                    // A product can appear after the initial probe. Only an exact, trusted identity permits one retry.
                    installed = backend.ReadProduct(package.Product);
                    action = Decide(package, installed);
                    if (action is "repair" or "retain-newer") continue;
                    return result with { PostcheckPassed = false, Details = details + "\n版本冲突后未找到同一产品，未猜测或卸载其他运行库。" };
                }
                if (result.ExitCode is not (0 or 3010 or 1641)) return result;
                // A process exit, an INF wrapper, or a 1638 code alone cannot prove successful repair.
                installed = backend.ReadProduct(package.Product);
                ValidateInstalled(package, installed);
                if (Normalize(Version.Parse(installed.Version)) < Normalize(Version.Parse(package.Version)))
                    return result with { PostcheckPassed = false, Details = details + "\nMSI 产品版本仍低于安装包。" };
                var files = backend.VerifyFiles(package);
                diagnostics.Save(step.Id + "-msi-postcheck.json", new { Installed = installed, Files = files });
                return result with { PostcheckPassed = files.Success, Details = details + "\n" + files.Message };
            }
            return new(1638, "同一产品修复仍发生冲突，已停止后续步骤。", PostcheckPassed: false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return previous is not null ? previous with { PostcheckPassed = false, Details = previous.Details + "\nMSI 身份/文件复核失败：" + ex.Message }
                : new(null, "MSI 身份无法确认，未启动安装：" + ex.Message, PostcheckPassed: false);
        }
    }

    internal static ProcessStartInfo BuildCommand(string path, bool repair, string logPath, bool forceSameVersion = false)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/i", path, "/qn", "/norestart", "REBOOT=ReallySuppress", "/L*V", logPath }) info.ArgumentList.Add(arg);
        // e replaces equal/older versions; unlike a, it preserves newer shared DLLs.
        if (repair) { info.ArgumentList.Add("REINSTALL=ALL"); info.ArgumentList.Add(forceSameVersion ? "REINSTALLMODE=vemus" : "REINSTALLMODE=vomus"); }
        return info;
    }

    private static void ValidatePackage(RuntimeMsiPackage package, RuntimeRepairStep step)
    {
        var arch = step.Branch.EndsWith("(x64)", StringComparison.Ordinal) ? "x64"
            : step.Branch.EndsWith("(x86)", StringComparison.Ordinal) ? "x86" : "";
        if (!Guid.TryParse(package.Product, out _) || !Version.TryParse(package.Version, out _)
            || package.Architecture != arch || arch.Length == 0 || package.Files.Count == 0
            || !package.Name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase)
            || package.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase)
            || !package.Publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装包产品、微软发布者、架构、版本或关键 DLL 元数据不完整。");
    }

    private static void ValidateInstalled(RuntimeMsiPackage package, RuntimeMsiProduct installed)
    {
        if (installed.State != 5 || !installed.Code.Equals(package.Product, StringComparison.OrdinalIgnoreCase)
            || !installed.Name.Equals(package.Name, StringComparison.OrdinalIgnoreCase)
            || !installed.Publisher.Equals(package.Publisher, StringComparison.OrdinalIgnoreCase)
            || installed.Architecture != package.Architecture || !Version.TryParse(installed.Version, out _))
            throw new InvalidDataException("现有 MSI 产品的名称、发布者、架构、版本或状态不匹配；未对其他产品重装。");
    }

    private static string Decide(RuntimeMsiPackage package, RuntimeMsiProduct installed)
    {
        if (!installed.Code.Equals(package.Product, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("MSI 产品码不匹配。");
        if (installed.State == -1) return "install";
        ValidateInstalled(package, installed);
        return Normalize(Version.Parse(installed.Version)) > Normalize(Version.Parse(package.Version)) ? "retain-newer" : "repair";
    }
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}

internal sealed class RuntimeMsiInstallBackend(RuntimeRepairProcessRunner runner,
    Func<string, string, string>? componentPath = null, string? verifiedSourceRoot = null) : IRuntimeMsiInstallBackend
{
    public RuntimeMsiPackage ReadPackage(string path)
    {
        var package = RuntimeMsiMetadata.ReadPackage(path);
        return verifiedSourceRoot is null ? package : WithSourceHashes(package, verifiedSourceRoot);
    }
    internal static RuntimeMsiPackage WithSourceHashes(RuntimeMsiPackage package, string root)
    {
        // Called only for the payload already verified against the pinned repair manifest.
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("源目录不能为重解析点。");
        var sources = Directory.EnumerateFiles(root, "*", new EnumerationOptions
            { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToLookup(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        return package with { Files = package.Files.Select(file =>
        {
            var hashes = sources[file.FileName].Select(path =>
            {
                using var stream = File.OpenRead(path);
                return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (hashes.Length != 1) throw new IOException("强力修复源文件缺失或存在歧义：" + file.FileName);
            return file with { Sha256 = hashes[0] };
        }).ToArray() };
    }
    public RuntimeMsiProduct ReadProduct(string product) => RuntimeMsiMetadata.ReadInstalledProduct(product);
    public Task<RuntimeStepResult> RunAsync(ProcessStartInfo info, string label, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        => runner.RunAsync(info, label, timeout, progress, token);

    public OperationResult VerifyFiles(RuntimeMsiPackage package)
    {
        var errors = new List<string>();
        foreach (var file in package.Files)
        {
            try
            {
                var keyPath = (componentPath ?? RuntimeMsiMetadata.ReadComponentPath)(package.Product, file.Component);
                if (!Path.GetFileName(keyPath).Equals(file.KeyFileName, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(file.FileName) != file.FileName) throw new IOException("MSI 组件路径与安装包不一致。");
                var path = Path.Combine(Path.GetDirectoryName(keyPath)!, file.FileName);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new BinaryReader(stream);
                if (reader.ReadUInt16() != 0x5A4D) throw new IOException("DLL 格式无效。");
                stream.Position = 0x3c;
                var peOffset = reader.ReadInt32();
                if (peOffset < 64 || peOffset > stream.Length - 6) throw new IOException("DLL PE 头无效。");
                stream.Position = peOffset;
                if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != (package.Architecture == "x64" ? 0x8664 : 0x14c))
                    throw new IOException("DLL 架构不符合所选分支。");
                var vi = FileVersionInfo.GetVersionInfo(path);
                var actual = new Version(vi.FileMajorPart, vi.FileMinorPart, vi.FileBuildPart, vi.FilePrivatePart);
                var expected = new Version(file.Version.Major, file.Version.Minor, Math.Max(file.Version.Build, 0), Math.Max(file.Version.Revision, 0));
                if (actual < expected) throw new IOException($"DLL 版本 {actual} 低于安装包 {file.Version}。");
                if (actual == expected && file.Sha256.Length != 0)
                {
                    stream.Position = 0;
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                    if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("同版本 DLL 内容与已校验的内置源文件不一致。");
                }
            }
            catch (Exception ex) { errors.Add(file.FileName + "：" + ex.Message); }
        }
        return errors.Count == 0 && package.Files.Count > 0 ? OperationResult.Ok($"MSI 登记及 {package.Files.Count} 个关键 DLL 的存在、版本和架构复核通过。"
            + (package.Files.All(f => f.Sha256.Length != 0) ? "同版本文件已与内置源文件比对 SHA256；较新文件保留，仅核对版本和架构。" : "不等于所有 DLL 字节完整性验证。"))
            : OperationResult.Fail("关键 DLL 复核未通过：" + string.Join("；", errors.Take(8)));
    }
}
