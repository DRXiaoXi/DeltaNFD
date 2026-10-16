using System.Text.Json;

namespace DeltaNFD.Backends;

/// <summary>后端结果的固定状态取值。任何超时、断线、未知状态都不得折算为 ok。</summary>
public static class BackendResultStatuses
{
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Partial = "partial";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Ok, Failed, Cancelled, Partial,
    };
}

/// <summary>hello（宿主→后端）：一次性 nonce、宿主三段版本、绝对数据目录。</summary>
public sealed record BackendHello(string Nonce, string HostVersion, string DataDirectory);

/// <summary>ready（后端→宿主）：回显 nonce 并声明自身身份，供宿主复核。</summary>
public sealed record BackendReady(
    string Nonce,
    string Module,
    string ModuleVersion,
    int ProtocolMinor,
    int Pid,
    long StartTimeUtcTicks,
    string ImagePath,
    string ImageSha256);

/// <summary>invoke（宿主→后端）：白名单命令 + 参数对象。</summary>
public sealed record BackendInvoke(string Command, JsonElement Args);

/// <summary>progress（后端→宿主）：0..100 或 null 的百分比 + 纯文本 + 阶段标识。</summary>
public sealed record BackendProgress(int? Percent, string Message, string Stage);

/// <summary>结果明细项：部分成功时逐项标注。</summary>
public sealed record BackendResultItem(string Id, string Status, string Message);

/// <summary>result（后端→宿主）。data 为各命令专用结果对象（可为空）。</summary>
public sealed record BackendResult(
    string Status,
    string Code,
    string Message,
    IReadOnlyList<BackendResultItem> Items,
    bool PendingRestore,
    JsonElement? Data)
{
    public static BackendResult Ok(string code, string message, object? data = null, bool pendingRestore = false) =>
        new(BackendResultStatuses.Ok, code, message, [], pendingRestore, data is null ? null : ToElement(data));

    public static BackendResult Failed(string code, string message) =>
        new(BackendResultStatuses.Failed, code, message, [], false, null);

    public static BackendResult Cancelled(string code, string message, IReadOnlyList<BackendResultItem>? items = null) =>
        new(BackendResultStatuses.Cancelled, code, message, items ?? [], false, null);

    public static BackendResult PartialResult(string code, string message, IReadOnlyList<BackendResultItem> items, object? data = null, bool pendingRestore = false) =>
        new(BackendResultStatuses.Partial, code, message, items, pendingRestore, data is null ? null : ToElement(data));

    private static JsonElement ToElement(object data) =>
        JsonSerializer.SerializeToElement(data, BackendJson.SerializerOptions);
}

/// <summary>
/// status（后端→宿主）：空闲 / 执行中 / 已停止。
/// <see cref="Data"/> 为可选的模块状态快照（如 CPU/帧格后端的实际生效状态），
/// 前端据此展示真实状态而不把偏好当作生效；无快照时为 null。
/// </summary>
public sealed record BackendStatus(string State, Guid? CurrentRequestId, JsonElement? Data = null);

/// <summary>各帧 payload 的编解码。所有解析都做「未知/缺字段/类型错误即拒绝」的严格校验。</summary>
public static class BackendMessages
{
    public const string StateIdle = "idle";
    public const string StateBusy = "busy";
    public const string StateStopped = "stopped";

    public static byte[] EncodeHello(string module, Guid sessionId, Guid requestId, BackendHello hello) =>
        BackendProtocol.Encode(BackendFrameType.Hello, module, sessionId, requestId, BackendJson.PayloadOf(hello));

    public static byte[] EncodeReady(string module, Guid sessionId, Guid requestId, BackendReady ready) =>
        BackendProtocol.Encode(BackendFrameType.Ready, module, sessionId, requestId, BackendJson.PayloadOf(ready));

    public static byte[] EncodeInvoke(string module, Guid sessionId, Guid requestId, BackendInvoke invoke) =>
        BackendProtocol.Encode(BackendFrameType.Invoke, module, sessionId, requestId, BackendJson.PayloadOf(invoke));

    public static byte[] EncodeProgress(string module, Guid sessionId, Guid requestId, BackendProgress progress) =>
        BackendProtocol.Encode(BackendFrameType.Progress, module, sessionId, requestId, BackendJson.PayloadOf(progress));

    public static byte[] EncodeResult(string module, Guid sessionId, Guid requestId, BackendResult result) =>
        BackendProtocol.Encode(BackendFrameType.Result, module, sessionId, requestId, BackendJson.PayloadOf(result));

    public static byte[] EncodeCancel(string module, Guid sessionId, Guid requestId, Guid referencedRequestId) =>
        BackendProtocol.Encode(BackendFrameType.Cancel, module, sessionId, requestId,
            $$"""{"referencedRequestId":"{{referencedRequestId:D}}"}""");

    public static byte[] EncodeStop(string module, Guid sessionId, Guid requestId) =>
        BackendProtocol.Encode(BackendFrameType.Stop, module, sessionId, requestId, "{}");

    public static byte[] EncodeStatus(string module, Guid sessionId, Guid requestId, BackendStatus status) =>
        BackendProtocol.Encode(BackendFrameType.Status, module, sessionId, requestId, BackendJson.PayloadOf(status));

    public static byte[] EncodePong(string module, Guid sessionId, Guid requestId) =>
        BackendProtocol.Encode(BackendFrameType.Pong, module, sessionId, requestId, "{}");

    public static BackendHello ParseHello(JsonElement payload)
    {
        BackendJson.EnsureNoExtraProperties(payload, ["nonce", "hostVersion", "dataDirectory"], "hello");
        if (!BackendJson.TryGetString(payload, "nonce", out var nonce) || !IsNonce(nonce))
            throw new BackendContractException("hello：nonce 必须是 32 字节随机数的 Base64 文本。");
        if (!BackendJson.TryGetString(payload, "hostVersion", out var hostVersion) ||
            !IsThreePartVersion(hostVersion))
            throw new BackendContractException("hello：hostVersion 必须是三段数字版本。");
        if (!BackendJson.TryGetString(payload, "dataDirectory", out var dataDirectory) ||
            !Path.IsPathFullyQualified(dataDirectory))
            throw new BackendContractException("hello：dataDirectory 必须是绝对路径。");
        return new BackendHello(nonce, hostVersion, dataDirectory);
    }

    public static BackendReady ParseReady(JsonElement payload)
    {
        BackendJson.EnsureNoExtraProperties(payload,
            ["nonce", "module", "moduleVersion", "protocolMinor", "pid", "startTimeUtcTicks", "imagePath", "imageSha256"],
            "ready");
        if (!BackendJson.TryGetString(payload, "nonce", out var nonce) || !IsNonce(nonce))
            throw new BackendContractException("ready：nonce 必须是 32 字节随机数的 Base64 文本。");
        var module = BackendJson.RequireString(payload, "module", "ready");
        if (!BackendModules.All.ContainsKey(module))
            throw new BackendContractException($"ready：未知模块“{module}”。");
        var moduleVersion = BackendJson.RequireString(payload, "moduleVersion", "ready");
        if (!IsThreePartVersion(moduleVersion))
            throw new BackendContractException("ready：moduleVersion 必须是三段数字版本。");
        if (!BackendJson.TryGetInt(payload, "protocolMinor", out var protocolMinor) ||
            protocolMinor is < 0 or > BackendProtocol.ProtocolMinor)
            throw new BackendContractException("ready：protocolMinor 越界或不兼容。");
        if (!BackendJson.TryGetInt(payload, "pid", out var pid) || pid <= 0)
            throw new BackendContractException("ready：pid 必须是正整数。");
        if (!BackendJson.TryGetLong(payload, "startTimeUtcTicks", out var ticks) || ticks <= 0)
            throw new BackendContractException("ready：startTimeUtcTicks 必须是正数。");
        if (!BackendJson.TryGetString(payload, "imagePath", out var imagePath) || !Path.IsPathFullyQualified(imagePath))
            throw new BackendContractException("ready：imagePath 必须是绝对路径。");
        if (!BackendJson.TryGetString(payload, "imageSha256", out var sha) || !IsSha256(sha))
            throw new BackendContractException("ready：imageSha256 必须是 64 位十六进制。");
        return new BackendReady(nonce, module, moduleVersion, protocolMinor, pid, ticks, imagePath, sha.ToLowerInvariant());
    }

    public static BackendInvoke ParseInvoke(JsonElement payload)
    {
        BackendJson.EnsureNoExtraProperties(payload, ["command", "args"], "invoke");
        var command = BackendJson.RequireString(payload, "command", "invoke");
        if (!payload.TryGetProperty("args", out var args) || args.ValueKind is not JsonValueKind.Object)
            throw new BackendContractException("invoke：args 必须是对象。");
        return new BackendInvoke(command, args.Clone());
    }

    public static BackendProgress ParseProgress(JsonElement payload)
    {
        BackendJson.EnsureNoExtraProperties(payload, ["percent", "message", "stage"], "progress");
        int? percent = null;
        if (payload.TryGetProperty("percent", out var percentElement) && percentElement.ValueKind is not JsonValueKind.Null)
        {
            if (percentElement.ValueKind is not JsonValueKind.Number || !percentElement.TryGetInt32(out var value) ||
                value is < 0 or > 100)
                throw new BackendContractException("progress：percent 必须是 0..100 的整数或 null。");
            percent = value;
        }
        if (!BackendJson.TryGetString(payload, "message", out var message) ||
            message.Length > BackendProtocol.MaxUiTextChars)
            throw new BackendContractException($"progress：message 必须是 ≤{BackendProtocol.MaxUiTextChars} 字符的文本。");
        var stage = BackendJson.TryGetString(payload, "stage", out var stageText) ? stageText : "";
        if (stage.Length > 128) throw new BackendContractException("progress：stage 过长。");
        return new BackendProgress(percent, message, stage);
    }

    public static BackendResult ParseResult(JsonElement payload)
    {
        BackendJson.EnsureNoExtraProperties(payload,
            ["status", "code", "message", "items", "pendingRestore", "data"], "result");
        if (!BackendJson.TryGetString(payload, "status", out var status) || !BackendResultStatuses.All.Contains(status))
            throw new BackendContractException("result：status 取值非法。");
        var code = BackendJson.RequireString(payload, "code", "result");
        if (!BackendJson.TryGetString(payload, "message", out var message) ||
            message.Length > BackendProtocol.MaxUiTextChars)
            throw new BackendContractException($"result：message 必须是 ≤{BackendProtocol.MaxUiTextChars} 字符的文本。");
        if (!BackendJson.TryGetBool(payload, "pendingRestore", out var pendingRestore))
            throw new BackendContractException("result：pendingRestore 必须是布尔值。");

        var items = new List<BackendResultItem>();
        if (payload.TryGetProperty("items", out var itemsElement))
        {
            if (itemsElement.ValueKind is not JsonValueKind.Array)
                throw new BackendContractException("result：items 必须是数组。");
            foreach (var item in itemsElement.EnumerateArray())
            {
                if (item.ValueKind is not JsonValueKind.Object)
                    throw new BackendContractException("result：items 项必须是对象。");
                BackendJson.EnsureNoExtraProperties(item, ["id", "status", "message"], "result.items");
                var id = BackendJson.RequireString(item, "id", "result.items");
                if (!BackendJson.TryGetString(item, "status", out var itemStatus) ||
                    !BackendResultStatuses.All.Contains(itemStatus))
                    throw new BackendContractException("result：items 项 status 取值非法。");
                var itemMessage = BackendJson.TryGetString(item, "message", out var im) ? im : "";
                items.Add(new BackendResultItem(id, itemStatus, itemMessage));
            }
        }

        JsonElement? data = null;
        if (payload.TryGetProperty("data", out var dataElement) && dataElement.ValueKind is not JsonValueKind.Null)
            data = dataElement.Clone();

        return new BackendResult(status, code, message, items, pendingRestore, data);
    }

    public static Guid ParseCancel(JsonElement payload)
    {
        BackendJson.EnsureNoExtraProperties(payload, ["referencedRequestId"], "cancel");
        if (!BackendJson.TryGetString(payload, "referencedRequestId", out var text) ||
            !Guid.TryParseExact(text, "D", out var requestId) || requestId == Guid.Empty)
            throw new BackendContractException("cancel：referencedRequestId 必须是 UUID。");
        return requestId;
    }

    public static BackendStatus ParseStatus(JsonElement payload)
    {
        BackendJson.EnsureNoExtraProperties(payload, ["state", "currentRequestId", "data"], "status");
        var state = BackendJson.RequireString(payload, "state", "status");
        if (state is not (StateIdle or StateBusy or StateStopped))
            throw new BackendContractException($"status：state 取值非法（{state}）。");
        Guid? current = null;
        if (payload.TryGetProperty("currentRequestId", out var currentElement) &&
            currentElement.ValueKind is not JsonValueKind.Null)
        {
            if (!BackendJson.TryGetString(payload, "currentRequestId", out var text) ||
                !Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty)
                throw new BackendContractException("status：currentRequestId 必须是 UUID 或 null。");
            current = id;
        }

        JsonElement? data = null;
        if (payload.TryGetProperty("data", out var dataElement) && dataElement.ValueKind is not JsonValueKind.Null)
        {
            if (dataElement.ValueKind is not JsonValueKind.Object)
                throw new BackendContractException("status：data 必须是对象或 null。");
            data = dataElement.Clone();
        }

        return new BackendStatus(state, current, data);
    }

    public static bool IsNonce(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length != 44 || !text.EndsWith('=')) return false;
        try
        {
            return Convert.FromBase64String(text).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsSha256(string? text) =>
        text is { Length: 64 } && text.All(c => Uri.IsHexDigit(c));

    /// <summary>三段数字版本（如 0.90.1）；拒绝预发布/构建元数据。</summary>
    public static bool IsThreePartVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('.');
        return parts.Length == 3 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit));
    }
}
