using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeltaNFD.Services;

internal sealed record Vc14DependencyProvider(string Location, string Key, string ProductCode, string Values,
    IReadOnlyList<string> Clients, string ClientValues = "");
internal sealed record Vc14DependencyOwner(string Location, string Name, string Version, string Publisher,
    bool CacheExists, bool CacheVerified, string Values, Vc14BundleProof? Proof = null, string ReadIssue = "",
    string IdentitySource = "Registry");
internal sealed record Vc14DependencyReference(string Location, string Provider, string Client,
    IReadOnlyList<Vc14DependencyOwner> Owners, bool Approved, string Reason);
internal sealed record Vc14DependencyReport(Vc14Entry Product, IReadOnlyList<Vc14DependencyProvider> Providers,
    IReadOnlyList<Vc14DependencyReference> References, IReadOnlyList<string> Issues);
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
        var observed = false;
        var saved = false;
        try
        {
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
            return Evaluate(product, providers, client => ReadOwners(client, product.Architecture), VerifyPinnedBaseline,
                report => { observed = true; saved = SaveReport(report); });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (!observed) saved = SaveReport(new(product, providers, [], ["依赖登记读取未完成：" + ex.Message]));
            var path = ReportPath(product);
            throw new IOException(ex.Message + (saved ? "\n本次依赖诊断：" + path : "\n本次依赖诊断未能保存，详见应用日志。"), ex);
        }
    }

    // Pure policy boundary: fixtures do not access a registry or start installers.
    internal static Vc14DependencyApproval Evaluate(Vc14Entry product, IReadOnlyList<Vc14DependencyProvider> providers,
        Func<string, IReadOnlyList<Vc14DependencyOwner>> readOwners, Func<string, bool> verifyPinnedBaseline,
        Action<Vc14DependencyReport>? observe = null)
    {
        if (!Guid.TryParse(product.Code, out var id) || id == Guid.Empty
            || product.Architecture is not ("x64" or "x86") || product.Role is not ("Minimum" or "Additional")
            || !Vc14BetaUninstall.InScope(product.Name, product.Version, product.Architecture))
            throw new IOException("待核验依赖的 MSI 身份不属于 C++ 14.x。");
        var ordered = providers.OrderBy(p => p.Location, StringComparer.Ordinal).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var clients = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var proofs = new List<object>();
        var references = new List<Vc14DependencyReference>();
        var issues = new List<string>();
        foreach (var provider in ordered)
        {
            var match = ProviderName.Match(provider.Key);
            if (!match.Success || !SameCode(provider.ProductCode, product.Code)
                || !match.Groups[1].Value.Equals(product.Role, StringComparison.OrdinalIgnoreCase)
                || !match.Groups[2].Value.Equals(product.Architecture == "x64" ? "amd64" : "x86", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add("运行库依赖提供者的产品码、架构或角色不匹配：" + provider.Key);
                continue;
            }
            foreach (var client in provider.Clients.Order(StringComparer.OrdinalIgnoreCase))
            {
                Vc14DependencyOwner[] owners = [];
                try
                {
                    if (!Guid.TryParseExact(client, "B", out var code) || code == Guid.Empty)
                        throw new IOException("引用不是有效的包装 GUID");
                    owners = readOwners(client).OrderBy(o => o.Location, StringComparer.Ordinal).ToArray();
                    var baseline = Vc14Reinstall.BaselineBundles.TryGetValue(product.Architecture, out var baselineId) && SameCode(client, baselineId);
                    var pinned = baseline && verifyPinnedBaseline(product.Architecture);
                    if (baseline && !pinned) throw new IOException("内置 14.42 原包未通过固定哈希、版本或微软签名校验");
                    owners = owners.Select(o => CompleteMissingOwnerIdentity(client, product.Architecture, o)).ToArray();
                    if (pinned) owners = owners.Select(o => CompletePinnedBaselineIdentity(product.Architecture, o)).ToArray();
                    if (owners.Length == 0 && !pinned)
                        throw new IOException("四视图未找到引用者的卸载登记，精确 GUID 缓存目录也没有可核验原包（孤立引用）");
                    foreach (var owner in owners)
                    {
                        if (owner.ReadIssue.Length > 0) throw new IOException(owner.Location + "：" + owner.ReadIssue);
                        if (!owner.Publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                            || !IsRuntimeName(owner.Name, product.Architecture))
                            throw new IOException("引用归属为其他软件或名称/发布者/架构不符：" + owner.Name + "；" + owner.Location);
                        if (!Version.TryParse(owner.Version, out var parsed) || parsed.Major != 14)
                            throw new IOException("引用者不是已核验的 14.x 版本：" + owner.Version);
                        if (baseline && !Vc14Catalog.SameVersion(owner.Version, Vc14Reinstall.BaselineVersion))
                            throw new IOException("固定 14.42 包装码与登记版本互相矛盾：" + owner.Version);
                        if (owner.CacheExists ? !owner.CacheVerified : !pinned)
                            throw new IOException(owner.Proof is { Verified: true } cache
                                ? $"缓存原包版本/架构与登记不一致：登记 {owner.Version}；原包 {cache.Version}/{cache.Architecture}"
                                : owner.Proof?.Reason ?? "引用者的缓存缺失或未通过微软签名/版本核验");
                        if (owner.Proof is { } proof && (!proof.Verified || !SameCode(proof.BundleId, client)
                            || proof.Architecture != product.Architecture || !Vc14Catalog.SameVersion(proof.Version, owner.Version)
                            || !Regex.IsMatch(proof.Sha256, "^[0-9a-fA-F]{64}$")))
                            throw new IOException("缓存内置包装身份与登记/引用不一致：" + proof.Reason);
                        // Cross-version owners require the package's signed intrinsic ID, not just ARP labels.
                        if (!baseline && !Vc14Catalog.SameVersion(owner.Version, product.Version) && owner.Proof is not { Verified: true })
                            throw new IOException("历史包装版本不同且缺少签名原包内置 BundleId 证明：" + owner.Version);
                    }
                    var historical = owners.Any(o => !Vc14Catalog.SameVersion(o.Version, product.Version));
                    references.Add(new(provider.Location, provider.Key, client, owners, true,
                        historical ? "已核验的历史 C++ 14.x 自身包装引用" : "已核验的 C++ 14.x 自身包装引用"));
                    clients.Add(client);
                    proofs.Add(new { provider.Location, provider.Key, Client = client, Owners = owners,
                        PinnedBaselineSha256 = pinned ? Vc14Reinstall.BaselineHashes[product.Architecture] : "" });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    var reason = ex.Message + (owners.Length > 0 ? "；引用者：" + string.Join("；", owners.Take(4)
                        .Select(o => $"{o.Name} {o.Version}（{o.Location}）")) : "");
                    references.Add(new(provider.Location, provider.Key, client, owners, false, reason));
                    issues.Add(provider.Key + "；引用 " + client + "：" + reason);
                }
            }
        }
        observe?.Invoke(new(product, ordered, references, issues));
        if (issues.Count > 0) throw new IOException("14.x 卸载前依赖核验未通过，未执行卸载：\n" + string.Join("\n", issues.Take(8))
            + (issues.Count > 8 ? "\n其他引用见诊断报告。" : ""));
        var evidence = JsonSerializer.Serialize(new { Product = product, Providers = ordered, Proofs = proofs });
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence))), clients.ToArray(), evidence);
    }

    private static bool IsRuntimeName(string name, string arch) => Regex.IsMatch(name,
        @"^Microsoft Visual C\+\+ (?:2015-2022|2015-2019|2015|2017|2019|2022|2026|v14) Redistributable \("
        + arch + @"\)(?: - 14\..*)?$", RegexOptions.IgnoreCase);

    private static string ReportPath(Vc14Entry product) => Path.Combine(Log.LogDirectory,
        $"vc14-dependencies-{product.Architecture}-{product.Role}.json");

    private static bool SaveReport(Vc14DependencyReport report)
    {
        var path = ReportPath(report.Product);
        try
        {
            Vc14Diagnostics.Write(path, new { CapturedUtc = DateTimeOffset.UtcNow,
                ProductVersion = typeof(Vc14DependencyPolicy).Assembly.GetName().Version?.ToString(),
                OwnerLookupScope = new[] { "HKLM:Registry64:Uninstall", "HKLM:Registry32:Uninstall",
                    "HKCU:Registry64:Uninstall", "HKCU:Registry32:Uninstall", "ProgramData/Package Cache/{ExactReferenceGuid}/vc_redist.{Architecture}.exe",
                    "Assets/Vc14OwnershipOriginals: pinned Microsoft originals (ownership evidence only)" },
                Report = report });
            if (report.Issues.Count > 0) Log.Warn("14.x 依赖核验未通过：" + string.Join("；", report.Issues.Take(8)) + "；诊断：" + path);
            else Log.Info("14.x 依赖核验通过：" + report.Product.Code + "；自身包装引用=" + report.References.Count + "；诊断：" + path);
            return true;
        }
        catch (Exception ex) { Log.Warn("14.x 依赖诊断保存失败：" + ex.Message); return false; }
    }

    internal static IReadOnlyList<Vc14DependencyOwner> ReadOwners(string client, string expectedArch)
    {
        var result = new List<Vc14DependencyOwner>();
        foreach (var (hiveName, view) in Vc14Catalog.RegistryViews())
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(hiveName, view);
                using var key = hive.OpenSubKey(Uninstall + client);
                if (key is null) continue;
                var version = Vc14Catalog.RegistrationVersion(key);
                var bundleVersion = key.GetValue("BundleVersion") as string;
                var issue = !string.IsNullOrEmpty(bundleVersion) && !Vc14Catalog.SameVersion(bundleVersion, version)
                    ? "运行库包装的登记版本互相矛盾" : "";
                var name = key.GetValue("DisplayName") as string ?? "";
                var arch = Regex.IsMatch(name, @"\(x64\)", RegexOptions.IgnoreCase) ? "x64" : "x86";
                var cache = key.GetValue("BundleCachePath") as string;
                var exists = CacheExists(cache);
                var proof = exists ? Vc14BundleIdentity.Read(cache!, client, expectedArch) : null;
                var verified = proof is { Verified: true } && Vc14Catalog.SameVersion(proof.Version, version) && arch == expectedArch;
                result.Add(new(hiveName + ":" + view + ":" + Uninstall + client, name, version,
                    key.GetValue("Publisher") as string ?? "", exists, verified,
                    JsonSerializer.Serialize(new { Registry = Values(key), CachePath = cache }), proof, issue));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                result.Add(new(hiveName + ":" + view + ":" + Uninstall + client, "", "", "", false, false, "",
                    ReadIssue: "引用归属登记无法读取：" + ex.Message));
            }
        }
        var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache");
        var cached = ResolveCacheOwners(client, expectedArch, result, cacheRoot);
        return Vc14OwnershipOriginals.Resolve(client, expectedArch, cached, Path.Combine(BackendEnvironment.AssetsRoot, "Vc14OwnershipOriginals"));
    }

    internal static IReadOnlyList<Vc14DependencyOwner> ResolveCacheOwners(string client, string expectedArch,
        IReadOnlyList<Vc14DependencyOwner> registrations, string cacheRoot)
    {
        var result = registrations.ToList();
        if (result.Count == 0 || result.All(o => !o.CacheExists))
        {
            foreach (var candidate in Vc14BundleIdentity.FixedCacheCandidates(cacheRoot, client, expectedArch))
            {
                try { if (!CacheExists(candidate)) continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    result.Add(new("ExactGuidPackageCache:" + candidate, "", "", "", false, false, "",
                        ReadIssue: "精确 GUID 缓存无法读取：" + ex.Message));
                    continue;
                }
                var proof = Vc14BundleIdentity.Read(candidate, client, expectedArch);
                if (result.Count == 0)
                {
                    // Registration may be gone after a CRT upgrade. Only a Microsoft-signed
                    // original whose embedded bundle GUID equals the reference can prove this owner.
                    result.Add(new("ExactGuidPackageCache:" + candidate,
                        proof.Name,
                        proof.Version, "Microsoft Corporation", true, proof.Verified, "NoArpRegistration", proof));
                }
                else
                    result = result.Select(o => o with { CacheExists = true,
                        CacheVerified = proof.Verified && Vc14Catalog.SameVersion(o.Version, proof.Version), Proof = proof }).ToList();
            }
        }
        return result.Select(o => CompleteMissingOwnerIdentity(client, expectedArch, o)).ToArray();
    }

    // Burn may retain only Installed/Resume after an interrupted operation. Missing labels are
    // not conflicting labels. Fill only absent fields from the exact signed intrinsic identity;
    // preserve raw registry values so confirmation still detects every registration change.
    internal static Vc14DependencyOwner CompleteMissingOwnerIdentity(string client, string arch, Vc14DependencyOwner owner)
    {
        if (owner.ReadIssue.Length > 0 || owner.Proof is not { Verified: true } proof
            || !SameCode(proof.BundleId, client) || proof.Architecture != arch
            || !Regex.IsMatch(proof.Sha256, "^[0-9a-fA-F]{64}$") || !IsRuntimeName(proof.Name, arch)) return owner;
        var nameMissing = string.IsNullOrWhiteSpace(owner.Name);
        var versionMissing = string.IsNullOrWhiteSpace(owner.Version);
        var publisherMissing = string.IsNullOrWhiteSpace(owner.Publisher);
        if (!nameMissing && !versionMissing && !publisherMissing) return owner;
        var name = nameMissing ? proof.Name : owner.Name;
        var version = versionMissing ? proof.Version : owner.Version;
        var publisher = publisherMissing ? "Microsoft Corporation" : owner.Publisher;
        if (!IsRuntimeName(name, arch) || !Vc14Catalog.SameVersion(version, proof.Version)
            || !publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)) return owner;
        return owner with { Name = name, Version = version, Publisher = publisher, CacheVerified = true,
            IdentitySource = "SignedOriginalMissingArpFields" };
    }

    private static Vc14DependencyOwner CompletePinnedBaselineIdentity(string arch, Vc14DependencyOwner owner)
    {
        // Called only after verifying this architecture's exact pinned BundleId, SHA256 and signature.
        // Existing bad caches and nonempty contradictory fields must never use the missing-cache path.
        if (owner.ReadIssue.Length > 0 || owner.CacheExists || owner.Proof is not null) return owner;
        var name = $"Microsoft Visual C++ 2015-2022 Redistributable ({arch}) - 14.42.34433";
        if ((!string.IsNullOrWhiteSpace(owner.Name) && !owner.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(owner.Version) && !Vc14Catalog.SameVersion(owner.Version, Vc14Reinstall.BaselineVersion))
            || (!string.IsNullOrWhiteSpace(owner.Publisher) && !owner.Publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)))
            return owner;
        return owner with { Name = name, Version = Vc14Reinstall.BaselineVersion, Publisher = "Microsoft Corporation",
            IdentitySource = "PinnedOriginalMissingArpFields" };
    }

    internal static bool HasVerifiedBaselineOwners(string arch, IReadOnlyList<Vc14DependencyOwner> owners, bool pinned = false)
        => Vc14Reinstall.BaselineBundles.TryGetValue(arch, out var id) && owners.Count > 0
        && owners.Select(o => pinned ? CompletePinnedBaselineIdentity(arch, o) : o).All(o =>
            o.ReadIssue.Length == 0 && IsRuntimeName(o.Name, arch)
            && o.Publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
            && Vc14Catalog.SameVersion(o.Version, Vc14Reinstall.BaselineVersion)
            && ((pinned && !o.CacheExists && o.Proof is null)
            || (o.CacheVerified && o.Proof is { Verified: true } p && SameCode(p.BundleId, id) && p.Architecture == arch
            && Vc14Catalog.SameVersion(p.Version, Vc14Reinstall.BaselineVersion)
            && Regex.IsMatch(p.Sha256, "^[0-9a-fA-F]{64}$"))));

    private static bool CacheExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0) throw new IOException("缓存登记指向目录而非原包");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static bool VerifyPinnedBaseline(string arch)
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
