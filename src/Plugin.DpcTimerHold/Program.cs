using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// 「DPC 计时器常驻（Win10 研究）」拓展插件后端（规范 1.0）：
// 解析固定启动参数 → 连接宿主随机命名管道 → hello/ready 握手 → 处理
// probe（只读）/ start（修改型、可恢复）/ restore / status / cancel / stop / ping → 断线安全退出。
//
// 本插件不包含、不提取、不直接加载任何驱动；start 只启动玩家自备的原版 DPC 1.4.0 EXE，
// 该程序自行创建并按需启动 dpclat_driver 内核服务并持有 1.0000ms 计时器请求；restore 负责
// 正常关闭该进程并核验服务清理。插件不修改注册表/BCD/证书/启动配置，不结束非本插件启动的进程。

namespace Plugin.DpcTimerHold;

internal static class Program
{
    private const string ProtocolVersion = "1.0";
    private const string PluginId = DpcRules.PluginId;
    private const string PluginVersion = DpcRules.PluginVersion;
    private const int MaxFrameBytes = 1024 * 1024;

    private static readonly object WriteGate = new();
    private static readonly JsonSerializerOptions JsonTextOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static BinaryWriter? _writer;
    private static Guid _sessionId = Guid.Empty;
    private static string _dataDirectory = "";
    private static Task? _activeWork;
    private static Guid _activeRequestId = Guid.Empty;
    private static CancellationTokenSource? _activeCancellation;
    private static volatile bool _stopping;

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

        using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var host = Process.GetProcessById(hostPid);
            var hostStart = host.StartTime;
            await pipe.ConnectAsync(5000, handshake.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != hostPid ||
                host.HasExited || host.StartTime != hostStart)
                throw new PluginProtocolException("管道服务端不是指定的存活宿主。");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("连接宿主管道失败：" + ex.Message);
            return 2;
        }

        _writer = new BinaryWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
        try
        {
            var hello = await ReadFrameAsync(pipe, handshake.Token);
            if (hello.SessionId != expectedSession) throw new PluginProtocolException("hello 会话不匹配。");
            var (nonce, dataDirectory) = ParseHello(hello);
            _sessionId = hello.SessionId;
            _dataDirectory = dataDirectory;
            WriteFrame(_writer, PluginIpcType.Ready, _sessionId, hello.RequestId, $$"""
                {"nonce":"{{nonce}}","pluginId":"{{PluginId}}","pluginVersion":"{{PluginVersion}}","protocol":"{{ProtocolVersion}}","pid":{{Environment.ProcessId}}}
                """.ReplaceLineEndings(""));

            while (pipe.IsConnected)
            {
                PluginIpcFrame frame;
                try { frame = await ReadFrameAsync(pipe, CancellationToken.None); }
                catch (EndOfStreamException) { break; } // 宿主断开：安全退出，不隐式释放外部 DPC
                if (frame.SessionId != _sessionId) throw new PluginProtocolException("消息会话不匹配。");

                switch (frame.Type)
                {
                    case PluginIpcType.Invoke:
                        HandleInvoke(frame);
                        break;
                    case PluginIpcType.Cancel:
                        CancelActive(frame);
                        break;
                    case PluginIpcType.Stop:
                        _stopping = true;
                        await DrainActiveAsync().ConfigureAwait(false);
                        lock (WriteGate)
                            WriteOutcome(_writer, frame.RequestId,
                                new HoldOutcome("success", "OK", "已排空并退出。", [], false, []));
                        return 0;
                    case PluginIpcType.Status:
                        lock (WriteGate)
                            WriteOutcome(_writer, frame.RequestId, DpcNative.CreateController().DescribeStatus(_dataDirectory));
                        break;
                    case PluginIpcType.Restore:
                        HandleRestore(frame);
                        break;
                    case PluginIpcType.Ping:
                        lock (WriteGate)
                            WriteFrame(_writer, PluginIpcType.Pong, _sessionId, frame.RequestId, "{}");
                        break;
                    default:
                        lock (WriteGate)
                            WriteOutcome(_writer, frame.RequestId,
                                new HoldOutcome("failed", "OFFLINE_UNSUPPORTED",
                                    "本插件不支持脱机移交或重连。", [], false, []));
                        break;
                }
            }
            return 0;
        }
        catch (Exception ex) when (ex is PluginProtocolException or OperationCanceledException or JsonException
                                       or IOException or InvalidOperationException or DecoderFallbackException)
        {
            Console.Error.WriteLine("协议错误，断开连接：" + ex.Message);
            return 1;
        }
        finally
        {
            _activeCancellation?.Dispose();
            _writer?.Dispose();
        }
    }

    // ---------------------------------------------------------------- 调用分发

    private static void HandleInvoke(PluginIpcFrame frame)
    {
        if (_stopping)
        {
            lock (WriteGate)
                WriteOutcome(_writer, frame.RequestId,
                    new HoldOutcome("failed", "UNAUTHORIZED", "已收到停止请求，拒绝新操作。", [], false, []));
            return;
        }

        lock (WriteGate)
        {
            if (_activeWork is { IsCompleted: false })
            {
                WriteOutcome(_writer, frame.RequestId,
                    new HoldOutcome("failed", "BUSY", "当前已有请求在执行，未排队。", [], false, []));
                return;
            }

            var operationId = ParseInvoke(frame.Payload, out var exePath, out var risk, out var inputError);
            if (operationId is null)
            {
                WriteOutcome(_writer, frame.RequestId,
                    new HoldOutcome("failed", "PROTOCOL_ERROR", inputError, [], false, []));
                return;
            }

            _activeRequestId = frame.RequestId;
            _activeCancellation = new CancellationTokenSource();
            var token = _activeCancellation.Token;
            var requestId = frame.RequestId;
            _activeWork = Task.Run(() =>
            {
                HoldOutcome outcome;
                try
                {
                    var controller = DpcNative.CreateController();
                    outcome = operationId switch
                    {
                        "probe" => controller.Probe(exePath, _dataDirectory),
                        "start" => controller.Start(exePath, risk, _dataDirectory, token, ReportProgress),
                        _ => new HoldOutcome("failed", "UNAUTHORIZED", $"未实现的操作“{operationId}”。", [], false, []),
                    };
                }
                catch (OperationCanceledException)
                {
                    outcome = new HoldOutcome("cancelled", "CANCELLED", "操作已取消。", [], true, []);
                }
                catch (Exception ex)
                {
                    outcome = new HoldOutcome("failed", "RESULT_UNKNOWN",
                        "执行异常，结果未知：" + ex.GetType().Name + " —— " + ex.Message, [], true, []);
                }

                lock (WriteGate)
                {
                    if (_activeRequestId == requestId)
                    {
                        WriteOutcome(_writer, requestId, outcome);
                        _activeRequestId = Guid.Empty;
                    }
                }
            });
        }
    }

    private static void CancelActive(PluginIpcFrame frame)
    {
        if (!frame.Payload.TryGetProperty("referencedRequestId", out var referenced) ||
            referenced.ValueKind is not JsonValueKind.String ||
            !Guid.TryParseExact(referenced.GetString(), "D", out var target) || target == Guid.Empty)
            return;
        lock (WriteGate)
            if (_activeRequestId == target) _activeCancellation?.Cancel();
    }

    private static async Task DrainActiveAsync()
    {
        Task? work;
        lock (WriteGate)
        {
            work = _activeWork;
            _activeCancellation?.Cancel();
        }
        if (work is null || work.IsCompleted) return;
        try { await work.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
    }

    /// <summary>解析 invoke 的 operationId、values（exePath/risk）与 target；失败返回 null 并给出原因。</summary>
    private static string? ParseInvoke(JsonElement payload, out string exePath, out bool risk, out string error)
    {
        exePath = "";
        risk = false;
        error = "";
        try
        {
            EnsureFields(payload, "operationId", "values", "target");
            if (!payload.TryGetProperty("operationId", out var operationElement) ||
                operationElement.ValueKind is not JsonValueKind.String)
                throw new PluginProtocolException("invoke 缺少 operationId。");
            var operationId = operationElement.GetString()!;

            if (payload.TryGetProperty("target", out var target) && target.ValueKind is not JsonValueKind.Null)
                throw new PluginProtocolException("本插件不接受目标参数。");

            if (payload.TryGetProperty("values", out var values) && values.ValueKind is not JsonValueKind.Null)
            {
                if (values.ValueKind is not JsonValueKind.Object) throw new PluginProtocolException("values 必须是对象。");
                EnsureFields(values, "exePath", "risk");
                if (values.TryGetProperty("exePath", out var pathElement))
                {
                    if (pathElement.ValueKind is not JsonValueKind.String)
                        throw new PluginProtocolException("exePath 必须是字符串。");
                    exePath = pathElement.GetString() ?? "";
                    if (exePath.Length > 4096) throw new PluginProtocolException("exePath 超过 4096 字符上限。");
                }
                if (values.TryGetProperty("risk", out var riskElement))
                {
                    if (riskElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new PluginProtocolException("risk 必须是布尔值。");
                    risk = riskElement.GetBoolean();
                }
            }

            if (operationId is not ("probe" or "start"))
                throw new PluginProtocolException("未声明的操作：" + operationId);
            return operationId;
        }
        catch (PluginProtocolException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // ---------------------------------------------------------------- restore

    private static void HandleRestore(PluginIpcFrame frame)
    {
        HoldOutcome outcome;
        try
        {
            if (!frame.Payload.TryGetProperty("backupIds", out var ids) || ids.ValueKind is not JsonValueKind.Array)
                throw new PluginProtocolException("restore 缺少 backupIds 数组。");

            var controller = DpcNative.CreateController();
            var items = new List<ProbeItem>();
            var allOk = true;
            var pending = false;
            var validIds = new List<string>();
            foreach (var element in ids.EnumerateArray())
            {
                if (element.ValueKind is not JsonValueKind.String || element.GetString() is not { Length: > 0 } id)
                {
                    items.Add(new ProbeItem("invalid", "failed", "backupIds 项必须是非空字符串。"));
                    allOk = false;
                    continue;
                }
                var result = controller.Restore(id, _dataDirectory, CancellationToken.None, ReportProgress);
                validIds.Add(id);
                items.AddRange(result.Items);
                items.Add(new ProbeItem(id, result.Status == "success" ? "success" : "failed",
                    result.Code + "：" + result.Message));
                if (result.Status != "success") allOk = false;
                pending |= result.PendingRestore;
            }

            outcome = allOk && !pending
                ? new HoldOutcome("success", "OK", "释放已完成，本次启动的 DPC 已正常关闭并核验服务清理。",
                    items, false, [])
                : new HoldOutcome("failed", "RESTORE_FAILED", "释放未全部成功，记录与证据保留，可重试。",
                    items, true, validIds);
        }
        catch (PluginProtocolException ex)
        {
            outcome = new HoldOutcome("failed", "PROTOCOL_ERROR", ex.Message, [], true, []);
        }
        catch (Exception ex)
        {
            outcome = new HoldOutcome("failed", "RESULT_UNKNOWN",
                "释放异常，结果未知：" + ex.GetType().Name + " —— " + ex.Message, [], true, []);
        }

        lock (WriteGate) WriteOutcome(_writer, frame.RequestId, outcome);
    }

    private static void ReportProgress(int percent, string message)
    {
        lock (WriteGate)
        {
            if (_writer is null || _activeRequestId == Guid.Empty) return;
            WriteFrame(_writer, PluginIpcType.Progress, _sessionId, _activeRequestId,
                "{\"percent\":" + percent + ",\"message\":\"" + JsonString(message) + "\"}");
        }
    }

    // ---------------------------------------------------------------- 结果与帧

    private static void WriteOutcome(BinaryWriter? writer, Guid requestId, HoldOutcome outcome)
    {
        if (writer is null) return;
        var items = string.Join(",", outcome.Items.Select(i =>
            "{\"id\":\"" + JsonString(i.Id) + "\",\"status\":\"" + i.Status +
            "\",\"message\":\"" + JsonString(i.Message) + "\"}"));
        var backups = string.Join(",", outcome.BackupIds.Select(b => "\"" + JsonString(b) + "\""));
        var payload = "{\"status\":\"" + outcome.Status + "\",\"code\":\"" + outcome.Code +
            "\",\"message\":\"" + JsonString(outcome.Message) + "\",\"items\":[" + items +
            "],\"pendingRestore\":" + (outcome.PendingRestore ? "true" : "false") +
            ",\"backupIds\":[" + backups + "]}";
        WriteFrame(writer, PluginIpcType.Result, _sessionId, requestId, payload);
    }

    private static string JsonString(string text) => JsonSerializer.Serialize(text, JsonTextOptions)[1..^1];

    // ---- 最小协议实现（与本插件独立，不引用宿主）----

    private sealed record PluginIpcFrame(PluginIpcType Type, Guid SessionId, Guid RequestId, JsonElement Payload);

    private enum PluginIpcType
    {
        Hello, Ready, Invoke, Progress, Result, Cancel, Stop, Restore, Status,
        OfflinePrepare, OfflineCommit, Ping, Pong, Reconnect,
    }

    private sealed class PluginProtocolException(string message) : Exception(message);

    private static (string Nonce, string DataDirectory) ParseHello(PluginIpcFrame frame)
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
        return (nonce, dataDirectory);
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

    private static void WriteFrame(BinaryWriter? writer, PluginIpcType type, Guid sessionId, string payloadJson) =>
        WriteFrame(writer, type, sessionId, Guid.NewGuid(), payloadJson);

    private static void WriteFrame(BinaryWriter? writer, PluginIpcType type, Guid sessionId, Guid requestId, string payloadJson)
    {
        if (writer is null) return;
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
}
