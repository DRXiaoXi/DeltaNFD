using System.Runtime.InteropServices;
using System.Text;

namespace DeltaNFD.Services;

internal static class RuntimeMsiMetadata
{
    internal static RuntimeMsiPackage ReadPackage(string path)
    {
        string Property(string name) => ReadRows(path,
            $"SELECT `Value` FROM `Property` WHERE `Property`='{name}'", 1).Single()[0];
        var files = ReadRows(path, "SELECT `File`,`Component_`,`FileName`,`Version` FROM `File`", 4)
            .ToDictionary(r => r[0], StringComparer.OrdinalIgnoreCase);
        var components = ReadRows(path, "SELECT `Component`,`ComponentId`,`Attributes`,`KeyPath` FROM `Component`", 4)
            .ToDictionary(r => r[0], StringComparer.OrdinalIgnoreCase);
        var dlls = new List<RuntimeMsiFile>();
        foreach (var file in files.Values)
        {
            var name = file[2].Split('|')[^1];
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !Version.TryParse(file[3], out var version)) continue;
            if (!components.TryGetValue(file[1], out var component) || !Guid.TryParse(component[1], out var componentId)
                || !int.TryParse(component[2], out var attributes) || (attributes & 4) != 0
                || !files.TryGetValue(component[3], out var keyFile)) continue;
            dlls.Add(new(componentId.ToString("B"), keyFile[2].Split('|')[^1], name, version));
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
            throw new IOException($"MSI 组件未在本机完整安装：{component}；状态 {state}。");
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
