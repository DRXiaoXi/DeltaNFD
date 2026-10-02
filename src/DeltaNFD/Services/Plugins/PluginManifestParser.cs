using System.Text.Json;

namespace DeltaNFD.Services.Plugins;

/// <summary>宿主兼容范围：最小含端、最大不含端（规范第 3 节）。</summary>
public sealed record PluginHostRange(Version MinInclusive, Version MaxExclusive);

/// <summary>权限声明：类别固定，用途必须逐一说明实际资源。</summary>
public sealed record PluginPermission(string Id, string Purpose);

/// <summary>后端入口声明。entry 为包内 POSIX 相对路径，须位于 backend/ 下。</summary>
public sealed record PluginBackend(string Entry, string Architecture);

/// <summary>能力声明：continuous 允许宿主存活期间驻留；offlineAutonomous 允许脱机移交。</summary>
public sealed record PluginCapabilities(bool Continuous, bool OfflineAutonomous);

/// <summary>
/// 插件操作声明。mutating 操作必须 reversible（v1 不接受声明不可恢复的修改操作）；
/// resourceIds 为宿主约定的冲突检测范围，非 API 白名单。
/// </summary>
public sealed record PluginOperation(string Id, string Title, bool Mutating, bool Reversible, bool TargetScoped,
    IReadOnlyList<string> ResourceIds, int TimeoutSeconds);

/// <summary>manifest.json 解析结果（不可变）。</summary>
public sealed record PluginManifest(
    int SchemaVersion,
    string Id,
    string Name,
    string Author,
    string Version,
    string ProtocolVersion,
    int ProtocolMajor,
    int ProtocolMinor,
    PluginHostRange HostCompatibility,
    PluginBackend Backend,
    IReadOnlyList<PluginPermission> Permissions,
    PluginCapabilities Capabilities,
    IReadOnlyList<PluginOperation> Operations)
{
    public PluginOperation? FindOperation(string id) =>
        Operations.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.Ordinal));

    /// <summary>宿主版本是否落在兼容范围内。</summary>
    public bool IsCompatible(Version host) =>
        host >= HostCompatibility.MinInclusive && host < HostCompatibility.MaxExclusive;

    /// <summary>操作 ID 与 UI 无关的包内唯一标识（规范第 3 节：操作 ID 包内唯一）。</summary>
    public bool HasOperation(string id) => FindOperation(id) is not null;
}

/// <summary>
/// manifest.json 严格解析器（规范第 3 节）。拒绝重复属性、未知字段、类型不符、
/// 非法 ID/版本/协议/范围；不加载或运行任何后端代码。
/// </summary>
public static class PluginManifestParser
{
    // 宿主品牌域：插件 ID 不得以此开头伪装官方（大小写折叠后判断）。
    private static readonly string[] ReservedIdPrefixes = ["deltanfd.", "delta-nfd.", "deltanfd-", "delta-nfd-"];

    public static PluginManifest Parse(byte[] utf8) => Parse(utf8, origin: "manifest.json");

    public static PluginManifest Parse(byte[] utf8, string origin)
    {
        using var document = PluginJson.ParseStrict(utf8, origin);
        return ParseElement(document.RootElement, origin);
    }

    public static PluginManifest ParseElement(JsonElement root, string origin)
    {
        if (root.ValueKind is not JsonValueKind.Object)
            throw new PluginContractException($"{origin}：根节点必须是对象。");
        EnsureNoExtraProperties(root, ["schemaVersion", "id", "name", "author", "version", "protocolVersion", "hostCompatibility", "backend", "permissions", "capabilities", "operations"], origin);

        var schema = RequireInt(root, "schemaVersion", origin);
        if (schema != PluginContract.SchemaVersion)
            throw new PluginContractException($"{origin}：schemaVersion 必须为 {PluginContract.SchemaVersion}，实际 {schema}。");

        var id = RequireIdString(root, "id", origin);
        if (id.Length is < 3 or > 80)
            throw new PluginContractException($"{origin}：id 长度须在 3-80 之间。");
        if (!IsReverseDomainId(id))
            throw new PluginContractException($"{origin}：id 必须是小写 ASCII 反向域名标识（如 org.example.hardware-report）。");
        var folded = id.ToLowerInvariant();
        if (ReservedIdPrefixes.Any(p => folded.StartsWith(p, StringComparison.Ordinal)))
            throw new PluginContractException($"{origin}：id 不得使用宿主品牌域伪装官方插件。");

        var name = RequireUiString(root, "name", origin);
        var author = RequireUiString(root, "author", origin);
        var version = ParseVersion(RequireString(root, "version", origin), origin, "version");

        var protocolText = RequireString(root, "protocolVersion", origin);
        var (protocolMajor, protocolMinor) = ParseProtocol(protocolText, origin);

        if (!TryGetObject(root, "hostCompatibility", out var hostRange))
            throw new PluginContractException($"{origin}：缺少 hostCompatibility。");
        EnsureNoExtraProperties(hostRange, ["minInclusive", "maxExclusive"], origin);
        if (!TryGetString(hostRange, "minInclusive", out var minText) ||
            !TryGetString(hostRange, "maxExclusive", out var maxText))
            throw new PluginContractException($"{origin}：hostCompatibility 必须包含 minInclusive 与 maxExclusive。");
        var min = ParseVersion(minText, origin, "hostCompatibility.minInclusive");
        var max = ParseVersion(maxText, origin, "hostCompatibility.maxExclusive");
        if (min >= max)
            throw new PluginContractException($"{origin}：hostCompatibility 的 minInclusive 必须小于 maxExclusive。");

        if (!TryGetObject(root, "backend", out var backend))
            throw new PluginContractException($"{origin}：缺少 backend。");
        EnsureNoExtraProperties(backend, ["entry", "architecture"], origin);
        if (!TryGetString(backend, "entry", out var entry))
            throw new PluginContractException($"{origin}：backend.entry 缺失。");
        entry = PluginPath.NormalizeSeparators(entry);
        if (!entry.StartsWith("backend/", StringComparison.Ordinal) || entry.Length == "backend/".Length)
            throw new PluginContractException($"{origin}：backend.entry 必须是 backend/ 下的相对路径。");
        if (!entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new PluginContractException($"{origin}：backend.entry 必须指向 EXE 入口。");
        if (!PluginPath.IsSafeRelativePath(entry))
            throw new PluginContractException($"{origin}：backend.entry 不是安全的相对路径：{entry}");
        if (!TryGetString(backend, "architecture", out var architecture) ||
            !string.Equals(architecture, "x64", StringComparison.OrdinalIgnoreCase))
            throw new PluginContractException($"{origin}：backend.architecture 必须为 x64。");

        if (!TryGetArray(root, "permissions", out var permissions))
            throw new PluginContractException($"{origin}：缺少 permissions 数组。");
        var parsedPermissions = new List<PluginPermission>();
        var seenPermissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in permissions.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.Object)
                throw new PluginContractException($"{origin}：permissions 项必须是对象。");
            EnsureNoExtraProperties(item, ["id", "purpose"], origin);
            if (!TryGetString(item, "id", out var permissionId) ||
                !PluginContract.PermissionCategories.Contains(permissionId))
                throw new PluginContractException($"{origin}：权限类别必须属于固定集合（{string.Join("、", PluginContract.PermissionCategories)}）。");
            if (!seenPermissions.Add(permissionId))
                throw new PluginContractException($"{origin}：权限“{permissionId}”重复声明。");
            if (!TryGetString(item, "purpose", out var purpose) || purpose.Trim().Length == 0)
                throw new PluginContractException($"{origin}：权限“{permissionId}”缺少用途说明。");
            parsedPermissions.Add(new PluginPermission(permissionId, purpose));
        }

        if (!TryGetObject(root, "capabilities", out var capabilities))
            throw new PluginContractException($"{origin}：缺少 capabilities。");
        EnsureNoExtraProperties(capabilities, ["continuous", "offlineAutonomous"], origin);
        if (!TryGetBool(capabilities, "continuous", out var continuous) ||
            !TryGetBool(capabilities, "offlineAutonomous", out var offlineAutonomous))
            throw new PluginContractException($"{origin}：capabilities 必须包含 continuous 与 offlineAutonomous 布尔值。");

        if (!TryGetArray(root, "operations", out var operations))
            throw new PluginContractException($"{origin}：缺少 operations 数组。");
        if (operations.GetArrayLength() == 0)
            throw new PluginContractException($"{origin}：operations 不能为空。");
        var parsedOperations = new List<PluginOperation>();
        var seenOperations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in operations.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.Object)
                throw new PluginContractException($"{origin}：operations 项必须是对象。");
            EnsureNoExtraProperties(item, ["id", "title", "mutating", "reversible", "targetScoped", "timeoutSeconds", "resourceIds"], origin);
            if (!TryGetString(item, "id", out var operationId) || !IsShortId(operationId))
                throw new PluginContractException($"{origin}：操作 id 必须为 1-64 字符、字母开头、仅含字母数字 _ -。");
            if (!seenOperations.Add(operationId))
                throw new PluginContractException($"{origin}：操作 id“{operationId}”重复。");
            if (!TryGetString(item, "title", out var title) || title.Trim().Length == 0)
                throw new PluginContractException($"{origin}：操作“{operationId}”缺少标题。");
            if (!TryGetBool(item, "mutating", out var mutating) ||
                !TryGetBool(item, "reversible", out var reversible) ||
                !TryGetBool(item, "targetScoped", out var targetScoped))
                throw new PluginContractException($"{origin}：操作“{operationId}”缺少 mutating/reversible/targetScoped 布尔值。");
            if (mutating && !reversible)
                throw new PluginContractException($"{origin}：修改操作“{operationId}”必须 reversible:true（v1 不接受不可恢复的修改）。");
            var timeout = PluginContract.DefaultTimeoutSeconds;
            if (item.TryGetProperty("timeoutSeconds", out _) && !TryGetInt(item, "timeoutSeconds", out _))
                throw new PluginContractException($"{origin}：timeoutSeconds 必须是整数。");
            if (TryGetInt(item, "timeoutSeconds", out var configured))
            {
                if (configured is < PluginContract.MinTimeoutSeconds or > PluginContract.MaxTimeoutSeconds)
                    throw new PluginContractException($"{origin}：操作“{operationId}”的 timeoutSeconds 须在 {PluginContract.MinTimeoutSeconds}-{PluginContract.MaxTimeoutSeconds} 之间。");
                timeout = configured;
            }
            var resources = new List<string>();
            if (item.TryGetProperty("resourceIds", out _) && !TryGetArray(item, "resourceIds", out _))
                throw new PluginContractException($"{origin}：resourceIds 必须是数组。");
            if (TryGetArray(item, "resourceIds", out var resourceArray))
            {
                var seenResources = new HashSet<string>(StringComparer.Ordinal);
                foreach (var resource in resourceArray.EnumerateArray())
                {
                    if (resource.ValueKind is not JsonValueKind.String ||
                        resource.GetString() is not { } resourceId ||
                        !PluginContract.OperationResourceIds.Contains(resourceId))
                        throw new PluginContractException($"{origin}：操作“{operationId}”的 resourceIds 含未知资源标识。");
                    if (!seenResources.Add(resourceId))
                        throw new PluginContractException($"{origin}：操作“{operationId}”的 resourceIds 重复。");
                    resources.Add(resourceId);
                }
                if (mutating && resources.Count == 0)
                    throw new PluginContractException($"{origin}：修改操作“{operationId}”必须声明 resourceIds 供冲突检测。");
            }
            else if (mutating)
                throw new PluginContractException($"{origin}：修改操作“{operationId}”必须声明 resourceIds 供冲突检测。");
            parsedOperations.Add(new PluginOperation(operationId, title, mutating, reversible, targetScoped, resources, timeout));
        }

        return new PluginManifest(schema, id, name, author, FormatVersion(version), protocolText, protocolMajor, protocolMinor,
            new PluginHostRange(min, max), new PluginBackend(entry, "x64"), parsedPermissions,
            new PluginCapabilities(continuous, offlineAutonomous), parsedOperations);
    }

    /// <summary>协议版本：主版本必须为 1，次版本不得高于宿主（规范第 3 节，不静默降级）。</summary>
    public static (int Major, int Minor) ParseProtocol(string text, string origin)
    {
        var parts = text.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) ||
            major < 0 || minor < 0 || text.StartsWith('+') || text.StartsWith('-') ||
            parts[0].Length > 1 || parts[0].Trim().Length != parts[0].Length || parts[1].Trim().Length != parts[1].Length ||
            (parts[0].Length > 1 && parts[0][0] == '0') || (parts[1].Length > 1 && parts[1][0] == '0'))
            throw new PluginContractException($"{origin}：protocolVersion 必须形如 1.0，实际“{text}”。");
        if (major != PluginContract.ProtocolMajorVersion)
            throw new PluginContractException($"{origin}：协议主版本必须为 {PluginContract.ProtocolMajorVersion}，实际 {major}。");
        if (minor > PluginContract.ProtocolMinorVersion)
            throw new PluginContractException($"{origin}：插件要求协议次版本 {minor}，高于宿主支持的 {PluginContract.ProtocolMinorVersion}，不静默降级。");
        return (major, minor);
    }

    /// <summary>三段数字 SemVer，不接受预发布/构建元数据（规范第 3 节）。</summary>
    public static Version ParseVersion(string text, string origin, string field)
    {
        if (string.IsNullOrEmpty(text) || !char.IsAsciiDigit(text[0]) || text.Contains('+') || text.Contains('-'))
            throw new PluginContractException($"{origin}：{field} 必须是三段数字版本（如 1.0.0），实际“{text}”。");
        var parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit) || (p.Length > 1 && p[0] == '0')))
            throw new PluginContractException($"{origin}：{field} 必须是三段数字版本（如 1.0.0），实际“{text}”。");
        if (!TryParseVersion(text, out var version))
            throw new PluginContractException($"{origin}：{field} 数值超界：“{text}”。");
        return version;
    }

    public static bool TryParseVersion(string text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrEmpty(text)) return false;
        var parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit) || (p.Length > 1 && p[0] == '0')))
            return false;
        if (!Version.TryParse(text, out var parsed) || parsed.Revision != -1) return false;
        if (parsed.Major > int.MaxValue || parsed.Minor > int.MaxValue || parsed.Build > int.MaxValue) return false;
        version = parsed;
        return true;
    }

    public static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";

    private static bool IsReverseDomainId(string id)
    {
        // 3-80 字符小写 ASCII；各段字母开头，仅字母数字连字符（规范第 3 节）。
        if (id.Any(c => c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.'))) return false;
        var segments = id.Split('.');
        if (segments.Length < 2) return false;
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || !char.IsAsciiLetter(segment[0])) return false;
            if (segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return false;
        }
        return true;
    }

    internal static bool IsShortId(string id)
    {
        if (id is null || id.Length is < 1 or > 64) return false;
        if (!char.IsAsciiLetter(id[0])) return false;
        return id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    }

    // ---- 严格取值辅助：仅接受已知字段、类型必须匹配（调用方先经 ParseStrict 保证无重复属性）----

    internal static bool TryGetString(JsonElement parent, string name, out string value)
    {
        value = "";
        if (!parent.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind is not JsonValueKind.String) return false;
        value = property.GetString() ?? "";
        return true;
    }

    internal static bool TryGetInt(JsonElement parent, string name, out int value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind is not JsonValueKind.Number) return false;
        return property.TryGetInt32(out value);
    }

    internal static bool TryGetBool(JsonElement parent, string name, out bool value)
    {
        value = false;
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind is not JsonValueKind.True and not JsonValueKind.False) return false;
        value = property.GetBoolean();
        return true;
    }

    internal static bool TryGetDouble(JsonElement parent, string name, out double value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind is not JsonValueKind.Number) return false;
        return property.TryGetDouble(out value);
    }

    internal static bool TryGetDecimal(JsonElement parent, string name, out decimal value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind is not JsonValueKind.Number) return false;
        return property.TryGetDecimal(out value);
    }

    internal static bool TryGetObject(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind is not JsonValueKind.Object) return false;
        value = property;
        return true;
    }

    internal static bool TryGetArray(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind is not JsonValueKind.Array) return false;
        value = property;
        return true;
    }

    internal static void EnsureNoExtraProperties(JsonElement parent, string[] allowed, string origin)
    {
        foreach (var property in parent.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new PluginContractException($"{origin}：未知字段“{property.Name}”。");
    }

    internal static string RequireString(JsonElement parent, string name, string origin) =>
        TryGetString(parent, name, out var value) && value.Length > 0
            ? value
            : throw new PluginContractException($"{origin}：缺少字符串字段“{name}”或类型不符。");

    internal static string RequireIdString(JsonElement parent, string name, string origin) =>
        TryGetString(parent, name, out var value) ? value : throw new PluginContractException($"{origin}：缺少字符串字段“{name}”。");

    internal static string RequireUiString(JsonElement parent, string name, string origin) =>
        TryGetString(parent, name, out var value) && value.Trim().Length > 0 && value.Length <= PluginContract.MaxUiTextChars
            ? value
            : throw new PluginContractException($"{origin}：字段“{name}”必须为 1-{PluginContract.MaxUiTextChars} 字符的非空文本。");

    internal static int RequireInt(JsonElement parent, string name, string origin) =>
        TryGetInt(parent, name, out var value)
            ? value
            : throw new PluginContractException($"{origin}：缺少整数字段“{name}”或类型不符。");
}
