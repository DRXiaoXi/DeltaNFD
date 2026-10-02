using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeltaNFD.Services.Plugins;

/// <summary>一次操作调用的结果（规范第 6 节 result 的宿主侧视图）。</summary>
public sealed record PluginInvokeResult(bool Succeeded, string Status, string Code, string Message,
    IReadOnlyList<PluginIpcResultItem> Items, bool PendingRestore, IReadOnlyList<string> BackupIds)
{
    public static PluginInvokeResult ProtocolError(string message) =>
        new(false, "failed", "PROTOCOL_ERROR", message, [], false, []);
    public static PluginInvokeResult BackendCrashed(string message) =>
        new(false, "failed", "BACKEND_CRASHED", message, [], false, []);
    public static PluginInvokeResult ResultUnknown(string message) =>
        new(false, "failed", "RESULT_UNKNOWN", message, [], false, []);
}

/// <summary>
/// 插件运行时（规范第 5、6 节）：显式启动后端入口（不搜索 PATH、不经 shell）、
/// 身份核对（PID+启动时间+规范路径）、每插件至多一个后端、每会话至多一个变更操作在途
/// （BUSY，不排队）、命令超时先 cancel 等待 5 秒、心跳 ping/pong、stop 排空 10 秒、
/// 崩溃不自动重启。普通模式按操作启动、完成后停止。
/// </summary>
public sealed class PluginRuntimeService
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatLostAfter = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly Dictionary<string, RunningBackend> _running = new(StringComparer.Ordinal);
    private readonly HashSet<string> _starting = new(StringComparer.Ordinal);
    private readonly HashSet<string> _recovering = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private int _handoverDepth;
    private readonly string _dataRoot;
    private readonly PluginBackupStore _backups;
    private readonly PluginHandoffStore _handoffs;
    private readonly PluginAuthorizationStore _authorizations;
    private readonly PluginRunStore _runs;
    public PluginRunStore Runs => _runs;
    public PluginAuthorizationStore Authorizations => _authorizations;

    /// <summary>backups 缺省为 null：无仓库时不落账（冒烟工程/测试用）；主程序经 PluginManagerService 注入。</summary>
    public PluginRuntimeService(PluginBackupStore? backups = null, PluginHandoffStore? handoffs = null, string? dataRoot = null, PluginAuthorizationStore? authorizations = null)
    {
        var defaultDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Delta NFD", "PluginData");
        var backupDirectory = backups is null ? (handoffs is null ? null : Path.GetDirectoryName(handoffs.PathName)) : Path.GetDirectoryName(backups.PathName);
        _dataRoot = Path.GetFullPath(dataRoot ?? (backupDirectory is null ? defaultDataRoot :
            Path.GetFileName(backupDirectory) == "backups" ? Path.GetDirectoryName(backupDirectory)! : backupDirectory));
        _backups = backups ?? new PluginBackupStore(Path.Combine(_dataRoot, "backups", "backups.json"));
        _handoffs = handoffs ?? new PluginHandoffStore(Path.Combine(_dataRoot, "offline-handoff.json"));
        _runs = new PluginRunStore(Path.Combine(_dataRoot, "managed-runs.json"));
        _authorizations = authorizations ?? (_dataRoot.Equals(defaultDataRoot, StringComparison.OrdinalIgnoreCase)
            ? PluginAuthorizationStore.ForCurrentUser() : new PluginAuthorizationStore(Path.Combine(_dataRoot, "authorizations.json")));
    }

    /// <summary>备份仓库：invoke 返回 backupIds 且操作为 mutating 时记录备份元数据。</summary>
    public PluginBackupStore Backups => _backups;

    private sealed record RunningBackend(
        Process Process, PluginBackendSession Session, Guid SessionId,
        string PluginId, string PluginVersion, long StartTimeTicks, string EntryPath,
        CancellationTokenSource LoopCancellation, Task LoopTask, System.Threading.Timer? Heartbeat,
        PluginJobHandle? Job, PluginPackageLease PackageLease)
    {
        /// <summary>最近一次 pong 到达时间；心跳定时器据此判定失联（15 秒）。</summary>
        public DateTimeOffset LastPongUtc { get; set; } = DateTimeOffset.UtcNow;
        /// <summary>失联标记：置位后界面提示人工处理，不自动强杀（规范第 6 节）。</summary>
        public bool LostContact { get; set; }
        /// <summary>continuous 驻留会话标记（普通按操作模式 false）。</summary>
        public bool Persistent { get; init; }
        public Guid PendingPing { get; set; }
        public string StopDetail { get; set; } = "";
    }

    public bool IsRunning(string pluginId)
    {
        lock (_gate) if (_running.ContainsKey(pluginId) || _starting.Contains(pluginId)) return true;
        return !_runs.TryList(out var records, out _) || records.Any(r => r.PluginId == pluginId);
    }

    public IDisposable BlockNewOperations()
    {
        lock (_gate) _handoverDepth++;
        return new HandoverLease(this);
    }
    private sealed class HandoverLease(PluginRuntimeService owner) : IDisposable
    {
        private PluginRuntimeService? _owner = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _owner, null);
            if (value is not null) lock (value._gate) value._handoverDepth--;
        }
    }
    public bool HasInFlightOperations { get { lock (_gate) return _recovering.Count > 0 || _starting.Count > 0 || _running.Values.Any(b => !b.Persistent); } }

    public IReadOnlyList<string> RunningPluginIds
    {
        get
        {
            if (!_runs.TryList(out var records, out _)) return ["<unreadable-managed-runs>"];
            lock (_gate) return _running.Keys.Concat(_starting).Concat(records.Select(r => r.PluginId)).Distinct().ToList();
        }
    }

    /// <summary>
    /// 按操作启动后端并执行一次操作，完成后停止（规范第 5 节普通模式默认路径）。
    /// 运行前复核索引条目（授权 + 安装目录入口存在且未越界）。
    /// </summary>
    public async Task<PluginInvokeResult> InvokeOnceAsync(PluginIndexEntry entry, string operationId,
        IReadOnlyDictionary<string, object?> values, string? targetJson = null)
    {
        using var diagnostics = PluginDiagnostics.UseDirectory(Path.Combine(_dataRoot, "diagnostics"));
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.State != PluginPackageState.Authorized)
        {
            PluginDiagnostics.Record("invoke", "blocked", pluginId: entry.Id, operationId: operationId, errorCode: "UNAUTHORIZED");
            return PluginInvokeResult.ProtocolError("插件未授权，拒绝运行。");
        }

        var entryPath = entry.EntryExecutable;
        if (!File.Exists(entryPath) || !PluginPath.IsWithinRoot(entry.InstallDirectory, entryPath))
            return PluginInvokeResult.ProtocolError("插件入口不存在或越界，拒绝运行。");

        var declared = await ReadManifestAsync(entry);
        var requested = declared?.FindOperation(operationId);
        if (declared is null || declared.Id != entry.Id || declared.Version != entry.Version || requested is null)
            return PluginInvokeResult.ProtocolError("清单身份或操作声明与索引不符，未启动后端。");
        using var targetLease = requested.TargetScoped ? GameTargetService.Default.BeginOperation() : null;
        if (requested.TargetScoped && targetJson is null)
        {
            var captured = CaptureSingleTarget();
            if (captured is null) return new(false, "failed", "TARGET_CHANGED", "未找到可确认的单实例目标；多实例暂拒绝，不猜测目标。", [], false, []);
            targetJson = JsonSerializer.Serialize(new { generation = captured.Generation, executablePath = captured.ExecutablePath,
                pid = captured.Pid, startTimeUtcTicks = captured.StartTimeUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        RunningBackend? backend = null;
        var stopAttempted = false;
        try
        {
            try
            {
                backend = await StartAsync(entry);
            }
            catch (InvalidOperationException ex)
            {
                // 规范第 6 节：每插件至多一个变更请求在途，忙时返回 BUSY，不排队。
                return new PluginInvokeResult(false, "failed", "BUSY", ex.Message, [], false, []);
            }
            if (backend is null)
                return PluginInvokeResult.ProtocolError("插件后端启动或握手失败（详见插件页状态）。");

            var result = await InvokeCoreAsync(backend, operationId, values, entry, targetJson);
            stopAttempted = true;
            if (!await StopAsync(backend.PluginId))
            {
                PluginDiagnostics.Record("invoke-completion", "unknown", pluginId: entry.Id, operationId: operationId,
                    sessionId: backend.SessionId.ToString("D"), errorCode: "STOP_UNCONFIRMED");
                return result with { Succeeded = false, Code = "STOP_UNCONFIRMED", Message = result.Message + " 后端停止未确认，运行记录保留。" };
            }
            return result;
        }
        finally
        {
            if (backend is not null && !stopAttempted) await StopAsync(backend.PluginId);
        }
    }

    /// <summary>启动后端并完成 hello/ready 握手；失败返回 null（进程被清理）。</summary>
    private async Task<RunningBackend?> StartAsync(PluginIndexEntry entry, bool recovery = false)
    {
        if (!_runs.TryList(out var oldRuns, out var runError)) throw new InvalidOperationException(runError);
        if (oldRuns.Any(r => r.PluginId == entry.Id))
            throw new InvalidOperationException("插件存在旧运行记录，无法确认退出；禁止重复启动，请先处理失联插件。");
        if (!_handoffs.TryGet(entry.Id, out var handed, out var handoffError)) throw new InvalidOperationException(handoffError);
        if (handed is not null) throw new InvalidOperationException("插件有自主/未知移交记录，禁止启动第二份后端。");
        lock (_gate)
        {
            if ((!recovery && (_handoverDepth > 0 || _recovering.Contains(entry.Id))) || _running.ContainsKey(entry.Id) || !_starting.Add(entry.Id))
                throw new InvalidOperationException($"插件“{entry.Id}”已有后端在运行。");
        }

        var session = new PluginBackendSession();
        var sessionId = Guid.NewGuid();
        Process? process = null;
        var registered = false;
        var recorded = false;
        PluginJobHandle? job = null;
        PluginPackageLease? package = null;
        var startWatch = System.Diagnostics.Stopwatch.StartNew();
        PluginDiagnostics.Record("start", "", pluginId: entry.Id, pluginVersion: entry.Version,
            sessionId: sessionId.ToString("D"));
        try
        {
            package = PluginPackageIntegrity.VerifyAndLock(entry);
            if (!_authorizations.Matches(entry, package.FileTableDigest, requireOffline: false, out var proofError))
                throw new InvalidOperationException(proofError);
            var start = new ProcessStartInfo
            {
                FileName = entry.EntryExecutable,
                WorkingDirectory = entry.InstallDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // 严格标准参数（规范第 5 节）：pipe、session、host-pid；nonce 不进命令行。
            start.ArgumentList.Add("--dnfd-pipe");
            start.ArgumentList.Add(session.PipeName);
            start.ArgumentList.Add("--dnfd-session");
            start.ArgumentList.Add(sessionId.ToString("D"));
            start.ArgumentList.Add("--dnfd-host-pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            process = Process.Start(start);
            if (process is null)
            {
                PluginDiagnostics.Record("start", "failed", detail: "Process.Start 未返回进程", pluginId: entry.Id, sessionId: sessionId.ToString("D"));
                return null;
            }
            var startTicks = process.StartTime.ToUniversalTime().Ticks;
            using var host = Process.GetCurrentProcess();
            if (!_runs.TryAdd(new(sessionId, entry.Id, entry.Version, entry.PackageSha256, entry.EntryExecutable,
                process.Id, startTicks, host.Id, host.StartTime.ToUniversalTime().Ticks), out runError))
            {
                PluginDiagnostics.Record("record-failed", "failed", detail: "首次运行记录未保存，未发送操作请求",
                    pluginId: entry.Id, sessionId: sessionId.ToString("D"), errorCode: "RESULT_UNKNOWN");
                return null;
            }
            recorded = true;
            job = PluginJobHandle.TryCreateFor(process);
            if (job is null)
            {
                PluginDiagnostics.Record("start", "failed", detail: "Job 关联失败，未发送操作请求", pluginId: entry.Id, sessionId: sessionId.ToString("D"));
                return null;
            }

            if (!await session.WaitForClientAsync(process.Id, HandshakeTimeout, CancellationToken.None) ||
                process.HasExited)
            {
                PluginDiagnostics.Record("handshake", "failed",
                    detail: process.HasExited ? "后端进程在管道连接前退出" : "管道客户端 PID 核对失败或连接超时",
                    pluginId: entry.Id, pluginVersion: entry.Version, sessionId: sessionId.ToString("D"),
                    elapsedMs: startWatch.ElapsedMilliseconds);
                return null;
            }

            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var dataDirectory = Path.Combine(_dataRoot, entry.Id);
            var helloJson = "{\"nonce\":\"" + nonce + "\",\"hostVersion\":\"" + AppVersion.NumericText +
                "\",\"dataDirectory\":\"" + dataDirectory.Replace("\\", "\\\\") + "\"}";
            var helloRequest = Guid.NewGuid();
            session.WriteFrame(PluginIpcType.Hello, sessionId, helloRequest, helloJson);

            var readyRead = await session.ReadFrameAsync(HandshakeTimeout, CancellationToken.None);
            if (readyRead.Disconnected || readyRead.Frame is not { } readyFrame || readyFrame.Type != PluginIpcType.Ready)
            {
                PluginDiagnostics.Record("handshake", "failed",
                    detail: readyRead.Disconnected ? "握手期间后端断开" : "首帧不是 ready（类型 " + (readyRead.Frame?.Type.ToString() ?? "无") + "）",
                    pluginId: entry.Id, pluginVersion: entry.Version, sessionId: sessionId.ToString("D"),
                    requestId: helloRequest.ToString("D"), elapsedMs: startWatch.ElapsedMilliseconds);
                return null;
            }
            var ready = PluginIpc.ParseReady(readyFrame.Payload);
            if (readyFrame.SessionId != sessionId || readyFrame.RequestId != helloRequest || ready.Nonce != nonce || ready.PluginId != entry.Id ||
                ready.PluginVersion != entry.Version || ready.Pid != process.Id)
            {
                var reason = readyFrame.SessionId != sessionId ? "会话不匹配"
                    : readyFrame.RequestId != helloRequest ? "请求 ID 不匹配"
                    : ready.Nonce != nonce ? "nonce 回显不匹配"
                    : ready.PluginId != entry.Id ? "插件 ID 不匹配"
                    : ready.PluginVersion != entry.Version ? "插件版本不匹配" : "PID 不匹配";
                PluginDiagnostics.Record("handshake", "failed", detail: "ready 四重核对失败：" + reason,
                    pluginId: entry.Id, pluginVersion: entry.Version, sessionId: sessionId.ToString("D"),
                    requestId: helloRequest.ToString("D"), elapsedMs: startWatch.ElapsedMilliseconds);
                return null;
            }
            PluginDiagnostics.Record("handshake", "ok", pluginId: entry.Id, pluginVersion: entry.Version,
                sessionId: sessionId.ToString("D"), requestId: helloRequest.ToString("D"),
                elapsedMs: startWatch.ElapsedMilliseconds);

            var loopCancellation = new CancellationTokenSource();
            // Job Object 管理但不用 kill-on-close：宿主崩溃时插件进程自主存活（规范第 5 节）。
            var backend = new RunningBackend(process, session, sessionId, entry.Id, entry.Version,
                startTicks, entry.EntryExecutable, loopCancellation, Task.CompletedTask, null, job, package);
            lock (_gate) { _running[entry.Id] = backend; }
            registered = true;
            return backend;
        }
        catch (Exception ex)
        {
            PluginDiagnostics.Record("start", "failed", detail: "启动/握手阶段异常，原始异常消息不写入日志",
                pluginId: entry.Id, pluginVersion: entry.Version, sessionId: sessionId.ToString("D"),
                exceptionType: ex.GetType().Name, elapsedMs: startWatch.ElapsedMilliseconds);
            return null;
        }
        finally
        {
            lock (_gate) _starting.Remove(entry.Id);
            if (!registered)
            {
                TryKill(process);
                if (recorded && process is not null && process.HasExited &&
                    job is not null && await WaitForJobExitAsync(job, process.Id))
                    _runs.TryRemove(entry.Id, sessionId, out _);
                job?.Dispose();
                process?.Dispose();
                session.Dispose();
                package?.Dispose();
            }
        }
    }

    private async Task<PluginInvokeResult> InvokeCoreAsync(RunningBackend backend, string operationId,
        IReadOnlyDictionary<string, object?> values, PluginIndexEntry entry, string? targetJson)
    {
        var manifest = await ReadManifestAsync(entry);
        if (manifest is null)
            return PluginInvokeResult.ProtocolError("读取插件 manifest 失败，拒绝运行。");
        var operation = manifest.FindOperation(operationId);
        if (operation is null)
            return PluginInvokeResult.ProtocolError($"操作“{operationId}”未在 manifest 声明，拒绝执行。");

        var valuesJson = SerializeValues(values);
        var payloadJson = "{\"operationId\":\"" + operationId + "\",\"values\":" + valuesJson +
            ",\"target\":" + (targetJson is null ? "null" : targetJson) + "}";
        var requestId = Guid.NewGuid();
        string? intentId = null;
        PluginIpcTarget? target;
        using (var payload = JsonDocument.Parse(payloadJson)) target = PluginIpc.ParseTarget(payload.RootElement);
        if (operation.TargetScoped && target is null)
            return new(false, "failed", "TARGET_CHANGED", "目标操作缺少经过核对的目标快照，未发送请求。", [], false, []);
        if (target is not null && !ValidateTarget(target))
            return new(false, "failed", "TARGET_CHANGED", "目标身份/代次无法复核，未发送请求。", [], false, []);
        if (operation.Mutating)
        {
            intentId = "intent-" + requestId.ToString("N");
            if (!_backups.TryRecord(new PluginBackupEntry
            {
                BackupId = intentId, PluginId = entry.Id, PluginVersion = entry.Version, PackageSha256 = entry.PackageSha256,
                OperationId = operation.Id, ResourceId = operation.ResourceIds[0], ResourceIds = operation.ResourceIds,
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O"), IsInvocationIntent = true, Target = target,
                RestoreAttemptResult = "修改请求尚未确认；这是宿主执行意图，不是已取得系统原值。",
            }, out var intentError)) return PluginInvokeResult.ProtocolError("写前执行记录保存失败，未发送修改请求：" + intentError);
        }
        var invokeWatch = System.Diagnostics.Stopwatch.StartNew();
        PluginDiagnostics.Record("invoke", "", detail: mutatingSuffix(operation),
            pluginId: entry.Id, pluginVersion: entry.Version, sessionId: backend.SessionId.ToString("D"),
            requestId: requestId.ToString("D"), operationId: operation.Id);
        try
        {
            backend.Session.WriteFrame(PluginIpcType.Invoke, backend.SessionId, requestId, payloadJson);
        }
        catch (IOException)
        {
            PluginDiagnostics.Record("invoke", "failed", detail: "请求发送时后端断开",
                pluginId: entry.Id, pluginVersion: entry.Version, sessionId: backend.SessionId.ToString("D"),
                requestId: requestId.ToString("D"), operationId: operation.Id,
                elapsedMs: invokeWatch.ElapsedMilliseconds, errorCode: "BACKEND_CRASHED");
            return PluginInvokeResult.BackendCrashed("插件后端在请求发送时断开。");
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(operation.TimeoutSeconds,
            PluginContract.MinTimeoutSeconds, PluginContract.MaxTimeoutSeconds));
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            PluginFrameRead read;
            try
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                read = await backend.Session.ReadFrameAsync(remaining, backend.LoopCancellation.Token);
            }
            catch (TimeoutException) { break; }
            catch (PluginContractException ex)
            {
                PluginDiagnostics.Record("invoke", "failed", detail: "执行响应不符合协议", pluginId: entry.Id,
                    sessionId: backend.SessionId.ToString("D"), requestId: requestId.ToString("D"), operationId: operation.Id,
                    errorCode: "PROTOCOL_ERROR", exceptionType: ex.GetType().Name, elapsedMs: invokeWatch.ElapsedMilliseconds);
                return PluginInvokeResult.ProtocolError(ex.Message);
            }
            catch (IOException ex)
            {
                PluginDiagnostics.Record("invoke", "unknown", detail: "执行期间管道断开", pluginId: entry.Id,
                    sessionId: backend.SessionId.ToString("D"), requestId: requestId.ToString("D"), operationId: operation.Id,
                    errorCode: "BACKEND_CRASHED", exceptionType: ex.GetType().Name, elapsedMs: invokeWatch.ElapsedMilliseconds);
                return PluginInvokeResult.BackendCrashed("插件后端在执行期间断开。");
            }

            if (read.Disconnected)
                return PluginInvokeResult.BackendCrashed("插件后端在执行期间退出。");
            var frame = read.Frame!;
            if (frame.SessionId != backend.SessionId)
                return PluginInvokeResult.ProtocolError("插件响应会话不匹配。");
            switch (frame.Type)
            {
                case PluginIpcType.Progress:
                    continue; // 进度展示由上层订阅；这里继续等待 result
                case PluginIpcType.Pong:
                    backend.LastPongUtc = DateTimeOffset.UtcNow;
                    continue;
                case PluginIpcType.Result:
                    if (frame.RequestId != requestId)
                        return PluginInvokeResult.ProtocolError("插件 result 的 requestId 与请求不匹配。");
                    var result = PluginIpc.ParseResult(frame.Payload);
                    if (!RecordBackups(entry, operation, result, target, out var backupError) ||
                        (intentId is not null && !_backups.TryResolveIntent(entry.Id, intentId, result.BackupIds, out backupError)))
                    {
                        PluginDiagnostics.Record("record-failed", "failed", detail: "操作结果/恢复记录未能可靠落盘，原始记录错误不写入诊断",
                            pluginId: entry.Id, pluginVersion: entry.Version, sessionId: backend.SessionId.ToString("D"),
                            requestId: requestId.ToString("D"), operationId: operation.Id,
                            elapsedMs: invokeWatch.ElapsedMilliseconds, errorCode: "RESULT_UNKNOWN");
                        return new(false, "pending_restore", "RESULT_UNKNOWN", "操作结果/恢复记录未能可靠落盘：" + backupError, result.Items, true, result.BackupIds);
                    }
                    PluginDiagnostics.Record("invoke", result.Status == "success" ? "ok" : result.Status,
                        detail: "后端已返回结果（原始消息不写入诊断）", pluginId: entry.Id, pluginVersion: entry.Version,
                        sessionId: backend.SessionId.ToString("D"), requestId: requestId.ToString("D"),
                        operationId: operation.Id, elapsedMs: invokeWatch.ElapsedMilliseconds,
                        errorCode: PluginDiagnostics.BackendCode(result.Code), backupId: string.Join(",", result.BackupIds.Take(3)));
                    return new PluginInvokeResult(
                        result.Status == "success", result.Status, result.Code, result.Message,
                        result.Items, result.PendingRestore, result.BackupIds);
                default:
                    return PluginInvokeResult.ProtocolError($"插件在执行期间发送了不期望的消息 {frame.Type}。");
            }
        }

        // 超时：先 cancel，等待 5 秒；无响应 → 结果未知（规范第 6 节）。
        PluginDiagnostics.Record("timeout", "unknown", detail: "操作超时，发出合作取消",
            pluginId: entry.Id, pluginVersion: entry.Version, sessionId: backend.SessionId.ToString("D"),
            requestId: requestId.ToString("D"), operationId: operation.Id,
            elapsedMs: invokeWatch.ElapsedMilliseconds);
        try
        {
            backend.Session.WriteFrame(PluginIpcType.Cancel, backend.SessionId, Guid.NewGuid(),
                "{\"referencedRequestId\":\"" + requestId.ToString("D") + "\"}");
        }
        catch (IOException)
        {
            return PluginInvokeResult.BackendCrashed("插件后端超时后断开，结果未知（可能已执行）。");
        }
        try
        {
            var graceRead = await backend.Session.ReadFrameAsync(CancelGrace, backend.LoopCancellation.Token);
            if (!graceRead.Disconnected && graceRead.Frame is { Type: PluginIpcType.Result } resultFrame &&
                resultFrame.RequestId == requestId && resultFrame.SessionId == backend.SessionId)
            {
                var cancelled = PluginIpc.ParseResult(resultFrame.Payload);
                if (!RecordBackups(entry, operation, cancelled, target, out var backupError) ||
                    (intentId is not null && !_backups.TryResolveIntent(entry.Id, intentId, cancelled.BackupIds, out backupError)))
                    return new(false, "pending_restore", "RESULT_UNKNOWN", "取消结果的恢复记录未能可靠落盘，执行意图保留：" + backupError,
                        cancelled.Items, true, cancelled.BackupIds);
                PluginDiagnostics.Record("cancel", cancelled.Status, detail: "后端已返回取消结果（原始消息不写入诊断）",
                    pluginId: entry.Id, pluginVersion: entry.Version, sessionId: backend.SessionId.ToString("D"),
                    requestId: requestId.ToString("D"), operationId: operation.Id,
                    elapsedMs: invokeWatch.ElapsedMilliseconds, errorCode: PluginDiagnostics.BackendCode(cancelled.Code));
                return new PluginInvokeResult(false, cancelled.Status, cancelled.Code, cancelled.Message,
                    cancelled.Items, cancelled.PendingRestore, cancelled.BackupIds);
            }
        }
        catch (TimeoutException) { }
        catch (PluginContractException) { }
        catch (IOException) { }
        PluginDiagnostics.Record("timeout", "unknown", detail: "取消宽限期内无确认，结果未知",
            pluginId: entry.Id, pluginVersion: entry.Version, sessionId: backend.SessionId.ToString("D"),
            requestId: requestId.ToString("D"), operationId: operation.Id,
            elapsedMs: invokeWatch.ElapsedMilliseconds, errorCode: "RESULT_UNKNOWN");
        return PluginInvokeResult.ResultUnknown(
            $"操作“{operationId}”超时且未在 {CancelGrace.TotalSeconds:0} 秒内确认取消，结果未知（可能已执行修改）。");
    }

    private static string mutatingSuffix(PluginOperation operation) =>
        operation.Mutating ? "（修改操作）" : "（只读操作）";

    /// <summary>
    /// 修改操作返回 backupIds 时落账备份元数据（规范第 7 节：修改前持久保存首次原值与独立 backupId）。
    /// 原值内容以 result items 文本为证据——宿主不能独立证实后端真的保存了原值，只如实记录。
    /// </summary>
    private bool RecordBackups(PluginIndexEntry entry, PluginOperation operation,
        PluginIpcResult result, PluginIpcTarget? target, out string error)
    {
        error = "";
        if (!operation.Mutating) return true;
        if (result.BackupIds.Count == 0) { error = "后端未提供恢复标识；执行意图保留供人工核对。"; return false; }
        foreach (var backupId in result.BackupIds.Distinct())
        {
            if (backupId.Length is < 1 or > 128) { error = "后端恢复标识长度非法。"; return false; }
            var evidence = string.Join(Environment.NewLine,
                result.Items.Select(i => $"{i.Id}|{i.Status}|{i.Message}"));
                var record = new PluginBackupEntry
                {
                    BackupId = backupId,
                    PluginId = entry.Id,
                    PluginVersion = entry.Version,
                    PackageSha256 = entry.PackageSha256,
                    OperationId = operation.Id,
                    ResourceId = operation.ResourceIds[0], ResourceIds = operation.ResourceIds,
                    CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
                    BackendReport = evidence, Target = target,
                    Status = PluginBackupStatus.PendingRestore,
                };
                // 同一 backupId 只记首个资源；多资源场景由插件给出多个 backupId。
                if (!_backups.TryRecord(record, out error)) return false;
        }
        return true;
    }

    /// <summary>
    /// 恢复某插件的待恢复备份（规范第 6、7 节）：按操作启动后端 → restore(backupIds) →
    /// 每项按 result 归结状态；TARGET_CHANGED/RESTORE_FAILED/结果未知保持标记不删记录。
    /// 返回面向用户的结论文本。
    /// </summary>
    public async Task<string> RestoreAsync(PluginIndexEntry entry, CancellationToken cancellation = default)
    {
        using var diagnostics = PluginDiagnostics.UseDirectory(Path.Combine(_dataRoot, "diagnostics"));
        ArgumentNullException.ThrowIfNull(entry);
        var restoreWatch = Stopwatch.StartNew();
        var trace = Guid.NewGuid().ToString("D");
        PluginDiagnostics.Record("restore", "", pluginId: entry.Id, pluginVersion: entry.Version, requestId: trace);
        try { await _recoveryGate.WaitAsync(cancellation); }
        catch (OperationCanceledException)
        {
            PluginDiagnostics.Record("restore", "cancelled", detail: "等待恢复门禁时取消，未发请求",
                pluginId: entry.Id, requestId: trace, elapsedMs: restoreWatch.ElapsedMilliseconds);
            return "恢复已取消，尚未发送恢复请求；原记录保留。";
        }
        try
        {
            lock (_gate)
            {
                if (_starting.Contains(entry.Id) || _running.ContainsKey(entry.Id))
                {
                    PluginDiagnostics.Record("restore", "blocked", detail: "当前后端尚未停止", pluginId: entry.Id, requestId: trace);
                    return "插件仍由当前宿主管理，请先停止后再恢复。";
                }
                _recovering.Add(entry.Id);
            }
            var result = await RestoreCoreAsync(entry, cancellation);
            var confirmed = _backups.TryListPending(entry.Id, out var remaining, out _) && remaining.Count == 0;
            PluginDiagnostics.Record("restore", confirmed ? "ok" : "unknown", detail: "恢复结束；以持久化待恢复记录判定，不解析提示文字",
                pluginId: entry.Id, pluginVersion: entry.Version, requestId: trace, elapsedMs: restoreWatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            PluginDiagnostics.Record("restore", ex is OperationCanceledException ? "cancelled" : "unknown", detail: "恢复异常，保留原记录",
                pluginId: entry.Id, requestId: trace, exceptionType: ex.GetType().Name, elapsedMs: restoreWatch.ElapsedMilliseconds);
            return "恢复未确认：" + ex.GetType().Name + "；原记录保留，请重试或人工检查。";
        }
        finally { lock (_gate) _recovering.Remove(entry.Id); _recoveryGate.Release(); }
    }

    private async Task<string> RestoreCoreAsync(PluginIndexEntry entry, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!_backups.TryListPending(entry.Id, out var pending, out var error))
            return "读取待恢复记录失败，禁止报告已恢复：" + error;
        if (pending.Count == 0)
            return "没有待恢复的备份。";
        if (entry.State == PluginPackageState.Authorized)
        {
            // 恢复不要求授权仍在（默认禁用也必须能恢复），此处允许直接运行 restore。
        }

        if (pending.Any(b => b.IsInvocationIntent))
            return "存在结果未知的修改请求，没有确认对应的后端原值备份；记录保留，请人工核对。";
        if (pending.Any(b => b.PackageSha256 != entry.PackageSha256 || b.PluginVersion != entry.Version))
            return "恢复包身份与记录不同，禁止交给另一版本处理；原记录保留。";
        if (pending.Any(b => b.Target is not null && !ValidateTarget(b.Target, requireGeneration: false)))
            return "TARGET_CHANGED：恢复目标身份/代次无法复核，原记录保留。";
        if (!_handoffs.TryGet(entry.Id, out var handed, out var handoffError)) return handoffError;
        if (handed is not null) return await RestoreAutonomousAsync(entry, pending, handed, cancellation);
        RunningBackend? backend;
        try { backend = await StartAsync(entry, recovery: true); }
        catch (InvalidOperationException ex) { return "恢复暂缓：" + ex.Message; }
        if (backend is null)
            return "插件后端启动或握手失败，无法自动恢复；备份记录已保留，请人工处理或重试。";
        try
        {
            var summary = new List<string>();
            // 逐条恢复：每条独立 backupId，幂等（RESTORE_FAILED 可重试）。
            foreach (var backup in pending)
            {
                if (!_backups.TryUpdateStatus(entry.Id, backup.BackupId, PluginBackupStatus.Restoring, "恢复请求已发出", out _, out var stateError))
                {
                    summary.Add(backup.BackupId + "：状态保存失败，未发送恢复请求：" + stateError);
                    continue;
                }
                var requestId = Guid.NewGuid();
                PluginDiagnostics.Record("restore-item", "", pluginId: entry.Id, pluginVersion: entry.Version,
                    sessionId: backend.SessionId.ToString("D"), requestId: requestId.ToString("D"), backupId: backup.BackupId);
                backend.Session.WriteFrame(PluginIpcType.Restore, backend.SessionId, requestId,
                    JsonSerializer.Serialize(new { backupIds = new[] { backup.BackupId } }));
                PluginFrameRead read;
                try
                {
                    read = await backend.Session.ReadFrameAsync(TimeSpan.FromSeconds(30), cancellation);
                }
                catch (TimeoutException)
                {
                    _backups.TryUpdateStatus(entry.Id, backup.BackupId, PluginBackupStatus.RestoreFailed,
                        "恢复超时（结果未知）", out _, out _);
                    summary.Add($"{backup.BackupId}：超时，结果未知（可能已恢复或未恢复）。");
                    continue;
                }
                catch (IOException)
                {
                    _backups.TryUpdateStatus(entry.Id, backup.BackupId, PluginBackupStatus.RestoreFailed,
                        "后端在恢复期间断开", out _, out _);
                    summary.Add($"{backup.BackupId}：后端断开，恢复未确认。");
                    continue;
                }
                if (read.Disconnected || read.Frame is not { Type: PluginIpcType.Result } resultFrame ||
                    resultFrame.RequestId != requestId || resultFrame.SessionId != backend.SessionId)
                {
                    _backups.TryUpdateStatus(entry.Id, backup.BackupId, PluginBackupStatus.RestoreFailed,
                        "恢复响应无效或断开", out _, out _);
                    summary.Add($"{backup.BackupId}：恢复响应无效，保留记录。");
                    continue;
                }
                var result = PluginIpc.ParseResult(resultFrame.Payload);
                if (result.Status == "success" && !result.PendingRestore && result.Items.All(i => i.Status == "success"))
                {
                    var saved = _backups.TryUpdateStatus(entry.Id, backup.BackupId, PluginBackupStatus.Restored,
                        result.Message, out _, out var saveError);
                    PluginDiagnostics.Record("restore-item", saved ? "ok" : "unknown", pluginId: entry.Id,
                        sessionId: backend.SessionId.ToString("D"), requestId: requestId.ToString("D"), backupId: backup.BackupId);
                    summary.Add(saved ? $"{backup.BackupId}：恢复完成。" : $"{backup.BackupId}：恢复报告未持久化，记录保留：{saveError}");
                }
                else
                {
                    PluginDiagnostics.Record("restore-item", "failed", pluginId: entry.Id,
                        sessionId: backend.SessionId.ToString("D"), requestId: requestId.ToString("D"), backupId: backup.BackupId,
                        errorCode: PluginDiagnostics.BackendCode(result.Code));
                    // TARGET_CHANGED / RESTORE_FAILED / partial：保留标记与证据。
                    _backups.TryUpdateStatus(entry.Id, backup.BackupId, PluginBackupStatus.RestoreFailed,
                        $"{result.Code}：{result.Message}", out _, out _);
                    summary.Add($"{backup.BackupId}：{result.Code} —— {result.Message}");
                }
            }
            return string.Join(Environment.NewLine, summary);
        }
        finally
        {
            await StopCoreAsync(entry.Id, recovery: true);
        }
    }

    private async Task<string> RestoreAutonomousAsync(PluginIndexEntry entry, IReadOnlyList<PluginBackupEntry> pending,
        PluginHandoffEntry handed, CancellationToken cancellation)
    {
        if (handed.State != PluginHandoffState.Autonomous || handed.PluginVersion != entry.Version ||
            !handed.PackageSha256.Equals(entry.PackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(handed.EntryExecutable).Equals(Path.GetFullPath(entry.EntryExecutable), StringComparison.OrdinalIgnoreCase))
            return "自主运行身份或移交结果未确认，未发送恢复请求，记录保留。";
        var summary = new List<string>();
        try
        {
            using var package = PluginPackageIntegrity.VerifyAndLock(entry);
            if (!_authorizations.Matches(entry, package.FileTableDigest, false, out var authorizationError)) return authorizationError;
            using var session = await PluginReconnectSession.ConnectAsync(handed, cancellation);
            foreach (var backup in pending)
            {
                cancellation.ThrowIfCancellationRequested();
                if (backup.Target is not null && !ValidateTarget(backup.Target, requireGeneration: false))
                { summary.Add(backup.BackupId + "：TARGET_CHANGED，未发送恢复请求。"); break; }
                if (!_backups.TryUpdateStatus(entry.Id, backup.BackupId, PluginBackupStatus.Restoring, "自主后端恢复请求准备发送", out _, out var stateError))
                { summary.Add(backup.BackupId + "：状态保存失败，未发送请求：" + stateError); break; }
                try
                {
                    var frame = await session.ExchangeAsync(PluginIpcType.Restore, JsonSerializer.Serialize(new { backupIds = new[] { backup.BackupId } }),
                        TimeSpan.FromSeconds(30), cancellation);
                    var result = PluginIpc.ParseResult(frame.Payload);
                    var restored = result.Status == "success" && !result.PendingRestore && result.Items.All(i => i.Status == "success");
                    var saved = _backups.TryUpdateStatus(entry.Id, backup.BackupId, restored ? PluginBackupStatus.Restored : PluginBackupStatus.RestoreFailed,
                        result.Code + ": " + result.Message, out _, out var saveError);
                    PluginDiagnostics.Record("restore-autonomous", restored && saved ? "ok" : "unknown", pluginId: entry.Id,
                        sessionId: frame.SessionId.ToString("D"), requestId: frame.RequestId.ToString("D"), backupId: backup.BackupId,
                        errorCode: PluginDiagnostics.BackendCode(result.Code));
                    summary.Add(saved ? backup.BackupId + (restored ? "：恢复完成。" : "：恢复未完成，记录保留：" + result.Code)
                        : backup.BackupId + "：恢复报告未持久化，记录保留：" + saveError);
                    if (!restored || !saved) break;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or PluginContractException)
                {
                    _backups.TryUpdateStatus(entry.Id, backup.BackupId, PluginBackupStatus.RestoreFailed, "自主恢复结果未知：" + ex.GetType().Name, out _, out _);
                    summary.Add(backup.BackupId + "：自主恢复结果未知，原记录保留。" );
                    break;
                }
            }
            summary.Add("本次只处理恢复；自主运行记录保留，未宣称进程已停止。");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or PluginContractException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        { summary.Add("自主恢复无法确认：" + ex.Message + "；记录保留，未启动替代后端。"); }
        return string.Join(Environment.NewLine, summary);
    }

    /// <summary>
    /// 持续运行（规范第 5、6 节 continuous）：manifest 声明 continuous 且已授权的插件可驻留；
    /// 每 5 秒 ping，15 秒无 pong 标记失联（界面提示人工处理，不自动强杀正在修改系统的插件）；
    /// 崩溃不自动重启。宿主退出前应 StopAllAsync（脱机移交场景除外——见第 7、8 项）。
    /// </summary>
    public async Task<string> StartPersistentAsync(PluginIndexEntry entry, CancellationToken cancellation = default)
    {
        using var diagnostics = PluginDiagnostics.UseDirectory(Path.Combine(_dataRoot, "diagnostics"));
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
            if (_running.ContainsKey(entry.Id))
                return $"插件“{entry.Id}”已在运行。";
        if (entry.State != PluginPackageState.Authorized)
            return "插件未授权，拒绝持续运行。";

        var manifest = await ReadManifestAsync(entry);
        if (manifest is null)
            return "读取插件 manifest 失败，拒绝持续运行。";
        if (!manifest.Capabilities.Continuous)
            return $"插件“{entry.Name}”未声明 continuous 能力，拒绝持续运行。";

        RunningBackend? backend;
        try { backend = await StartAsync(entry); }
        catch (InvalidOperationException ex) { return ex.Message; }
        if (backend is null)
            return "插件后端启动或握手失败。";

        // 驻留条目替换（标记 Persistent），并启动心跳。
        lock (_gate)
        {
            if (_running.TryGetValue(entry.Id, out var existing))
                _running[entry.Id] = existing with { Persistent = true };
        }
        // 驻留会话的 pong 监听：持续读帧刷新 LastPongUtc（普通按操作模式由 invoke 循环处理）。
        var receiveLoop = Task.Run(async () =>
        {
            while (true)
            {
                RunningBackend? current;
                lock (_gate)
                {
                    if (!_running.TryGetValue(entry.Id, out var found) || !found.Persistent) return;
                    current = found;
                }
                PluginFrameOutcome read;
                try
                {
                    read = await current.Session.ReadFrameAsyncRaw(TimeSpan.FromMinutes(5), current.LoopCancellation.Token);
                }
                catch { return; }
                if (read.Disconnected) return;
                if (read.Frame is { Type: PluginIpcType.Pong } pong)
                    lock (_gate)
                        if (_running.TryGetValue(entry.Id, out var active) && pong.SessionId == active.SessionId && pong.RequestId == active.PendingPing)
                            active.LastPongUtc = DateTimeOffset.UtcNow;
            }
        });
        lock (_gate)
            if (_running.TryGetValue(entry.Id, out var active)) _running[entry.Id] = active with { LoopTask = receiveLoop };
        var heartbeat = new System.Threading.Timer(_ =>
        {
            RunningBackend? current;
            lock (_gate)
            {
                if (!_running.TryGetValue(entry.Id, out var found) || !found.Persistent) return;
                current = found;
            }
            if (current is null) return;
            try
            {
                if (current.Process.HasExited || !current.Session.IsConnected)
                {
                    // 崩溃/断开不自动重启（规范第 5 节）；停止心跳即可，进程状态由界面查询呈现。
                    current.Heartbeat?.Dispose();
                    return;
                }
                if (DateTimeOffset.UtcNow - current.LastPongUtc > HeartbeatLostAfter)
                {
                    current.LostContact = true; // 界面提示人工处理；不自动强杀。
                }
                if (current.LostContact) return;
                var ping = Guid.NewGuid();
                lock (_gate) current.PendingPing = ping;
                current.Session.WriteFrame(PluginIpcType.Ping, current.SessionId, ping, "{}");
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }, null, HeartbeatInterval, HeartbeatInterval);
        lock (_gate)
        {
            if (_running.TryGetValue(entry.Id, out var existing))
                _running[entry.Id] = existing with { Heartbeat = heartbeat };
        }
        return "";
    }

    /// <summary>查询插件运行状态（驻留/按操作/未运行 + 失联/退出标记）。</summary>
    public PluginRunStatus QueryStatus(string pluginId)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(pluginId, out var backend))
            {
                if (!_runs.TryList(out var records, out var error))
                    return new(true, false, false, true, false, error);
                if (records.Any(r => r.PluginId == pluginId))
                    return new(true, false, false, true, false, "旧宿主运行记录尚未处理；进程状态未知，禁止重复启动，不自动强杀。");
                return new PluginRunStatus(false, false, false, false, false, "");
            }
            return new PluginRunStatus(
                IsRunningFlag: true,
                Persistent: backend.Persistent,
                ProcessAlive: !backend.Process.HasExited,
                LostContact: backend.LostContact,
                SessionConnected: backend.Session.IsConnected,
                Detail: (backend.Process.HasExited
                    ? "后端进程已退出（崩溃或自行结束；不自动重启）。"
                    : backend.LostContact ? "心跳失联（15 秒无响应）；已暂停自动操作，请人工处理。"
                    : backend.Persistent ? "持续运行中。" : "操作执行中。") + backend.StopDetail);
        }
    }

    /// <summary>
    /// 脱机移交（规范第 8 节）：宿主先把 runId+插件身份+pending 记录落盘，再发 offline.prepare；
    /// 校验插件返回的重连端点并保存 PID/启动 ticks/规范路径/包哈希/端点，再发 offline.commit；
    /// 收到确认才标记 Autonomous；中途断线标记 Unknown，不删记录不声称退出。
    /// 前置条件（总开关/授权/声明/单独授权）由调用方核对；此处只做协议与身份。
    /// </summary>
    public async Task<string> HandoffToOfflineAsync(PluginIndexEntry entry, CancellationToken cancellation = default)
    {
        using var diagnostics = PluginDiagnostics.UseDirectory(Path.Combine(_dataRoot, "diagnostics"));
        ArgumentNullException.ThrowIfNull(entry);
        var watch = Stopwatch.StartNew();
        var trace = Guid.NewGuid().ToString("D");
        PluginDiagnostics.Record("handoff-request", "", pluginId: entry.Id, pluginVersion: entry.Version, requestId: trace);
        try
        {
            var result = await HandoffCoreAsync(entry, cancellation);
            PluginDiagnostics.Record("handoff-request", result.Length == 0 ? "ok" : "unknown",
                detail: result.Length == 0 ? "移交已确认并保存记录" : "未取得完整移交成功确认，记录保留",
                pluginId: entry.Id, pluginVersion: entry.Version, requestId: trace, elapsedMs: watch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            PluginDiagnostics.Record("handoff-request", "unknown", pluginId: entry.Id, requestId: trace,
                elapsedMs: watch.ElapsedMilliseconds, exceptionType: ex.GetType().Name);
            throw;
        }
    }

    private async Task<string> HandoffCoreAsync(PluginIndexEntry entry, CancellationToken cancellation)
    {
        using var diagnostics = PluginDiagnostics.UseDirectory(Path.Combine(_dataRoot, "diagnostics"));
        ArgumentNullException.ThrowIfNull(entry);
        var handoffWatch = System.Diagnostics.Stopwatch.StartNew();
        PluginDiagnostics.Record("handoff", "", detail: "准备移交",
            pluginId: entry.Id, pluginVersion: entry.Version);
        var manifest = await ReadManifestAsync(entry);
        if (entry.State != PluginPackageState.Authorized || entry.OfflineAuthorizedUtc is null || manifest?.Capabilities.OfflineAutonomous != true)
            return "移交授权/能力校验失败，未启动后端。";
        try
        {
            using var package = PluginPackageIntegrity.VerifyAndLock(entry);
            if (!_authorizations.Matches(entry, package.FileTableDigest, requireOffline: true, out var authorizationError))
                return "移交授权校验失败：" + authorizationError;
        }
        catch (Exception ex) { return "移交文件校验失败：" + ex.Message; }
        if (!_backups.TryListPending(entry.Id, out var outstanding, out var backupError) || outstanding.Count > 0)
            return "插件恢复记录未清空，不能移交：" + backupError;
        var runId = Guid.NewGuid();
        RunningBackend? backend;
        try { backend = await StartAsync(entry); }
        catch (InvalidOperationException ex) { return ex.Message; }
        if (backend is null)
            return "插件后端启动失败，未移交（未产生自主进程）。";
        runId = backend.SessionId;
        var committed = false;
        var commitSent = false;
        var recorded = false;
        try
        {
            // 步骤 1：记录先落盘（Preparing）——失败即中止，绝不在无记录时移交。
            var prepareRecord = new PluginHandoffEntry
            {
                RunId = runId,
                PluginId = entry.Id,
                PluginVersion = entry.Version,
                PackageSha256 = entry.PackageSha256,
                EntryExecutable = entry.EntryExecutable,
                ProcessId = backend.Process.Id,
                StartTimeUtcTicks = backend.Process.StartTime.ToUniversalTime().Ticks.ToString(),
                ReconnectPipeName = "pending",
                State = PluginHandoffState.Preparing,
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
                Note = "offline.prepare 未发出",
            };
            if (!_handoffs.TryUpsert(prepareRecord, out var recordError))
                return "写入移交记录失败，已中止（未移交）：" + recordError;
            recorded = true;

            // 步骤 2：offline.prepare —— 插件应答重连端点。
            var requestId = Guid.NewGuid();
            backend.Session.WriteFrame(PluginIpcType.OfflinePrepare, backend.SessionId, requestId,
                "{\"runId\":\"" + runId.ToString("D") + "\"}");
            var prepareRead = await backend.Session.ReadFrameAsync(TimeSpan.FromSeconds(30), cancellation);
            if (prepareRead.Disconnected || prepareRead.Frame is not { Type: PluginIpcType.Result } prepareResult ||
                prepareResult.RequestId != requestId || prepareResult.SessionId != backend.SessionId)
            {
                _handoffs.TryUpsert(prepareRecord with { State = PluginHandoffState.Unknown, Note = "prepare 无有效响应；记录保留" }, out _);
                return "prepare 未确认，移交标记为未知（记录保留，不声称退出）。";
            }
            var preparePayload = PluginIpc.ParseResult(prepareResult.Payload);
            var endpoint = preparePayload.Items.FirstOrDefault(i => i.Id == "reconnectPipeName")?.Message ?? "";
            if (preparePayload.Status != "success" || endpoint.Length == 0 || endpoint.Contains(".."))
            {
                _handoffs.TryUpsert(prepareRecord with { State = PluginHandoffState.Unknown, Note = "prepare 端点无效：" + preparePayload.Code }, out _);
                return "未提供有效重连端点，移交中止（记录保留）。";
            }

            // 步骤 3：保存身份+端点（仍 Preparing），再发 offline.commit。
            var endpointRecord = prepareRecord with { ReconnectPipeName = endpoint, Note = "commit 未发出" };
            if (!_handoffs.TryUpsert(endpointRecord, out var endpointError))
                return "保存端点记录失败，已中止：" + endpointError;

            var commitRequest = Guid.NewGuid();
            commitSent = true;
            backend.Session.WriteFrame(PluginIpcType.OfflineCommit, backend.SessionId, commitRequest,
                "{\"runId\":\"" + runId.ToString("D") + "\"}");
            var commitRead = await backend.Session.ReadFrameAsync(TimeSpan.FromSeconds(30), cancellation);
            if (commitRead.Disconnected || commitRead.Frame is not { Type: PluginIpcType.Result } commitResult ||
                commitResult.RequestId != commitRequest || commitResult.SessionId != backend.SessionId)
            {
                _handoffs.TryUpsert(endpointRecord with { State = PluginHandoffState.Unknown, Note = "commit 无响应；可能已自主也可能未移交" }, out _);
                return "commit 未确认，移交标记为未知（不声称退出、不启动第二份后端）。";
            }
            var commitPayload = PluginIpc.ParseResult(commitResult.Payload);
            if (commitPayload.Status != "success")
            {
                _handoffs.TryUpsert(endpointRecord with { State = PluginHandoffState.Unknown, Note = "commit 拒绝：" + commitPayload.Code }, out _);
                return "插件拒绝移交（" + commitPayload.Code + "），记录保留。";
            }

            // 步骤 4：确认 Autonomous。宿主连接随后断开——插件必须自处理（规范第 8 节）。
            if (!_handoffs.TryUpsert(endpointRecord with { State = PluginHandoffState.Autonomous, Note = "commit 已确认" }, out var commitError))
                return "commit 已应答但保存失败，不能宣称移交成功：" + commitError;
            if (!_runs.TryRemove(entry.Id, backend.SessionId, out var runError))
                return "commit 已确认但受管理记录未能交接，记录保留：" + runError;
            committed = true;
            backend.LoopCancellation.Cancel();
            backend.Session.Dispose();
            return ""; // 成功。
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or PluginContractException or OperationCanceledException)
        { return "移交结果未确认，记录保留：" + ex.Message; }
        finally
        {
            if (committed)
            {
                lock (_gate) _running.Remove(entry.Id);
                backend.Job?.Dispose(); backend.Process.Dispose(); backend.LoopCancellation.Dispose(); backend.PackageLease.Dispose();
            }
            else if (!commitSent)
            {
                if (await StopAsync(entry.Id) && recorded) _handoffs.TryRemove(entry.Id, runId, out _);
            }
            else
            {
                backend.LostContact = true;
                lock (_gate) _running[entry.Id] = backend with { Persistent = true };
            }
        }
    }

    /// <summary>
    /// 关闭总开关/退出脱机时的排空：重连记录中的自主后端 → 身份复核 → stop → 确认退出后删记录。
    /// 身份不明（PID 复用/路径变化/端点失效）保留记录并返回失败，不按名称强杀、不启第二份后端。
    /// </summary>
    public async Task<IReadOnlyList<string>> DrainOfflinePluginsAsync(CancellationToken cancellation = default)
    {
        using var diagnostics = PluginDiagnostics.UseDirectory(Path.Combine(_dataRoot, "diagnostics"));
        await _recoveryGate.WaitAsync(cancellation);
        try { return await DrainOfflineCoreAsync(cancellation); }
        finally { _recoveryGate.Release(); }
    }

    private async Task<IReadOnlyList<string>> DrainOfflineCoreAsync(CancellationToken cancellation)
    {
        var drainWatch = System.Diagnostics.Stopwatch.StartNew();
        if (!_handoffs.TryList(out var entries, out _))
        {
            PluginDiagnostics.Record("drain", "failed", detail: "移交仓库不可读");
            return ["移交仓库不可读，无法排空（记录保留）。"];
        }
        var failures = new List<string>();
        foreach (var record in entries)
        {
            try
            {
                if (!_backups.TryListPending(record.PluginId, out var pending, out var backupError) || pending.Count > 0)
                {
                    failures.Add("自主插件存在待恢复或不可读记录，请先恢复再停止：" + record.PluginId + " " + backupError);
                    continue;
                }
                Process process;
                try { process = Process.GetProcessById(record.ProcessId); }
                catch (ArgumentException)
                {
                    // 进程已退出：正常清理记录（确认退出才删）。
                    if (!_handoffs.TryRemove(record.PluginId, record.RunId, out var removeError)) failures.Add(removeError);
                    continue;
                }
                var startTicks = process.StartTime.ToUniversalTime().Ticks;
                using var ownedProcess = process;
                if (startTicks.ToString() != record.StartTimeUtcTicks)
                {
                    failures.Add("插件进程 PID 复用或启动时间不符，保留记录人工处理。（" + record.PluginId + "）");
                    continue;
                }
                using var session = await PluginReconnectSession.ConnectAsync(record, cancellation);
                var stopped = await session.ExchangeAsync(PluginIpcType.Stop, "{}", TimeSpan.FromSeconds(8), cancellation);
                var stopResult = PluginIpc.ParseResult(stopped.Payload);
                if (stopResult.Status != "success" || stopResult.PendingRestore || stopResult.Items.Any(i => i.Status != "success"))
                    throw new IOException("停止确认无效或仍待恢复，记录保留。");
                if (await session.WaitForExitAsync(cancellation))
                {
                    if (!_handoffs.TryRemove(record.PluginId, record.RunId, out var removeError)) failures.Add(removeError);
                }
                else
                {
                    failures.Add("15 秒未退出，不宣称零进程；保留记录人工处理。（" + record.PluginId + "）");
                }
            }
            catch (Exception ex)
            {
                failures.Add("排空异常（" + ex.GetType().Name + "），记录保留。（" + record.PluginId + "）");
                PluginDiagnostics.Record("drain", "failed", detail: "排空异常",
                    pluginId: record.PluginId, exceptionType: ex.GetType().Name);
            }
        }
        PluginDiagnostics.Record("drain", failures.Count == 0 ? "ok" : "failed",
            detail: failures.Count == 0 ? "排空完成" : "部分插件未能确认停止",
            elapsedMs: drainWatch.ElapsedMilliseconds);
        return failures;
    }

    /// <summary>停止后端：发 stop → 排空确认 → 等待退出（≤10 秒）；不隐式恢复保留设置。</summary>
    public Task<bool> StopAsync(string pluginId) => StopCoreAsync(pluginId, recovery: false);

    private async Task<bool> StopCoreAsync(string pluginId, bool recovery)
    {
        using var diagnostics = PluginDiagnostics.UseDirectory(Path.Combine(_dataRoot, "diagnostics"));
        RunningBackend? backend;
        lock (_gate)
        {
            if (!recovery && _recovering.Contains(pluginId)) return false;
            if (_starting.Contains(pluginId)) return false;
            if (!_running.TryGetValue(pluginId, out backend))
                return _runs.TryList(out var records, out _) && !records.Any(r => r.PluginId == pluginId);
        }
        if (backend is null) return true;
        var confirmed = false;
        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            backend.Heartbeat?.Dispose();
            backend.LoopCancellation.Cancel();
            await backend.LoopTask.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                if (backend.Session.IsConnected)
                {
                    var stopRequest = Guid.NewGuid();
                    var write = backend.Session.WriteFrameAsync(PluginIpcType.Stop, backend.SessionId, stopRequest, "{}");
                    var deadline = DateTimeOffset.UtcNow + StopTimeout;
                    while (!backend.Process.HasExited)
                    {
                        var remaining = deadline - DateTimeOffset.UtcNow;
                        if (remaining <= TimeSpan.Zero) return false;
                        var read = await backend.Session.ReadFrameAsync(remaining, default);
                        if (read.Disconnected) break;
                        if (read.Frame is { Type: PluginIpcType.Result } response && response.SessionId == backend.SessionId && response.RequestId == stopRequest)
                        {
                            if (PluginIpc.ParseResult(response.Payload).Status != "success") return false;
                            break;
                        }
                    }
                    await write;
                }
            }
            catch (IOException) { }

            if (!backend.Process.HasExited)
            {
                using var cts = new CancellationTokenSource(StopTimeout);
                await backend.Process.WaitForExitAsync(cts.Token);
            }
            confirmed = backend.Job is not null && await WaitForJobExitAsync(backend.Job, backend.Process.Id);
            if (!confirmed) backend.StopDetail = "Job 成员退出未确认：" + backend.Job?.LastQueryDetail;
            if (confirmed)
            {
                confirmed = _runs.TryRemove(pluginId, backend.SessionId, out var runError);
                if (!confirmed) backend.StopDetail = runError;
            }
            PluginDiagnostics.Record("stop", confirmed ? "ok" : "unknown",
                detail: confirmed ? "停止已确认" : ("停止未确认：" + backend.StopDetail),
                pluginId: backend.PluginId, pluginVersion: backend.PluginVersion,
                sessionId: backend.SessionId.ToString("D"), elapsedMs: stopWatch.ElapsedMilliseconds);
            return confirmed;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or TimeoutException or PluginContractException)
        {
            PluginDiagnostics.Record("stop", "unknown", detail: "停止期间异常，运行记录保留", pluginId: pluginId,
                pluginVersion: backend.PluginVersion, sessionId: backend.SessionId.ToString("D"), exceptionType: ex.GetType().Name,
                elapsedMs: stopWatch.ElapsedMilliseconds);
            backend.StopDetail = "停止异常：" + ex.GetType().Name + " " + ex.Message; backend.LostContact = true; return false;
        }
        finally
        {
            if (confirmed)
            {
                lock (_gate) _running.Remove(pluginId);
                backend.Session.Dispose();
                backend.LoopCancellation.Dispose();
                backend.Job?.Dispose();
                backend.Process.Dispose();
                backend.PackageLease.Dispose();
            }
        }
    }

    /// <summary>停止全部（模式切换/卸载/退出前）。返回失败的插件 ID 列表（不抛出）。</summary>
    public async Task<IReadOnlyList<string>> StopAllAsync()
    {
        List<string> failed = [];
        foreach (var pluginId in RunningPluginIds)
            if (!await StopAsync(pluginId))
                failed.Add(pluginId);
        return failed;
    }

    private static async Task<bool> WaitForJobExitAsync(PluginJobHandle job, int exitedParentPid)
    {
        var deadline = DateTimeOffset.UtcNow + StopTimeout;
        do
        {
            if (!job.TryGetActiveProcessCount(out var count, exitedParentPid)) return false;
            if (count == 0) return true;
            await Task.Delay(100);
        } while (DateTimeOffset.UtcNow < deadline);
        return false;
    }

    private static async Task<PluginManifest?> ReadManifestAsync(PluginIndexEntry entry)
    {
        try
        {
            var path = Path.Combine(entry.InstallDirectory, "manifest.json");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > PluginContract.MaxJsonBytes) return null;
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes);
            var manifest = PluginManifestParser.Parse(bytes);
            var executable = Path.GetFullPath(Path.Combine(entry.InstallDirectory, manifest.Backend.Entry.Replace('/', Path.DirectorySeparatorChar)));
            if (manifest.Id != entry.Id || manifest.Version != entry.Version ||
                !executable.Equals(Path.GetFullPath(entry.EntryExecutable), StringComparison.OrdinalIgnoreCase)) return null;
            return manifest;
        }
        catch (PluginContractException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static PluginIpcTarget? CaptureSingleTarget()
    {
        var service = GameTargetService.Default;
        var processes = service.GetProcesses(forceRefresh: true);
        try
        {
            if (processes.Length != 1 || !service.Matches(processes[0])) return null;
            var process = processes[0];
            return new(service.Current.Generation, GameTargetService.CanonicalPath(GameTargetService.ReadProcessPath(process.Id)),
                process.Id, process.StartTime.ToUniversalTime().Ticks);
        }
        catch { return null; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static bool ValidateTarget(PluginIpcTarget target, bool requireGeneration = true)
    {
        try
        {
            var service = GameTargetService.Default;
            using var process = Process.GetProcessById(target.Pid);
            return (!requireGeneration || service.Current.Generation == target.Generation) && service.Matches(process) &&
                process.StartTime.ToUniversalTime().Ticks == target.StartTimeUtcTicks &&
                GameTargetService.CanonicalPath(GameTargetService.ReadProcessPath(process.Id)).Equals(
                    GameTargetService.CanonicalPath(target.ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string SerializeValues(IReadOnlyDictionary<string, object?> values)
    {
        if (values is null || values.Count == 0) return "{}";
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in values)
            {
                writer.WritePropertyName(key);
                switch (value)
                {
                    case null: writer.WriteNullValue(); break;
                    case bool b: writer.WriteBooleanValue(b); break;
                    case string s: writer.WriteStringValue(s); break;
                    case double d: writer.WriteNumberValue(d); break;
                    case int i: writer.WriteNumberValue(i); break;
                    case long l: writer.WriteNumberValue(l); break;
                    default: writer.WriteStringValue(value.ToString()); break;
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void TryKill(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
        catch { /* 进程可能已退出 */ }
    }
}

/// <summary>插件运行状态快照（QueryStatus）。</summary>
public sealed record PluginRunStatus(
    bool IsRunningFlag, bool Persistent, bool ProcessAlive, bool LostContact, bool SessionConnected, string Detail);
