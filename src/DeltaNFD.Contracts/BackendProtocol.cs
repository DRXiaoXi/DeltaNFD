using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaNFD.Backends;

/// <summary>协议违规（帧超长、坏 JSON、重复属性、未知类型、缺字段、越权命令等）。调用方一律断开连接。</summary>
public sealed class BackendContractException(string message) : Exception(message);

/// <summary>官方后端 IPC 帧类型（区别于第三方插件协议，二者互不复用类型）。</summary>
public enum BackendFrameType
{
    /// <summary>宿主→后端：握手，携带 nonce / 宿主版本 / 数据目录。</summary>
    Hello,

    /// <summary>后端→宿主：握手应答，携带身份与模块版本。</summary>
    Ready,

    /// <summary>宿主→后端：执行一条白名单命令。</summary>
    Invoke,

    /// <summary>后端→宿主：进度。</summary>
    Progress,

    /// <summary>后端→宿主：结果（成功 / 失败 / 取消 / 部分成功）。</summary>
    Result,

    /// <summary>宿主→后端：取消指定请求。</summary>
    Cancel,

    /// <summary>宿主→后端：排空并退出。</summary>
    Stop,

    /// <summary>后端→宿主：状态快照。</summary>
    Status,

    Ping,

    Pong,
}

/// <summary>解析后的帧。payload 为原始 JSON 元素，交由各消息专用解析方法二次校验。</summary>
public sealed record BackendFrame(BackendFrameType Type, string Module, Guid SessionId, Guid RequestId, JsonElement Payload);

/// <summary>
/// 官方后端 IPC 1.0 帧编解码。帧 = 4 字节 little-endian 无符号长度 + UTF-8 JSON，
/// 长度 1..<see cref="MaxFrameBytes"/>；超长、非法 UTF-8、坏 JSON、重复属性、未知/缺字段
/// 立即抛 <see cref="BackendContractException"/>。帧结构复用第三方插件已有的长度前缀管道约定。
/// </summary>
public static class BackendProtocol
{
    public const string ProtocolVersion = "1.0";
    public const int ProtocolMajor = 1;
    public const int ProtocolMinor = 0;
    public const int MaxFrameBytes = 1024 * 1024;
    public const int MaxJsonBytes = 1024 * 1024;
    public const int MaxUiTextChars = 4096;

    private static readonly IReadOnlyDictionary<string, BackendFrameType> Types =
        new Dictionary<string, BackendFrameType>(StringComparer.Ordinal)
        {
            ["hello"] = BackendFrameType.Hello,
            ["ready"] = BackendFrameType.Ready,
            ["invoke"] = BackendFrameType.Invoke,
            ["progress"] = BackendFrameType.Progress,
            ["result"] = BackendFrameType.Result,
            ["cancel"] = BackendFrameType.Cancel,
            ["stop"] = BackendFrameType.Stop,
            ["status"] = BackendFrameType.Status,
            ["ping"] = BackendFrameType.Ping,
            ["pong"] = BackendFrameType.Pong,
        };

    public static string TypeName(BackendFrameType type) => Types.Single(p => p.Value == type).Key;

    /// <summary>尝试从缓冲区读取一个完整帧；不够一帧时返回 false 不消费，非法时抛异常。</summary>
    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out BackendFrame frame)
    {
        frame = null!;
        if (buffer.Length < 4) return false;
        Span<byte> lengthBytes = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(lengthBytes);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        if (length is 0 or > MaxFrameBytes)
            throw new BackendContractException($"帧长度非法：{length}（允许 1..{MaxFrameBytes}）。");
        if (buffer.Length < 4 + (int)length) return false;

        var payload = buffer.Slice(4, (int)length);
        buffer = buffer.Slice(4 + (int)length);
        return TryParseFrame(payload, out frame);
    }

    /// <summary>编码一帧：4 字节小端长度 + payload JSON。</summary>
    public static byte[] EncodeFrame(string payloadJson)
    {
        var payload = Encoding.UTF8.GetBytes(payloadJson);
        if (payload.Length is 0 or > MaxFrameBytes)
            throw new BackendContractException($"payload 长度非法：{payload.Length}。");
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>构造 envelope 并编码；payloadJson 为已序列化的 JSON 对象片段。</summary>
    public static byte[] Encode(BackendFrameType type, string module, Guid sessionId, Guid requestId, string payloadJson)
    {
        var envelope = $$"""
            {"protocol":"{{ProtocolVersion}}","type":"{{TypeName(type)}}","module":{{BackendJson.Quote(module)}},"sessionId":"{{sessionId:D}}","requestId":"{{requestId:D}}","payload":{{payloadJson}}}
            """.ReplaceLineEndings("");
        return EncodeFrame(envelope);
    }

    public static bool TryParseFrame(ReadOnlySequence<byte> payload, out BackendFrame frame)
    {
        frame = null!;
        using var document = BackendJson.ParseStrict(payload.ToArray(), "IPC 帧");
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object)
            throw new BackendContractException("IPC 帧：根节点必须是对象。");
        BackendJson.EnsureNoExtraProperties(root, ["protocol", "type", "module", "sessionId", "requestId", "payload"], "IPC 帧");

        if (!BackendJson.TryGetString(root, "protocol", out var protocol) || protocol != ProtocolVersion)
            throw new BackendContractException($"IPC 帧：protocol 必须为“{ProtocolVersion}”。");
        if (!BackendJson.TryGetString(root, "type", out var typeText) || !Types.TryGetValue(typeText, out var type))
            throw new BackendContractException($"IPC 帧：未知消息类型“{typeText}”。");
        if (!BackendJson.TryGetString(root, "module", out var module) ||
            !BackendModules.All.ContainsKey(module))
            throw new BackendContractException("IPC 帧：module 必须是已知官方模块 id。");
        if (!BackendJson.TryGetString(root, "sessionId", out var sessionText) ||
            !Guid.TryParseExact(sessionText, "D", out var sessionId) || sessionId == Guid.Empty)
            throw new BackendContractException("IPC 帧：sessionId 必须是 UUID。");
        if (!BackendJson.TryGetString(root, "requestId", out var requestText) ||
            !Guid.TryParseExact(requestText, "D", out var requestId) || requestId == Guid.Empty)
            throw new BackendContractException("IPC 帧：requestId 必须是 UUID。");
        if (!root.TryGetProperty("payload", out var payloadElement) || payloadElement.ValueKind is not JsonValueKind.Object)
            throw new BackendContractException("IPC 帧：payload 必须是对象。");

        frame = new BackendFrame(type, module, sessionId, requestId, payloadElement.Clone());
        return true;
    }
}

/// <summary>严格 JSON 解析与取值助手：拒绝注释、尾随逗号、重复属性、越界数值。</summary>
public static class BackendJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>严格解析：校验 UTF-8、大小上限与重复属性。失败一律抛 <see cref="BackendContractException"/>。</summary>
    public static JsonDocument ParseStrict(byte[] utf8, string what)
    {
        if (utf8 is null || utf8.Length == 0)
            throw new BackendContractException($"{what}：内容为空。");
        if (utf8.Length > BackendProtocol.MaxJsonBytes)
            throw new BackendContractException($"{what}：超过 {BackendProtocol.MaxJsonBytes} 字节上限。");
        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            throw new BackendContractException($"{what}：不是合法 UTF-8。");
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
            throw new BackendContractException($"{what}：JSON 解析失败（{ex.Message}）。");
        }

        if (!HasDuplicateProperty(document.RootElement, out var duplicate))
            return document;
        document.Dispose();
        throw new BackendContractException($"{what}：存在重复属性“{duplicate}”。");
    }

    /// <summary>true = 发现重复属性。</summary>
    private static bool HasDuplicateProperty(JsonElement element, out string duplicate)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) { duplicate = property.Name; return true; }
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

    public static void EnsureNoExtraProperties(JsonElement element, IReadOnlyCollection<string> allowed, string what)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new BackendContractException($"{what}：存在未知字段“{property.Name}”。");
        }
    }

    public static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = "";
        if (!element.TryGetProperty(name, out var property) || property.ValueKind is not JsonValueKind.String)
            return false;
        value = property.GetString() ?? "";
        return true;
    }

    public static string RequireString(JsonElement element, string name, string what) =>
        TryGetString(element, name, out var value) && value.Length > 0
            ? value
            : throw new BackendContractException($"{what}：缺少非空字符串字段“{name}”。");

    public static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) &&
               property.ValueKind is JsonValueKind.Number && property.TryGetInt32(out value);
    }

    public static bool TryGetLong(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) &&
               property.ValueKind is JsonValueKind.Number && property.TryGetInt64(out value);
    }

    public static bool TryGetBool(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property)) return false;
        switch (property.ValueKind)
        {
            case JsonValueKind.True: value = true; return true;
            case JsonValueKind.False: value = false; return true;
            default: return false;
        }
    }

    public static bool TryGetArray(JsonElement element, string name, out JsonElement array)
    {
        array = default;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind is not JsonValueKind.Array)
            return false;
        array = property;
        return true;
    }

    /// <summary>序列化为 JSON 字符串字面量（用于自建 envelope；值本身必须是普通文本）。</summary>
    public static string Quote(string text) => JsonSerializer.Serialize(text);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, SerializerOptions);

    public static T Deserialize<T>(JsonElement element) where T : notnull
    {
        try
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText(), SerializerOptions)
                   ?? throw new BackendContractException($"反序列化 {typeof(T).Name} 得到 null。");
        }
        catch (JsonException ex)
        {
            throw new BackendContractException($"反序列化 {typeof(T).Name} 失败（{ex.Message}）。");
        }
    }

    /// <summary>把 payload 序列化为 envelope 片段：{"payload":...} 里的 payload 对象。</summary>
    public static string PayloadOf(object payload) => JsonSerializer.Serialize(payload, SerializerOptions);
}
