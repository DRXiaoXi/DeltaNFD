using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DeltaNFD.Services.AlphaRestore;

namespace DeltaNFD.Services;

internal sealed class Vc14DllVersionMismatch(string architecture, string file, string actual, string expected = "所选 14.x 基线")
    : IOException($"{architecture} {file} 实际版本为 {actual}，未确认是 {expected}，不能报告修复成功。")
{
    internal string Architecture { get; } = architecture;
}

internal sealed record Vc14SourceFile(string Path, long Bytes, string Sha256);
internal sealed record Vc14FileRepairPackage(string Architecture, string Role, string Code, string Name,
    string Version, string UpgradeCode, string OriginalSha256, Vc14SourceFile[] Files)
{
    internal string Msi => Files.Single(f => f.Path.EndsWith(".msi", StringComparison.Ordinal)).Path;
}

internal static class Vc14FileRepairSource
{
    // Both MSI file repair and DLL replacement use one deployed media directory per baseline.
    internal const string DirectoryName = "Vc14FileRepair14.44";
    internal static string DirectoryNameFor(Vc14Baseline baseline) => Vc14BaselineInfo.For(baseline).FileRepairDirectory;
    internal static string InstalledRoot => Vc14BaselineInfo.Default.FileRepairRoot;
    internal static string InstalledRootFor(Vc14Baseline baseline) => Vc14BaselineInfo.For(baseline).FileRepairRoot;
    internal static IReadOnlyList<Vc14FileRepairPackage> Packages => All[Vc14Baseline.V144];
    internal static IReadOnlyList<Vc14FileRepairPackage> PackagesFor(Vc14Baseline baseline) => All[baseline];

    private static readonly IReadOnlyDictionary<Vc14Baseline, Vc14FileRepairPackage[]> All = LoadAll();

    private static IReadOnlyDictionary<Vc14Baseline, Vc14FileRepairPackage[]> LoadAll()
    {
        var resources = new Dictionary<Vc14Baseline, string>
        {
            [Vc14Baseline.V144] = "Vc14FileRepair14.44.catalog.json",
        };
        var result = new Dictionary<Vc14Baseline, Vc14FileRepairPackage[]>();
        foreach (var (baseline, resource) in resources)
        {
            var info = Vc14BaselineInfo.For(baseline);
            using var stream = typeof(Vc14FileRepairSource).Assembly.GetManifestResourceStream(resource)
                ?? throw new IOException($"缺少 {info.Label} 原始 MSI/CAB 清单（{resource}）。");
            var packages = JsonSerializer.Deserialize<Vc14FileRepairPackage[]>(stream)
                ?? throw new IOException(info.Label + " 原包清单无效。");
            if (packages.Length != 4 || Vc14Catalog.Families.Any(f => packages.Count(p => p.Architecture == f.Arch && p.Role == f.Role
                && p.UpgradeCode.Equals(f.Upgrade, StringComparison.OrdinalIgnoreCase)) != 1)
                || packages.Any(p => !Vc14Catalog.SameVersion(p.Version, info.Version)
                    || p.OriginalSha256 != info.ExeHashes[p.Architecture] || p.Files.Length != 2))
                throw new IOException(info.Label + " 原始 MSI 范围不符。");
            result[baseline] = packages;
        }
        return result;
    }

    internal static IReadOnlyList<Vc14FileRepairPackage> Verify(string root, string arch) =>
        Verify(root, arch, Vc14Baseline.V144);

    internal static IReadOnlyList<Vc14FileRepairPackage> Verify(string root, string arch, Vc14Baseline baseline)
    {
        if (arch is not ("x64" or "x86")) throw new IOException("强制重装架构无效。");
        var info = Vc14BaselineInfo.For(baseline);
        var packages = PackagesFor(baseline).Where(p => p.Architecture == arch).ToArray();
        foreach (var package in packages)
        {
            foreach (var file in package.Files)
            {
                var path = OfficialCppCatalog.Resolve(root, file.Path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != file.Bytes || Convert.ToHexString(SHA256.HashData(stream)) != file.Sha256)
                    throw new IOException(info.Label + " 强制重装原始 MSI/CAB 校验失败：" + file.Path);
            }
            var msi = OfficialCppCatalog.Resolve(root, package.Msi);
            var metadata = RuntimeMsiMetadata.ReadPackage(msi);
            var expected = new Vc14Entry(package.Code, package.Name, package.Version, arch, package.Role, package.UpgradeCode);
            Vc14Catalog.Validate(expected, metadata.Product, metadata.Name, metadata.Version, metadata.Publisher,
                metadata.Architecture, metadata.UpgradeCode);
            var media = package.Files.Single(f => f.Path.EndsWith(".cab", StringComparison.Ordinal)).Path;
            if (!Path.GetDirectoryName(media)!.Equals(Path.GetDirectoryName(package.Msi), StringComparison.Ordinal)
                || !RuntimeMsiMetadata.MediaCabinets(msi).SequenceEqual([Path.GetFileName(media)]))
                throw new IOException(info.Label + " MSI 原始媒体关系不符。");
        }
        return packages;
    }

    internal static void ValidateInstalled(Vc14FileRepairPackage source, Vc14Entry? installed)
    {
        if (installed is null || !installed.Code.Equals(source.Code, StringComparison.OrdinalIgnoreCase)
            || installed.Architecture != source.Architecture || installed.Role != source.Role || installed.Name != source.Name
            || !Vc14Catalog.SameVersion(installed.Version, source.Version)
            || !installed.UpgradeCode.Equals(source.UpgradeCode, StringComparison.OrdinalIgnoreCase))
            throw new IOException("强制文件重装仅适用于已经核验安装的同一 " + source.Version + " 组件，当前身份已变化。");
    }

    internal static ProcessStartInfo BuildCommand(string root, Vc14FileRepairPackage package, string log)
    {
        var msi = OfficialCppCatalog.Resolve(root, package.Msi);
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(msi)! };
        foreach (var arg in new[] { "/i", msi, "REINSTALL=ALL", "REINSTALLMODE=amus", "/qn", "/norestart", "/L*V", log }) info.ArgumentList.Add(arg);
        return info;
    }
}

/// <summary>One ordinary repair, then one forced-file repair for a proven version mismatch.</summary>
internal static class Vc14FileRecovery
{
    internal static async Task<Vc14StepResult> RunAsync(IVc14ReinstallEnvironment environment,
        IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = Vc14BaselineInfo.For(environment.Baseline);
        try { environment.VerifyInstalled(); return new(0, Detail: "实际 " + info.Version + " DLL复检通过。"); }
        catch (IOException ex) { environment.Trace("dll-file-recovery-required", new { Error = ex.Message }); }
        foreach (var arch in new[] { "x64", "x86" })
        {
            token.ThrowIfCancellationRequested();
            try { environment.VerifyInstalled(arch); continue; }
            catch (IOException ex) { environment.Trace("dll-mismatch-before-repair", new { arch, Error = ex.Message }); }
            if (environment.IsSafeMode) return new(null, Detail: "文件重装需回到正常模式执行。", PostcheckPassed: false);
            if (environment.Blocker() is { } blocker) return new(null, Detail: blocker, PostcheckPassed: false);
            environment.VerifyBaselines();
            progress?.Report(arch + "：DLL未通过校验，先尝试一次原包普通修复…");
            var ordinary = await environment.InstallAsync(arch, progress, token);
            environment.Trace("dll-align-repair", new { arch, Result = ordinary });
            if (!ordinary.Success || ordinary.Reboot) return ordinary;
            token.ThrowIfCancellationRequested();
            try { environment.VerifyInstalled(arch); continue; }
            catch (Vc14DllVersionMismatch ex)
            {
                if (ex.Architecture != arch) throw new IOException("DLL校验架构不一致，未强制重装。", ex);
                environment.Trace("dll-force-repair-intent", new { arch, Error = ex.Message });
            }
            // Do not treat an unreadable/missing DLL or an unknown MSI scan as permission to force.
            var scan = environment.Scan();
            if (scan.Issues.Count != 0 || scan.Products.Count != 4 || Vc14Catalog.Families.Any(f =>
                scan.Products.Count(p => p.Architecture == f.Arch && p.Role == f.Role
                    && Vc14Catalog.SameVersion(p.Version, info.Version)) != 1))
                return new(null, Detail: info.Label + " 双架构组件身份未确认，未执行强制文件重装。", PostcheckPassed: false);
            if (environment.Blocker() is { } changed) return new(null, Detail: changed, PostcheckPassed: false);
            token.ThrowIfCancellationRequested();
            progress?.Report(arch + "：普通修复后DLL仍未对齐，回退到忽略文件版本的原始MSI重装…");
            var forced = await environment.ForceFileRepairAsync(arch, progress, token);
            environment.Trace("dll-force-repair-result", new { arch, Result = forced });
            if (!forced.Success || forced.Reboot) return forced;
            token.ThrowIfCancellationRequested();
            try { environment.VerifyInstalled(arch); }
            catch (IOException ex)
            {
                return new(forced.ExitCode, Detail: "普通修复及强制文件重装后，实际DLL仍未通过校验。"
                    + Vc14Diagnostics.ExplainDllMismatch(environment.DiagnosticsDirectory, ex.Message), PostcheckPassed: false);
            }
        }
        environment.VerifyInstalled();
        return new(0, Detail: "实际 " + info.Version + " DLL复检通过。");
    }
}
