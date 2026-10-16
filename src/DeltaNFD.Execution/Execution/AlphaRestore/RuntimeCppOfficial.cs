using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeltaNFD.Services.AlphaRestore;

internal sealed record OfficialCppFile(string Path, long Bytes, string Sha256);
internal sealed record OfficialCppPackage(string Year, string Arch, string Version, string Url, OfficialCppFile[] Files)
{
    internal string Id => Year + "-" + Arch;
    internal string Exe => Files.Single(f => f.Path.EndsWith("/vcredist.exe", StringComparison.Ordinal)).Path;
}

internal static class OfficialCppCatalog
{
    internal static readonly IReadOnlyList<OfficialCppPackage> Packages = Load();
    private static OfficialCppPackage[] Load()
    {
        using var stream = typeof(OfficialCppCatalog).Assembly.GetManifestResourceStream("RuntimeCppOfficial.catalog.json")
            ?? throw new IOException("缺少内置微软旧 C++ 原包清单。");
        var packages = JsonSerializer.Deserialize<OfficialCppPackage[]>(stream) ?? throw new IOException("旧 C++ 清单无效。");
        if (packages.Length != 10 || packages.Select(p => p.Id).Distinct().Count() != 10
            || packages.Any(p => p.Year is not ("2005" or "2008" or "2010" or "2012" or "2013") || p.Arch is not ("x64" or "x86")))
            throw new IOException("旧 C++ 清单范围无效。");
        return packages;
    }
    internal static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p is ".." or "." or "")) throw new IOException("原包路径无效。");
        var fullRoot = Path.GetFullPath(root);
        var path = fullRoot;
        foreach (var part in relative.Split('/'))
        {
            path = Path.Combine(path, part);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("原包路径不能包含重解析点。");
        }
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0) throw new IOException("原包目录不能为重解析点。");
        return path;
    }
    internal static IReadOnlyList<RuntimeMsiPackage> Verify(string root, OfficialCppPackage package)
    {
        foreach (var file in package.Files)
        {
            var path = Resolve(root, file.Path);
            using var content = File.OpenRead(path);
            if (content.Length != file.Bytes || !Convert.ToHexString(SHA256.HashData(content)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("微软原包或原始 MSI/CAB 校验失败：" + file.Path);
        }
        var children = package.Files.Where(f => f.Path.EndsWith(".msi", StringComparison.Ordinal)).Select(f =>
        {
            var child = RuntimeMsiMetadata.ReadPackage(Resolve(root, f.Path));
            RuntimeMsiInstaller.ValidatePackage(child, new(package.Id, package.Id, package.Year + " (" + package.Arch + ")", "msi", f.Path));
            if (!child.Name.Contains(package.Year, StringComparison.Ordinal) || !Guid.TryParse(child.UpgradeCode, out _))
                throw new IOException("微软原始 MSI 年份或升级身份不符。");
            return child;
        }).ToArray();
        if (children.Length != (package.Year is "2012" or "2013" ? 2 : 1)) throw new IOException("微软原包子组件数量不符。");
        return children;
    }
    internal static void VerifyAll(string root, CancellationToken token)
    {
        foreach (var package in Packages) { token.ThrowIfCancellationRequested(); Verify(root, package); }
    }
}

internal interface IOfficialCppEnvironment
{
    IReadOnlyList<RuntimeMsiPackage> Verify(OfficialCppPackage package);
    RuntimeMsiProduct ReadProduct(string product);
    IReadOnlyList<RuntimeMsiPackage> NewerRelated(RuntimeMsiPackage source);
    OperationResult VerifyFiles(RuntimeMsiPackage package);
    Task<RuntimeStepResult> ExecuteAsync(OfficialCppPackage package, bool repair, string logPath, TimeSpan timeout,
        IProgress<string>? progress, CancellationToken token);
}

/// <summary>One reviewed original per year/architecture; process exit alone never proves repair.</summary>
internal sealed class OfficialCppInstaller(IOfficialCppEnvironment environment, RuntimeRepairDiagnostics diagnostics)
{
    internal async Task<RuntimeStepResult> RunAsync(OfficialCppPackage package, bool forceRepair, TimeSpan timeout,
        IProgress<string>? progress, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        RuntimeStepResult? last = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var sources = environment.Verify(package);
            var present = new List<RuntimeMsiPackage>();
            var newer = 0;
            var missing = false;
            var exactVersion = true;
            var expectedProducts = new Dictionary<string, RuntimeMsiProduct>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                var installed = environment.ReadProduct(source.Product);
                diagnostics.Save("official-" + package.Id + "-identity-" + source.Product.Trim('{', '}') + ".json", new { Source = source, Installed = installed });
                expectedProducts.Add(source.Product, installed);
                if (installed.State == 5)
                {
                    // Serviced VC2008 can keep its ProductCode while changing both
                    // ProductVersion and the version suffix in ProductName. Compare
                    // its installed identity with its own verified cache, not the
                    // older source's name, before retaining the newer product.
                    if (Version.TryParse(installed.Version, out var installedVersion)
                        && Normal(installedVersion) > Normal(Version.Parse(source.Version)))
                    {
                        var related = environment.NewerRelated(source).SingleOrDefault(p => p.Product.Equals(source.Product, StringComparison.OrdinalIgnoreCase))
                            ?? throw new IOException("同产品较新版本的缓存无法核对，未降级。");
                        if (!RuntimeCppOverwriteEnvironment.CanRetainRelatedPackage(source, related))
                            throw new IOException("较新产品的升级系列、架构、发布者或版本未核验，未降级。");
                        RuntimeMsiInstaller.ValidateInstalled(related, installed);
                        if (Normal(Version.Parse(related.Version)) != Normal(installedVersion))
                            throw new IOException("较新产品的缓存与已安装版本不符，未继续。");
                        if (!environment.VerifyFiles(related).Success) throw new IOException("较新运行库文件异常，请使用匹配版本原包修复；未以旧原包降级。");
                        newer++; continue;
                    }
                    RuntimeMsiInstaller.ValidateInstalled(source, installed);
                    present.Add(source);
                    exactVersion &= Normal(Version.Parse(installed.Version)) == Normal(Version.Parse(source.Version));
                }
                else if (installed.State == -1)
                {
                    var candidates = environment.NewerRelated(source);
                    if (candidates.Count > 1) throw new IOException("同系列较新产品不唯一，未猜测覆盖。");
                    if (candidates.Count == 1)
                    {
                        if (!environment.VerifyFiles(candidates[0]).Success) throw new IOException("较新产品文件未通过复检，未降级。");
                        newer++; continue;
                    }
                    missing = true;
                }
                else throw new IOException("MSI 产品安装状态无法确认：" + installed.State);
            }
            if (newer > 0)
                return newer == sources.Count ? new(0, "保留并复检了同系列较新产品，未降级：" + package.Id, Skipped: true, RetainedNewer: true)
                    : new(null, "同一原包的子组件混有较新版本与缺失/旧版，请使用匹配的原包处理；未执行降级或卸载。", PostcheckPassed: false);
            if (!forceRepair && !missing && exactVersion && present.All(p => environment.VerifyFiles(p).Success))
                return new(0, "同版本 MSI 和关键文件复检通过，跳过重复安装：" + package.Id, Skipped: true);
            var repair = !missing;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var remaining = timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) return new(null, "原包步骤预算已耗尽，未启动新的尝试。", PostcheckPassed: false);
                sources = environment.Verify(package); // Recheck original bytes immediately before execution.
                foreach (var source in sources)
                {
                    if (environment.ReadProduct(source.Product) != expectedProducts[source.Product]
                        || environment.NewerRelated(source).Count != 0)
                        throw new IOException("MSI 身份或较新版本在执行前发生变化，请重新预览；未启动该原包。");
                }
                var log = Path.Combine(diagnostics.DirectoryPath, "official-" + package.Id + (attempt == 0 ? "" : "-repair") + ".log");
                diagnostics.Save("official-" + package.Id + "-intent.json", new { package.Id, package.Version, Repair = repair, Attempt = attempt + 1, Sources = sources });
                last = await environment.ExecuteAsync(package, repair, log, remaining, progress, token);
                if (!last.Success || last.RequiresReboot || last.OutcomeUnknown) return last;
                token.ThrowIfCancellationRequested();
                var checks = new List<string>();
                foreach (var source in sources)
                {
                    var installed = environment.ReadProduct(source.Product);
                    expectedProducts[source.Product] = installed;
                    if (installed.State == -1) { checks.Add("原包返回成功但子组件尚未安装：" + source.Name); continue; }
                    RuntimeMsiInstaller.ValidateInstalled(source, installed);
                    if (Normal(Version.Parse(installed.Version)) != Normal(Version.Parse(source.Version)))
                        throw new IOException("执行期间版本发生变化，请重新核对；未继续覆盖。");
                    var files = environment.VerifyFiles(source);
                    if (!files.Success) checks.Add(files.Message);
                }
                diagnostics.Save("official-" + package.Id + "-postcheck.json", new { Passed = checks.Count == 0, Errors = checks });
                if (checks.Count == 0) return last with { Details = last.Details + "\n原包子组件与关键 DLL 复检通过。" };
                // Only one repair after a completed install with verified product identities.
                if (repair || attempt != 0) return last with { PostcheckPassed = false, Details = string.Join("\n", checks) };
                repair = true;
            }
            return new(null, "原包修复未完成。", PostcheckPassed: false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(last?.ExitCode, "微软原包安装/修复未完成（" + package.Id + "）：" + ex.Message, PostcheckPassed: false); }
    }
    internal static Version Normal(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    internal static ProcessStartInfo BuildCommand(string root, OfficialCppPackage package, bool repair, string log)
    {
        ProcessStartInfo info;
        if (package.Year == "2005" || repair && package.Year == "2008")
        {
            // 2005 is IExpress, not Burn. Use its unmodified MSI/CAB; 2008 repair uses its original MSI too.
            var source = package.Files.Single(f => f.Path.EndsWith(".msi", StringComparison.Ordinal));
            info = RuntimeMsiInstaller.BuildCommand(OfficialCppCatalog.Resolve(root, source.Path), repair, log, true);
        }
        else
        {
            info = new(OfficialCppCatalog.Resolve(root, package.Exe)) { UseShellExecute = false, CreateNoWindow = true };
            var args = package.Year is "2005" or "2008" ? new[] { "/q", "/norestart" }
                : package.Year == "2010" ? (repair ? new[] { "/repair", "/quiet", "/norestart", "/log", log } : new[] { "/quiet", "/norestart", "/log", log })
                : new[] { repair ? "/repair" : "/install", "/quiet", "/norestart", "/log", log };
            foreach (var arg in args) info.ArgumentList.Add(arg);
        }
        info.WorkingDirectory = Path.GetDirectoryName(info.FileName)!;
        return info;
    }
}

internal sealed class OfficialCppEnvironment(string root) : IOfficialCppEnvironment
{
    private readonly RuntimeRepairProcessRunner _runner = new(AppDataPaths.Root);
    private readonly RuntimeMsiInstallBackend _files = new(new RuntimeRepairProcessRunner(AppDataPaths.Root));
    public IReadOnlyList<RuntimeMsiPackage> Verify(OfficialCppPackage package) => OfficialCppCatalog.Verify(root, package);
    public RuntimeMsiProduct ReadProduct(string product) => RuntimeMsiMetadata.ReadInstalledProduct(product);
    public OperationResult VerifyFiles(RuntimeMsiPackage package) => _files.VerifyFiles(package);
    public IReadOnlyList<RuntimeMsiPackage> NewerRelated(RuntimeMsiPackage source)
    {
        var found = new List<RuntimeMsiPackage>();
        foreach (var product in RuntimeMsiMetadata.RelatedProducts(source.UpgradeCode).Append(source.Product).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var installed = ReadProduct(product);
            if (installed.State == -1) continue;
            if (installed.State != 5) throw new IOException("升级系列存在未确认的产品状态。");
            var cached = RuntimeMsiMetadata.ReadInstalledPackage(product);
            RuntimeMsiMetadata.VerifyCachedIdentity(installed);
            if (RuntimeCppOverwriteEnvironment.CanRetainRelatedPackage(source, cached)) found.Add(cached);
        }
        return found;
    }
    public async Task<RuntimeStepResult> ExecuteAsync(OfficialCppPackage package, bool repair, string logPath, TimeSpan timeout,
        IProgress<string>? progress, CancellationToken token)
    {
        if (await new RuntimeGuardService().RepairProtectionBlockerAsync() is { } protection) return new(-1, protection);
        if (RuntimeRepairPreflight.Read().Blocker is { } blocked) return new(-1, blocked);
        Verify(package);
        return await _runner.RunAsync(OfficialCppInstaller.BuildCommand(root, package, repair, logPath),
            "微软原包 " + package.Id + (repair ? " 修复" : " 安装"), timeout, progress, token);
    }
}
