using System.Text;
using System.Text.Json;

namespace DeltaNFD.Services.Plugins;

/// <summary>
/// 拓展插件规范 1.0 的合同常量与错误码（见《拓展插件技术规范》第 3、6 节）。
/// 错误码同时用于导入验证与 IPC 结果，宿主不得据此断言后端确实完成了系统操作。
/// </summary>
public static class PluginContract
{
    public const int SchemaVersion = 1;
    public const string ProtocolVersion = "1.0";
    public const int ProtocolMajorVersion = 1;
    public const int ProtocolMinorVersion = 0;
    public const int MaxJsonBytes = 1024 * 1024;
    public const int MaxFrameBytes = 1024 * 1024;
    public const int MaxUiTextChars = 4096;
    public const int MaxUiControls = 100;
    public const int MaxFormValueChars = 4096;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 300;
    public const int DefaultTimeoutSeconds = 30;
    public const int HandshakeTimeoutSeconds = 10;
    public const int RecentResultsLimit = 128;

    /// <summary>权限类别固定集合（规范第 3 节）；用途描述必须逐一说明实际资源。</summary>
    public static readonly IReadOnlySet<string> PermissionCategories = new HashSet<string>(StringComparer.Ordinal)
    {
        "hardware.read", "target.read", "target.modify", "system.modify",
        "files.modify", "network.access", "process.launch",
    };

    /// <summary>插件声明与宿主约定的修改资源范围（冲突检测键）。</summary>
    public static readonly IReadOnlySet<string> OperationResourceIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "cpu.affinity", "cpu.sets", "process.priority", "dwm.restart",
        "power.scheme", "power.setting", "timer.resolution", "gpu.driver-profile",
        "runtime.redist", "registry.security", "filesystem.tweak",
    };

    /// <summary>IPC result.status 固定取值（规范第 6 节）。</summary>
    public static readonly IReadOnlySet<string> ResultStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "success", "failed", "partial", "cancelled", "pending_restore",
    };

    public static string DescribeHostCompatibility(Version host, PluginManifest manifest) => manifest is null
        ? "清单缺失"
        : host >= manifest.HostCompatibility.MinInclusive && host < manifest.HostCompatibility.MaxExclusive
            ? $"兼容（宿主 {AppVersion.Format(host)}）"
            : $"不兼容（要求 {AppVersion.Format(manifest.HostCompatibility.MinInclusive)} ≤ 宿主 < {AppVersion.Format(manifest.HostCompatibility.MaxExclusive)}，当前 {AppVersion.Format(host)}）";

    public static string DescribeTrust() =>
        "插件后端是以管理员身份运行的完全受信任第三方程序。能力声明、确认弹窗、协议校验和独立进程都不是权限沙箱，" +
        "不能保证备份真实、改动可恢复或反作弊兼容；有效签名仅标识签名者，不等于安全认证。";
}

/// <summary>JSON 解析设置：拒绝注释、尾随逗号与重复属性（规范第 3 节“拒绝重复属性、未知字段”）。</summary>
internal static class PluginJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>严格解析：校验 UTF-8、大小上限与重复属性。失败一律抛 <see cref="PluginContractException"/>。</summary>
    public static JsonDocument ParseStrict(byte[] utf8, string what)
    {
        if (utf8 is null || utf8.Length == 0)
            throw new PluginContractException($"{what}：内容为空。");
        if (utf8.Length > PluginContract.MaxJsonBytes)
            throw new PluginContractException($"{what}：超过 {PluginContract.MaxJsonBytes} 字节上限。");
        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            throw new PluginContractException($"{what}：不是合法 UTF-8。");
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        }
        catch (JsonException ex)
        {
            throw new PluginContractException($"{what}：JSON 解析失败（{ex.Message}）。");
        }
        if (!HasDuplicateProperty(document.RootElement, out var duplicate))
            return document;
        document.Dispose();
        throw new PluginContractException($"{what}：存在重复属性“{duplicate}”。");
    }

    private static bool HasDuplicateProperty(JsonElement element, out string duplicate)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name)) { duplicate = property.Name; return true; }
                    if (HasDuplicateProperty(property.Value, out duplicate)) return true;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (HasDuplicateProperty(item, out duplicate)) return true;
                break;
        }
        duplicate = "";
        return false;
    }
}

/// <summary>合同解析失败的统一异常；消息直接面向用户展示。</summary>
public sealed class PluginContractException(string message) : Exception(message);
