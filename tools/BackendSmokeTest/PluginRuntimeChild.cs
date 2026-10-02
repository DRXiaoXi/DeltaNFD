using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace BackendSmokeTest;

/// <summary>
/// 插件运行时端到端检查用的测试后端子进程（真实进程、真实命名管道）。
/// 模式由可执行文件名决定（PluginChild&lt;Mode&gt;.exe）：normal / hang / cancel / crash / wrongready。
/// 只实现规范第 6 节最小协议；不修改系统。由 --plugin-runtime-child 进入。
/// </summary>
internal static class PluginRuntimeChild
{
    private const int MaxFrameBytes = 1024 * 1024;
    private static string _dataDirectory = "";

    public static void Run()
    {
        var mode = ResolveMode();
        string? pipeName = null, sessionText = null, hostPidText = null;
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) return;
            switch (args[i])
            {
                case "--dnfd-pipe": pipeName = args[i + 1]; break;
                case "--dnfd-session": sessionText = args[i + 1]; break;
                case "--dnfd-host-pid": hostPidText = args[i + 1]; break;
                default: return;
            }
        }
        if (pipeName is null || !Guid.TryParseExact(sessionText, "D", out var session) || session == Guid.Empty ||
            !int.TryParse(hostPidText, out var hostPid) || hostPid <= 0)
            return;
        TryServe(mode, pipeName, session, hostPid);
    }

    private static string ResolveMode()
    {
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        const string prefix = "PluginChild";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "normal";
        var mode = name[prefix.Length..].ToLowerInvariant();
        return mode.Length == 0 ? "normal" : mode;
    }

    private static void TryServe(string mode, string pipeName, Guid session, int hostPid)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var host = Process.GetProcessById(hostPid);
            var hostStart = host.StartTime;
            pipe.Connect(5000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != (uint)hostPid ||
                host.HasExited || host.StartTime != hostStart)
                return;
            using var reader = new BinaryReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            using var writer = new BinaryWriter(pipe, new UTF8Encoding(false), leaveOpen: true);

            var hello = ReadFrame(reader);
            if (hello is null || hello.Value.Type != "hello" || hello.Value.SessionId != session) return;
            var nonce = hello.Value.Payload.GetProperty("nonce").GetString() ?? "";
            _dataDirectory = hello.Value.Payload.GetProperty("dataDirectory").GetString() ?? Environment.CurrentDirectory;
            var manifest = ReadManifest();
            if (mode == "wrongready")
            {
                WriteFrame(writer, "ready", session, Guid.NewGuid(),
                    "{\"nonce\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\",\"pluginId\":\"" + manifest.Id +
                    "\",\"pluginVersion\":\"" + manifest.Version + "\",\"protocol\":\"1.0\",\"pid\":" + Environment.ProcessId + "}");
                return;
            }
            if (mode == "forgedsession")
            {
                WriteFrame(writer, "ready", Guid.NewGuid(), hello.Value.RequestId,
                    JsonSerializer.Serialize(new { nonce, pluginId = manifest.Id, pluginVersion = manifest.Version,
                        protocol = "1.0", pid = Environment.ProcessId }));
                Thread.Sleep(10_000);
                return;
            }
            WriteFrame(writer, "ready", session, hello.Value.RequestId,
                "{\"nonce\":\"" + nonce + "\",\"pluginId\":\"" + manifest.Id +
                "\",\"pluginVersion\":\"" + manifest.Version + "\",\"protocol\":\"1.0\",\"pid\":" + Environment.ProcessId + "}");

            while (true)
            {
                var frame = ReadFrame(reader);
                if (frame is null) return;
                if (mode == "garbage")
                {
                    // 对抗模式：每收到一帧，先回一个坏帧（超长长度头），再回正常 result，
                    // 验证宿主在坏帧时断开且不误处理正常响应。
                    var bad = new byte[8];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bad, 0xFFFFFFu);
                    writer.Write(bad);
                    writer.Flush();
                    return;
                }
                if (mode == "repeat" && frame.Value.Type == "invoke")
                {
                    // 对抗模式：对一次请求回两份相同 result（重复请求不得二次生效）；stop 仍正常处理。
                    var opId = frame.Value.Payload.TryGetProperty("operationId", out var op) ? op.GetString() : "report";
                    var dup = "{\"status\":\"success\",\"code\":\"OK\",\"message\":\"重复响应\"," +
                        "\"items\":[],\"pendingRestore\":false,\"backupIds\":[]}";
                    WriteFrame(writer, "result", session, frame.Value.RequestId, dup);
                    WriteFrame(writer, "result", session, frame.Value.RequestId, dup);
                    continue;
                }
                if (mode == "forgedsession")
                {
                    // 对抗模式：ready 用合法 nonce 但伪造 sessionId（不匹配命令行），宿主应拒绝。
                    if (frame.Value.Type == "hello")
                    {
                        _ = frame.Value.Payload.GetProperty("nonce").GetString();
                        _ = ReadManifest();
                        WriteFrame(writer, "result", Guid.NewGuid(), frame.Value.RequestId,
                            "{\"status\":\"success\",\"code\":\"OK\",\"message\":\"伪造\"," +
                            "\"items\":[],\"pendingRestore\":false,\"backupIds\":[]}");
                    }
                    continue;
                }
                switch (frame.Value.Type)
                {
                    case "invoke":
                        var requestId = frame.Value.RequestId;
                        if (mode == "crash") return; // 不响应即退出 → 宿主判定后端崩溃
                        if (mode == "descendant")
                        {
                            using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = Environment.ProcessPath!, Arguments = "--plugin-test-delayed-exit",
                                UseShellExecute = false, CreateNoWindow = true,
                            });
                        }
                        if (mode is "hang" or "cancel" or "cancelbackup" or "cancelwrong") continue; // 不响应 → 宿主超时并 cancel
                        var operationId = frame.Value.Payload.TryGetProperty("operationId", out var opElement)
                            ? opElement.GetString() : "";
                        if (operationId == "tune")
                        {
                            var tuneBackupId = mode == "restorefail" ? "bk-test-2" : "bk-test-1";
                            // 修改操作：声明 backupId 与待恢复标记（宿主应落账备份）。
                            WriteFrame(writer, "result", session, requestId,
                                "{\"status\":\"success\",\"code\":\"OK\",\"message\":\"已修改（测试）\"," +
                                "\"items\":[{\"id\":\"tune\",\"status\":\"success\",\"message\":\"原值=X，已写入 Y\"}]," +
                                "\"pendingRestore\":true,\"backupIds\":[\"" + tuneBackupId + "\"]}");
                        }
                        else if (operationId == "conflict")
                        {
                            WriteFrame(writer, "result", session, requestId,
                                "{\"status\":\"failed\",\"code\":\"TARGET_CHANGED\",\"message\":\"目标已变化\"," +
                                "\"items\":[],\"pendingRestore\":true,\"backupIds\":[\"bk-test-2\"]}");
                        }
                        else
                        {
                            WriteFrame(writer, "result", session, requestId,
                                "{\"status\":\"success\",\"code\":\"OK\",\"message\":\"测试报告（只读）\"," +
                                "\"items\":[{\"id\":\"report\",\"status\":\"success\",\"message\":\"未修改系统\"}]," +
                                "\"pendingRestore\":false,\"backupIds\":[]}");
                        }
                        break;
                    case "restore":
                        var restoreId = frame.Value.Payload.TryGetProperty("backupIds", out var idsElement) &&
                            idsElement.GetArrayLength() > 0 ? idsElement[0].GetString() : "";
                        if (mode == "badrestore")
                        {
                            WriteFrame(writer, "result", session, frame.Value.RequestId,
                                "{\"status\":\"success\",\"code\":\"\",\"message\":\"bad fixture\",\"items\":[],\"pendingRestore\":false,\"backupIds\":[]}");
                            break;
                        }
                        if (mode == "restorefail" && restoreId == "bk-test-2")
                        {
                            WriteFrame(writer, "result", session, frame.Value.RequestId,
                                "{\"status\":\"failed\",\"code\":\"RESTORE_FAILED\",\"message\":\"第三方已改值，不覆盖\"," +
                                "\"items\":[],\"pendingRestore\":true,\"backupIds\":[\"" + restoreId + "\"]}");
                        }
                        else
                        {
                            WriteFrame(writer, "result", session, frame.Value.RequestId,
                                "{\"status\":\"success\",\"code\":\"OK\",\"message\":\"已恢复原值\"," +
                                "\"items\":[],\"pendingRestore\":false,\"backupIds\":[]}");
                        }
                        break;
                    case "cancel":
                        // hang 模式模拟无响应后端：连 cancel 也不回（宿主应判 RESULT_UNKNOWN）。
                        if (mode == "hang") continue;
                        // cancel 感知：对未完成请求回 cancelled（规范：cancel 不能表示已还原）。
                        var referenced = frame.Value.Payload.TryGetProperty("referencedRequestId", out var refElement) &&
                            Guid.TryParse(refElement.GetString(), out var refId) ? refId : Guid.NewGuid();
                        if (mode is "cancelbackup" or "cancelwrong")
                        {
                            WriteFrame(writer, "result", mode == "cancelwrong" ? Guid.NewGuid() : session, referenced,
                                "{\"status\":\"cancelled\",\"code\":\"CANCELLED\",\"message\":\"temporary fixture\",\"items\":[],\"pendingRestore\":true,\"backupIds\":[\"bk-cancel\"]}");
                            break;
                        }
                        WriteFrame(writer, "result", session, referenced,
                            "{\"status\":\"cancelled\",\"code\":\"CANCELLED\",\"message\":\"已取消（未修改系统）\"," +
                            "\"items\":[],\"pendingRestore\":false,\"backupIds\":[]}");
                        break;
                    case "stop":
                        WriteFrame(writer, "result", session, frame.Value.RequestId,
                            "{\"status\":\"success\",\"code\":\"OK\",\"message\":\"已排空并退出。\"," +
                            "\"items\":[],\"pendingRestore\":false,\"backupIds\":[]}");
                        return;
                    case "ping":
                        if (mode == "noheartbeat") continue; // 不回 pong → 宿主心跳失联
                        WriteFrame(writer, "pong", session, frame.Value.RequestId, "{}");
                        break;
                    case "offline.prepare":
                    {
                        // 规范第 8 节：建立随机名重连管道 + 数据目录保存自身状态，返回端点。
                        var reconnectPipe = "DeltaNFD_PluginReconnect_" + Guid.NewGuid().ToString("N");
                        WriteFrame(writer, "result", session, frame.Value.RequestId,
                            "{\"status\":\"success\",\"code\":\"OK\",\"message\":\"prepare 已就绪\"," +
                            "\"items\":[{\"id\":\"reconnectPipeName\",\"status\":\"success\",\"message\":\"" + reconnectPipe + "\"}]," +
                            "\"pendingRestore\":false,\"backupIds\":[]}");
                        // 提交确认后转入自主服务循环（单独线程持管道服务端直到收到 stop）。
                        var preparedRun = frame.Value.Payload.TryGetProperty("runId", out var runElement)
                            ? runElement.GetString() : "";
                        if (!Guid.TryParseExact(preparedRun, "D", out var preparedId) || preparedId == Guid.Empty) return;
                        // 用非后台线程持有自主服务（Main 返回前不退出）；宿主断开后由它维持进程。
                        var reconnectThread = new Thread(() => ServeReconnect(reconnectPipe, preparedId.ToString("D"), mode))
                        { IsBackground = false, Name = "plugin-reconnect" };
                        reconnectThread.Start();
                        break;
                    }
                    case "offline.commit":
                        // 确认自主运行；宿主管道随即断开，由 ServeReconnect 维持。
                        WriteFrame(writer, "result", session, frame.Value.RequestId,
                            "{\"status\":\"success\",\"code\":\"OK\",\"message\":\"已确认自主运行\"," +
                            "\"items\":[],\"pendingRestore\":false,\"backupIds\":[]}");
                        return; // 结束宿主会话；进程不退出（ServeReconnect 持有）
                    default:
                        // 其他消息（status/restore 等）返回失败但保持连接。
                        WriteFrame(writer, "result", session, frame.Value.RequestId,
                            "{\"status\":\"failed\",\"code\":\"OFFLINE_UNSUPPORTED\",\"message\":\"测试后端不支持。\"," +
                            "\"items\":[],\"pendingRestore\":false,\"backupIds\":[]}");
                        break;
                }
            }
        }
        catch
        {
            // 测试后端：任何异常直接退出（宿主应判定为崩溃/断开）。
        }
    }

    private static (string Id, string Version) ReadManifest()
    {
        try
        {
            var path = Path.Combine(Environment.CurrentDirectory, "manifest.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                return (root.GetProperty("id").GetString() ?? "org.example.test",
                        root.GetProperty("version").GetString() ?? "1.0.0");
            }
        }
        catch { }
        return ("org.example.test", "1.0.0");
    }

    private readonly record struct ChildFrame(string Type, Guid SessionId, Guid RequestId, JsonElement Payload);

    private static ChildFrame? ReadFrame(BinaryReader reader)
    {
        var header = reader.ReadBytes(4);
        if (header.Length < 4) return null;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaxFrameBytes) return null;
        var payload = reader.ReadBytes((int)length);
        if (payload.Length < length) return null;
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString() ?? "";
        _ = Guid.TryParseExact(root.GetProperty("sessionId").GetString(), "D", out var session);
        _ = Guid.TryParseExact(root.GetProperty("requestId").GetString(), "D", out var request);
        return new ChildFrame(type, session, request, root.GetProperty("payload").Clone());
    }

    private static void WriteFrame(BinaryWriter writer, string type, Guid session, Guid requestId, string payloadJson)
    {
        var envelope = "{\"protocol\":\"1.0\",\"type\":\"" + type + "\",\"sessionId\":\"" + session.ToString("D") +
            "\",\"requestId\":\"" + requestId.ToString("D") + "\",\"payload\":" + payloadJson + "}";
        var payload = Encoding.UTF8.GetBytes(envelope);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        writer.Write(frame);
        writer.Flush();
    }

    /// <summary>自主期间的重连管道服务端：接受一次连接，等待 stop 后退出（模拟最小自主后端）。</summary>
    private static void ServeReconnect(string pipeName, string runId, string mode)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            while (!cts.IsCancellationRequested)
            {
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            // 数据目录保存自身状态（规范第 8 节）。
            try
            {
                var statePath = Path.Combine(_dataDirectory, pipeName + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                File.WriteAllText(statePath, "{\"runId\":\"" + runId + "\"}");
            }
            catch { }
            server.WaitForConnectionAsync(cts.Token).GetAwaiter().GetResult();
            using var reader = new BinaryReader(server, new UTF8Encoding(false), leaveOpen: true);
            using var writer = new BinaryWriter(server, new UTF8Encoding(false), leaveOpen: true);
            var hello = ReadFrame(reader);
            if (hello is null || hello.Value.Type != "reconnect" || hello.Value.SessionId == Guid.Empty ||
                hello.Value.Payload.GetProperty("runId").GetString() != runId) return;
            var hostPid = hello.Value.Payload.GetProperty("hostPid").GetInt32();
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var clientPid) || clientPid != hostPid) return;
            var manifest = ReadManifest();
            WriteFrame(writer, "ready", hello.Value.SessionId, hello.Value.RequestId,
                JsonSerializer.Serialize(new { nonce = hello.Value.Payload.GetProperty("nonce").GetString(),
                    pluginId = manifest.Id, pluginVersion = manifest.Version, protocol = "1.0", pid = Environment.ProcessId }));
            while (true)
            {
                var frame = ReadFrame(reader);
                if (frame is null) break;
                if (frame.Value.SessionId != hello.Value.SessionId) return;
                if (frame.Value.Type == "restore")
                {
                    var failed = mode == "restorefail";
                    WriteFrame(writer, "result", frame.Value.SessionId, frame.Value.RequestId,
                        JsonSerializer.Serialize(new { status = failed ? "failed" : "success", code = failed ? "RESTORE_FAILED" : "OK",
                            message = "自主恢复测试（没有系统修改）", items = Array.Empty<object>(), pendingRestore = failed, backupIds = Array.Empty<string>() }));
                    continue;
                }
                if (frame.Value.Type == "stop")
                {
                    WriteFrame(writer, "result", frame.Value.SessionId, frame.Value.RequestId,
                        "{\"status\":\"success\",\"code\":\"OK\",\"message\":\"已停止\"," +
                        "\"items\":[],\"pendingRestore\":false,\"backupIds\":[]}");
                    return; // 自主后端正常退出。
                }
            }
            }
        }
        catch
        {
            // 自主测试后端：任何异常直接退出。
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
