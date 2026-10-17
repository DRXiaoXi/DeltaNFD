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
    /// <summary>Appx CRT 系统组件包（由 Windows 管理，不能也不应卸载）。</summary>
    internal static bool IsAppxPackage(string name) => Regex.IsMatch(name,
        @"^Visual C\+\+ Library CRT (?:(?:ARM64|X64|X86) )?(?:Desktop )?Appx (?:Resource )?Package$", RegexOptions.IgnoreCase);
    /// <summary>红色只由真实安装的 14.x MSI 组件驱动；包装/残留登记最高 Warning，
    /// 但与已核验同架构同版本 MSI 组件对应的包装登记（如基线 14.42 Burn 包装）为 Normal。</summary>
    internal static Vc14Severity Classify(string name, string version, bool installedMsi, IReadOnlyList<Vc14Entry> products)
    {
        if (!installedMsi)
        {
            if (IsAppxPackage(name)) return Vc14Severity.Normal;
            var arch = Regex.IsMatch(name, @"\bx64\b", RegexOptions.IgnoreCase) ? "x64"
                : name.Contains("arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x86";
            if (products.Any(p => p.Architecture == arch && SameVersion(p.Version, version))) return Vc14Severity.Normal;
            return Vc14Severity.Warning;
        }
        return Classify(name, version);
    }
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
        var diagnostics = new Vc14MsiDiagnosticSession();
        foreach (var family in Families)
        {
            string? readingCode = null;
            try
            {
                foreach (var code in Related(family.Upgrade))
                {
                    readingCode = code;
                    var p = Read(code, family.Arch, family.Role, family.Upgrade, diagnostics, "升级家族 " + family.Arch + " " + family.Role);
                    readingCode = null;
                    if (p is null) continue;
                    products.Add(p);
                    notices.Add(new(p.Name, p.Version, p.Architecture, Classify(p.Name, p.Version), "已核对 MSI 身份；此颜色为兼容性规则，不等于 DLL 损坏或已验证掉帧。"));
                }
            }
            catch (Exception ex) { diagnostics.AddIssue(issues, family.Arch + " " + family.Role, ex, readingCode); }
        }
        // MSI 家族动态发现：2026 等新产品线使用固定四家族之外的升级码，
        // 仅靠 Families 枚举会漏掉其 MSI（导致 14.42 降级安装被拒，退出码 1638）。
        foreach (var discovered in DiscoverMsiProducts(products.Select(p => p.Code).ToHashSet(StringComparer.OrdinalIgnoreCase), issues, diagnostics))
        {
            try
            {
                if (products.Any(p => p.Code.Equals(discovered.Code, StringComparison.OrdinalIgnoreCase))) continue;
                products.Add(discovered);
                notices.Add(new(discovered.Name, discovered.Version, discovered.Architecture, Classify(discovered.Name, discovered.Version),
                    "已核对 MSI 身份（动态发现家族：" + discovered.UpgradeCode + "）；此颜色为兼容性规则，不等于 DLL 损坏或已验证掉帧。"));
            }
            catch (Exception ex) { issues.Add("动态 MSI 家族：" + ex.Message); }
        }
        // 包装登记：HKLM 与 HKCU、64/32 视图统一枚举；Appx 系统包只作说明条目。
        foreach (var (hiveName, view) in RegistryViews())
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(hiveName, view);
                using var root = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var key in root?.GetSubKeyNames() ?? [])
                {
                    using var sub = root!.OpenSubKey(key);
                    var name = sub?.GetValue("DisplayName") as string ?? "";
                    if (!name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase) || name.Contains("Debug", StringComparison.OrdinalIgnoreCase)) continue;
                    var version = RegistrationVersion(sub);
                    if (!name.Contains("Visual C++ v14", StringComparison.OrdinalIgnoreCase) && (!Version.TryParse(version, out var parsed) || parsed.Major != 14)) continue;
                    if (products.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                    var arch = Regex.IsMatch(name, @"\bx64\b", RegexOptions.IgnoreCase) ? "x64" : name.Contains("arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x86";
                    if (IsAppxPackage(name))
                    {
                        notices.Add(new(name, version, arch, Vc14Severity.Normal, "系统组件包，由 Windows 管理；不属于 Win32 可再发行运行库，不参与版本判定或卸载。"));
                        continue;
                    }
                    var associated = products.Any(p => p.Architecture == arch && SameVersion(p.Version, version));
                    if (associated) notices.Add(new(name, version, arch, Classify(name, version, false, products), "同版本安装包装登记；不作为单独卸载对象。"));
                    else notices.Add(new(name, version, arch, Classify(name, version, false, products), "安装登记；当前关联组件未确认，可能是残留。可用“仅清理失效登记”核验 MSI 显示项；不会据此猜测卸载产品码。"));
                }
            }
            catch (Exception ex) { issues.Add("安装包装登记读取失败：" + ex.Message); }
        }
        return new(products.DistinctBy(p => p.Code, StringComparer.OrdinalIgnoreCase).ToArray(),
            notices.DistinctBy(n => n.Name + n.Version + n.Architecture).ToArray(), issues)
        { MsiStateDiagnostics = diagnostics.Results, MsiStateDiagnosticsTruncated = diagnostics.Truncated };
    }
    internal static bool SameVersion(string a, string b) => Version.TryParse(a, out var x) && Version.TryParse(b, out var y)
        && x.Major == y.Major && x.Minor == y.Minor && Math.Max(0, x.Build) == Math.Max(0, y.Build) && Math.Max(0, x.Revision) == Math.Max(0, y.Revision);

    /// <summary>登记扫描统一覆盖 HKLM/HKCU 的 64/32 位视图（每用户 Burn 包装常见于 HKCU）。</summary>
    internal static IEnumerable<(RegistryHive Hive, RegistryView View)> RegistryViews()
    {
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64);
        yield return (RegistryHive.LocalMachine, RegistryView.Registry32);
        yield return (RegistryHive.CurrentUser, RegistryView.Registry64);
        yield return (RegistryHive.CurrentUser, RegistryView.Registry32);
    }

    /// <summary>DisplayVersion → BundleVersion → Version(DWORD) 回退，格式化为四段版本字符串。</summary>
    internal static string RegistrationVersion(RegistryKey? key)
    {
        if (key is null) return "";
        if (key.GetValue("DisplayVersion") is string display && !string.IsNullOrWhiteSpace(display)) return display;
        if (key.GetValue("BundleVersion") is string bundle && !string.IsNullOrWhiteSpace(bundle)) return bundle;
        if (key.GetValue("Version") is int dword && dword > 0)
        {
            var major = dword >> 24 & 0xFF; var minor = dword >> 16 & 0xFF;
            var build = dword >> 8 & 0xFF; var revision = dword & 0xFF;
            return $"{major}.{minor}.{build}.{revision}";
        }
        return "";
    }

    /// <summary>
    /// 动态发现固定四家族之外的 14.x release CRT MSI（如 2026 产品线的新升级码）。
    /// 枚举登记中 WindowsInstaller=1 的 14.x 条目，从缓存 LocalPackage 只读解析身份并核验。
    /// </summary>
    internal static IEnumerable<Vc14Entry> DiscoverMsiProducts(HashSet<string> knownCodes, ICollection<string>? issues = null,
        Vc14MsiDiagnosticSession? diagnostics = null)
    {
        foreach (var (hiveName, view) in RegistryViews())
        {
            using var hive = RegistryKey.OpenBaseKey(hiveName, view);
            using var root = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            foreach (var keyName in root?.GetSubKeyNames() ?? [])
            {
                if (!Guid.TryParse(keyName, out _) || knownCodes.Contains(keyName)) continue;
                using var sub = root!.OpenSubKey(keyName);
                if (sub is null) continue;
                var name = sub.GetValue("DisplayName") as string ?? "";
                var version = RegistrationVersion(sub);
                if (!name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase) || name.Contains("Debug", StringComparison.OrdinalIgnoreCase)
                    || IsAppxPackage(name)) continue;
                if (!name.Contains("Visual C++ v14", StringComparison.OrdinalIgnoreCase) && (!Version.TryParse(version, out var parsed) || parsed.Major != 14)) continue;
                if (sub.GetValue("WindowsInstaller") is not 1) continue;
                Vc14Entry? entry;
                try { entry = ReadAnyWithDiagnostics(keyName, diagnostics, "动态登记 " + hiveName + "/" + view + "/" + keyName); }
                catch (Exception ex)
                {
                    // Failure to validate a candidate is unknown, not proof that no CRT is installed.
                    if (diagnostics is null) issues?.Add("动态 MSI 登记待核对：" + name + "：" + ex.Message);
                    else diagnostics.AddIssue(issues, "动态 MSI 登记待核对：" + name, ex, keyName);
                    continue;
                }
                if (entry is not null) yield return entry;
            }
        }
    }

    internal static IReadOnlyList<string> ApprovedClients(Vc14Entry entry) => Vc14DependencyPolicy.Read(entry).Clients;
    internal static string? DependencyIssue(IReadOnlyList<Vc14Entry> products)
    {
        try
        {
            foreach (var product in products) _ = Vc14DependencyPolicy.Read(product);
            return null;
        }
        catch (Exception ex) { return "共享依赖无法核对，未执行卸载：" + ex.Message; }
    }
    internal static Vc14Entry? Read(string code, string arch, string role, string upgrade,
        Vc14MsiDiagnosticSession? diagnostics = null, string source = "执行前固定家族核对")
    {
        var state = MsiQueryProductStateW(code);
        if (!RequireInstalledState(code, state, diagnostics, source)) return null;
        try
        {
            var name = Product(code, "InstalledProductName"); var version = Product(code, "VersionString");
            var publisher = Product(code, "Publisher"); var cache = Product(code, "LocalPackage");
            if (!File.Exists(cache)) throw new IOException("缓存 MSI 缺失：" + code);
            var p = new Vc14Entry(code, name, version, arch, role, upgrade);
            Validate(p, Property(cache, "ProductCode"), Property(cache, "ProductName"), Property(cache, "ProductVersion"),
                Property(cache, "Manufacturer"), Architecture(cache), Property(cache, "UpgradeCode"));
            if (!publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)) throw new IOException("已安装发布者不符。");
            return p;
        }
        catch { diagnostics?.Observe(code, state, source, expand: true); throw; }
    }

    /// <summary>不要求固定升级家族的产品读取：按缓存 MSI 核验产品码/名称/版本/发布者/架构/升级码。
    /// 供卸载前复核动态发现的家族（如 2026 产品线）使用；身份不符抛异常，未安装返回 null。</summary>
    internal static Vc14Entry? ReadAny(string code) => ReadAnyWithDiagnostics(code, null, "执行前动态家族核对");

    private static Vc14Entry? ReadAnyWithDiagnostics(string code, Vc14MsiDiagnosticSession? diagnostics, string source)
    {
        var state = MsiQueryProductStateW(code);
        if (!RequireInstalledState(code, state, diagnostics, source)) return null;
        try
        {
            var name = Product(code, "InstalledProductName"); var version = Product(code, "VersionString");
            var publisher = Product(code, "Publisher"); var cache = Product(code, "LocalPackage");
            if (!File.Exists(cache)) throw new IOException("缓存 MSI 缺失：" + code);
            var cacheCode = Property(cache, "ProductCode"); var cacheName = Property(cache, "ProductName");
            var cacheVersion = Property(cache, "ProductVersion"); var cachePublisher = Property(cache, "Manufacturer");
            var cacheArchitecture = Architecture(cache); var cacheUpgrade = Property(cache, "UpgradeCode");
            if (!Guid.TryParse(code, out var parsed) || parsed == Guid.Empty || !Guid.TryParse(cacheCode, out var cached) || parsed != cached
                || !name.Equals(cacheName, StringComparison.OrdinalIgnoreCase) || !version.Equals(cacheVersion, StringComparison.OrdinalIgnoreCase)
                || !publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                || !cachePublisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                || !Version.TryParse(version, out var parsedVersion) || parsedVersion.Major != 14
                || !Regex.IsMatch(name, @"^Microsoft Visual C\+\+ (?:2015|2017|2019|2022|2026|v14) " + cacheArchitecture + @" (Minimum|Additional) Runtime - 14\.", RegexOptions.IgnoreCase))
                throw new InvalidDataException("MSI 的身份、版本、架构或发布者不属于可核验的微软 C++ 14.x 可再发行组件。");
            var role = name.Contains(" Additional ", StringComparison.OrdinalIgnoreCase) ? "Additional" : "Minimum";
            return new Vc14Entry(code, name, version, cacheArchitecture, role, cacheUpgrade);
        }
        catch { diagnostics?.Observe(code, state, source, expand: true); throw; }
    }

    // Preserve the original decision exactly: -1 returns no product; 5 proceeds to identity checks;
    // every other state still throws. Context evidence must never grant installation/removal permission.
    internal static bool RequireInstalledState(string code, int state, Vc14MsiDiagnosticSession? diagnostics, string source)
    {
        var observation = diagnostics?.Observe(code, state, source);
        if (state == -1) return false;
        if (state == 5) return true;
        throw new Vc14MsiStateException(observation ?? Vc14MsiStateDiagnostics.Capture(code, state, new Vc14MsiDiagnosticWindowsPlatform()));
    }

    /// <summary>拆分登记的卸载命令：正确处理带引号的可执行路径（如 "C:\目录\uninstall.exe" /quiet）。
    /// 供计划的存在性检查与实际执行共用同一解析，避免两处不一致。</summary>
    internal static (string Executable, string Arguments) SplitUninstallCommand(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) return ("", "");
        if (text.StartsWith('"'))
        {
            var closing = text.IndexOf('"', 1);
            if (closing > 1) return (text[1..closing].Trim(), text[(closing + 1)..].Trim());
        }
        var marker = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0) return (text[..(marker + 4)].Trim(), text[(marker + 4)..].Trim());
        var separator = text.IndexOf(' ');
        return separator < 0 ? (text, "") : (text[..separator].Trim(), text[(separator + 1)..].Trim());
    }

    /// <summary>解析登记命令的可执行文件完整路径：相对名（如 MsiExec.exe）按系统目录与 PATH 解析，
    /// 绝对路径原样返回。找不到返回 null。执行与存在性检查共用，保证活动记录始终写入完整路径。</summary>
    internal static string? ResolveUninstallExecutable(string command)
    {
        var (executable, _) = SplitUninstallCommand(command);
        if (executable.Length == 0) return null;
        try
        {
            if (Path.IsPathRooted(executable)) return File.Exists(executable) ? executable : null;
            foreach (var folder in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.System), Environment.GetFolderPath(Environment.SpecialFolder.SystemX86) })
            {
                if (folder.Length == 0) continue;
                var candidate = Path.Combine(folder, executable);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var folder in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    var candidate = Path.Combine(folder, executable);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch { /* 非法 PATH 项跳过 */ }
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>判断登记命令的可执行文件是否实际可用。误判有效命令为失效会把仍在使用的登记当孤儿删除。</summary>
    internal static bool UninstallExecutableAvailable(string command) => ResolveUninstallExecutable(command) is not null;
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
