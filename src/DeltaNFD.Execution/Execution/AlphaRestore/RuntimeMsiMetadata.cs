using System.Runtime.InteropServices;
using System.Text;

namespace DeltaNFD.Services.AlphaRestore;

internal sealed class RuntimeComponentPathException(string component, int state)
    : IOException($"MSI 组件路径无法确认：{component}；状态 {state}。")
{
    internal int State { get; } = state;
}

internal static class RuntimeMsiMetadata
{
    internal static IReadOnlyList<string> MediaCabinets(string path) => ReadRows(path, "SELECT `Cabinet` FROM `Media`", 1).Select(r => r[0]).ToArray();
    internal static RuntimeMsiPackage ReadPackage(string path)
    {
        string Property(string name) => ReadRows(path,
            $"SELECT `Value` FROM `Property` WHERE `Property`='{name}'", 1).Single()[0];
        var files = ReadRows(path, "SELECT `File`,`Component_`,`FileName`,`Version` FROM `File`", 4)
            .ToDictionary(r => r[0], StringComparer.OrdinalIgnoreCase);
        var components = ReadRows(path, "SELECT `Component`,`ComponentId`,`Attributes`,`KeyPath`,`Condition` FROM `Component`", 5)
            .ToDictionary(r => r[0], StringComparer.OrdinalIgnoreCase);
        var assemblies = new Dictionary<string, RuntimeSxsIdentity>(StringComparer.OrdinalIgnoreCase);
        if (ReadRows(path, "SELECT `Name` FROM `_Tables` WHERE `Name`='MsiAssembly'", 1).Count > 0)
        {
            var names = ReadRows(path, "SELECT `Component_`,`Name`,`Value` FROM `MsiAssemblyName`", 3)
                .GroupBy(r => r[0]).ToDictionary(g => g.Key, g => g.ToDictionary(r => r[1], r => r[2], StringComparer.OrdinalIgnoreCase));
            foreach (var row in ReadRows(path, "SELECT `Component_`,`Attributes`,`File_Application` FROM `MsiAssembly`", 3))
            {
                if (row[1] != "1" || row[2].Length != 0 || !names.TryGetValue(row[0], out var identity)) continue;
                string Required(string name) => identity.TryGetValue(name, out var value) ? value : throw new InvalidDataException("SxS 身份缺少 " + name);
                assemblies[row[0]] = new(Required("name"), Required("version"), Required("processorArchitecture"), Required("publicKeyToken"), identity.GetValueOrDefault("language"));
            }
        }
        var dlls = new List<RuntimeMsiFile>();
        foreach (var file in files.Values)
        {
            var name = file[2].Split('|')[^1];
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !Version.TryParse(file[3], out var version)) continue;
            if (!components.TryGetValue(file[1], out var component) || !Guid.TryParse(component[1], out var componentId)
                || !int.TryParse(component[2], out var attributes) || (attributes & 4) != 0
                || !files.TryGetValue(component[3], out var keyFile)) continue;
            dlls.Add(new(componentId.ToString("B"), keyFile[2].Split('|')[^1], name, version)
                { Assembly = assemblies.GetValueOrDefault(file[1]), Condition = component[4] });
        }
        return new(ReadProductCode(path), Property("ProductName"), Property("Manufacturer"),
            Property("ProductVersion"), ReadArchitecture(path), dlls)
        {
            UpgradeCode = ReadRows(path, "SELECT `Value` FROM `Property` WHERE `Property`='UpgradeCode'", 1).FirstOrDefault()?[0] ?? "",
        };
    }

    internal static RuntimeMsiPackage ReadInstalledPackage(string product)
    {
        var path = ReadInstalledProperty(product, "LocalPackage");
        var package = ReadPackage(path);
        if (!package.Product.Equals(product, StringComparison.OrdinalIgnoreCase)) throw new IOException("缓存产品码不一致。");
        return package;
    }

    internal static IReadOnlyList<string> RelatedProducts(string upgradeCode)
    {
        if (!Guid.TryParse(upgradeCode, out var code)) return [];
        var products = new List<string>();
        for (uint index = 0; index < 128; index++)
        {
            var buffer = new StringBuilder(39);
            var result = MsiEnumRelatedProductsW(code.ToString("B"), 0, index, buffer);
            if (result == 259) return products;
            if (result != 0 || !Guid.TryParse(buffer.ToString(), out var product)) throw new IOException("无法核对 MSI 升级关系：" + result);
            products.Add(product.ToString("B"));
        }
        throw new InvalidDataException("MSI 升级关系超出限制。");
    }

    // Read-only database access: never opens an MSI installation session or runs custom actions.
    private static List<string[]> ReadRows(string path, string sql, int fields)
    {
        var code = MsiOpenDatabaseW(path, IntPtr.Zero, out var database);
        if (code != 0) throw new IOException("无法只读打开安装包：" + code);
        uint view = 0, record = 0;
        try
        {
            code = MsiDatabaseOpenViewW(database, sql, out view);
            if (code == 0) code = MsiViewExecute(view, 0);
            if (code != 0) throw new IOException("无法读取 MSI 元数据：" + code);
            var rows = new List<string[]>();
            while ((code = MsiViewFetch(view, out record)) == 0)
            {
                try
                {
                    var values = new string[fields];
                    for (uint field = 1; field <= fields; field++)
                    {
                        var buffer = new StringBuilder(4096); uint length = 4096;
                        var read = MsiRecordGetStringW(record, field, buffer, ref length);
                        if (read != 0) throw new IOException("MSI 字段读取失败：" + read);
                        values[field - 1] = buffer.ToString();
                    }
                    rows.Add(values);
                    if (rows.Count > 10000) throw new InvalidDataException("MSI 元数据行数超出限制。");
                }
                finally { MsiCloseHandle(record); record = 0; }
            }
            if (code != 259) throw new IOException("MSI 记录枚举不完整：" + code);
            return rows;
        }
        finally { if (record != 0) MsiCloseHandle(record); if (view != 0) MsiCloseHandle(view); MsiCloseHandle(database); }
    }

    internal static string ReadComponentPath(string product, string component)
    {
        var buffer = new StringBuilder(32768); uint length = 32768;
        var state = MsiGetComponentPathExW(product, component, null, 4, buffer, ref length);
        if (state != 3 || !Path.IsPathFullyQualified(buffer.ToString()))
            throw new RuntimeComponentPathException(component, state);
        return buffer.ToString();
    }
    internal static RuntimeMsiProduct ReadInstalledProduct(string product)
    {
        var state = RuntimeGuardService.QueryMsiProductState(product);
        if (state == -1) return new(product, state);
        if (state != 5) return new(product, state);
        var cache = ReadInstalledProperty(product, "LocalPackage");
        if (!File.Exists(cache)) throw new IOException("MSI 缓存缺失，无法核验产品和架构：" + product);
        if (!ReadProductCode(cache).Equals(product, StringComparison.OrdinalIgnoreCase)) throw new IOException("缓存 MSI 产品码不匹配。");
        return new(product, state, ReadInstalledProperty(product, "InstalledProductName"),
            ReadInstalledProperty(product, "Publisher"), ReadInstalledProperty(product, "VersionString"), ReadArchitecture(cache));
    }

    internal static void VerifyCachedIdentity(RuntimeMsiProduct product)
    {
        var cache = ReadInstalledProperty(product.Code, "LocalPackage");
        if (!File.Exists(cache)) throw new IOException("强制尝试仍需可读的 MSI 缓存，未猜测卸载。");
        string Property(string name) => ReadRows(cache,
            $"SELECT `Value` FROM `Property` WHERE `Property`='{name}'", 1).Single()[0];
        ValidateCachedIdentity(product, ReadProductCode(cache), Property("ProductName"), Property("Manufacturer"),
            Property("ProductVersion"), ReadArchitecture(cache));
    }

    internal static void ValidateCachedIdentity(RuntimeMsiProduct installed, string code, string name, string publisher, string version, string architecture)
    {
        static Version Normal(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        if (installed.State != 5 || !Guid.TryParse(code, out var cacheCode) || !Guid.TryParse(installed.Code, out var productCode)
            || cacheCode == Guid.Empty || cacheCode != productCode
            || !name.Equals(installed.Name, StringComparison.OrdinalIgnoreCase)
            || !publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
            || !publisher.Equals(installed.Publisher, StringComparison.OrdinalIgnoreCase)
            || !Version.TryParse(version, out var cacheVersion) || !Version.TryParse(installed.Version, out var actualVersion)
            || Normal(cacheVersion) != Normal(actualVersion) || architecture != installed.Architecture)
            throw new InvalidDataException("缓存 MSI 与已安装的产品码、名称、发布者、版本或架构不符；强制尝试已停止。");
    }

    internal static string ReadArchitecture(string path)
    {
        var code = MsiGetSummaryInformationW(0, path, 0, out var summary);
        if (code != 0) throw new IOException("无法只读核验 MSI 架构：" + code);
        try
        {
            var buffer = new StringBuilder(1024); uint length = 1024;
            code = MsiSummaryInfoGetPropertyW(summary, 7, out _, out _, out _, buffer, ref length);
            if (code != 0) throw new IOException("MSI 架构字段读取失败：" + code);
            return buffer.ToString().Split(';')[0].ToLowerInvariant() switch
            { "intel" => "x86", "x64" or "amd64" => "x64", "arm64" => "arm64", _ => throw new IOException("不支持或未知的 MSI 平台：" + buffer) };
        }
        finally { MsiCloseHandle(summary); }
    }

    private static string ReadInstalledProperty(string product, string property)
    {
        var buffer = new StringBuilder(2048); uint length = 2048;
        // VC++ redistributables are per-machine; no WMI Win32_Product repair-triggering query.
        var code = MsiGetProductInfoExW(product, null, 4, property, buffer, ref length);
        if (code != 0) throw new IOException($"无法只读核对 MSI {product} 的 {property}：{code}");
        return buffer.ToString();
    }
    internal static string ReadProductCode(string path)
    {
        var code = MsiOpenDatabaseW(path, IntPtr.Zero, out var database);
        if (code != 0) throw new IOException($"无法只读打开 MSI（{code}）：{path}");
        uint view = 0, record = 0;
        try
        {
            code = MsiDatabaseOpenViewW(database, "SELECT `Value` FROM `Property` WHERE `Property`='ProductCode'", out view);
            if (code == 0) code = MsiViewExecute(view, 0);
            if (code == 0) code = MsiViewFetch(view, out record);
            var buffer = new StringBuilder(64); uint length = 64;
            if (code == 0) code = MsiRecordGetStringW(record, 1, buffer, ref length);
            if (code != 0 || !Guid.TryParse(buffer.ToString(), out var product)) throw new IOException("MSI 产品码无法确认：" + code);
            return product.ToString("B");
        }
        finally { if (record != 0) MsiCloseHandle(record); if (view != 0) MsiCloseHandle(view); MsiCloseHandle(database); }
    }
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiOpenDatabaseW(string path, IntPtr persistence, out uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiDatabaseOpenViewW(uint database, string query, out uint view);
    [DllImport("msi.dll")] private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll")] private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiRecordGetStringW(uint record, uint field, StringBuilder value, ref uint length);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(uint handle);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiGetProductInfoExW(string product, string? sid, uint context, string property, StringBuilder value, ref uint length);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiGetSummaryInformationW(uint database, string path, uint updateCount, out uint summary);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiSummaryInfoGetPropertyW(uint summary, uint property, out uint dataType, out int number, out System.Runtime.InteropServices.ComTypes.FILETIME time, StringBuilder value, ref uint length);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern int MsiGetComponentPathExW(string product, string component, string? sid, uint context, StringBuilder value, ref uint length);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiEnumRelatedProductsW(string upgradeCode, uint reserved, uint index, StringBuilder product);
}
