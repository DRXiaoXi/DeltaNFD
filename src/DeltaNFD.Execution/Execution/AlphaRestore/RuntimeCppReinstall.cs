using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace DeltaNFD.Services.AlphaRestore;

internal interface ICppReinstallEnvironment : IOfficialCppEnvironment
{
    IReadOnlyList<RuntimeUnifiedUninstallPlan> PlanRemoval(OfficialCppPackage package, IReadOnlyList<RuntimeMsiPackage> sources);
    Task<RuntimeStepResult> RemoveAsync(RuntimeUnifiedUninstallPlan plan, TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
}

/// <summary>Full reinstall only: validate the whole package before removing any child, then install the pinned original.</summary>
internal sealed class CppReinstaller(ICppReinstallEnvironment environment, RuntimeRepairDiagnostics diagnostics)
{
    internal static string PlanFingerprint(IReadOnlyList<RuntimeUnifiedUninstallPlan> plans) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plans)));
    internal static string BatchFingerprint(IEnumerable<(string Id, IReadOnlyList<RuntimeUnifiedUninstallPlan> Plans)> plans) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plans.Select(p => new { p.Id, Fingerprint = PlanFingerprint(p.Plans) }).OrderBy(p => p.Id, StringComparer.Ordinal))));

    internal IReadOnlyList<RuntimeUnifiedUninstallPlan> Preview(OfficialCppPackage package)
    {
        var sources = environment.Verify(package);
        var plans = environment.PlanRemoval(package, sources);
        foreach (var source in sources)
        {
            var installed = environment.ReadProduct(source.Product);
            if (installed.State != -1 && !plans.Any(p => p.Products.Contains(source.Product, StringComparer.OrdinalIgnoreCase)))
                throw new IOException("原包子组件未纳入已核验卸载计划，未继续。");
        }
        return plans;
    }

    internal async Task<RuntimeStepResult> RunAsync(OfficialCppPackage package, TimeSpan timeout,
        IProgress<string>? progress, CancellationToken token, string? expectedPlanFingerprint = null)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            token.ThrowIfCancellationRequested();
            var plans = Preview(package);
            if (expectedPlanFingerprint is not null && PlanFingerprint(plans) != expectedPlanFingerprint)
                throw new IOException("CPP 卸载计划与确认时不同，请重新预览。");
            var identity = JsonSerializer.Serialize(plans);
            diagnostics.Save("cpp-reinstall-" + package.Id + "-plan.json", plans);
            // Revalidate all children and original bytes immediately before the first removal.
            if (JsonSerializer.Serialize(Preview(package)) != identity) throw new IOException("C++ 身份或依赖已变化，请重新预览。");
            foreach (var plan in plans)
            {
                token.ThrowIfCancellationRequested();
                var remaining = timeout - watch.Elapsed;
                if (remaining <= TimeSpan.Zero) return new(null, "C++ 重装预算到期，已停止。", PostcheckPassed: false);
                var removed = await environment.RemoveAsync(plan, remaining, progress, token);
                if (!removed.Success || removed.RequiresReboot || removed.OutcomeUnknown) return removed;
                if (plan.Products.Any(p => environment.ReadProduct(p).State != -1))
                    return new(null, "MSI 卸载后仍存在或无法确认，未安装原包。", PostcheckPassed: false);
            }
            token.ThrowIfCancellationRequested();
            // No existing, newer, or newly appeared registration may turn a reinstall into a repair/retain operation.
            if (Preview(package).Count != 0) throw new IOException("卸载后仍有 C++ 登记或出现新组件，未继续安装。");
            var left = timeout - watch.Elapsed;
            if (left <= TimeSpan.Zero) return new(null, "C++ 重装预算到期，未启动安装。", PostcheckPassed: false);
            var result = await new OfficialCppInstaller(environment, diagnostics).RunAsync(package, false, left, progress, token, reinstallOnly: true);
            return result.RetainedNewer || result.Skipped ? result with { PostcheckPassed = false,
                Details = "重装期间组件状态发生变化，未确认原包重装完成。\n" + result.Details } : result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(null, "C++ 受控重装未完成：" + ex.Message, PostcheckPassed: false); }
    }
}

internal sealed class CppReinstallEnvironment(string root, RuntimeRepairDiagnostics diagnostics) : ICppReinstallEnvironment
{
    private readonly OfficialCppEnvironment _official = new(root);
    public IReadOnlyList<RuntimeMsiPackage> Verify(OfficialCppPackage package) => _official.Verify(package);
    public RuntimeMsiProduct ReadProduct(string product) => _official.ReadProduct(product);
    public IReadOnlyList<RuntimeMsiPackage> NewerRelated(RuntimeMsiPackage source) => _official.NewerRelated(source);
    public OperationResult VerifyFiles(RuntimeMsiPackage package) => _official.VerifyFiles(package);
    public Task<RuntimeStepResult> ExecuteAsync(OfficialCppPackage package, bool repair, string logPath, TimeSpan timeout,
        IProgress<string>? progress, CancellationToken token) => _official.ExecuteAsync(package, repair, logPath, timeout, progress, token);

    public IReadOnlyList<RuntimeUnifiedUninstallPlan> PlanRemoval(OfficialCppPackage package, IReadOnlyList<RuntimeMsiPackage> sources)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            foreach (var code in RuntimeMsiMetadata.RelatedProducts(source.UpgradeCode).Append(source.Product).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var installed = ReadProduct(code);
                if (installed.State == -1) continue;
                RuntimeMsiMetadata.VerifyCachedIdentity(installed);
                var cached = RuntimeMsiMetadata.ReadInstalledPackage(code);
                ValidateReplacement(source, cached, installed, package.Year, package.Arch);
                allowed.Add(code);
            }
        }
        var backend = new RuntimeUnifiedUninstallBackend(diagnostics);
        var scan = new RuntimeGuardService().ScanVcRedistsAsync().GetAwaiter().GetResult();
        var entries = scan.All.Where(e => e.DisplayName.Contains(package.Year, StringComparison.Ordinal)
            && (e.Architecture == package.Arch || e.Architecture is not ("x64" or "x86"))).ToArray();
        var plans = new List<RuntimeUnifiedUninstallPlan>();
        foreach (var entry in entries)
        {
            if (entry.Architecture != package.Arch) throw new IOException("旧 C++ 登记架构未知，未猜测卸载。");
            var plan = backend.Resolve(entry);
            backend.ValidateRegistration(plan);
            if (plan.Products.Length == 0 || plan.RetainedProducts.Length != 0 || plan.RelatedBundles.Length != 0
                || plan.Products.Any(p => !allowed.Contains(p)))
                throw new IOException("旧 C++ 包装包含未知、较新或未核验的子组件，未卸载。");
            foreach (var code in plan.Products)
            {
                var product = ReadProduct(code);
                RuntimeUnifiedUninstaller.ValidateProduct(entry, product);
                RuntimeMsiMetadata.VerifyCachedIdentity(product);
                if (entry.UninstallKind == "Msi") EnsureNoDependents(code);
            }
            plans.Add(plan);
        }
        var selected = RuntimeUnifiedUninstaller.SelectBatchPlans(plans);
        foreach (var plan in selected.Where(p => p.Entry.UninstallKind == "Msi"))
            foreach (var code in plan.Products) EnsureNoDependents(code);
        if (allowed.Any(p => !selected.Any(plan => plan.Products.Contains(p, StringComparer.OrdinalIgnoreCase))))
            throw new IOException("存在无法可靠映射到卸载登记的 MSI，未开始重装。");
        return selected;
    }

    internal static void ValidateReplacement(RuntimeMsiPackage source, RuntimeMsiPackage cached,
        RuntimeMsiProduct installed, string year, string arch)
    {
        RuntimeMsiInstaller.ValidateInstalled(cached, installed);
        RuntimeUnifiedUninstaller.ValidateProduct(new VcRedistEntry { KeyName = cached.Product,
            DisplayName = cached.Name, Architecture = arch, UninstallKind = "Msi" }, installed);
        if (!Guid.TryParse(cached.UpgradeCode, out var family) || family != Guid.Parse(source.UpgradeCode)
            || cached.Architecture != arch || !cached.Name.Contains(year, StringComparison.Ordinal)
            || !cached.Publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
            || cached.Files.Count == 0 || installed.State != 5
            || OfficialCppInstaller.Normal(Version.Parse(cached.Version)) != OfficialCppInstaller.Normal(Version.Parse(installed.Version))
            || OfficialCppInstaller.Normal(Version.Parse(cached.Version)) > OfficialCppInstaller.Normal(Version.Parse(source.Version)))
            throw new IOException("较新或身份不明的 C++ 组件不能以旧原包重装：" + installed.Code);
    }

    private static void EnsureNoDependents(string product)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hive.OpenSubKey(@"SOFTWARE\Classes\Installer\Dependencies");
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var provider = root.OpenSubKey(name) ?? throw new IOException("依赖提供者无法读取。");
                if (!Guid.TryParse(provider.GetValue("") as string, out var code) || code != Guid.Parse(product)) continue;
                using var clients = provider.OpenSubKey("Dependents");
                if (clients?.SubKeyCount > 0 || clients?.ValueCount > 0)
                    throw new IOException("MSI 仍有共享依赖，未强制卸载：" + product);
            }
        }
    }

    public async Task<RuntimeStepResult> RemoveAsync(RuntimeUnifiedUninstallPlan plan, TimeSpan timeout,
        IProgress<string>? progress, CancellationToken token)
    {
        if (await new RuntimeGuardService().RepairProtectionBlockerAsync() is { } blocker) return new(-1, blocker);
        foreach (var code in plan.Products)
        {
            var current = ReadProduct(code);
            if (current.State != -1) RuntimeMsiMetadata.VerifyCachedIdentity(current);
            if (plan.Entry.UninstallKind == "Msi") EnsureNoDependents(code);
        }
        var backend = new RuntimeUnifiedUninstallBackend(diagnostics);
        var currentPlan = backend.Resolve(plan.Entry);
        if (JsonSerializer.Serialize(currentPlan) != JsonSerializer.Serialize(plan)) return new(null, "卸载计划发生变化，未执行。", PostcheckPassed: false);
        var uninstaller = new RuntimeUnifiedUninstaller(backend, diagnostics, Path.Combine(AppDataPaths.Root, "cpp-reinstall-uninstall"));
        return await uninstaller.RunAsync(plan.Entry, "cpp-reinstall-" + Guid.NewGuid().ToString("N"), timeout, progress, token);
    }
}
