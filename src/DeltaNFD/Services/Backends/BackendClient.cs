using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Threading.Channels;
using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>
/// 前端侧官方后端会话：启动 EXE、建立管道、复核身份、发送白名单命令并汇聚进度/结果。
/// 超时、断线、协议违规一律返回失败，绝不折算为成功；后端缺失/不兼容时明确报错。
///
/// 读取所有权：握手完成后由单条后台读取循环独占管道读取，把 Progress/Result 路由给当前
/// 在途请求，把 Status 分发给订阅者。这样后端可在空闲时持续推送状态快照，而不会与
/// invoke 的结果帧竞争，也不会丢失任何 result。
/// </summary>
internal sealed class BackendClient : IAsyncDisposable
{
    private readonly BackendModuleSpec _spec;
    private readonly Process _process;
    private readonly long _processStartTicks;
    private readonly string _processImagePath;
    private FileStream? _imageLock;
    private int _disposed;
    private bool _unconfirmedOperation;
    private bool _stopSent;
    private bool _cpuRestoreConfirmed;
    private bool _cpuMayNeedRestore;
    private readonly NamedPipeServerStream _pipe;
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly SemaphoreSlim _invokeGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _dead;
    private volatile bool _readerEnded;
    private string? _lastCommand;

    private readonly object _pendingLock = new();
    private PendingRequest? _pending;
    private readonly Dictionary<Guid, BackendResult> _lateConfirmedRestores = [];
    private readonly HashSet<Guid> _abandonedRequests = [];
    private readonly Dictionary<Guid, string> _abandonedCommands = [];
    private TaskCompletionSource<bool>? _stopAck;

    private readonly object _statusLock = new();
    private readonly List<Channel<BackendStatus>> _statusSubscribers = [];

    private Task? _reader;

    private sealed class PendingRequest(Guid requestId, string command, IProgress<string>? progress)
    {
        public Guid RequestId { get; } = requestId;
        public string Command { get; } = command;
        public IProgress<string>? Progress { get; } = progress;
        public TaskCompletionSource<BackendResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>读取循环因断线/协议失败结束并已把该请求登记为未确认：invoke 不得再按结果落定。</summary>
        public volatile bool ReaderFailed;

        public void Conclude(BackendResult result) => Completion.TrySetResult(result);
    }

    private BackendClient(BackendModuleSpec spec, Process process, NamedPipeServerStream pipe, long processStartTicks, string processImagePath)
    {
        _spec = spec;
        _process = process;
        _pipe = pipe;
        _processStartTicks = processStartTicks;
        _processImagePath = processImagePath;
    }

    public string ModuleId => _spec.Id;
    public bool RequiresAttention => _unconfirmedOperation;
    internal bool CpuRestoreConfirmed => _cpuRestoreConfirmed;
    internal Guid SessionId => _sessionId;

    /// <summary>后端→前端的实际状态快照事件（订阅者按到达顺序收到）。</summary>
    public event Action<BackendStatus>? StatusReceived;

    /// <summary>
    /// 订阅后端状态帧（异步流）。读取循环在空闲时把 Status 快照写入订阅队列；
    /// 结果帧仍只走 invoke 的在途通道，不会被状态订阅偷走。
    /// </summary>
    public IAsyncEnumerable<BackendStatus> SubscribeStatusAsync(CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<BackendStatus>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        lock (_statusLock) _statusSubscribers.Add(channel);
        return ReadStatusAsync(channel, cancellationToken);
    }

    private async IAsyncEnumerable<BackendStatus> ReadStatusAsync(
        Channel<BackendStatus> channel,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var status in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return status;
        }
        finally
        {
            lock (_statusLock) _statusSubscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// 释放一个「结果未知但原进程已确认退出」的会话：解除对共享会话的占用，让只读核对/恢复后端可以启动。
    /// 仅清除内存中的占用，盘上的未确认记录原样保留（证据不丢失）；进程仍在运行或身份无法确认时拒绝。
    /// </summary>
    public async Task ReleaseExitedSessionAsync()
    {
        if (!_unconfirmedOperation) return;
        if (!ExitConfirmed) throw new BackendContractException("原后端仍在运行或身份无法确认，不能释放会话。");
        _unconfirmedOperation = false;
        await DisposeAsync();
    }

    public async Task ReleaseAcknowledgedSessionAsync()
    {
        if (!ExitConfirmed) throw new BackendContractException("原后端仍在运行，不能释放会话。");
        _unconfirmedOperation = false;
        await DisposeAsync();
    }
    public bool ExitConfirmed
    {
        get { if (Volatile.Read(ref _disposed) != 0) return true; try { return _process.HasExited; } catch { return false; } }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

    public bool IsUsable
    {
        get
        {
            if (Volatile.Read(ref _dead) != 0) return false;
            try { return !_process.HasExited; }
            catch { return false; }
        }
    }

    public static async Task<BackendClient> StartAsync(string moduleId, CancellationToken cancellationToken)
    {
        using var starting = BackendOperationGate.EnterStart();
        var spec = BackendModules.Require(moduleId);
        BackendUnconfirmedStore.Check(moduleId);
        var executable = BackendPaths.Resolve(moduleId);
        var imageLock = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        var expectedHash = Convert.ToHexString(SHA256.HashData(imageLock)).ToLowerInvariant();

        var pipeName = $"DeltaNFD.Backend.{spec.Id}.{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = $"--backend {spec.Id} --pipe {pipeName} --host-pid {Environment.ProcessId}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
        };

        Process process;
        try
        {
            process = Process.Start(startInfo)
                      ?? throw new BackendContractException($"无法启动官方后端 {spec.DisplayName}。");
        }
        catch (Exception ex)
        {
            pipe.Dispose();
            imageLock.Dispose();
            if (ex is BackendContractException) throw;
            throw new BackendContractException($"启动官方后端 {spec.DisplayName} 失败：{ex.Message}");
        }

        // 进程映像路径使用已复核过的解析路径（与后面握手里 _process.MainModule 校验一致）。
        // 不在启动瞬间读取 MainModule：刚启动的进程可能暂时不可读，导致写前意图记录缺少路径，
        // 从而在进程退出后也无法确认身份、永久阻塞人工核对。
        var client = new BackendClient(spec, process, pipe, ReadStartTicks(process), executable)
        {
            _imageLock = imageLock,
        };
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await pipe.WaitForConnectionAsync(connectTimeout.Token);
        }
        catch (Exception ex)
        {
            await client.DisposeAsync();
            throw new BackendContractException($"官方后端 {spec.DisplayName} 未在超时内建立连接：{ex.Message}");
        }

        try
        {
            await client.HandshakeAsync(executable, expectedHash, cancellationToken);
            client.StartReader();
            BackendSessions.Register(client);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private void StartReader()
    {
        // A reader outlives the lifecycle call that starts it. Do not inherit its temporary
        // permission to send control commands into future status callbacks.
        using (ExecutionContext.SuppressFlow()) _reader = Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                BackendFrame? frame;
                try
                {
                    frame = await BackendFrameStream.ReadFrameAsync(_pipe, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException)
                {
                    await EndReaderAsync("disconnected");
                    return;
                }

                if (frame is null) { await EndReaderAsync("disconnected"); return; }
                if (frame.SessionId != _sessionId || frame.Module != _spec.Id) { await EndReaderAsync("protocol"); return; }

                try
                {
                    switch (frame.Type)
                    {
                        case BackendFrameType.Progress:
                            DispatchProgress(frame);
                            break;
                        case BackendFrameType.Result:
                            await DispatchResultAsync(frame);
                            break;
                        case BackendFrameType.Status:
                            DispatchStatus(frame);
                            break;
                        case BackendFrameType.Pong:
                            break;
                        default:
                            await EndReaderAsync("protocol");
                            return;
                    }
                }
                catch (BackendContractException)
                {
                    await EndReaderAsync("protocol");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("官方后端读取循环异常终止", ex);
            await EndReaderAsync("disconnected");
        }
    }

    private void DispatchProgress(BackendFrame frame)
    {
        PendingRequest? pending;
        lock (_pendingLock) pending = _pending;
        if (pending is null || pending.RequestId != frame.RequestId) return;
        var update = BackendMessages.ParseProgress(frame.Payload);
        try { pending.Progress?.Report(update.Message); } catch (Exception ex) { Log.Warn("进度回调异常：" + ex.Message); }
    }

    private async Task DispatchResultAsync(BackendFrame frame)
    {
        var result = BackendMessages.ParseResult(frame.Payload);
        PendingRequest? pending;
        lock (_pendingLock)
        {
            if (_abandonedRequests.Contains(frame.RequestId))
            {
                if (_abandonedCommands.TryGetValue(frame.RequestId, out var command)
                    && command == BackendCommands.CpuFrameRestore && ConfirmedRestore(result))
                {
                    _lateConfirmedRestores[frame.RequestId] = result;
                    if (BackendUnconfirmedStore.ArchiveConfirmedRestore(ModuleId, frame.RequestId, _sessionId, result))
                    {
                        _lateConfirmedRestores.Remove(frame.RequestId);
                        _cpuRestoreConfirmed = true;
                        _cpuMayNeedRestore = false;
                        _unconfirmedOperation = false;
                        Log.Info("CPU/帧格迟到还原已确认，证据已归档；未重放请求。");
                    }
                }
                return;
            }
            pending = _pending;
            if (pending is null || pending.RequestId != frame.RequestId)
                throw new BackendContractException("官方后端返回了不匹配的 requestId。");
            if (pending.Command == BackendCommands.CpuFrameRestore && ConfirmedRestore(result))
            { _cpuRestoreConfirmed = true; _cpuMayNeedRestore = false; }
            if (pending.Command == BackendCommands.CpuFrameRecover && result.Status == BackendResultStatuses.Ok && result.Data is { } recovered)
            {
                var outcome = BackendJson.Deserialize<RecoverOutcome>(recovered);
                if (outcome is { Scope: "all", Success: true, Verified: true, VerifiedSnapshot: { Complete: true } snapshot }
                    && snapshot.Module == BackendModules.CpuFrame && !snapshot.RecoveryScopes.Any(s => s.Available))
                { _cpuRestoreConfirmed = true; _cpuMayNeedRestore = false; }
            }
            pending.Conclude(result);
        }
        await Task.CompletedTask;
    }

    private void DispatchStatus(BackendFrame frame)
    {
        var status = BackendMessages.ParseStatus(frame.Payload);
        if (status.State == BackendMessages.StateStopped)
        {
            if (status.Data is { } data)
            {
                var outcome = BackendJson.Deserialize<OperationResult>(data);
                if (!outcome.Success)
                {
                    _unconfirmedOperation = true;
                    RetainRestoreFailure(ModuleId == BackendModules.System ? BackendCommands.SystemDefenderPostApply
                        : ModuleId == BackendModules.CpuFrame ? BackendCommands.CpuFrameRestore : _lastCommand ?? "");
                    Log.Warn("后端已停止，但收尾恢复失败，证据保留：" + outcome.Message);
                }
                else if (_spec.Id == BackendModules.CpuFrame) _cpuRestoreConfirmed = true;
                else if (_spec.Id == BackendModules.System && BackendUnconfirmedStore.ArchiveDefenderCleanup(_sessionId))
                    _unconfirmedOperation = false;
            }
            _stopAck?.TrySetResult(true);
        }
        lock (_statusLock)
        {
            foreach (var channel in _statusSubscribers) channel.Writer.TryWrite(status);
        }
        try { StatusReceived?.Invoke(status); } catch (Exception ex) { Log.Warn("状态订阅处理失败：" + ex.Message); }
    }

    /// <summary>读取循环终止：标记连接不可信，把原因如实体现在在途请求与未确认记录上（不折算成功）。</summary>
    private async Task EndReaderAsync(string reason)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_readerEnded) return;
        _readerEnded = true;
        Interlocked.Exchange(ref _dead, 1);
        PendingRequest? pending;
        lock (_pendingLock) pending = _pending;
        if (pending is not null && !pending.Completion.Task.IsCompleted)
        {
            pending.ReaderFailed = true;
            await PromoteUnknownAsync(pending.RequestId, pending.Command, reason);
            pending.Conclude(FailureResult("backend.disconnected", pending.Command, "与官方后端的连接中断"));
        }
        else if (pending is null && _spec.Id == BackendModules.CpuFrame && !_stopSent && _cpuMayNeedRestore && !_cpuRestoreConfirmed && !_unconfirmedOperation)
        {
            // A monitor can mutate processes without an in-flight UI request. Never silently restart it.
            await PromoteUnknownAsync(Guid.NewGuid(), _lastCommand ?? BackendCommands.CpuFrameMonitorStart, reason);
        }
        lock (_statusLock)
        {
            foreach (var channel in _statusSubscribers) channel.Writer.TryComplete();
        }
    }

    private async Task HandshakeAsync(string executable, string expectedHash, CancellationToken cancellationToken)
    {
        if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var peer) || peer != _process.Id)
            throw new BackendContractException("实际管道客户端不是本次启动的后端，拒绝握手。");
        var nonceBytes = RandomNumberGenerator.GetBytes(32);
        var nonce = Convert.ToBase64String(nonceBytes);
        var sessionRequestId = Guid.NewGuid();
        var hello = new BackendHello(nonce, AppVersion.NumericText, AppDataPaths.Root);
        await WriteFrameAsync(BackendMessages.EncodeHello(_spec.Id, _sessionId, sessionRequestId, hello), cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var frame = await BackendFrameStream.ReadFrameAsync(_pipe, timeout.Token)
                    ?? throw new BackendContractException("官方后端在握手后立即断开连接。");

        if (frame.Type != BackendFrameType.Ready)
            throw new BackendContractException($"官方后端握手应答类型非法：{frame.Type}（应为 ready）。");
        if (!string.Equals(frame.Module, _spec.Id, StringComparison.Ordinal))
            throw new BackendContractException($"官方后端模块不一致：{frame.Module}（应为 {_spec.Id}）。");
        if (frame.SessionId != _sessionId || frame.RequestId != sessionRequestId)
            throw new BackendContractException("官方后端握手会话不一致。");

        var ready = BackendMessages.ParseReady(frame.Payload);
        if (ready.Module != _spec.Id || !SamePath(_process.MainModule?.FileName ?? "", executable))
            throw new BackendContractException("后端模块或实际映像路径与预期不符。");
        if (!string.Equals(ready.Nonce, nonce, StringComparison.Ordinal))
            throw new BackendContractException("官方后端未回显正确的 nonce。");
        EnsureCompatibleModuleVersion(ready.ModuleVersion);
        if (ready.ProtocolMinor > BackendProtocol.ProtocolMinor)
            throw new BackendContractException("官方后端协议次版本高于宿主，拒绝连接。");
        if (ready.Pid != _process.Id)
            throw new BackendContractException($"官方后端 PID 不一致：{ready.Pid}（应为 {_process.Id}）。");

        long expectedStartTicks;
        try
        {
            expectedStartTicks = _process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex)
        {
            throw new BackendContractException("无法读取官方后端启动时间：" + ex.Message);
        }

        if (ready.StartTimeUtcTicks != expectedStartTicks)
            throw new BackendContractException("官方后端启动时间与宿主记录不一致，已拒绝连接。");

        if (!SamePath(ready.ImagePath, executable))
            throw new BackendContractException($"官方后端映像路径与预期不符：{ready.ImagePath}。");
        EnsureImageHashMatches(ready.ImageSha256, expectedHash);
    }

    /// <summary>
    /// 正式握手的版本兼容判定：后端上报的三段版本必须与宿主一致，否则明确拒绝连接（绝不静默接受）。
    /// 供握手调用，也供边界检查直接驱动同一段判定逻辑。
    /// </summary>
    internal static void EnsureCompatibleModuleVersion(string moduleVersion)
    {
        if (!string.Equals(moduleVersion, AppVersion.NumericText, StringComparison.Ordinal))
            throw new BackendContractException(
                $"官方后端版本不兼容：{moduleVersion}（本程序要求 {AppVersion.NumericText}）。");
    }

    /// <summary>
    /// 正式握手的映像完整性判定：后端自报的磁盘哈希必须与宿主启动前读取的一致，
    /// 否则视为被替换并明确拒绝连接（绝不静默回退到前端执行）。
    /// </summary>
    internal static void EnsureImageHashMatches(string actualSha256, string expectedSha256)
    {
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
            throw new BackendContractException("官方后端文件哈希与磁盘内容不一致（可能被替换），已拒绝连接。");
    }

    /// <summary>执行一条白名单命令；progress 收到的是纯文本进度。返回值可能为 ok/partial/failed/cancelled。</summary>
    public async Task<BackendResult> InvokeAsync(string command, object args, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        BackendOperationGate.Check();
        if (!BackendModules.IsAllowed(_spec.Id, command))
            throw new BackendContractException($"命令“{command}”不在模块 {_spec.Id} 白名单内，已拒绝发送。");

        if (!IsUsable)
            return BackendResult.Failed("backend.unavailable", "官方后端未运行或已退出。");

        await _invokeGate.WaitAsync(cancellationToken);
        try
        {
            BackendOperationGate.Check();
            if (EmergencyPolicies.BlockReason(command) is { } emergencyReason)
                return BackendResult.Failed("emergency-policy.blocked", emergencyReason);
            if (!IsUsable)
                return BackendResult.Failed("backend.unavailable", "官方后端未运行或已退出。");
            if (_readerEnded)
                return BackendResult.Failed("backend.disconnected", "与官方后端的连接已中断，结果未知。");

            var requestId = Guid.NewGuid();
            _lastCommand = command;
            var argsElement = System.Text.Json.JsonSerializer.SerializeToElement(args, BackendJson.SerializerOptions);
            var invoke = new BackendInvoke(command, argsElement);
            var invokeFrame = BackendMessages.EncodeInvoke(_spec.Id, _sessionId, requestId, invoke);
            using var sending = BackendOperationGate.EnterStart();
            // 写前执行意图：可能写入系统的命令在发送前先落盘，前端崩溃也能留下未知证据。
            var mutating = BackendModules.RequiresWriteIntent(_spec.Id, command);
            if (mutating)
            {
                BackendUnconfirmedStore.BeginIntent(new BackendUnconfirmedStore.OperationIntent(
                    _spec.Id, command, requestId, _sessionId, Digest(argsElement), _process.Id, _processStartTicks, _processImagePath));
            }

            var pending = new PendingRequest(requestId, command, progress);
            lock (_pendingLock) _pending = pending;

            var timeout = BackendModules.TimeoutFor(_spec.Id, command);
            using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operationTimeout.CancelAfter(timeout);

            try
            {
                // Admission covers the actual send, so handover waits for admitted sends before taking its snapshot.
                if (_spec.Id == BackendModules.CpuFrame && BackendModules.IsMutating(ModuleId, command))
                { _cpuRestoreConfirmed = false; _cpuMayNeedRestore = true; }
                await WriteFrameAsync(invokeFrame, cancellationToken);
                sending.Dispose();
                BackendResult result;
                try
                {
                    result = await pending.Completion.Task.WaitAsync(operationTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (await MarkDeadAsync(requestId, command, "timeout"))
                        return FailureResult("backend.timeout", command, $"执行超时（上限 {timeout.TotalSeconds:0} 秒）");
                    result = await pending.Completion.Task;
                }
                catch (OperationCanceledException)
                {
                    if (await MarkDeadAsync(requestId, command, "cancel-unconfirmed"))
                        return FailureResult("backend.cancel-unconfirmed", command, "请求已取消");
                    result = await pending.Completion.Task;
                }

                if (mutating && !pending.ReaderFailed)
                {
                    BackendUnconfirmedStore.ResolveIntent(_spec.Id, requestId, result.Status, result.PendingRestore,
                        result.Items.Select(i => $"{i.Id}:{i.Status}").ToArray());
                    _unconfirmedOperation = BackendUnconfirmedStore.PendingModules().Contains(ModuleId);
                    if (_unconfirmedOperation && result.Status != BackendResultStatuses.Ok)
                        BackendAttention.Report(ModuleId);
                }
                else if (!pending.ReaderFailed && BackendModules.IsMutating(ModuleId, command)
                    && (result.PendingRestore || result.Status == BackendResultStatuses.Partial))
                    RetainRestoreFailure(command);
                if (command == BackendCommands.SystemDefenderPostApply && result.Status == BackendResultStatuses.Ok
                    && result.Data is { } cleanup && BackendJson.Deserialize<DefenderPostApplyResult>(cleanup).CleanupSucceeded
                    && BackendUnconfirmedStore.ArchiveDefenderCleanup(_sessionId))
                    _unconfirmedOperation = false;
                if (_spec.Id == BackendModules.CpuFrame && _cpuMayNeedRestore && !_cpuRestoreConfirmed && _readerEnded && _stopAck is null && !_unconfirmedOperation)
                    await PromoteUnknownAsync(Guid.NewGuid(), command, "monitor-disconnected-after-result");
                return result;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                await MarkDeadAsync(requestId, command, "send-or-receive-unconfirmed");
                return FailureResult("backend.transport-unconfirmed", command, "后端请求传输未确认：" + ex.Message);
            }
            finally
            {
                lock (_pendingLock) if (ReferenceEquals(_pending, pending)) _pending = null;
            }
        }
        finally
        {
            _invokeGate.Release();
        }
    }

    /// <summary>排空：取消在途请求并等待后端退出。</summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        await QuiesceAsync(timeout);
        if (_unconfirmedOperation || BackendUnconfirmedStore.PendingModules().Contains(ModuleId))
            throw new BackendContractException("后端已请求停止，仍有待核对记录；未把退出当成恢复成功。");
    }

    internal async Task QuiesceAsync(TimeSpan timeout)
    {
        if (ExitConfirmed)
        {
            if (_reader is not null) await _reader.WaitAsync(timeout);
            await FinalizeLateRestoresAsync(timeout);
            if (!_unconfirmedOperation) await DisposeAsync();
            return;
        }
        var stopAck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopAck = stopAck;
        using var wait = new CancellationTokenSource(timeout);
        var acquired = false;
        try
        {
            _stopSent = true;
            try { await WriteFrameAsync(BackendMessages.EncodeStop(_spec.Id, _sessionId, Guid.NewGuid()), wait.Token); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Stop may already have been sent by a timed-out request. A closed pipe is not
                // proof that the process is alive; still wait for its actual exit and reader tail.
                Log.Warn("停止通道已关闭，继续确认进程退出：" + ex.Message);
            }
            
            // 修复死锁：尝试获取锁，但有独立的短超时，避免与 InvokeAsync 死锁
            var lockTimeout = TimeSpan.FromSeconds(5);
            using var lockCts = CancellationTokenSource.CreateLinkedTokenSource(wait.Token);
            lockCts.CancelAfter(lockTimeout);
            
            try
            {
                await _invokeGate.WaitAsync(lockCts.Token);
                acquired = true;
            }
            catch (OperationCanceledException) when (!wait.Token.IsCancellationRequested)
            {
                // 获取锁超时，但整体超时未到：放弃锁等待，直接等进程退出
                Log.Warn($"QuiesceAsync: 等待 _invokeGate 超时 ({lockTimeout.TotalSeconds}秒)，放弃锁等待，直接等待进程退出（可能有在途命令）");
            }
            
            // 读取停止应答：只等进程退出会阻塞对端最后一次写入（既有排空语义，不靠超时强杀）。
            var exitTask = _process.WaitForExitAsync(wait.Token);
            await Task.WhenAny(stopAck.Task, exitTask).WaitAsync(wait.Token);
            await exitTask;
            if (_reader is not null) await _reader.WaitAsync(wait.Token);
            await FinalizeLateRestoresAsync(timeout, wait.Token);
            if (ModuleId == BackendModules.CpuFrame && _cpuMayNeedRestore && !_cpuRestoreConfirmed && !_unconfirmedOperation)
                await PromoteUnknownAsync(Guid.NewGuid(), _lastCommand ?? BackendCommands.CpuFrameMonitorStart, "stop-restore-unconfirmed");
            if (ModuleId == BackendModules.System && File.Exists(Path.Combine(AppDataPaths.Root, "defender-session.json")))
                RetainRestoreFailure(BackendCommands.SystemDefenderPostApply);
            if (!_unconfirmedOperation) await DisposeAsync();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _dead, 1);
            PendingRequest? pending;
            lock (_pendingLock) pending = _pending;
            // Waiting for exit can time out just as a late restore is verified. A historical
            // mutating command alone is not a new unknown operation after that obligation ended.
            if (_unconfirmedOperation || pending is not null && !pending.Completion.Task.IsCompleted && NeedsEvidence(pending.Command)
                || ModuleId == BackendModules.CpuFrame && _cpuMayNeedRestore && !_cpuRestoreConfirmed)
            {
                await PromoteUnknownAsync(pending?.RequestId ?? Guid.NewGuid(), pending?.Command ?? _lastCommand ?? "", "drain-unconfirmed");
            }
            throw new BackendContractException("后端未确认排空，未强杀进程，交接已阻止：" + ex.Message);
        }
        finally { _stopAck = null; if (acquired) _invokeGate.Release(); }
    }

    private async Task FinalizeLateRestoresAsync(TimeSpan timeout, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        while (!deadline.IsCancellationRequested)
        {
            lock (_pendingLock)
            {
                foreach (var pair in _lateConfirmedRestores.ToArray())
                {
                    // A console/installer child can still be alive when the successful reply arrives.
                    // Recheck after actual worker exit; retain the exact result, never replay the operation.
                    if (!BackendUnconfirmedStore.ArchiveConfirmedRestore(ModuleId, pair.Key, _sessionId, pair.Value)) continue;
                    _lateConfirmedRestores.Remove(pair.Key);
                    _cpuRestoreConfirmed = true;
                    _cpuMayNeedRestore = false;
                    _unconfirmedOperation = false;
                    Log.Info("迟到还原结果在子进程退出后完成复核与归档。");
                }
                if (_lateConfirmedRestores.Count == 0) return;
            }
            try { await Task.Delay(50, deadline.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal void RetainRestoreFailure(string command)
    {
        _unconfirmedOperation = true;
        PromoteUnknownAsync(Guid.NewGuid(), command, "restore-failed").GetAwaiter().GetResult();
    }

    internal void RetainCpuRestoreFailure()
    {
        lock (_pendingLock)
        {
            // A late result may have arrived between InvokeAsync returning and the lifecycle check.
            if (!_cpuRestoreConfirmed && !_unconfirmedOperation) RetainRestoreFailure(BackendCommands.CpuFrameRestore);
        }
    }

    private async Task<bool> MarkDeadAsync(Guid requestId, string command, string reason)
    {
        lock (_pendingLock)
        {
            if (_pending?.RequestId == requestId && _pending.Completion.Task.IsCompleted && !_pending.ReaderFailed) return false;
            _abandonedRequests.Add(requestId); _abandonedCommands[requestId] = command;
            Interlocked.Exchange(ref _dead, 1);
            PromoteUnknownAsync(requestId, command, reason).GetAwaiter().GetResult();
        }
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { _stopSent = true; await WriteFrameAsync(BackendMessages.EncodeStop(_spec.Id, _sessionId, Guid.NewGuid()), stopTimeout.Token); }
        catch { }
        return true;
    }

    private Task PromoteUnknownAsync(Guid requestId, string command, string reason)
    {
        if (!NeedsEvidence(command))
        {
            Log.Warn($"只读后端请求失败，不建立系统恢复义务：module={ModuleId}；command={command}；reason={reason}");
            return Task.CompletedTask;
        }
        _unconfirmedOperation = true;
        try
        {
            // 在进程仍存活时枚举它启动的子进程（安装器等），供核对/解除阻塞前确认它们也已结束。
            var (children, ok) = BackendUnconfirmedStore.CaptureDescendants(_process.Id);
            BackendUnconfirmedStore.PromoteIntent(_spec.Id, requestId, reason, _process.Id, _processStartTicks,
                _processImagePath, command, children, childrenUnknown: !ok, sessionId: _sessionId);
            BackendAttention.Report(ModuleId);
        }
        catch (Exception ex) { Log.Error("后端未知记录保存失败，仍阻止本轮交接", ex); }
        return Task.CompletedTask;
    }

    private bool NeedsEvidence(string command) => BackendModules.IsMutating(ModuleId, command)
        || ModuleId == BackendModules.CpuFrame && _cpuMayNeedRestore && !_cpuRestoreConfirmed;

    private BackendResult FailureResult(string code, string command, string reason) => BackendResult.Failed(code,
        NeedsEvidence(command) ? reason + "，修改结果未知；保留恢复证据，未强杀后端。" : reason + "，只读检测未完成，请重试。");

    private static bool ConfirmedRestore(BackendResult result) => result.Status == BackendResultStatuses.Ok && !result.PendingRestore
        && result.Data is { } data && BackendJson.Deserialize<OperationResult>(data).Success;

    private static string Digest(System.Text.Json.JsonElement args)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(args.GetRawText());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static long ReadStartTicks(Process process)
    {
        try { return process.StartTime.ToUniversalTime().Ticks; } catch { return 0; }
    }

    private async Task WriteFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await BackendFrameStream.WriteFrameAsync(_pipe, frame, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_unconfirmedOperation) throw new BackendContractException("后端修改结果未知，禁止丢弃会话记录或强杀。");
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _dead, 1);
        BackendSessions.Unregister(this);
        lock (_statusLock)
        {
            foreach (var channel in _statusSubscribers) channel.Writer.TryComplete();
            _statusSubscribers.Clear();
        }
        
        // 优化：如果 QuiesceAsync 已等待过读取器，不再重复等待；缩短超时从 15 秒到 3 秒
        if (_reader is not null && !_reader.IsCompleted && !_stopSent)
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _reader.WaitAsync(shutdown.Token); }
            catch (OperationCanceledException) { /* 超时，继续清理 */ }
        }
        
        try { _pipe.Dispose(); } catch { /* 忽略 */ }
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _process.WaitForExitAsync(wait.Token); } catch (OperationCanceledException) { }
            }
        }
        catch { /* 进程可能已退出 */ }
        finally
        {
            _process.Dispose();
            _imageLock?.Dispose();
        }
        if (_reader is not null)
        {
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* 读取循环随管道关闭结束 */ }
        }
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
