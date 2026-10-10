using Microsoft.Win32;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

internal sealed class RuntimeUnifiedUninstallBackend(RuntimeRepairDiagnostics diagnostics) : IRuntimeUnifiedUninstallBackend
{
    private const string Arp = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
    private const string Dependencies = @"SOFTWARE\Classes\Installer\Dependencies\";
    private readonly RuntimeRepairProcessRunner _runner = new(AppDataPaths.Root);

    public RuntimeMsiProduct ReadProduct(string product) => RuntimeMsiMetadata.ReadInstalledProduct(product);

    public RuntimeUnifiedUninstallPlan Resolve(VcRedistEntry entry)
    {
        var kind = RuntimeUninstallCommand.Classify(entry.KeyName, entry.UninstallString, entry.QuietUninstallString);
        if (kind == "Msi")
        {
            if (!RuntimeUninstallCommand.TryCreate(entry, null, out var plan, out var error)) throw new InvalidDataException(error);
            return new(entry, plan!.ProductCodes, []);
        }
        var registrations = ReadArpSnapshots(entry);
        if (registrations.Count == 0) throw new InvalidDataException("包装登记不存在，无法确认归属。");
        foreach (var snapshot in registrations) ValidateArp(entry, snapshot);
        if (kind == "Inf")
        {
            var products = RuntimeUninstallCommand.ReadQuietInfProducts(RuntimeUninstallCommand.GetInfPath(entry));
            if (products.Length == 0) throw new InvalidDataException("INF 关联 MSI 无法可靠确认。");
            return new(entry, products, registrations.ToArray());
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
            if (!string.Equals(own.GetValue("") as string, entry.KeyName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Bundle 依赖身份已变化。");
            using var ownDependents = own.OpenSubKey("Dependents");
            if (ownDependents?.GetSubKeyNames().Any(k => !k.Equals(entry.KeyName, StringComparison.OrdinalIgnoreCase)) == true)
                throw new InvalidDataException("Bundle 被其他安装包依赖，拒绝统一卸载。");
            registrations.Add(new(Dependencies + provider, (int)view, Capture(own)));
            using var root = hive.OpenSubKey(Dependencies);
            foreach (var name in root!.GetSubKeyNames())
            {
                // Only the known VC release-runtime dependency provider family, never Debug/SDK/VSTO.
                if (!Regex.IsMatch(name, @"^Microsoft\.VS\.VC_Runtime(?:Minimum|Additional)(?:VSU)?_(?:amd64|x86),v(?:11|12|14)$", RegexOptions.IgnoreCase)) continue;
                using var package = root.OpenSubKey(name);
                using var dependents = package?.OpenSubKey("Dependents");
                var clients = dependents?.GetSubKeyNames() ?? [];
                if (!clients.Any(k => k.Equals(entry.KeyName, StringComparison.OrdinalIgnoreCase))) continue;
                if (!Guid.TryParse(package?.GetValue("") as string, out var product)) throw new InvalidDataException("依赖提供者缺少 MSI 产品码。");
                var installed = ReadProduct(product.ToString("B"));
                if (installed.State != -1)
                {
                    if (ShouldRetainNewerMsi(entry, installed)) { retained.Add(product.ToString("B")); continue; }
                }
                if (clients.Any(k => !k.Equals(entry.KeyName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("关联 MSI 还被其他安装包依赖，未忽略依赖强卸载：" + name);
                productsFound.Add(product.ToString("B"));
            }
        }
        if (productsFound.Count + retained.Count == 0) throw new InvalidDataException("缺少 Bundle → MSI 的可靠依赖关系；未执行包装 EXE。");
        return new(entry, productsFound.ToArray(), registrations.Distinct().ToArray()) { RetainedProducts = retained.ToArray() };
    }

    public void ValidateRegistration(RuntimeUnifiedUninstallPlan plan)
    {
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
                RuntimeUnifiedUninstaller.ValidateProduct(plan.Entry, product);
                if (!Version.TryParse(plan.Entry.DisplayVersion, out var oldVersion) || !Version.TryParse(product.Version, out var newVersion)
                    || NormalizeVersion(newVersion) <= NormalizeVersion(oldVersion))
                    throw new IOException("保留的 MSI 版本已变化，未清理旧登记。");
            }
            if (current is not null && (current.Products.Any(p => !plan.Products.Contains(p, StringComparer.OrdinalIgnoreCase))
                || plan.Products.Any(p => ReadProduct(p).State != -1 && !current.Products.Contains(p, StringComparer.OrdinalIgnoreCase))
                || current.RetainedProducts.Any(p => !plan.RetainedProducts.Contains(p, StringComparer.OrdinalIgnoreCase))
                || plan.RetainedProducts.Any(p => ReadProduct(p).State != -1 && !current.RetainedProducts.Contains(p, StringComparer.OrdinalIgnoreCase))))
                throw new IOException("Bundle 的 MSI 关联已变化，未继续。");
        }
    }

    public async Task<RuntimeStepResult> UninstallAsync(string product, string logPath, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
    {
        var preflight = RuntimeRepairPreflight.Read();
        if (preflight.Blocker is { } blocker) return new(-1, blocker, RebootPending: preflight.RebootPending);
        if (_runner.CheckPending(preflight.InstallerBusy) is { } pending) return new(null, pending, OutcomeUnknown: true);
        var entry = new VcRedistEntry { DisplayName = "verified", Architecture = "", KeyName = product, UninstallKind = "Msi", UninstallString = "msiexec.exe /x " + product };
        if (!RuntimeUninstallCommand.TryCreate(entry, logPath, out var command, out var error)) return new(-1, error);
        var result = await _runner.RunAsync(command!.CreateStartInfo(), "工具统一 MSI 卸载 " + product, timeout, progress, token);
        try { if (File.Exists(logPath)) result = result with { Details = result.Details + "\n" + diagnostics.ReadErrorDetails(logPath) }; }
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

    internal static bool ShouldRetainNewerMsi(VcRedistEntry entry, RuntimeMsiProduct product)
    {
        RuntimeUnifiedUninstaller.ValidateProduct(entry, product);
        if (!Version.TryParse(product.Version, out var current) || !Version.TryParse(entry.DisplayVersion, out var original))
            throw new InvalidDataException("Bundle / MSI 版本无法核验。");
        var comparison = NormalizeVersion(current).CompareTo(NormalizeVersion(original));
        if (comparison < 0) throw new InvalidDataException("Bundle 与 MSI 版本不匹配，未猜测卸载。");
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
