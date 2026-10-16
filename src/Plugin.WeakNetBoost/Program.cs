using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// 「弱网加速」拓展插件后端（规范 1.0）：
// 解析固定启动参数 → 连接宿主随机命名管道 → hello/ready 握手 → 处理
// apply（修改型、可恢复）/ probe（只读）/ restore / status / cancel / stop / ping → 断线安全退出。
//
// 配置的生成与可恢复改写全部在 WeakNetBoostProfile（纯 BCL，无环境依赖）里实现，
// 便于宿主冒烟测试链接同一份源码验证；本文件只负责 IPC、路径解析与联网探测。
// 不引用宿主内部程序集，不请求 continuous / offlineAutonomous，不安装服务或自启动项。

namespace Plugin.WeakNetBoost;

internal static class Program
{
    private const string ProtocolVersion = "1.0";
    private const string PluginId = WeakNetBoostProfile.PluginId;
    private const string PluginVersion = "1.0.0";
    private const int MaxFrameBytes = 1024 * 1024;

    private static readonly object WriteGate = new();

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
                catch (EndOfStreamException) { break; } // 宿主断开：安全退出，不依赖宿主心跳
                if (frame.SessionId != _sessionId) throw new PluginProtocolException("消息会话不匹配。");

                switch (frame.Type)
                {
                    case PluginIpcType.Invoke:
                        HandleInvoke(frame);
                        break;
                    case PluginIpcType.Cancel:
                        // 宿主超时后读的是“原请求 requestId 的 result”，所以这里只转发取消，
                        // 由正在执行的任务自己用原 requestId 回报实际结果（规范第 6 节）。
                        CancelActive(frame);
                        break;
                    case PluginIpcType.Stop:
                        _stopping = true;
                        await DrainActiveAsync().ConfigureAwait(false);
                        lock (WriteGate)
                            WriteResult(_writer, frame.RequestId, Outcome.Simple("success", "OK", "已排空并退出。"));
                        return 0;
                    case PluginIpcType.Status:
                        lock (WriteGate)
                            WriteResult(_writer, frame.RequestId, DescribeStatus());
                        break;
                    case PluginIpcType.Restore:
                        // 文件级幂等还原，同步处理即可（不联网），回复同一 requestId 的 result。
                        HandleRestore(frame);
                        break;
                    case PluginIpcType.Ping:
                        lock (WriteGate)
                            WriteFrame(_writer, PluginIpcType.Pong, _sessionId, frame.RequestId, "{}");
                        break;
                    default:
                        // 未声明 continuous / offlineAutonomous，不接受移交与重连。
                        lock (WriteGate)
                            WriteResult(_writer, frame.RequestId,
                                Outcome.Simple("failed", "OFFLINE_UNSUPPORTED", "本插件不支持脱机移交或重连。"));
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
                WriteResult(_writer, frame.RequestId,
                    Outcome.Simple("failed", "UNAUTHORIZED", "已收到停止请求，拒绝新操作。"));
            return;
        }

        lock (WriteGate)
        {
            // 每插件同时只允许一个在途请求：忙时返回 BUSY，不排队（规范第 6 节）。
            if (_activeWork is { IsCompleted: false })
            {
                WriteResult(_writer, frame.RequestId, Outcome.Simple("failed", "BUSY", "当前已有请求在执行，未排队。"));
                return;
            }

            var operationId = ParseInvoke(frame.Payload, out var enabled, out var inputError);
            if (operationId is null)
            {
                WriteResult(_writer, frame.RequestId, Outcome.Simple("failed", "PROTOCOL_ERROR", inputError));
                return;
            }

            _activeRequestId = frame.RequestId;
            _activeCancellation = new CancellationTokenSource();
            var token = _activeCancellation.Token;
            var requestId = frame.RequestId;
            _activeWork = Task.Run(async () =>
            {
                Outcome outcome;
                try
                {
                    outcome = operationId switch
                    {
                        "apply" => Apply(opEnabled: enabled),
                        "probe" => await ProbeAsync(token).ConfigureAwait(false),
                        _ => Outcome.Simple("failed", "UNAUTHORIZED", $"未实现的操作“{operationId}”。"),
                    };
                }
                catch (Exception ex)
                {
                    outcome = Outcome.Simple("failed", "RESULT_UNKNOWN",
                        "执行异常，结果未知：" + ex.GetType().Name + " —— " + ex.Message);
                }

                lock (WriteGate)
                {
                    if (_activeRequestId == requestId)
                    {
                        WriteResult(_writer, requestId, outcome);
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
        {
            if (_activeRequestId == target) _activeCancellation?.Cancel();
        }
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

    /// <summary>解析 invoke 的 operationId、values 与 target；失败返回 null 并给出原因。</summary>
    private static string? ParseInvoke(JsonElement payload, out bool enabled, out string error)
    {
        enabled = false;
        error = "";
        try
        {
            EnsureFields(payload, "operationId", "values", "target");
            if (!payload.TryGetProperty("operationId", out var operationElement) ||
                operationElement.ValueKind is not JsonValueKind.String)
                throw new PluginProtocolException("invoke 缺少 operationId。");
            var operationId = operationElement.GetString()!;

            // 两个操作都不是 targetScoped：宿主发 null；出现对象说明目标语义不符。
            if (payload.TryGetProperty("target", out var target) && target.ValueKind is not JsonValueKind.Null)
                throw new PluginProtocolException("本插件不接受目标参数。");

            var hasEnabled = false;
            if (payload.TryGetProperty("values", out var values) && values.ValueKind is not JsonValueKind.Null)
            {
                if (values.ValueKind is not JsonValueKind.Object) throw new PluginProtocolException("values 必须是对象。");
                EnsureFields(values, "enabled");
                if (values.TryGetProperty("enabled", out var enabledValue))
                {
                    if (enabledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new PluginProtocolException("enabled 必须是布尔值。");
                    enabled = enabledValue.GetBoolean();
                    hasEnabled = true;
                }
            }

            // 不信任宿主：apply 必须显式给出开关值。
            if (operationId == "apply" && !hasEnabled)
                throw new PluginProtocolException("apply 缺少 enabled 布尔输入。");
            return operationId;
        }
        catch (PluginProtocolException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // ---------------------------------------------------------------- apply：开启 / 关闭

    private static Outcome Apply(bool opEnabled)
    {
        var configPath = ConfigPath();
        // 每次调用都用全新的 recovery 标识：宿主拒绝重复 backupId（规范第 7 节）。
        var backupId = WeakNetBoostProfile.NewBackupId();
        ReportProgress(20, opEnabled ? "应用弱网加速设置" : "还原更新源配置");

        var result = opEnabled
            ? WeakNetBoostProfile.Apply(configPath, _dataDirectory, backupId)
            : WeakNetBoostProfile.Restore(configPath, _dataDirectory, backupId);

        ReportProgress(100, "完成");
        var code = result.Ok ? "OK" : result.Kind switch
        {
            WeakNetBoostProfile.ChangeKind.Conflict => "RESTORE_FAILED",
            WeakNetBoostProfile.ChangeKind.MissingEvidence => "RESTORE_FAILED",
            WeakNetBoostProfile.ChangeKind.UnsafeTarget => "RESTORE_FAILED",
            _ => "RESULT_UNKNOWN",
        };
        return new Outcome(result.Ok ? "success" : "failed", code, result.Message,
            [new ResultItem("update-source", result.Ok ? "success" : "failed", result.Message)],
            PendingRestore: true, BackupIds: [backupId]);
    }

    // ---------------------------------------------------------------- restore：宿主发起的幂等还原

    private static void HandleRestore(PluginIpcFrame frame)
    {
        Outcome outcome;
        try
        {
            if (!frame.Payload.TryGetProperty("backupIds", out var ids) || ids.ValueKind is not JsonValueKind.Array)
                throw new PluginProtocolException("restore 缺少 backupIds 数组。");

            var configPath = ConfigPath();
            var items = new List<ResultItem>();
            var allOk = true;
            foreach (var element in ids.EnumerateArray())
            {
                if (element.ValueKind is not JsonValueKind.String || element.GetString() is not { Length: > 0 } id)
                {
                    items.Add(new ResultItem("invalid", "failed", "backupIds 项必须是非空字符串。"));
                    allOk = false;
                    continue;
                }

                if (!id.StartsWith(WeakNetBoostProfile.BackupIdPrefix, StringComparison.Ordinal))
                {
                    items.Add(new ResultItem(id, "failed", "不是本插件产生的恢复标识，不能代它还原。"));
                    allOk = false;
                    continue;
                }

                var result = WeakNetBoostProfile.Restore(configPath, _dataDirectory);
                items.Add(new ResultItem(id, result.Ok ? "success" : "failed", result.Message));
                allOk &= result.Ok;
            }

            // 宿主只在 status=success、pendingRestore=false 且每项都 success 时才清除待恢复标记。
            outcome = allOk
                ? new Outcome("success", "OK", "恢复已完成，更新源已回到本插件改写前的状态。", items, false, [])
                : new Outcome("failed", "RESTORE_FAILED", "恢复未全部成功，证据与待恢复标记保留。", items, false, []);
        }
        catch (PluginProtocolException ex)
        {
            outcome = new Outcome("failed", "PROTOCOL_ERROR", ex.Message, [], true, []);
        }

        lock (WriteGate) WriteResult(_writer, frame.RequestId, outcome);
    }

    private static Outcome DescribeStatus()
    {
        var text = WeakNetBoostProfile.DescribeState(ConfigPath(), _dataDirectory);
        return new Outcome("success", "OK", text, [new ResultItem("status", "success", text)],
            WeakNetBoostProfile.IsApplied(ConfigPath(), _dataDirectory), []);
    }

    // ---------------------------------------------------------------- probe：只读状态检查

    private static async Task<Outcome> ProbeAsync(CancellationToken cancellationToken)
    {
        var items = new List<ResultItem>();
        var configPath = ConfigPath();
        var applied = WeakNetBoostProfile.IsApplied(configPath, _dataDirectory);
        var exists = File.Exists(configPath);
        var backupNote = File.Exists(WeakNetBoostProfile.BackupPathFor(_dataDirectory))
            ? "原文件已备份"
            : "开启前该文件不存在";
        var localDetail = applied
            ? $"本插件已改写更新源：{configPath}；{backupNote}。"
            : $"当前未由本插件改写更新源：{configPath}；" +
              (exists ? "文件存在，使用的是用户自己的配置。" : "文件不存在，走程序内置默认源。");
        items.Add(new ResultItem("local", applied ? "success" : "partial", localDetail));

        ReportProgress(25, "探测 gh-proxy 加速节点");
        var manifestUrl = WeakNetBoostProfile.AcceleratorPrefix + WeakNetBoostProfile.RawManifestUrl;
        var (rawOk, rawDetail, rawBody) = await TryGetAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
        items.Add(new ResultItem("node", rawOk ? "success" : "failed",
            rawOk ? $"加速节点可达：{rawDetail}" : $"加速节点不可达：{rawDetail}"));

        if (rawOk && TryParseManifest(rawBody, out var version, out var installerUrl))
        {
            var accelerated = WeakNetBoostProfile.IsAcceleratedUrl(installerUrl);
            items.Add(new ResultItem("manifest", accelerated ? "success" : "partial",
                accelerated
                    ? $"加速清单版本 {version}，下载地址经加速：{WeakNetBoostProfile.DescribeUrlHost(installerUrl)}。"
                    : $"加速清单版本 {version}，但下载地址仍是直连 {WeakNetBoostProfile.DescribeUrlHost(installerUrl)}（发布端未把镜像条目排在首位，下载不会加速）。"));

            ReportProgress(65, "核对线上最新版本");
            var apiUrl = WeakNetBoostProfile.AcceleratorPrefix + WeakNetBoostProfile.ReleasesApiUrl;
            var (apiOk, apiDetail, apiBody) = await TryGetAsync(apiUrl, cancellationToken).ConfigureAwait(false);
            if (apiOk && TryParseLatestReleaseVersion(apiBody, out var liveVersion))
            {
                items.Add(CompareVersions(version, liveVersion) < 0
                    ? new ResultItem("freshness", "partial",
                        $"加速清单落后：清单最高 {version}，线上最新 {liveVersion}；新版可能因此看不到，需要发布端重新生成清单。")
                    : new ResultItem("freshness", "success", $"清单与线上一致（最高 {liveVersion}）。"));
            }
            else
            {
                items.Add(new ResultItem("freshness", "partial", "无法读取线上发布列表，未能核对清单新旧：" + apiDetail));
            }
        }
        else if (rawOk)
        {
            items.Add(new ResultItem("manifest", "failed", "加速节点返回的内容不是可解析的更新清单。"));
        }

        ReportProgress(100, "完成");
        var anyFailed = items.Any(i => i.Status == "failed");
        var anyPartial = items.Any(i => i.Status == "partial");
        var status = anyFailed && !rawOk ? "failed" : anyPartial ? "partial" : "success";
        // 只读操作不产生待恢复项；已开启与否写在 items 文案里，不借 pendingRestore 表达状态。
        return new Outcome(status, rawOk ? "OK" : "NETWORK_ERROR",
            $"状态检查完成：{items.Count} 项，其中提示 {items.Count(i => i.Status != "success")} 项。", items, false, []);
    }

    private static async Task<(bool Ok, string Detail, string Body)> TryGetAsync(string url, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(6) })
            {
                Timeout = TimeSpan.FromSeconds(10),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"DeltaNFD-WeakNetBoost/{PluginVersion}");
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return (response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}，{stopwatch.ElapsedMilliseconds} ms", body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return (false, $"{ex.GetType().Name}（{stopwatch.ElapsedMilliseconds} ms）：{ex.Message}", "");
        }
    }

    private static string ConfigPath() => WeakNetBoostProfile.ConfigPathFor(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>从静态清单里取版本号与首个下载地址（宿主按 installers[] 顺序取首个可用项）。</summary>
    internal static bool TryParseManifest(string json, out string version, out string installerUrl)
    {
        version = "";
        installerUrl = "";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object) return false;
            if (!root.TryGetProperty("version", out var versionElement) || versionElement.ValueKind is not JsonValueKind.String)
                return false;
            version = versionElement.GetString()!;
            if (!root.TryGetProperty("installers", out var installers) || installers.ValueKind is not JsonValueKind.Array)
                return false;
            foreach (var installer in installers.EnumerateArray())
                if (installer.ValueKind is JsonValueKind.Object &&
                    installer.TryGetProperty("url", out var urlElement) && urlElement.ValueKind is JsonValueKind.String)
                {
                    installerUrl = urlElement.GetString()!;
                    break;
                }
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>从 GitHub Release 列表里取最高的非草稿版本号。</summary>
    internal static bool TryParseLatestReleaseVersion(string json, out string version)
    {
        version = "";
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not JsonValueKind.Array) return false;
            Version? highest = null;
            var highestText = "";
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind is not JsonValueKind.Object) continue;
                if (release.TryGetProperty("draft", out var draft) && draft.ValueKind is JsonValueKind.True) continue;
                if (!release.TryGetProperty("tag_name", out var tag) || tag.ValueKind is not JsonValueKind.String) continue;
                if (!TryParseVersion(tag.GetString(), out var candidate)) continue;
                if (highest is null || CompareVersionValues(candidate, highest) > 0)
                {
                    highest = candidate;
                    highestText = tag.GetString() ?? "";
                }
            }
            if (highest is null) return false;
            version = highestText;
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var buffer = new StringBuilder();
        foreach (var ch in text.Trim())
            if (char.IsDigit(ch)) buffer.Append(ch);
            else if (ch == '.' && buffer.Length > 0 && buffer[^1] != '.') buffer.Append('.');
            else if (char.IsLetter(ch) && buffer.Length > 0 && buffer[^1] != '.') buffer.Append('.');
        var parts = buffer.ToString().Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        if (!Version.TryParse(string.Join('.', parts.Take(3)) + (parts.Length == 1 ? ".0" : ""), out var parsed) || parsed is null)
            return false;
        version = parsed;
        return true;
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private static int CompareVersionValues(Version a, Version b) =>
        NormalizeVersion(a).CompareTo(NormalizeVersion(b));

    private static int CompareVersions(string a, string b) =>
        TryParseVersion(a, out var left) && TryParseVersion(b, out var right)
            ? CompareVersionValues(left, right)
            : 0;

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

    private sealed record Outcome(string Status, string Code, string Message,
        IReadOnlyList<ResultItem> Items, bool PendingRestore, IReadOnlyList<string> BackupIds)
    {
        public static Outcome Simple(string status, string code, string message) =>
            new(status, code, message, [], false, []);
    }

    private sealed record ResultItem(string Id, string Status, string Message);

    private static void WriteResult(BinaryWriter? writer, Guid requestId, Outcome outcome)
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

    /// <summary>转义 JSON 字符串内容（不含外层引号）。保留中文可读，仅转义引号/反斜杠/控制字符。</summary>
    private static readonly JsonSerializerOptions JsonTextOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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
