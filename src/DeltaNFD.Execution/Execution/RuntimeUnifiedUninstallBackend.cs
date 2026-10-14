using Microsoft.Win32;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

internal sealed class RuntimeUnifiedUninstallBackend(RuntimeRepairDiagnostics diagnostics, bool allowNonstandard = false) : IRuntimeUnifiedUninstallBackend
{
    private const string Arp = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
    private const string Dependencies = @"SOFTWARE\Classes\Installer\Dependencies\";
    private readonly RuntimeRepairProcessRunner _runner = new(AppDataPaths.Root);

    public RuntimeMsiProduct ReadProduct(string product)
    {
        var installed = RuntimeMsiMetadata.ReadInstalledProduct(product);
        if (allowNonstandard && installed.State == 5)
        {
            RuntimeMsiMetadata.VerifyCachedIdentity(installed);
            diagnostics.Save("force-msi-identity-" + Guid.Parse(product).ToString("N") + ".json",
                new { Product = installed, CacheIdentityVerified = true });
        }
        return installed;
    }

    public RuntimeUnifiedUninstallPlan Resolve(VcRedistEntry entry)
    {
        var kind = RuntimeUninstallCommand.Classify(entry.KeyName, entry.UninstallString, entry.QuietUninstallString);
        if (kind == "Msi")
        {
            if (!RuntimeUninstallCommand.TryCreate(entry, null, out var plan, out var error)) throw new InvalidDataException(error);
            return new(entry, plan!.ProductCodes, []) { AllowNonstandard = allowNonstandard };
        }
        var registrations = ReadArpSnapshots(entry);
        if (registrations.Count == 0) throw new InvalidDataException("包装登记不存在，无法确认归属。");
        foreach (var snapshot in registrations) ValidateArp(entry, snapshot);
        if (kind == "Inf")
        {
            var products = RuntimeUninstallCommand.ReadQuietInfProducts(RuntimeUninstallCommand.GetInfPath(entry));
            if (products.Length == 0) throw new InvalidDataException("INF 关联 MSI 无法可靠确认。");
            var plan = new RuntimeUnifiedUninstallPlan(entry, products, registrations.ToArray()) { AllowNonstandard = allowNonstandard };
            return plan with { RelatedBundles = ResolveRelatedBundles(plan) };
        }
        if (kind != "Bundle" || !Guid.TryParse(entry.KeyName, out _)) throw new InvalidDataException("不支持的运行库包装登记。");
        var provider = ReadArpValue(registrations[0], "BundleProviderKey");
        if (!Regex.IsMatch(provider, @"^VC,redist\.(?:x86|x64),(?:x86|amd64),14\.\d+,bundle$", RegexOptions.IgnoreCase)
            && !provider.Equals(entry.KeyName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("未知 Bundle 依赖提供者，不猜测产品码。");
        var productsFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Version.TryParse(entry.DisplayVersion, out _)) throw new InvalidDataException("Bundle 版本无法核验。");
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var own = hive.OpenSubKey(Dependencies + provider);
            if (own is null) continue;
            var snapshot = new RuntimeRegistrationSnapshot(Dependencies + provider, (int)view, Capture(own));
            diagnostics.Save("bundle-provider-" + Guid.Parse(entry.KeyName).ToString("N") + "-" + (int)view + ".json",
                new { entry.KeyName, entry.DisplayName, Provider = provider, snapshot.KeyPath, snapshot.View,
                    Registration = JsonSerializer.Deserialize<JsonElement>(snapshot.Content) });
            ValidateBundleProviderIdentity(entry.KeyName, provider, snapshot);
            using var ownDependents = own.OpenSubKey("Dependents");
            var bundleClients = ownDependents?.GetSubKeyNames() ?? [];
            if (bundleClients.Any(k => !k.Equals(entry.KeyName, StringComparison.OrdinalIgnoreCase)))
                throw new RuntimeDependencyConflictException("Bundle 被其他安装包依赖，已保留登记：" + provider
                    + "；引用：" + string.Join("、", bundleClients) + "。可通过覆盖安装修复 C++。详情见 bundle-provider 日志。");
            registrations.Add(snapshot);
            using var root = hive.OpenSubKey(Dependencies);
            foreach (var name in root!.GetSubKeyNames())
            {
                // Only the known VC release-runtime dependency provider family, never Debug/SDK/VSTO.
                if (!Regex.IsMatch(name, @"^Microsoft\.VS\.VC_Runtime(?:Minimum|Additional)(?:VSU)?_(?:amd64|x86),v(?:11|12|14)$", RegexOptions.IgnoreCase)) continue;
                using var package = root.OpenSubKey(name);
                using var dependents = package?.OpenSubKey("Dependents");
                var clients = dependents?.GetSubKeyNames() ?? [];
                if (!clients.Any(k => k.Equals(entry.KeyName, StringComparison.OrdinalIgnoreCase))) continue;
                diagnostics.Save("dependency-" + Guid.Parse(entry.KeyName).ToString("N") + "-" + name + "-" + (int)view + ".json",
                    new { entry.KeyName, entry.DisplayName, Provider = name, View = (int)view,
                        Product = package?.GetValue("") as string, Dependents = clients,
                        Registration = JsonSerializer.Deserialize<JsonElement>(Capture(package!)) });
                if (!Guid.TryParse(package?.GetValue("") as string, out var product)) throw new InvalidDataException("依赖提供者缺少 MSI 产品码。");
                var installed = ReadProduct(product.ToString("B"));
                if (installed.State != -1)
                {
                    if (ShouldRetainNewerMsi(entry, installed, allowNonstandard)) { retained.Add(product.ToString("B")); continue; }
                }
                try { ValidateBundleDependents(entry.KeyName, clients); }
                catch (InvalidDataException)
                {
                    throw new RuntimeDependencyConflictException("关联 MSI 存在共享引用，已保留组件与依赖：" + product.ToString("B")
                        + "；Provider：" + name + "；引用：" + string.Join("、", clients)
                        + "。未执行强制卸载；可通过覆盖安装修复 C++。详情见 dependency 日志。");
                }
                productsFound.Add(product.ToString("B"));
            }
        }
        if (productsFound.Count + retained.Count == 0) throw new InvalidDataException("缺少 Bundle → MSI 的可靠依赖关系；未执行包装 EXE。");
        return new(entry, productsFound.ToArray(), registrations.Distinct().ToArray())
            { RetainedProducts = retained.ToArray(), AllowNonstandard = allowNonstandard };
    }

    internal static void ValidateBundleProviderIdentity(string bundle, string provider, RuntimeRegistrationSnapshot snapshot)
    {
        if (!Guid.TryParse(bundle, out var expected) || expected == Guid.Empty)
            throw new InvalidDataException("Bundle 身份无效。");
        using var json = JsonDocument.Parse(snapshot.Content);
        string? explicitIdentity = null;
        if (json.RootElement.GetProperty("Values").TryGetProperty("", out var value))
        {
            var kind = (RegistryValueKind)value.GetProperty("Kind").GetInt32();
            if (value.GetProperty("Data").ValueKind != JsonValueKind.String
                || kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
                throw new InvalidDataException($"Bundle 依赖身份值类型无效（注册表视图 {snapshot.View}）；原值已记录。");
            explicitIdentity = value.GetProperty("Data").GetString();
        }
        // WiX DependencyDetectProviderKeyBundleId uses the provider key when no explicit Id is set.
        // This only accepts a GUID key identifying this very bundle; named v14 providers still need an explicit Id.
        var identity = string.IsNullOrEmpty(explicitIdentity) ? provider : explicitIdentity;
        if (!Guid.TryParse(identity, out var actual) || actual != expected)
            throw new InvalidDataException($"Bundle 依赖身份与当前安装包不匹配（注册表视图 {snapshot.View}）；原值已记录。");
    }

    public void ValidateRegistration(RuntimeUnifiedUninstallPlan plan)
    {
        if (plan.AllowNonstandard != allowNonstandard) throw new IOException("卸载计划的强制尝试选项不一致，请重新预览。");
        var kind = RuntimeUninstallCommand.Classify(plan.Entry.KeyName, plan.Entry.UninstallString, plan.Entry.QuietUninstallString);
        if (kind == "Msi")
        {
            if (!RuntimeUninstallCommand.TryCreate(plan.Entry, null, out var msi, out _) || !msi!.ProductCodes.SequenceEqual(plan.Products, StringComparer.OrdinalIgnoreCase)
                || plan.Registrations.Length != 0 || plan.RetainedProducts.Length != 0) throw new InvalidDataException("MSI 计划身份不匹配。");
            return;
        }
        if (kind is not ("Inf" or "Bundle") || plan.Registrations.Length is < 1 or > 4) throw new InvalidDataException("包装计划身份无效。");
        foreach (var snapshot in plan.Registrations)
        {
            if (snapshot.View is not (256 or 512)) throw new InvalidDataException("注册表视图无效。");
            var expectedArp = Arp + plan.Entry.KeyName;
            var provider = plan.Registrations.FirstOrDefault(s => s.KeyPath == expectedArp);
            if (provider is null) throw new InvalidDataException("包装原值证据缺失。");
            ValidateArp(plan.Entry, provider);
            var expectedDependency = Dependencies + ReadArpValue(provider, "BundleProviderKey");
            if (snapshot.KeyPath != expectedArp && (kind != "Bundle" || snapshot.KeyPath != expectedDependency))
                throw new InvalidDataException("拒绝清理无关注册表路径。");
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, (RegistryView)snapshot.View);
            using var key = hive.OpenSubKey(snapshot.KeyPath);
            if (key is not null && Capture(key) != snapshot.Content) throw new IOException("包装登记发生外部变更，保留计划，不覆盖。");
        }
        if (kind == "Inf")
        {
            var current = RuntimeUninstallCommand.ReadQuietInfProducts(RuntimeUninstallCommand.GetInfPath(plan.Entry));
            if (!current.Order().SequenceEqual(plan.Products.Order(), StringComparer.OrdinalIgnoreCase) || plan.RetainedProducts.Length != 0) throw new IOException("INF 产品关联发生变化。");
            foreach (var owner in plan.RelatedBundles)
            {
                ValidateRelatedBundle(plan, owner);
                ValidateRegistration(owner);
                foreach (var retained in owner.RetainedProducts)
                {
                    var product = ReadProduct(retained);
                    if (product.State == -1) continue;
                    RuntimeUnifiedUninstaller.ValidateProduct(plan.Entry, product);
                    if (!Version.TryParse(product.Version, out var installedVersion) || !Version.TryParse(plan.Entry.DisplayVersion, out var targetVersion)
                        || NormalizeVersion(installedVersion) != NormalizeVersion(targetVersion))
                        throw new IOException("旧包装关联的升级产品与 INF 目标版本不同，未清理其引用。");
                }
            }
            var activeOwners = ResolveRelatedBundles(plan);
            if (activeOwners.Any(o => !plan.RelatedBundles.Any(saved => saved.Entry.KeyName.Equals(o.Entry.KeyName, StringComparison.OrdinalIgnoreCase))))
                throw new IOException("INF 产品出现未核实的新包装引用，未继续。");
        }
        else
        {
            // A resumed plan may include products already removed. Still-installed products must retain their association.
            RuntimeUnifiedUninstallPlan? current = null;
            if (plan.Products.Any(p => ReadProduct(p).State != -1)) current = Resolve(plan.Entry);
            else foreach (var retained in plan.RetainedProducts)
            {
                var product = ReadProduct(retained);
                if (product.State == -1) continue;
                if (!ShouldRetainNewerMsi(plan.Entry, product, allowNonstandard))
                    throw new IOException("保留的 MSI 版本已变化，未清理旧登记。");
            }
            if (current is not null && (current.Products.Any(p => !plan.Products.Contains(p, StringComparer.OrdinalIgnoreCase))
                || plan.Products.Any(p => ReadProduct(p).State != -1 && !current.Products.Contains(p, StringComparer.OrdinalIgnoreCase))
                || current.RetainedProducts.Any(p => !plan.RetainedProducts.Contains(p, StringComparer.OrdinalIgnoreCase))
                || plan.RetainedProducts.Any(p => ReadProduct(p).State != -1 && !current.RetainedProducts.Contains(p, StringComparer.OrdinalIgnoreCase))))
                throw new IOException("Bundle 的 MSI 关联已变化，未继续。");
        }
    }

    internal static void ValidateRelatedBundle(RuntimeUnifiedUninstallPlan inf, RuntimeUnifiedUninstallPlan owner)
    {
        var all = owner.Products.Concat(owner.RetainedProducts).ToArray();
        if (inf.Entry.UninstallKind != "Inf" || owner.Entry.UninstallKind != "Bundle" || owner.RelatedBundles.Length != 0
            || !RuntimeBulkUninstall.IsCandidate(owner.Entry)
            || RuntimeUninstallCommand.Classify(owner.Entry.KeyName, owner.Entry.UninstallString, owner.Entry.QuietUninstallString) != "Bundle"
            || all.Length == 0 || all.Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Length
            || all.Any(p => !inf.Products.Contains(p, StringComparer.OrdinalIgnoreCase))
            || owner.Entry.Architecture != inf.Entry.Architecture || !Guid.TryParse(owner.Entry.KeyName, out var id) || id == Guid.Empty
            || !Version.TryParse(owner.Entry.DisplayVersion, out var ownerVersion) || !Version.TryParse(inf.Entry.DisplayVersion, out var infVersion)
            || (owner.RetainedProducts.Length == 0 ? NormalizeVersion(ownerVersion) != NormalizeVersion(infVersion)
                : owner.Products.Length != 0 || NormalizeVersion(ownerVersion) >= NormalizeVersion(infVersion)))
            throw new InvalidDataException("INF 的包装引用身份、版本或产品范围无法匹配，未扩大卸载范围。");
    }

    private RuntimeUnifiedUninstallPlan[] ResolveRelatedBundles(RuntimeUnifiedUninstallPlan inf)
    {
        var owners = new Dictionary<string, RuntimeUnifiedUninstallPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hive.OpenSubKey(Dependencies);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                if (!Regex.IsMatch(name, @"^Microsoft\.VS\.VC_Runtime(?:Minimum|Additional)(?:VSU)?_(?:amd64|x86),v(?:11|12|14)$", RegexOptions.IgnoreCase)) continue;
                using var provider = root.OpenSubKey(name);
                if (!Guid.TryParse(provider?.GetValue("") as string, out var product)
                    || !inf.Products.Contains(product.ToString("B"), StringComparer.OrdinalIgnoreCase)) continue;
                using var dependents = provider!.OpenSubKey("Dependents");
                var clients = dependents?.GetSubKeyNames() ?? [];
                diagnostics.Save("dependency-" + product.ToString("N") + "-" + (int)view + ".json",
                    new { Provider = name, Product = product, View = (int)view, Dependents = clients });
                foreach (var client in clients)
                {
                    if (owners.ContainsKey(client)) continue;
                    if (!Guid.TryParse(client, out var bundle) || bundle == Guid.Empty)
                        throw new InvalidDataException("INF 关联 MSI 存在其他软件共享引用，未强制卸载：" + client);
                    using var ownerHive64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var ownerHive32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                    using var arp64 = ownerHive64.OpenSubKey(Arp + client);
                    using var arp32 = ownerHive32.OpenSubKey(Arp + client);
                    var arp = arp64 ?? arp32;
                    if (arp is null) throw new InvalidDataException("INF 关联 MSI 的包装引用缺少可靠登记，需核对日志：" + client);
                    var entry = new VcRedistEntry { KeyName = client,
                        DisplayName = arp.GetValue("DisplayName") as string ?? "", DisplayVersion = arp.GetValue("DisplayVersion") as string ?? "",
                        Architecture = inf.Entry.Architecture, UninstallKind = "Bundle",
                        UninstallString = arp.GetValue("UninstallString") as string ?? "", QuietUninstallString = arp.GetValue("QuietUninstallString") as string ?? "" };
                    if (!RuntimeBulkUninstall.IsCandidate(entry) || RuntimeUninstallCommand.Classify(client, entry.UninstallString, entry.QuietUninstallString) != "Bundle")
                        throw new InvalidDataException("INF 关联 MSI 被非运行库安装包引用，保留共享依赖：" + client);
                    var owner = Resolve(entry);
                    ValidateRelatedBundle(inf, owner);
                    owners.Add(client, owner);
                }
            }
        }
        return owners.Values.OrderBy(p => p.Entry.KeyName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static void ValidateBundleDependents(string bundle, IEnumerable<string> dependents)
    {
        if (!Guid.TryParse(bundle, out var id) || id == Guid.Empty) throw new InvalidDataException("Bundle 身份无效。");
        var clients = dependents.ToArray();
        if (clients.Length == 0 || clients.Any(c => !Guid.TryParse(c, out var client) || client != id))
            throw new InvalidDataException("关联 MSI 仍被其他安装包依赖或归属已变化，未忽略依赖强卸载。");
    }

    internal static RuntimeUninstallCommand.Plan BuildUninstallCommand(RuntimeUnifiedUninstallPlan plan, string product, string? logPath)
    {
        if (!Guid.TryParse(product, out var productId) || productId == Guid.Empty
            || !plan.Products.Contains(product, StringComparer.OrdinalIgnoreCase)
            || plan.RetainedProducts.Contains(product, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("MSI 不属于当前卸载计划，未启动。");
        var entry = new VcRedistEntry { DisplayName = "verified", Architecture = "", KeyName = product,
            UninstallKind = "Msi", UninstallString = "msiexec.exe /x " + product };
        if (!RuntimeUninstallCommand.TryCreate(entry, logPath, out var command, out var error)) throw new InvalidDataException(error);
        if (RuntimeUninstallCommand.Classify(plan.Entry.KeyName, plan.Entry.UninstallString, plan.Entry.QuietUninstallString) != "Bundle")
            return command!;
        if (!Guid.TryParse(plan.Entry.KeyName, out var bundle) || bundle == Guid.Empty)
            throw new InvalidDataException("Bundle 身份无效，未启动。");
        // WiX v3 wixdepca.cpp SplitIgnoredDependents accepts exact dependent keys. Never use ALL.
        // Keep the original dependency metadata until confirmed removal; failure/timeout needs no rollback.
        return command! with { Arguments = [.. command!.Arguments, "IGNOREDEPENDENCIES=" + bundle.ToString("B")] };
    }

    public async Task<RuntimeStepResult> UninstallAsync(RuntimeUnifiedUninstallPlan plan, string product, string logPath, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
    {
        var preflight = RuntimeRepairPreflight.Read();
        if (preflight.Blocker is { } blocker) return new(-1, blocker, RebootPending: preflight.RebootPending);
        if (_runner.CheckPending(preflight.InstallerBusy) is { } pending) return new(null, pending, OutcomeUnknown: true);
        token.ThrowIfCancellationRequested();
        ValidateRegistration(plan);
        var command = BuildUninstallCommand(plan, product, logPath);
        diagnostics.Event("msi-start-requested", new { Product = product, command.Executable, command.Arguments, Log = logPath });
        diagnostics.Save(Path.GetFileNameWithoutExtension(logPath) + "-command.json", new { command.Kind, command.Arguments,
            DependencyScope = command.Arguments.FirstOrDefault(a => a.StartsWith("IGNOREDEPENDENCIES=", StringComparison.Ordinal)) });
        var result = await _runner.RunAsync(command.CreateStartInfo(), "工具统一 MSI 卸载 " + product, timeout, progress, token,
            process => diagnostics.Event("msi-started", new { Product = product, Pid = process.Id, command.Executable }));
        if (result.OutcomeUnknown)
        {
            try
            {
                var activePath = Path.Combine(AppDataPaths.Root, "runtime-repair-active.json");
                if (File.Exists(activePath) && new FileInfo(activePath).Length < 64 * 1024)
                {
                    using var active = JsonDocument.Parse(File.ReadAllText(activePath));
                    diagnostics.Save(Path.GetFileNameWithoutExtension(logPath) + "-active.json", active.RootElement);
                }
            }
            catch (Exception ex) { diagnostics.Event("activity-copy-failed", new { Error = ex.Message }); }
        }
        try
        {
            if (File.Exists(logPath))
            {
                var lines = File.ReadLines(logPath).Where(line => line.Contains("WixDependencyCheck:", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("CustomAction WixDependencyCheck returned actual error code", StringComparison.OrdinalIgnoreCase)).Take(16);
                result = result with { Details = result.Details + "\n" + diagnostics.ReadErrorDetails(logPath) + "\n" + string.Join("\n", lines) };
            }
        }
        catch (Exception ex) { Log.Warn("统一卸载日志无法读取：" + ex.Message); }
        Log.Info($"统一卸载 MSI 返回：产品={product}；退出码={result.ExitCode}；未知={result.OutcomeUnknown}；日志={logPath}");
        return result;
    }

    public void CleanupRegistration(RuntimeUnifiedUninstallPlan plan)
    {
        ValidateRegistration(plan);
        if (plan.Products.Any(p => ReadProduct(p).State != -1)) throw new IOException("MSI 产品尚未全部移除。");
        if (RuntimeUninstallCommand.Classify(plan.Entry.KeyName, plan.Entry.UninstallString, plan.Entry.QuietUninstallString) == "Bundle") CleanupPackageDependencies(plan);
        foreach (var snapshot in plan.Registrations.OrderBy(s => s.KeyPath.StartsWith(Arp) ? 1 : 0))
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, (RegistryView)snapshot.View);
            using (var key = hive.OpenSubKey(snapshot.KeyPath))
                if (key is not null && Capture(key) != snapshot.Content) throw new IOException("登记清理前原值复核失败。");
            hive.DeleteSubKeyTree(snapshot.KeyPath, throwOnMissingSubKey: false);
            using var after = hive.OpenSubKey(snapshot.KeyPath);
            if (after is not null) throw new IOException("包装登记清理复检失败。");
            Log.Info("统一卸载：仅清理已验证包装登记，原值记录保留：" + snapshot.View + "；" + snapshot.KeyPath);
        }
    }

    private void CleanupPackageDependencies(RuntimeUnifiedUninstallPlan plan)
    {
        var remaining = new List<RuntimeRegistrationSnapshot>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hive.OpenSubKey(Dependencies);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                if (!Regex.IsMatch(name, @"^Microsoft\.VS\.VC_Runtime(?:Minimum|Additional)(?:VSU)?_(?:amd64|x86),v(?:11|12|14)$", RegexOptions.IgnoreCase)) continue;
                using var package = root.OpenSubKey(name);
                using var dependents = package?.OpenSubKey("Dependents");
                var clients = dependents?.GetSubKeyNames() ?? [];
                if (!clients.Any(c => c.Equals(plan.Entry.KeyName, StringComparison.OrdinalIgnoreCase))) continue;
                if (!Guid.TryParse(package?.GetValue("") as string, out var product)) throw new IOException("MSI 依赖产品码已变化。");
                if (plan.RetainedProducts.Contains(product.ToString("B"), StringComparer.OrdinalIgnoreCase))
                {
                    using var child = dependents!.OpenSubKey(plan.Entry.KeyName) ?? throw new IOException("旧包装依赖已变化。");
                    remaining.Add(new(Dependencies + name + "\\Dependents\\" + plan.Entry.KeyName, (int)view, Capture(child)));
                    continue;
                }
                if (!plan.Products.Contains(product.ToString("B"), StringComparer.OrdinalIgnoreCase)
                    || clients.Any(c => !c.Equals(plan.Entry.KeyName, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("MSI 依赖关系发生外部变更，未清理包装登记。");
                remaining.Add(new(Dependencies + name, (int)view, Capture(package!)));
            }
        }
        if (remaining.Count == 0) return;
        // Persist exact remaining dependency metadata before removing only the verified orphan providers.
        File.WriteAllText(Path.Combine(diagnostics.DirectoryPath, "dependency-cleanup-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(remaining));
        foreach (var snapshot in remaining)
        {
            if (plan.Products.Any(p => ReadProduct(p).State != -1)) throw new IOException("依赖清理前发现 MSI 重新登记。");
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, (RegistryView)snapshot.View);
            using (var key = hive.OpenSubKey(snapshot.KeyPath))
                if (key is not null && Capture(key) != snapshot.Content) throw new IOException("依赖清理前指纹已变化。");
            hive.DeleteSubKeyTree(snapshot.KeyPath, throwOnMissingSubKey: false);
        }
    }

    private static Version NormalizeVersion(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private sealed record Vc2013Bundle(Guid Id, string Architecture, Guid Minimum, Guid Additional);
    // Verified against Microsoft's signed 12.0.30501 packages, Burn manifest and BA data.
    // Bundle DisplayVersion is not the ProductVersion of the MSI packages in its chain.
    private static readonly Version Vc2013BundleVersion = new(12, 0, 30501, 0);
    private static readonly Version Vc2013MsiVersion = new(12, 0, 21005, 0);
    private static readonly Vc2013Bundle[] Vc2013Bundles =
    [
        new(new("050d4fc8-5d48-4b8f-8972-47c82c46020f"), "x64",
            new("A749D8E6-B613-3BE3-8F5F-045C84EBA29B"), new("929FBD26-9020-399B-9A7A-751D61F0B942")),
        new(new("f65db027-aff3-4070-886a-0d87064aabb1"), "x86",
            new("13A4EE12-23EA-3371-91EE-EFB36DDFFF3E"), new("F8CFEB22-A2E7-3971-9EDA-4B11EDEFC185")),
    ];

    internal static bool ShouldRetainNewerMsi(VcRedistEntry entry, RuntimeMsiProduct product, bool allowNonstandard = false)
    {
        RuntimeUnifiedUninstaller.ValidateProduct(entry, product);
        if (!Version.TryParse(product.Version, out var current) || !Version.TryParse(entry.DisplayVersion, out var original))
            throw new InvalidDataException("Bundle / MSI 版本无法核验。");
        var expected = NormalizeVersion(original);
        if (Guid.TryParse(entry.KeyName, out var bundleId) && Vc2013Bundles.FirstOrDefault(b => b.Id == bundleId) is { } bundle)
        {
            if (entry.UninstallKind != "Bundle" || entry.Architecture != bundle.Architecture || expected != Vc2013BundleVersion
                || !entry.DisplayName.Equals($"Microsoft Visual C++ 2013 Redistributable ({bundle.Architecture}) - 12.0.30501", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("VC2013 包装身份、架构或版本与已核验的官方清单不符。");
            expected = Vc2013MsiVersion;
            if (NormalizeVersion(current) == expected || allowNonstandard && NormalizeVersion(current) < expected)
            {
                var code = Guid.Parse(product.Code);
                var role = code == bundle.Minimum ? "Minimum" : code == bundle.Additional ? "Additional" : null;
                if (allowNonstandard)
                {
                    var actualRole = Regex.Match(product.Name, $@"^Microsoft Visual C\+\+ 2013 {bundle.Architecture} (Minimum|Additional) Runtime\b", RegexOptions.IgnoreCase);
                    if (!actualRole.Success || role is not null && !role.Equals(actualRole.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("VC2013 子组件角色不符，强制尝试也不猜测归属。");
                }
                else if (role is null || !product.Name.Equals($"Microsoft Visual C++ 2013 {bundle.Architecture} {role} Runtime - 12.0.21005", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("VC2013 子组件产品码或角色与已核验的官方清单不符。");
            }
        }
        var comparison = NormalizeVersion(current).CompareTo(expected);
        if (comparison < 0 && !allowNonstandard) throw new InvalidDataException("Bundle 与 MSI 版本不匹配，未猜测卸载。");
        return comparison > 0;
    }

    private static List<RuntimeRegistrationSnapshot> ReadArpSnapshots(VcRedistEntry entry)
    {
        if (entry.KeyName.IndexOfAny(['\\', '/']) >= 0 || entry.KeyName.Length == 0) throw new InvalidDataException("卸载条目键名无效。");
        var snapshots = new List<RuntimeRegistrationSnapshot>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hive.OpenSubKey(Arp + entry.KeyName);
            if (key is not null) snapshots.Add(new(Arp + entry.KeyName, (int)view, Capture(key)));
        }
        return snapshots;
    }

    private static void ValidateArp(VcRedistEntry entry, RuntimeRegistrationSnapshot snapshot)
    {
        if (ReadArpValue(snapshot, "DisplayName") != entry.DisplayName || ReadArpValue(snapshot, "DisplayVersion") != entry.DisplayVersion
            || !ReadArpValue(snapshot, "Publisher").Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
            || ReadArpValue(snapshot, "UninstallString") != entry.UninstallString)
            throw new IOException("包装登记与扫描条目不一致或发布者无法确认。");
    }

    private static string ReadArpValue(RuntimeRegistrationSnapshot snapshot, string name)
    {
        using var json = JsonDocument.Parse(snapshot.Content);
        return json.RootElement.GetProperty("Values").TryGetProperty(name, out var value) && value.GetProperty("Data").ValueKind == JsonValueKind.String
            ? value.GetProperty("Data").GetString()! : "";
    }

    private static string Capture(RegistryKey key, int depth = 0)
    {
        if (depth > 2 || key.GetValueNames().Length > 128 || key.GetSubKeyNames().Length > 128) throw new IOException("包装登记结构超限。");
        var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var name in key.GetValueNames()) values[name] = new { Kind = key.GetValueKind(name), Data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) };
        var children = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in key.GetSubKeyNames())
        { using var child = key.OpenSubKey(name) ?? throw new IOException("登记子键无法读取。"); children[name] = JsonSerializer.Deserialize<JsonElement>(Capture(child, depth + 1)); }
        var result = JsonSerializer.Serialize(new { Values = values, Children = children });
        if (result.Length > 128 * 1024) throw new IOException("包装登记内容超限。");
        return result;
    }
}
