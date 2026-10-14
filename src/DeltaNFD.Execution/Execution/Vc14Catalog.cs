using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>Only the four Microsoft release-CRT v14 MSI upgrade families; no SDK/Debug/older families.</summary>
internal static class Vc14Catalog
{
    internal static readonly (string Arch, string Role, string Upgrade)[] Families =
    [
        ("x64", "Minimum", "{36F68A90-239C-34DF-B58C-64B30153CE35}"),
        ("x64", "Additional", "{9B0BAA88-E15F-3A1F-ACC0-B206E9DDF71C}"),
        ("x86", "Minimum", "{65E5BD06-6392-3027-8C26-853107D3CF1A}"),
        ("x86", "Additional", "{C78B8E51-0C65-377E-85D1-282F689FE505}"),
    ];
    internal static Vc14Severity Classify(string name, string version)
    {
        if (name.Contains("Visual C++ v14", StringComparison.OrdinalIgnoreCase)) return Vc14Severity.Abnormal;
        if (!Version.TryParse(version, out var v)) return Vc14Severity.Warning;
        if (v.Major == 14 && v.Minor == 51) return Vc14Severity.Abnormal;
        return v.Major == 14 && v.Minor != 42 ? Vc14Severity.Warning : Vc14Severity.Normal;
    }
    internal static string Fingerprint(Vc14Scan scan, bool includeLegacy = true) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Products = scan.Products.OrderBy(p => p.Code, StringComparer.OrdinalIgnoreCase),
            Legacy = includeLegacy ? "" : "" }))));
    internal static void Validate(Vc14Entry product, string cacheCode, string cacheName, string cacheVersion, string publisher, string architecture, string upgrade)
    {
        var family = Families.SingleOrDefault(f => f.Arch == architecture && f.Upgrade.Equals(upgrade, StringComparison.OrdinalIgnoreCase));
        if (family == default || product.Role != family.Role || product.Architecture != architecture
            || !product.UpgradeCode.Equals(upgrade, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(product.Code, out var code) || code == Guid.Empty || !Guid.TryParse(cacheCode, out var cached) || code != cached
            || !product.Name.Equals(cacheName, StringComparison.OrdinalIgnoreCase) || !product.Version.Equals(cacheVersion, StringComparison.OrdinalIgnoreCase)
            || !publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
            || !Version.TryParse(product.Version, out var version) || version.Major != 14
            || !Regex.IsMatch(product.Name, @"^Microsoft Visual C\+\+ (?:2015|2017|2019|2022|2026|v14) " + architecture + " " + family.Role + @" Runtime - 14\.", RegexOptions.IgnoreCase))
            throw new InvalidDataException("MSI 的身份、版本、架构或升级家族不属于可核验的微软 C++ 14.x 可再发行组件。");
    }
    internal static Vc14Scan Scan()
    {
        var products = new List<Vc14Entry>(); var notices = new List<Vc14Notice>(); var issues = new List<string>();
        foreach (var family in Families)
        {
            try
            {
                foreach (var code in Related(family.Upgrade))
                {
                    var p = Read(code, family.Arch, family.Role, family.Upgrade);
                    if (p is null) continue;
                    products.Add(p);
                    notices.Add(new(p.Name, p.Version, p.Architecture, Classify(p.Name, p.Version), "已核对 MSI 身份；此颜色为兼容性规则，不等于 DLL 损坏或已验证掉帧。"));
                }
            }
            catch (Exception ex) { issues.Add(family.Arch + " " + family.Role + "：" + ex.Message); }
        }
        // Wrapper names are shown only when the same architecture/version is actually installed.
        // Orphan Burn/INF registrations are not mistaken for a second active CRT version.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var root = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var key in root?.GetSubKeyNames() ?? [])
                {
                    using var sub = root!.OpenSubKey(key);
                    var name = sub?.GetValue("DisplayName") as string ?? "";
                    if (!name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase) || name.Contains("Debug", StringComparison.OrdinalIgnoreCase)) continue;
                    var version = sub?.GetValue("DisplayVersion") as string ?? "";
                    if (!name.Contains("Visual C++ v14", StringComparison.OrdinalIgnoreCase) && (!Version.TryParse(version, out var parsed) || parsed.Major != 14)) continue;
                    if (products.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                    var arch = Regex.IsMatch(name, @"\bx64\b", RegexOptions.IgnoreCase) ? "x64" : name.Contains("arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x86";
                    var associated = products.Any(p => p.Architecture == arch && SameVersion(p.Version, version));
                    if (associated) notices.Add(new(name, version, arch, Classify(name, version), "同版本安装包装登记；不作为单独卸载对象。"));
                    else notices.Add(new(name, version, arch, Classify(name, version), "安装登记；当前关联组件未确认，可能是残留。不会据此猜测卸载产品码。"));
                }
            }
            catch (Exception ex) { issues.Add("安装包装登记读取失败：" + ex.Message); }
        }
        return new(products.DistinctBy(p => p.Code, StringComparer.OrdinalIgnoreCase).ToArray(),
            notices.DistinctBy(n => n.Name + n.Version + n.Architecture).ToArray(), issues);
    }
    internal static bool SameVersion(string a, string b) => Version.TryParse(a, out var x) && Version.TryParse(b, out var y)
        && x.Major == y.Major && x.Minor == y.Minor && Math.Max(0, x.Build) == Math.Max(0, y.Build) && Math.Max(0, x.Revision) == Math.Max(0, y.Revision);

    internal static string? DependencyIssue(IReadOnlyList<Vc14Entry> products) => CheckDependencies(products, null);
    internal static IReadOnlyList<string> ApprovedClients(Vc14Entry entry)
    {
        var clients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (CheckDependencies([entry], clients) is { } error) throw new IOException(error);
        return clients.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string? CheckDependencies(IReadOnlyList<Vc14Entry> products, HashSet<string>? approved)
    {
        try
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var root = hive.OpenSubKey(@"SOFTWARE\Classes\Installer\Dependencies");
                foreach (var key in root?.GetSubKeyNames() ?? [])
                {
                    if (!Regex.IsMatch(key, @"^Microsoft\.VS\.VC_Runtime(?:Minimum|Additional)(?:VSU)?_(?:amd64|x86),v14$", RegexOptions.IgnoreCase)) continue;
                    using var provider = root!.OpenSubKey(key);
                    var code = provider?.GetValue("") as string;
                    var product = products.FirstOrDefault(p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
                    if (product is null) continue;
                    using var dependents = provider!.OpenSubKey("Dependents");
                    foreach (var client in dependents?.GetSubKeyNames() ?? [])
                    {
                        var known = false;
                        foreach (var arpView in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                        {
                            using var arpHive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, arpView);
                            using var arp = arpHive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + client);
                            var name = arp?.GetValue("DisplayName") as string ?? "";
                            var version = arp?.GetValue("DisplayVersion") as string ?? "";
                            var publisher = arp?.GetValue("Publisher") as string ?? "";
                            known |= Guid.TryParse(client, out _) && publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                                && Regex.IsMatch(name, @"^Microsoft Visual C\+\+ (?:2015-2022|2015-2019|2015|2017|2019|2022|2026|v14) Redistributable \(" + product.Architecture + @"\)(?: - 14\..*)?$", RegexOptions.IgnoreCase)
                                && (SameVersion(version, product.Version) || SameVersion(version, "14.42.34433.0"))
                                && Vc14PackageTrust.Verify(arp!.GetValue("BundleCachePath") as string, version, product.Architecture);
                        }
                        if (!known) return "14.x 组件存在无法核验的共享依赖，已保留组件，未执行卸载：" + key + "；引用：" + client;
                        approved?.Add(Guid.Parse(client).ToString("B"));
                    }
                }
            }
            return null;
        }
        catch (Exception ex) { return "共享依赖无法核对，未执行卸载：" + ex.Message; }
    }
    internal static Vc14Entry? Read(string code, string arch, string role, string upgrade)
    {
        var state = MsiQueryProductStateW(code);
        if (state == -1) return null;
        if (state != 5) throw new IOException("产品安装状态未确认：" + code);
        var name = Product(code, "InstalledProductName"); var version = Product(code, "VersionString");
        var publisher = Product(code, "Publisher"); var cache = Product(code, "LocalPackage");
        if (!File.Exists(cache)) throw new IOException("缓存 MSI 缺失：" + code);
        var p = new Vc14Entry(code, name, version, arch, role, upgrade);
        Validate(p, Property(cache, "ProductCode"), Property(cache, "ProductName"), Property(cache, "ProductVersion"),
            Property(cache, "Manufacturer"), Architecture(cache), Property(cache, "UpgradeCode"));
        if (!publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)) throw new IOException("已安装发布者不符。");
        return p;
    }
    private static IReadOnlyList<string> Related(string upgrade)
    {
        var values = new List<string>();
        for (uint i = 0; i < 128; i++)
        {
            var buffer = new StringBuilder(39); var error = MsiEnumRelatedProductsW(upgrade, 0, i, buffer);
            if (error == 259) return values;
            if (error != 0 || !Guid.TryParse(buffer.ToString(), out _)) throw new IOException("MSI 家族枚举失败：" + error);
            values.Add(buffer.ToString());
        }
        throw new IOException("MSI 家族数量异常。");
    }
    private static string Product(string code, string property)
    {
        var buffer = new StringBuilder(4096); uint length = 4096;
        var error = MsiGetProductInfoExW(code, null, 4, property, buffer, ref length);
        if (error != 0) throw new IOException("MSI 登记读取失败：" + property + " " + error);
        return buffer.ToString();
    }
    private static string Property(string path, string property)
    {
        var error = MsiOpenDatabaseW(path, IntPtr.Zero, out var db); uint view = 0, record = 0;
        if (error != 0) throw new IOException("无法只读打开 MSI：" + error);
        try
        {
            error = MsiDatabaseOpenViewW(db, "SELECT `Value` FROM `Property` WHERE `Property`='" + property + "'", out view);
            if (error == 0) error = MsiViewExecute(view, 0);
            if (error == 0) error = MsiViewFetch(view, out record);
            var buffer = new StringBuilder(4096); uint length = 4096;
            if (error == 0) error = MsiRecordGetStringW(record, 1, buffer, ref length);
            if (error != 0) throw new IOException("MSI 属性读取失败：" + property + " " + error);
            return buffer.ToString();
        }
        finally { if (record != 0) MsiCloseHandle(record); if (view != 0) MsiCloseHandle(view); MsiCloseHandle(db); }
    }
    private static string Architecture(string path)
    {
        var error = MsiGetSummaryInformationW(0, path, 0, out var handle);
        if (error != 0) throw new IOException("MSI 架构无法读取。");
        try
        {
            var buffer = new StringBuilder(1024); uint length = 1024;
            error = MsiSummaryInfoGetPropertyW(handle, 7, out _, out _, out _, buffer, ref length);
            if (error != 0) throw new IOException("MSI 架构字段无法读取。");
            return buffer.ToString().Split(';')[0].ToLowerInvariant() switch { "intel" => "x86", "x64" or "amd64" => "x64", _ => "unknown" };
        }
        finally { MsiCloseHandle(handle); }
    }
    internal static bool Installed(string code) => MsiQueryProductStateW(code) != -1;
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern int MsiQueryProductStateW(string product);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiEnumRelatedProductsW(string upgrade, uint reserved, uint index, StringBuilder product);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiGetProductInfoExW(string product, string? sid, uint context, string property, StringBuilder value, ref uint length);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiOpenDatabaseW(string path, IntPtr mode, out uint db);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiDatabaseOpenViewW(uint db, string query, out uint view);
    [DllImport("msi.dll")] private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll")] private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiRecordGetStringW(uint record, uint field, StringBuilder value, ref uint length);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(uint handle);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiGetSummaryInformationW(uint db, string path, uint update, out uint summary);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiSummaryInfoGetPropertyW(uint summary, uint property, out uint type, out int number, out System.Runtime.InteropServices.ComTypes.FILETIME time, StringBuilder value, ref uint length);
}
