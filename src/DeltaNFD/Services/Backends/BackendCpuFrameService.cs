using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>
/// 前端唯一的 CPU/帧格后端会话：持有唯一的持久后端进程与其状态订阅。
/// 供游戏监控、优先级、CPU 调度、DWM、计时器、临时优化与电源守护调用；
/// 状态快照由后端空闲时主动推送，前端据此更新只读镜像与实际生效状态展示。
/// 不再在前端进程内自行枚举目标进程（无重复监控）。
/// </summary>
internal sealed class BackendCpuFrameService : IAsyncDisposable
{
    private static readonly Lazy<BackendCpuFrameService> Lazy = new(() => new BackendCpuFrameService());
    internal static BackendCpuFrameService Instance => Lazy.Value;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private BackendClient? _client;
    private CancellationTokenSource? _pumpCts;
    private Task? _pump;
    private int _pumpGeneration;

    /// <summary>最近一次后端状态快照（实际生效状态；未收到为 null）。</summary>
    internal CpuFrameStatusSnapshot? Latest { get; private set; }

    /// <summary>后端状态快照到达事件（在后台线程触发，订阅方自行封送 UI）。</summary>
    internal event Action<CpuFrameStatusSnapshot>? StatusUpdated;
    internal event Action? ConnectionEnded;

    private async Task<BackendClient> EnsureClientAsync(CancellationToken cancellationToken)
    {
        BackendOperationGate.Check();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            BackendOperationGate.Check();
            if (_client is { IsUsable: true }) return _client;
            if (_client is not null)
            {
                if (_client.RequiresAttention)
                    throw new BackendContractException("上次官方后端请求结果未知，禁止自动启动第二份后端。");
                await _client.QuiesceAsync(BackendSessions.StopTimeoutFor(BackendModules.CpuFrame));
                if (_client.RequiresAttention) throw new BackendContractException("CPU/帧格收尾仍有未确认操作，请先核对旧记录。");
                _client = null;
            }
            _client = await BackendClient.StartAsync(BackendModules.CpuFrame, cancellationToken);
            StartPump(_client);
            return _client;
        }
        finally { _gate.Release(); }
    }

    private void StartPump(BackendClient client)
    {
        var generation = Interlocked.Increment(ref _pumpGeneration);
        try { _pumpCts?.Cancel(); } catch (ObjectDisposedException) { }
        _pumpCts = new CancellationTokenSource();
        var token = _pumpCts.Token;
        _pump = Task.Run(async () =>
        {
            try
            {
                await ConsumeStatusAsync(client.SubscribeStatusAsync(token), status =>
                {
                    if (generation != Volatile.Read(ref _pumpGeneration) || status.Data is not { } data) return;
                    CpuFrameStatusSnapshot snapshot;
                    try { snapshot = BackendJson.Deserialize<CpuFrameStatusSnapshot>(data); }
                    catch (BackendContractException) { return; }
                    Latest = snapshot;
                    // 只读镜像：前端据此在状态展示/目标切换阻塞里读取后端实际生效的调度状态，不执行调度。
                    CpuFrameStatusState.DualCcdSchedulingActive = snapshot.DualCcdSchedulingActive;
                    CpuFrameStatusState.SingleCcdExcludeActive = snapshot.SingleCcdExcludeActive;
                    try { StatusUpdated?.Invoke(snapshot); } catch (Exception ex) { Log.Warn("CPU/帧格状态订阅处理失败：" + ex.Message); }
                }, () => { if (generation == Volatile.Read(ref _pumpGeneration)) PageBackendSessions.Notify(ConnectionEnded); }, TimeSpan.FromSeconds(10), token);
            }
            catch { /* 连接结束或取消 */ }
            finally { if (generation == Volatile.Read(ref _pumpGeneration)) PageBackendSessions.Notify(ConnectionEnded); }
        });
    }

    internal static async Task ConsumeStatusAsync(IAsyncEnumerable<BackendStatus> source, Action<BackendStatus> accept,
        Action unavailable, TimeSpan timeout, CancellationToken token)
    {
        await using var reader = source.GetAsyncEnumerator(token);
        while (true)
        {
            var next = reader.MoveNextAsync().AsTask();
            bool available;
            try { available = await next.WaitAsync(timeout, token); }
            catch (TimeoutException)
            {
                unavailable(); // Invalidate the shared observation without spawning another monitor or executor.
                available = await next.WaitAsync(token); // Keep exactly one outstanding read, including after a timeout.
            }
            if (!available) return;
            accept(reader.Current);
        }
    }

    /// <summary>确保后端监控已启动（幂等）。</summary>
    internal async Task EnsureMonitorAsync(CancellationToken cancellationToken = default)
    {
        var client = await EnsureClientAsync(cancellationToken);
        var result = await client.InvokeAsync(BackendCommands.CpuFrameMonitorStart, new { }, null, cancellationToken);
        if (result.Status is not (BackendResultStatuses.Ok or BackendResultStatuses.Partial))
            throw new BackendContractException("启动 CPU/帧格监控失败：" + result.Message);
    }

    private async Task<OperationResult> InvokeAsync(string command, object args, CancellationToken cancellationToken = default)
    {
        try
        {
        var client = await EnsureClientAsync(cancellationToken);
        var result = await client.InvokeAsync(command, args, null, cancellationToken);
        return BackendResults.Map(result, command);
        }
        catch (Exception ex) when (ex is BackendContractException or InvalidOperationException or IOException)
        {
            Log.Error("CPU/帧格操作未完成：" + command, ex);
            return OperationResult.Fail("CPU/帧格操作未完成：" + ex.Message + "\n若有旧操作记录，请先在「核对未确认操作」中处理后重试。");
        }
    }

    internal Task<OperationResult> ApplyAffinityAsync(ulong mask, CancellationToken ct = default) =>
        InvokeAsync(BackendCommands.CpuFrameAffinityApply, new CpuFrameAffinityArgs { Mask = mask }, ct);

    internal Task<OperationResult> ApplyDualCcdAsync(int gameCcdIndex, CancellationToken ct = default) =>
        InvokeAsync(BackendCommands.CpuFrameCcdApply, new CpuFrameCcdArgs { GameCcdIndex = gameCcdIndex }, ct);

    internal Task<OperationResult> RevertDualCcdAsync(CancellationToken ct = default) =>
        InvokeAsync(BackendCommands.CpuFrameCcdRevert, new { }, ct);

    internal Task<OperationResult> ApplySingleCcdAsync(CancellationToken ct = default) =>
        InvokeAsync(BackendCommands.CpuFrameSingleCcdApply, new { }, ct);

    internal Task<OperationResult> RevertSingleCcdAsync(CancellationToken ct = default) =>
        InvokeAsync(BackendCommands.CpuFrameSingleCcdRevert, new { }, ct);

    internal Task<OperationResult> RestoreAllAsync(CancellationToken ct = default) =>
        InvokeAsync(BackendCommands.CpuFrameRestore, new { }, ct);

    internal Task<OperationResult> RestoreOfflineCpuSetsAsync(CancellationToken ct = default) =>
        InvokeAsync(BackendCommands.CpuFrameOfflineRestore, new { }, ct);

    internal Task<OperationResult> RestartDwmAsync(CancellationToken ct = default) =>
        InvokeAsync(BackendCommands.CpuFrameDwmRestart, new { }, ct);

    internal Task<OperationResult> ApplyTimerAsync(bool enabled, CancellationToken ct = default) =>
        InvokeAsync(BackendCommands.CpuFrameTimerApply, new CpuFrameToggleArgs { Enabled = enabled }, ct);

    internal Task<OperationResult> ApplyTweaksAsync(CpuFrameTempArgs args, CancellationToken ct = default) =>
        InvokeAsync(BackendCommands.CpuFrameTweaksApply, args, ct);

    internal Task<OperationResult> RevertTweaksAsync(CancellationToken ct = default) =>
        InvokeAsync(BackendCommands.CpuFrameTweaksRevert, new { }, ct);

    internal Task<OperationResult> SetPowerLockAsync(bool enabled, string targetGuid, CancellationToken ct = default) =>
        InvokeAsync(BackendCommands.CpuFramePowerLock, new CpuFramePowerLockArgs { Enabled = enabled, TargetGuid = targetGuid ?? "" }, ct);

    internal Task<OperationResult> UnlockPowerAsync(CancellationToken ct = default) =>
        InvokeAsync(BackendCommands.CpuFramePowerUnlock, new { }, ct);

    internal Task<OperationResult> ApplyPriorityAsync(bool enabled, CancellationToken ct = default) =>
        InvokeAsync(BackendCommands.CpuFramePriorityApply, new CpuFrameToggleArgs { Enabled = enabled }, ct);

    internal Task<OperationResult> ApplyBoostAsync(bool timer, bool gamePower = false, CancellationToken ct = default) =>
        InvokeAsync(BackendCommands.CpuFrameBoostApply,
            new CpuFrameBoostArgs { Timer = timer, EcoQos = gamePower }, ct);

    internal Task<OperationResult> CleanMemoryNowAsync(CancellationToken ct = default) =>
        Task.FromResult(OperationResult.Fail("旧帧格内存清理已下线；请使用进程管理中的后台内存回收。"));

    internal async Task<IReadOnlyList<FrameIncompletePowerBackup>> GetIncompletePowerBackupsAsync(CancellationToken ct = default)
    {
        var client = await EnsureClientAsync(ct);
        var result = await client.InvokeAsync(BackendCommands.CpuFramePowerIncomplete, new { }, null, ct);
        return BackendResults.RequireData<List<FrameIncompletePowerBackup>>(result, "读取帧格旧电源记录");
    }

    internal async Task<OperationResult> PreserveCurrentPowerValuesAsync(
        IReadOnlyList<FrameIncompletePowerBackup> confirmed, CancellationToken ct = default)
    {
        var client = await EnsureClientAsync(ct);
        var result = await client.InvokeAsync(BackendCommands.CpuFramePowerPreserve,
            new CpuFramePreservePowerArgs { Confirmed = confirmed.ToList() }, null, ct);
        return BackendResults.Map(result, "归档帧格旧电源记录");
    }

    internal async Task<CpuTopology> GetTopologyAsync(CancellationToken ct = default)
    {
        var client = await EnsureClientAsync(ct);
        var result = await client.InvokeAsync(BackendCommands.CpuFrameTopology, new { }, null, ct);
        return BackendResults.RequireData<CpuTopology>(result, "读取 CPU 拓扑");
    }

    internal async Task<(bool Ok, string Reason)> CanRestoreAffinityAsync(ulong mask, CancellationToken ct = default)
    {
        var client = await EnsureClientAsync(ct);
        var result = await client.InvokeAsync(BackendCommands.CpuFrameAffinityCheck, new CpuFrameAffinityCheckArgs { Mask = mask }, null, ct);
        var data = BackendResults.RequireData<CpuFrameAffinityCheckResult>(result, "复核 CPU 亲和性归属");
        return (data.Ok, data.Reason);
    }

    internal async Task<bool> HasAffinityRestorePendingAsync(CancellationToken ct = default)
    {
        var client = await EnsureClientAsync(ct);
        var result = await client.InvokeAsync(BackendCommands.CpuFrameAffinityPending, new { }, null, ct);
        return BackendResults.RequireData<CpuFramePendingResult>(result, "读取 CPU 亲和性待还原状态").Pending;
    }

    internal async Task<(bool Detected, int RemovedMb, string RawOutput)> DetectMemoryLimitAsync(CancellationToken ct = default)
    {
        var client = await EnsureClientAsync(ct);
        var result = await client.InvokeAsync(BackendCommands.CpuFrameMemoryLimitDetect, new { }, null, ct);
        var data = BackendResults.RequireData<CpuFrameMemoryLimitResult>(result, "检测内存限制");
        return (data.Detected, data.RemovedMb, data.RawOutput);
    }

    internal Task<OperationResult> RemoveMemoryLimitAsync(CancellationToken ct = default) =>
        InvokeAsync(BackendCommands.CpuFrameMemoryLimitRemove, new { }, ct);

    internal async Task<CpuFrameStatusSnapshot> GetStatusAsync(CancellationToken ct = default)
    {
        var client = await EnsureClientAsync(ct);
        var result = await client.InvokeAsync(BackendCommands.CpuFrameStatus, new { }, null, ct);
        return BackendResults.RequireData<CpuFrameStatusSnapshot>(result, "读取 CPU/帧格状态");
    }

    public async ValueTask DisposeAsync()
    {
        try { _pumpCts?.Cancel(); } catch (ObjectDisposedException) { }
        await _gate.WaitAsync();
        try
        {
            if (_client is not null)
            {
                try { await _client.DisposeAsync(); }
                catch (BackendContractException) { /* 结果未知的会话保留登记，由人工核对处理 */ }
                _client = null;
            }
        }
        finally { _gate.Release(); }
        if (_pump is not null)
        {
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }
}
