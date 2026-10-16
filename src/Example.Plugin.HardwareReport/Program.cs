using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// 拓展插件规范 1.0 最小只读示例：解析固定启动参数 → 连接宿主管道 → hello/ready 握手 →
// 处理 report（只读）/ cancel / status / stop / ping → 断线安全退出。
// 不引用宿主内部服务；不修改系统；不请求持续或脱机能力。

namespace Example.Plugin;

internal static class Program
{
    private const string ProtocolVersion = "1.0";
    private const int MaxFrameBytes = 1024 * 1024;
    private static Guid _sessionId = Guid.Empty;
    private static string _dataDirectory = "";
    private static bool _stopping;
    private static readonly object Gate = new();

    private static async Task<int> Main(string[] args)
    {
        string? pipeName = null, sessionId = null, hostPidText = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !seen.Add(args[i])) return 2;
            if (args[i] == "--dnfd-pipe") pipeName = args[i + 1];
            else if (args[i] == "--dnfd-session") sessionId = args[i + 1];
            else if (args[i] == "--dnfd-host-pid") hostPidText = args[i + 1];
            else return 2;
        }
        if (string.IsNullOrEmpty(pipeName) || !Guid.TryParseExact(sessionId, "D", out var expectedSession) ||
            expectedSession == Guid.Empty || !int.TryParse(hostPidText, out var hostPid) || hostPid <= 0)
        {
            Console.Error.WriteLine("需要 --dnfd-pipe、--dnfd-session 和 --dnfd-host-pid 参数。");
            return 2;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // 握手 10 秒
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var host = Process.GetProcessById(hostPid);
            var hostStart = host.StartTime;
            await pipe.ConnectAsync(5000, cts.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != hostPid ||
                host.HasExited || host.StartTime != hostStart)
                throw new PluginProtocolException("管道服务端不是指定的存活宿主。");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("连接宿主管道失败：" + ex.Message);
            return 2;
        }
        using var writer = new BinaryWriter(pipe, new UTF8Encoding(false), leaveOpen: true);

        try
        {
            // hello → ready：回显 nonce、身份与实际 PID（规范第 6 节）。
            var hello = await ReadFrameAsync(pipe, cts.Token);
            if (hello.SessionId != expectedSession) throw new PluginProtocolException("hello 会话不匹配。");
            var (nonce, hostVersion, dataDirectory) = ParseHello(hello);
            _sessionId = hello.SessionId;
            _dataDirectory = dataDirectory;
            WriteFrame(writer, PluginIpcType.Ready, _sessionId, hello.RequestId, $$"""
                {"nonce":"{{nonce}}","pluginId":"org.example.hardware-report","pluginVersion":"1.0.0","protocol":"{{ProtocolVersion}}","pid":{{Environment.ProcessId}}}
                """.ReplaceLineEndings(""));

            // 握手完成进入命令循环；ping/pong 心跳每 5 秒一次由宿主驱动。
            while (pipe.IsConnected)
            {
                PluginIpcFrame frame;
                try { frame = await ReadFrameAsync(pipe, CancellationToken.None); }
                catch (EndOfStreamException) { break; } // 宿主断开：安全退出，不依赖宿主心跳
                if (frame.SessionId != _sessionId) throw new PluginProtocolException("消息会话不匹配。");
                switch (frame.Type)
                {
                    case PluginIpcType.Invoke when frame.Payload.TryGetProperty("operationId", out var op) &&
                                                   op.GetString() == "report":
                        await HandleReportAsync(frame, writer);
                        break;
                    case PluginIpcType.Invoke:
                        WriteResult(writer, frame.RequestId, "failed", "UNAUTHORIZED", $"未实现的操作。");
                        break;
                    case PluginIpcType.Cancel:
                        // report 是只读瞬时操作，无可取消的工作；如实报告实际结果。
                        WriteResult(writer, frame.RequestId, "success", "CANCELLED", "无可取消的进行中请求。");
                        break;
                    case PluginIpcType.Status:
                        WriteResult(writer, frame.RequestId, "success", "OK", "无进行中请求，无待恢复备份。");
                        break;
                    case PluginIpcType.Stop:
                        lock (Gate) _stopping = true; // 禁止新操作，排空后确认退出
                        WriteResult(writer, frame.RequestId, "success", "OK", "已排空并退出。");
                        return 0;
                    case PluginIpcType.Ping:
                        WriteFrame(writer, PluginIpcType.Pong, _sessionId, frame.RequestId, "{}");
                        break;
                    default:
                        // restore/offline.* 不在本示例能力范围内
                        WriteResult(writer, frame.RequestId, "failed", "OFFLINE_UNSUPPORTED", "只读示例不支持该消息。");
                        break;
                }
            }
            return 0;
        }
        catch (Exception ex) when (ex is PluginProtocolException or OperationCanceledException or JsonException or IOException or InvalidOperationException or DecoderFallbackException)
        {
            Console.Error.WriteLine("协议错误，断开连接：" + ex.Message);
            return 1;
        }
    }

    private static async Task HandleReportAsync(PluginIpcFrame frame, BinaryWriter writer)
    {
        lock (Gate)
            if (_stopping) { WriteResult(writer, frame.RequestId, "failed", "UNAUTHORIZED", "已收到停止请求。"); return; }
        try
        {
            // 验证输入：detail 布尔（后端必须自行校验，不信任宿主）。
            EnsureFields(frame.Payload, "operationId", "values", "target");
            var detail = false;
            if (frame.Payload.TryGetProperty("values", out var values))
            {
                if (values.ValueKind != JsonValueKind.Object) throw new PluginProtocolException("values 必须是对象。");
                EnsureFields(values, "detail");
                if (values.TryGetProperty("detail", out var detailValue))
                {
                    if (detailValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new PluginProtocolException("detail 必须是布尔值。");
                    detail = detailValue.GetBoolean();
                }
            }

            var summary = await Task.Run(() => BuildSummary(detail));
            WriteResult(writer, frame.RequestId, "success", "OK", "报告已生成（仅查询，未修改系统）。",
                items: [new ResultItem("report", "success", summary)]);
        }
        catch (PluginProtocolException ex)
        {
            WriteResult(writer, frame.RequestId, "failed", "PROTOCOL_ERROR", ex.Message);
        }
    }

    /// <summary>只读系统摘要；不做任何修改、不联网。</summary>
    private static string BuildSummary(bool detail)
    {
        var builder = new StringBuilder();
        builder.Append($"系统 {Environment.OSVersion.VersionString}；处理器 {Environment.ProcessorCount} 逻辑核心");
        if (detail)
        {
            builder.Append($"；64 位进程={Environment.Is64BitProcess}；机器名={Environment.MachineName}");
            builder.Append($";.NET {Environment.Version}；运行目录={AppContext.BaseDirectory}");
            builder.Append($";数据目录={_dataDirectory}");
        }
        return builder.ToString();
    }

    // ---- 最小协议实现（与本示例独立，不引用宿主）----

    private sealed record PluginIpcFrame(PluginIpcType Type, Guid SessionId, Guid RequestId, JsonElement Payload);


    private enum PluginIpcType
    {
        Hello, Ready, Invoke, Progress, Result, Cancel, Stop, Restore, Status,
        OfflinePrepare, OfflineCommit, Ping, Pong, Reconnect,
    }

    private sealed record ResultItem(string Id, string Status, string Message);

    private sealed class PluginProtocolException(string message) : Exception(message);

    private static (string Nonce, string HostVersion, string DataDirectory) ParseHello(PluginIpcFrame frame)
    {
        if (frame.Type is not PluginIpcType.Hello) throw new PluginProtocolException("首帧必须是 hello。");
        if (frame.SessionId == Guid.Empty) throw new PluginProtocolException("hello 缺少会话 ID。");
        if (!frame.Payload.TryGetProperty("nonce", out var nonceElement) || nonceElement.GetString() is not { } nonce ||
            nonce.Length == 0)
            throw new PluginProtocolException("hello 缺少 nonce。");
        if (!frame.Payload.TryGetProperty("hostVersion", out var versionElement) ||
            versionElement.GetString() is not { } hostVersion || hostVersion.Length == 0)
            throw new PluginProtocolException("hello 缺少宿主版本。");
        if (!frame.Payload.TryGetProperty("dataDirectory", out var dataElement) ||
            dataElement.GetString() is not { } dataDirectory || !Path.IsPathRooted(dataDirectory))
            throw new PluginProtocolException("hello 缺少有效数据目录。");
        EnsureFields(frame.Payload, "nonce", "hostVersion", "dataDirectory");
        try
        {
            if (nonce.Length != 44 || Convert.FromBase64String(nonce).Length != 32)
                throw new PluginProtocolException("nonce 必须是 32 字节 Base64。");
        }
        catch (FormatException) { throw new PluginProtocolException("nonce 不是有效 Base64。"); }
        if (!Version.TryParse(hostVersion, out var version) || version.Build < 0 || version.Revision != -1)
            throw new PluginProtocolException("宿主版本必须为三段数字。");
        return (nonce, hostVersion, dataDirectory);
    }

    private static async Task<PluginIpcFrame> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaxFrameBytes)
            throw new PluginProtocolException($"帧长度非法：{length}。");
        var payload = new byte[(int)length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        _ = new UTF8Encoding(false, true).GetString(payload);
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object) throw new PluginProtocolException("帧根节点必须是对象。");
        CheckDuplicates(root);
        EnsureFields(root, "protocol", "type", "sessionId", "requestId", "payload");
        if (!root.TryGetProperty("protocol", out var protocol) || protocol.GetString() != ProtocolVersion)
            throw new PluginProtocolException("缺少有效协议版本。");
        string? type = null, sessionId = null, requestId = null;
        JsonElement payloadElement = default;
        foreach (var property in root.EnumerateObject())
            switch (property.Name)
            {
                case "protocol":
                    if (property.Value.GetString() != ProtocolVersion)
                        throw new PluginProtocolException("协议版本不匹配。");
                    break;
                case "type": type = property.Value.GetString(); break;
                case "sessionId": sessionId = property.Value.GetString(); break;
                case "requestId": requestId = property.Value.GetString(); break;
                case "payload": payloadElement = property.Value.Clone(); break;
                default: throw new PluginProtocolException($"未知字段“{property.Name}”。");
            }
        var knownTypes = Enum.GetValues<PluginIpcType>().ToDictionary(t => t switch
        {
            PluginIpcType.OfflinePrepare => "offline.prepare",
            PluginIpcType.OfflineCommit => "offline.commit",
            _ => t.ToString().ToLowerInvariant(),
        });
        if (type is null || !knownTypes.TryGetValue(type, out var parsed))
            throw new PluginProtocolException($"未知消息类型“{type}”。");
        if (!Guid.TryParseExact(sessionId, "D", out var session) || !Guid.TryParseExact(requestId, "D", out var request))
            throw new PluginProtocolException("sessionId/requestId 必须是 UUID。");
        if (session == Guid.Empty || request == Guid.Empty || payloadElement.ValueKind != JsonValueKind.Object)
            throw new PluginProtocolException("会话/请求必须非零且 payload 必须是对象。");
        return new PluginIpcFrame(parsed, session, request, payloadElement);
    }

    private static void EnsureFields(JsonElement value, params string[] fields)
    {
        foreach (var property in value.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal))
                throw new PluginProtocolException("未知字段：" + property.Name);
    }

    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new PluginProtocolException("重复字段：" + property.Name);
                CheckDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

    private static void WriteFrame(BinaryWriter writer, PluginIpcType type, Guid sessionId, string payloadJson) =>
        WriteFrame(writer, type, sessionId, Guid.NewGuid(), payloadJson);

    private static void WriteFrame(BinaryWriter writer, PluginIpcType type, Guid sessionId, Guid requestId, string payloadJson)
    {
        var typeName = type switch
        {
            PluginIpcType.Hello => "hello", PluginIpcType.Ready => "ready", PluginIpcType.Invoke => "invoke",
            PluginIpcType.Progress => "progress", PluginIpcType.Result => "result", PluginIpcType.Cancel => "cancel",
            PluginIpcType.Stop => "stop", PluginIpcType.Restore => "restore", PluginIpcType.Status => "status",
            PluginIpcType.OfflinePrepare => "offline.prepare", PluginIpcType.OfflineCommit => "offline.commit",
            PluginIpcType.Ping => "ping", PluginIpcType.Pong => "pong", PluginIpcType.Reconnect => "reconnect",
            _ => throw new PluginProtocolException("未知消息类型。"),
        };
        var envelope = $$"""
            {"protocol":"{{ProtocolVersion}}","type":"{{typeName}}","sessionId":"{{sessionId:D}}","requestId":"{{requestId:D}}","payload":{{payloadJson}}}
            """.ReplaceLineEndings("");
        var payload = Encoding.UTF8.GetBytes(envelope);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        writer.Write(frame);
        writer.Flush();
    }

    private static void WriteResult(BinaryWriter writer, Guid requestId, string status, string code, string message,
        IReadOnlyList<ResultItem>? items = null)
    {
        var itemsJson = items is null || items.Count == 0
            ? ""
            : string.Join(",", items.Select(i =>
                "{\"id\":\"" + Json(i.Id) + "\",\"status\":\"" + i.Status +
                "\",\"message\":\"" + Json(i.Message) + "\"}"));
        var payloadJson = "{\"status\":\"" + status + "\",\"code\":\"" + code + "\",\"message\":\"" +
            Json(message) + "\",\"items\":[" + itemsJson + "],\"pendingRestore\":false,\"backupIds\":[]}";
        WriteResultFrame(writer, requestId, payloadJson);
    }

    /// <summary>result 帧与请求共用 requestId（规范第 6 节）。</summary>
    private static void WriteResultFrame(BinaryWriter writer, Guid requestId, string payloadJson) =>
        WriteFrame(writer, PluginIpcType.Result, _sessionId, requestId, payloadJson);

    private static string Json(string text) => JsonSerializer.Serialize(text)[1..^1];
}
