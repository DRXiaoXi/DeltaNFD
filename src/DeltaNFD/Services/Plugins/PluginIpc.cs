using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace DeltaNFD.Services.Plugins;

/// <summary>IPC 消息类型（规范第 6 节消息合同表）。</summary>
public enum PluginIpcType
{
    Hello, Ready, Invoke, Progress, Result, Cancel, Stop, Restore, Status,
    OfflinePrepare, OfflineCommit, Ping, Pong, Reconnect,
}

/// <summary>解析后的 IPC 帧。payload 为原始 JSON 元素，由各消息类型的专用方法二次解析。</summary>
public sealed record PluginIpcFrame(PluginIpcType Type, Guid SessionId, Guid RequestId, JsonElement Payload)
{
    public void Deconstruct(out PluginIpcType type, out Guid sessionId, out Guid requestId, out JsonElement payload)
    {
        type = Type; sessionId = SessionId; requestId = RequestId; payload = Payload;
    }
}

/// <summary>
/// IPC 1.0 帧编解码（规范第 6 节）。帧 = 4 字节 little-endian 无符号长度 + UTF-8 JSON，
/// 长度 1..1048576；超长、非法 UTF-8、坏 JSON、重复属性、未知/缺字段立即断开（抛
/// <see cref="PluginContractException"/>，由调用方关闭连接）。流式读取必须处理半帧与多帧。
/// </summary>
public static class PluginIpc
{
    private static readonly IReadOnlyDictionary<string, PluginIpcType> Types =
        new Dictionary<string, PluginIpcType>(StringComparer.Ordinal)
        {
            ["hello"] = PluginIpcType.Hello,
            ["ready"] = PluginIpcType.Ready,
            ["invoke"] = PluginIpcType.Invoke,
            ["progress"] = PluginIpcType.Progress,
            ["result"] = PluginIpcType.Result,
            ["cancel"] = PluginIpcType.Cancel,
            ["stop"] = PluginIpcType.Stop,
            ["restore"] = PluginIpcType.Restore,
            ["status"] = PluginIpcType.Status,
            ["offline.prepare"] = PluginIpcType.OfflinePrepare,
            ["offline.commit"] = PluginIpcType.OfflineCommit,
            ["ping"] = PluginIpcType.Ping,
            ["pong"] = PluginIpcType.Pong,
            ["reconnect"] = PluginIpcType.Reconnect,
        };

    public static string TypeName(PluginIpcType type) => Types.Single(p => p.Value == type).Key;

    /// <summary>尝试从缓冲区读取一个完整帧；不够一帧时返回 false 不消费，非法时抛异常。</summary>
    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out PluginIpcFrame frame)
    {
        frame = null!;
        if (buffer.Length < 4) return false;
        Span<byte> lengthBytes = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(lengthBytes);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        if (length is 0 or > PluginContract.MaxFrameBytes)
            throw new PluginContractException($"帧长度非法：{length}（允许 1..{PluginContract.MaxFrameBytes}）。");
        if (buffer.Length < 4 + (int)length) return false;

        var payload = buffer.Slice(4, (int)length);
        buffer = buffer.Slice(4 + (int)length);
        return TryParseFrame(payload, out frame);
    }

    /// <summary>单帧解析（自带长度前缀时的便捷入口，主要用于测试与构造）。</summary>
    public static bool ParseFrame(byte[] framed, out PluginIpcFrame frame)
    {
        if (framed is null || framed.Length < 5)
            throw new PluginContractException("帧数据不足。");
        var buffer = new ReadOnlySequence<byte>(framed);
        if (!TryReadFrame(ref buffer, out frame) || !buffer.IsEmpty)
            throw new PluginContractException("单帧长度不匹配或存在多余数据。");
        return true;
    }

    public static bool TryParseFrame(ReadOnlySequence<byte> payload, out PluginIpcFrame frame)
    {
        frame = null!;
        var utf8 = payload.ToArray();
        using var document = PluginJson.ParseStrict(utf8, "IPC 帧");
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object)
            throw new PluginContractException("IPC 帧：根节点必须是对象。");
        PluginManifestParser.EnsureNoExtraProperties(root, ["protocol", "type", "sessionId", "requestId", "payload"], "IPC 帧");

        if (!PluginManifestParser.TryGetString(root, "protocol", out var protocol) || protocol != PluginContract.ProtocolVersion)
            throw new PluginContractException($"IPC 帧：protocol 必须为“{PluginContract.ProtocolVersion}”。");
        if (!PluginManifestParser.TryGetString(root, "type", out var typeText) || !Types.TryGetValue(typeText, out var type))
            throw new PluginContractException($"IPC 帧：未知消息类型“{typeText}”。");
        if (!PluginManifestParser.TryGetString(root, "sessionId", out var sessionText) || !Guid.TryParseExact(sessionText, "D", out var sessionId) || sessionId == Guid.Empty)
            throw new PluginContractException("IPC 帧：sessionId 必须是 UUID。");
        if (!PluginManifestParser.TryGetString(root, "requestId", out var requestText) || !Guid.TryParseExact(requestText, "D", out var requestId) || requestId == Guid.Empty)
            throw new PluginContractException("IPC 帧：requestId 必须是 UUID。");
        if (!root.TryGetProperty("payload", out var payloadElement) || payloadElement.ValueKind is not JsonValueKind.Object)
            throw new PluginContractException("IPC 帧：payload 必须是对象。");

        frame = new PluginIpcFrame(type, sessionId, requestId, payloadElement.Clone());
        return true;
    }

    /// <summary>编码一帧：4 字节小端长度 + payload JSON。</summary>
    public static byte[] EncodeFrame(string payloadJson)
    {
        var payload = Encoding.UTF8.GetBytes(payloadJson);
        if (payload.Length is 0 or > PluginContract.MaxFrameBytes)
            throw new PluginContractException($"payload 长度非法：{payload.Length}。");
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>构造 envelope 并编码；payloadProperties 为已序列化的 JSON 属性片段。</summary>
    public static byte[] Encode(PluginIpcType type, Guid sessionId, Guid requestId, string payloadJson)
    {
        var envelope = $$"""
            {"protocol":"{{PluginContract.ProtocolVersion}}","type":"{{TypeName(type)}}","sessionId":"{{sessionId:D}}","requestId":"{{requestId:D}}","payload":{{payloadJson}}}
            """.ReplaceLineEndings("");
        return EncodeFrame(envelope);
    }

    /// <summary>hello payload（宿主→后端）：nonce、宿主数字版本、数据目录。</summary>
    public static (string Nonce, Version HostVersion, string DataDirectory) ParseHello(JsonElement payload)
    {
        PluginManifestParser.EnsureNoExtraProperties(payload, ["nonce", "hostVersion", "dataDirectory"], "hello");
        if (!PluginManifestParser.TryGetString(payload, "nonce", out var nonce) ||
            !TryParseNonce(nonce))
            throw new PluginContractException("hello：nonce 必须是 32 字节随机数的 Base64 文本。");
        if (!PluginManifestParser.TryGetString(payload, "hostVersion", out var versionText) ||
            !PluginManifestParser.TryParseVersion(versionText, out var hostVersion))
            throw new PluginContractException("hello：hostVersion 必须是三段数字版本。");
        if (!PluginManifestParser.TryGetString(payload, "dataDirectory", out var dataDirectory) ||
            !Path.IsPathRooted(dataDirectory))
            throw new PluginContractException("hello：dataDirectory 必须是绝对路径。");
        return (nonce, hostVersion, dataDirectory);
    }

    /// <summary>ready payload（后端→宿主）：回显 nonce、插件身份、协议、实际 PID。</summary>
    public static (string Nonce, string PluginId, string PluginVersion, int ProtocolMinor, int Pid) ParseReady(
        JsonElement payload)
    {
        PluginManifestParser.EnsureNoExtraProperties(payload, ["nonce", "pluginId", "pluginVersion", "protocol", "pid"], "ready");
        if (!PluginManifestParser.TryGetString(payload, "nonce", out var nonce) || !TryParseNonce(nonce))
            throw new PluginContractException("ready：nonce 必须是 32 字节随机数的 Base64 文本。");
        if (!PluginManifestParser.TryGetString(payload, "pluginId", out var pluginId) || pluginId.Length == 0)
            throw new PluginContractException("ready：pluginId 缺失。");
        if (!PluginManifestParser.TryGetString(payload, "pluginVersion", out var pluginVersion) ||
            !PluginManifestParser.TryParseVersion(pluginVersion, out _))
            throw new PluginContractException("ready：pluginVersion 必须是三段数字版本。");
        var protocol = PluginManifestParser.ParseProtocol(
            PluginManifestParser.RequireIdString(payload, "protocol", "ready"), "ready");
        if (!PluginManifestParser.TryGetInt(payload, "pid", out var pid) || pid <= 0)
            throw new PluginContractException("ready：pid 必须是正整数。");
        return (nonce, pluginId, pluginVersion, protocol.Minor, pid);
    }

    /// <summary>invoke payload（宿主→后端）：operationId、values、target。</summary>
    public static (string OperationId, Dictionary<string, object?> Values, PluginIpcTarget? Target) ParseInvoke(
        JsonElement payload, PluginManifest manifest, PluginUi ui)
    {
        PluginManifestParser.EnsureNoExtraProperties(payload, ["operationId", "values", "target"], "invoke");
        if (!PluginManifestParser.TryGetString(payload, "operationId", out var operationId))
            throw new PluginContractException("invoke：缺少 operationId。");
        var operation = manifest.FindOperation(operationId) ?? throw new PluginContractException(
            $"invoke：操作“{operationId}”未在 manifest 声明，拒绝执行。");
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (payload.TryGetProperty("values", out var valuesElement))
        {
            if (valuesElement.ValueKind is not JsonValueKind.Object)
                throw new PluginContractException("invoke：values 必须是对象。");
            foreach (var property in valuesElement.EnumerateObject())
            {
                object? value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number when property.Value.TryGetDouble(out var d) => d,
                    JsonValueKind.Null => null,
                    _ => throw new PluginContractException($"invoke：控件“{property.Name}”的值类型不支持。"),
                };
                values[property.Name] = value;
            }
        }
        if (!PluginUiParser.TryValidateValues(ui, values, out var error))
            throw new PluginContractException("invoke：" + error);
        if (operation.TargetScoped)
        {
            // targetScoped 操作必须携带统一目标快照（规范第 7 节）。
            var target = ParseTarget(payload);
            if (target is null) throw new PluginContractException("invoke：目标操作必须携带 target 快照。");
            return (operationId, values, target);
        }
        return (operationId, values, ParseTarget(payload));
    }

    /// <summary>统一目标快照（规范第 7 节）：代次、规范 EXE 路径、PID/UTC 启动 ticks。ticks 为十进制字符串避免 JSON 数字精度损失（第 8 节同）。</summary>
    public static PluginIpcTarget? ParseTarget(JsonElement payload)
    {
        if (!payload.TryGetProperty("target", out var target)) return null;
        if (target.ValueKind is JsonValueKind.Null) return null;
        if (target.ValueKind != JsonValueKind.Object)
            throw new PluginContractException("target 必须是对象或 null。");
        PluginManifestParser.EnsureNoExtraProperties(target, ["generation", "executablePath", "pid", "startTimeUtcTicks"], "target");
        if (!PluginManifestParser.TryGetInt(target, "generation", out var generation) || generation < 1)
            throw new PluginContractException("target：generation 必须是正整数。");
        if (!PluginManifestParser.TryGetString(target, "executablePath", out var executablePath) ||
            !Path.IsPathRooted(executablePath))
            throw new PluginContractException("target：executablePath 必须是绝对路径。");
        if (!PluginManifestParser.TryGetInt(target, "pid", out var pid) || pid <= 0)
            throw new PluginContractException("target：pid 必须是正整数。");
        if (!PluginManifestParser.TryGetString(target, "startTimeUtcTicks", out var ticksText) ||
            !long.TryParse(ticksText, out var ticks) || ticks <= 0)
            throw new PluginContractException("target：startTimeUtcTicks 必须是十进制字符串。");
        return new PluginIpcTarget(generation, executablePath, pid, ticks);
    }

    /// <summary>progress payload：percent 为 0..100 或 null；message 纯文本。</summary>
    public static (int? Percent, string Message) ParseProgress(JsonElement payload)
    {
        PluginManifestParser.EnsureNoExtraProperties(payload, ["percent", "message"], "progress");
        int? percent = null;
        if (payload.TryGetProperty("percent", out var percentElement) &&
            percentElement.ValueKind is not JsonValueKind.Null)
        {
            if (percentElement.ValueKind is not JsonValueKind.Number || !percentElement.TryGetInt32(out var value) ||
                value is < 0 or > 100)
                throw new PluginContractException("progress：percent 必须是 0..100 的整数或 null。");
            percent = value;
        }
        if (!PluginManifestParser.TryGetString(payload, "message", out var message) || message.Length > PluginContract.MaxUiTextChars)
            throw new PluginContractException($"progress：message 必须是 ≤{PluginContract.MaxUiTextChars} 字符的文本。");
        return (percent, message);
    }

    /// <summary>result payload（规范第 6 节示例）。</summary>
    public static PluginIpcResult ParseResult(JsonElement payload)
    {
        PluginManifestParser.EnsureNoExtraProperties(payload,
            ["status", "code", "message", "items", "pendingRestore", "backupIds"], "result");
        if (!PluginManifestParser.TryGetString(payload, "status", out var status) ||
            !PluginContract.ResultStatuses.Contains(status))
            throw new PluginContractException($"result：status 必须是 {string.Join("/", PluginContract.ResultStatuses)} 之一。");
        if (!PluginManifestParser.TryGetString(payload, "code", out var code) || code.Length == 0)
            throw new PluginContractException("result：code 不能为空。");
        if (!PluginManifestParser.TryGetString(payload, "message", out var message) ||
            message.Length > PluginContract.MaxUiTextChars)
            throw new PluginContractException($"result：message 必须是 ≤{PluginContract.MaxUiTextChars} 字符的文本。");
        if (!PluginManifestParser.TryGetBool(payload, "pendingRestore", out var pendingRestore))
            throw new PluginContractException("result：pendingRestore 必须是布尔值。");
        var items = new List<PluginIpcResultItem>();
        if (payload.TryGetProperty("items", out var itemsElement))
        {
            if (itemsElement.ValueKind is not JsonValueKind.Array)
                throw new PluginContractException("result：items 必须是数组。");
            foreach (var item in itemsElement.EnumerateArray())
            {
                if (item.ValueKind is not JsonValueKind.Object)
                    throw new PluginContractException("result：items 项必须是对象。");
                PluginManifestParser.EnsureNoExtraProperties(item, ["id", "status", "message"], "result.items");
                if (!PluginManifestParser.TryGetString(item, "id", out var itemId) ||
                    !PluginManifestParser.TryGetString(item, "status", out var itemStatus) ||
                    !PluginContract.ResultStatuses.Contains(itemStatus) ||
                    !PluginManifestParser.TryGetString(item, "message", out var itemMessage))
                    throw new PluginContractException("result：items 项必须包含 id、status（固定取值）与 message。");
                items.Add(new PluginIpcResultItem(itemId, itemStatus, itemMessage));
            }
        }
        var backupIds = new List<string>();
        if (payload.TryGetProperty("backupIds", out var backupsElement))
        {
            if (backupsElement.ValueKind is not JsonValueKind.Array)
                throw new PluginContractException("result：backupIds 必须是数组。");
            foreach (var backup in backupsElement.EnumerateArray())
            {
                if (backup.ValueKind is not JsonValueKind.String || backup.GetString() is not { Length: > 0 } backupId)
                    throw new PluginContractException("result：backupIds 项必须是非空字符串。");
                backupIds.Add(backupId);
            }
        }
        return new PluginIpcResult(status, code, message, items, pendingRestore, backupIds);
    }

    /// <summary>cancel payload：referencedRequestId；合作取消，不能表示已还原。</summary>
    public static Guid ParseCancel(JsonElement payload)
    {
        PluginManifestParser.EnsureNoExtraProperties(payload, ["referencedRequestId"], "cancel");
        if (!PluginManifestParser.TryGetString(payload, "referencedRequestId", out var text) ||
            !Guid.TryParseExact(text, "D", out var requestId))
            throw new PluginContractException("cancel：referencedRequestId 必须是 UUID。");
        return requestId;
    }

    /// <summary>restore payload：backupIds；幂等恢复并报告每项结果。</summary>
    public static IReadOnlyList<string> ParseRestore(JsonElement payload)
    {
        PluginManifestParser.EnsureNoExtraProperties(payload, ["backupIds"], "restore");
        if (!PluginManifestParser.TryGetArray(payload, "backupIds", out var array))
            throw new PluginContractException("restore：backupIds 必须是数组。");
        var ids = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.String || item.GetString() is not { Length: > 0 } id)
                throw new PluginContractException("restore：backupIds 项必须是非空字符串。");
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>offline.prepare / offline.commit payload：runId 为 UUID。</summary>
    public static Guid ParseRunId(JsonElement payload, string what)
    {
        PluginManifestParser.EnsureNoExtraProperties(payload,
            what == "offline.prepare" ? ["runId", "target"] : ["runId"], what);
        if (what == "offline.prepare") ParseTarget(payload);
        if (!PluginManifestParser.TryGetString(payload, "runId", out var text) ||
            !Guid.TryParseExact(text, "D", out var runId) || runId == Guid.Empty)
            throw new PluginContractException($"{what}：runId 必须是非零 UUID。");
        return runId;
    }

    public static (Guid RunId, PluginIpcTarget? Target) ParseOfflinePrepare(JsonElement payload) =>
        (ParseRunId(payload, "offline.prepare"), ParseTarget(payload));

    private static bool TryParseNonce(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length != 44 || !text.EndsWith("=", StringComparison.Ordinal)) return false;
        try
        {
            var bytes = Convert.FromBase64String(text);
            return bytes.Length == 32;
        }
        catch (FormatException) { return false; }
    }
}

/// <summary>统一目标快照（来自 GameTargetService，规范第 7 节）。</summary>
public sealed record PluginIpcTarget(long Generation, string ExecutablePath, int Pid, long StartTimeUtcTicks);

public sealed record PluginIpcResultItem(string Id, string Status, string Message);

public sealed record PluginIpcResult(
    string Status, string Code, string Message, IReadOnlyList<PluginIpcResultItem> Items, bool PendingRestore,
    IReadOnlyList<string> BackupIds);
