using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeltaNFD.Services;

internal sealed record Vc14DependencyProvider(string Location, string Key, string ProductCode, string Values,
    IReadOnlyList<string> Clients, string ClientValues = "");
internal sealed record Vc14DependencyOwner(string Location, string Name, string Version, string Publisher,
    bool CacheExists, bool CacheVerified, string Values);
internal sealed record Vc14DependencyApproval(string Fingerprint, IReadOnlyList<string> Clients, string Evidence)
{
    internal string Argument => Clients.Count == 0 ? "" : "IGNOREDEPENDENCIES=" + string.Join(";", Clients);
}

/// <summary>只放行核验过的 CRT 自身包装引用。不删除依赖登记，不使用全局忽略。</summary>
internal static class Vc14DependencyPolicy
{
    private const string Dependencies = @"SOFTWARE\Classes\Installer\Dependencies";
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
    private static readonly Regex ProviderName = new(@"^Microsoft\.VS\.VC_Runtime(Minimum|Additional)(?:VSU)?_(amd64|x86),v14$", RegexOptions.IgnoreCase);

    internal static Vc14DependencyApproval Read(Vc14Entry product)
    {
        var providers = new List<Vc14DependencyProvider>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hive.OpenSubKey(Dependencies);
            foreach (var name in root?.GetSubKeyNames() ?? [])
            {
                if (!ProviderName.IsMatch(name)) continue;
                using var provider = root!.OpenSubKey(name) ?? throw new IOException("运行库依赖登记在核对中消失，请重试。");
                var code = provider.GetValue("") as string ?? "";
                if (!SameCode(code, product.Code)) continue;
                using var dependents = provider.OpenSubKey("Dependents");
                var clients = (dependents?.GetSubKeyNames() ?? []).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                if (clients.Length > 1000) throw new IOException("运行库依赖数量异常，未执行卸载。");
                var values = new List<string> { dependents is null ? "absent" : Values(dependents) };
                foreach (var client in clients)
                {
                    using var key = dependents!.OpenSubKey(client) ?? throw new IOException("依赖引用在核对中消失，请重试。");
                    if (key.SubKeyCount != 0) throw new IOException("依赖引用包含未知子项，未执行卸载：" + client);
                    values.Add(client + ":" + Values(key));
                }
                providers.Add(new("LocalMachine:" + view + ":" + Dependencies, name, code, Values(provider), clients,
                    JsonSerializer.Serialize(values)));
            }
        }
        return Evaluate(product, providers, ReadOwners, VerifyPinnedBaseline);
    }

    // Pure policy boundary: fixtures do not access a registry or start installers.
    internal static Vc14DependencyApproval Evaluate(Vc14Entry product, IReadOnlyList<Vc14DependencyProvider> providers,
        Func<string, IReadOnlyList<Vc14DependencyOwner>> readOwners, Func<string, bool> verifyPinnedBaseline)
    {
        if (!Guid.TryParse(product.Code, out var id) || id == Guid.Empty
            || product.Architecture is not ("x64" or "x86") || product.Role is not ("Minimum" or "Additional")
            || !Vc14BetaUninstall.InScope(product.Name, product.Version, product.Architecture))
            throw new IOException("待核验依赖的 MSI 身份不属于 C++ 14.x。");
        var ordered = providers.OrderBy(p => p.Location, StringComparer.Ordinal).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var clients = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var proofs = new List<object>();
        foreach (var provider in ordered)
        {
            var match = ProviderName.Match(provider.Key);
            if (!match.Success || !SameCode(provider.ProductCode, product.Code)
                || !match.Groups[1].Value.Equals(product.Role, StringComparison.OrdinalIgnoreCase)
                || !match.Groups[2].Value.Equals(product.Architecture == "x64" ? "amd64" : "x86", StringComparison.OrdinalIgnoreCase))
                throw new IOException("运行库依赖提供者的产品码、架构或角色不匹配，未执行卸载。");
            foreach (var client in provider.Clients.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!Guid.TryParseExact(client, "B", out var code) || code == Guid.Empty)
                    throw Block(provider.Key, client);
                var owners = readOwners(client).OrderBy(o => o.Location, StringComparer.Ordinal).ToArray();
                var baseline = Vc14Reinstall.BaselineBundles.TryGetValue(product.Architecture, out var baselineId) && SameCode(client, baselineId);
                var pinned = baseline && verifyPinnedBaseline(product.Architecture);
                if (baseline && !pinned) throw new IOException("内置 14.42 原包未通过固定哈希、版本或微软签名校验，未放行旧包装引用：" + client);
                // A missing ARP entry is accepted ONLY for the exact architecture-specific bundle
                // proven by the pinned Microsoft baseline. An arbitrary orphan GUID is never approved.
                if (owners.Length == 0 && !pinned) throw Block(provider.Key, client);
                foreach (var owner in owners)
                {
                    var expectedVersion = baseline ? Vc14Reinstall.BaselineVersion : product.Version;
                    if (!owner.Publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                        || !Regex.IsMatch(owner.Name, @"^Microsoft Visual C\+\+ (?:2015-2022|2015-2019|2015|2017|2019|2022|2026|v14) Redistributable \("
                            + product.Architecture + @"\)(?: - 14\..*)?$", RegexOptions.IgnoreCase)
                        || !Vc14Catalog.SameVersion(owner.Version, expectedVersion)
                        || (owner.CacheExists ? !owner.CacheVerified : !pinned)) throw Block(provider.Key, client);
                }
                clients.Add(client);
                proofs.Add(new { provider.Location, provider.Key, Client = client, Owners = owners,
                    PinnedBaselineSha256 = pinned ? Vc14Reinstall.BaselineHashes[product.Architecture] : "" });
            }
        }
        var evidence = JsonSerializer.Serialize(new { Product = product, Providers = ordered, Proofs = proofs });
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence))), clients.ToArray(), evidence);
    }

    private static IOException Block(string provider, string client) => new(
        "14.x 组件被其他软件或无法核验的包装引用，未执行卸载：" + provider + "；引用：" + client
        + "。仅对已核验的运行库自身包装放行，不会删除共享依赖或忽略所有引用。");

    private static IReadOnlyList<Vc14DependencyOwner> ReadOwners(string client)
    {
        var result = new List<Vc14DependencyOwner>();
        foreach (var (hiveName, view) in Vc14Catalog.RegistryViews())
        {
            using var hive = RegistryKey.OpenBaseKey(hiveName, view);
            using var key = hive.OpenSubKey(Uninstall + client);
            if (key is null) continue;
            var version = Vc14Catalog.RegistrationVersion(key);
            var bundleVersion = key.GetValue("BundleVersion") as string;
            if (!string.IsNullOrEmpty(bundleVersion) && !Vc14Catalog.SameVersion(bundleVersion, version))
                throw new IOException("运行库包装的登记版本互相矛盾，未执行卸载：" + client);
            var name = key.GetValue("DisplayName") as string ?? "";
            var arch = Regex.IsMatch(name, @"\(x64\)", RegexOptions.IgnoreCase) ? "x64" : "x86";
            var cache = key.GetValue("BundleCachePath") as string;
            var exists = !string.IsNullOrWhiteSpace(cache) && File.Exists(cache);
            var verified = exists && Vc14PackageTrust.Verify(cache, version, arch);
            var cacheHash = exists ? HashFile(cache!) : "";
            result.Add(new(hiveName + ":" + view + ":" + Uninstall + client, name, version,
                key.GetValue("Publisher") as string ?? "", exists, verified,
                JsonSerializer.Serialize(new { Registry = Values(key), CacheHash = cacheHash })));
        }
        return result;
    }

    private static bool VerifyPinnedBaseline(string arch)
    {
        var path = Vc14ReinstallEnvironment.Baseline(arch);
        return HashFile(path).Equals(Vc14Reinstall.BaselineHashes[arch], StringComparison.OrdinalIgnoreCase)
            && Vc14PackageTrust.Verify(path, Vc14Reinstall.BaselineVersion, arch);
    }
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static bool SameCode(string first, string second) => Guid.TryParse(first, out var a) && Guid.TryParse(second, out var b) && a == b;
    private static string Values(RegistryKey key) => JsonSerializer.Serialize(key.GetValueNames().Order(StringComparer.Ordinal)
        .Select(n => new { Name = n, Kind = key.GetValueKind(n).ToString(), Value = key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames) }).ToArray());

    internal static Vc14StepResult VerifyRemoval(Vc14StepResult step, Func<Vc14Entry?> readProduct, Func<string> readLog)
    {
        if (!step.Success || step.Reboot) return step;
        try
        {
            if (readProduct() is null) return step;
            string log;
            try { log = readLog(); } catch { log = ""; }
            var references = Regex.Matches(log, "Found dependent \\\"(\\{[0-9a-fA-F-]{36}\\})\\\"")
                .Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var reason = log.Contains("WixDependencyCheck", StringComparison.Ordinal) && log.Contains("Return value 5", StringComparison.Ordinal)
                ? "Windows Installer 的依赖检查提前结束了卸载（WixDependencyCheck），组件实际仍在。引用：" + string.Join("；", references)
                : "卸载进程返回成功，但复检发现 MSI 组件实际仍在。";
            return step with { PostcheckPassed = false, Detail = reason + " 已停止后续卸载和安装，请核对依赖记录；重复点击不会解决。\n" + step.Detail };
        }
        catch (Exception ex) { return step with { Unknown = true, PostcheckPassed = false, Detail = "卸载后 MSI 状态无法确认，已停止：" + ex.Message + "\n" + step.Detail }; }
    }

    internal static string ReadInstallerLog(string path)
    {
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[2 * 1024 * 1024];
        var length = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, length);
    }
}
