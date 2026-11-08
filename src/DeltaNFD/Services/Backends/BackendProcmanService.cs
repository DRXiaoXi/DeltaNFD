using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>
/// 前端唯一的进程管理后端会话：按需启动、串行调用（BackendCaller 复用）。
/// 页面打开时 1 秒轮询列表，隐藏后停止轮询（页面自己控制节奏；规则引擎在后端独立运行）。
/// 只读命令失败只影响展示，不阻断其他命令（不重蹈退出自锁）。
/// </summary>
internal sealed class BackendProcmanService
{
    private static readonly Lazy<BackendProcmanService> Lazy = new(() => new BackendProcmanService());
    internal static BackendProcmanService Instance => Lazy.Value;

    private readonly BackendCaller _caller = new(BackendModules.Procman);
    private readonly object _statusGate = new();
    private BackendClient? _observedClient;
    private Action<BackendStatus>? _statusHandler;
    private ProcmanStatusSnapshot? _latestStatus;
    private string _statusFailure = "";
    internal ProcmanStatusSnapshot? LatestStatus => Volatile.Read(ref _latestStatus);
    internal string StatusError => _observedClient is not { IsUsable: true } ? "进程管理后端连接已中断" : Volatile.Read(ref _statusFailure);
    internal bool HasCurrentStatus => StatusError.Length == 0 && LatestStatus is { } status && IsFresh(status.UpdatedUtc);
    internal static bool IsFresh(DateTimeOffset utc)
    {
        var age = DateTimeOffset.UtcNow - utc;
        return age >= TimeSpan.FromSeconds(-2) && age <= TimeSpan.FromSeconds(10);
    }
    internal event Action<ProcmanStatusSnapshot>? StatusUpdated;

    internal async Task<BackendResult> InvokeAsync(string command, object args, CancellationToken ct = default)
    {
        try
        {
            var result = await _caller.InvokeAsync(command, args, null, ct);
            ObserveClient();
            if (command == BackendCommands.ProcmanStatus)
            {
                if (result.Status == BackendResultStatuses.Ok && result.Data is { } data)
                    AcceptStatus(BackendJson.Deserialize<ProcmanStatusSnapshot>(data));
                else Volatile.Write(ref _statusFailure, "进程管理状态读取未完成：" + result.Message);
            }
            return result;
        }
        catch (Exception ex) { Volatile.Write(ref _statusFailure, ex.Message); throw; }
    }
    private void ObserveClient()
    {
        lock (_statusGate)
        {
            var client = _caller.SharedClient;
            if (ReferenceEquals(client, _observedClient)) return;
            if (_observedClient is not null && _statusHandler is not null) _observedClient.StatusReceived -= _statusHandler;
            _observedClient = client;
            Volatile.Write(ref _statusFailure, "等待进程管理新会话状态确认");
            _statusHandler = status => OnStatus(client, status);
            if (client is not null) client.StatusReceived += _statusHandler;
        }
    }
    private void OnStatus(BackendClient? source, BackendStatus status)
    {
        lock (_statusGate)
        {
            if (!ReferenceEquals(source, _observedClient) || source is not { IsUsable: true }) return;
            try { if (status.Data is { } data) AcceptStatus(BackendJson.Deserialize<ProcmanStatusSnapshot>(data)); }
            catch (Exception ex) { Volatile.Write(ref _statusFailure, "进程管理状态无法解析：" + ex.Message); }
        }
    }
    private void AcceptStatus(ProcmanStatusSnapshot status)
    {
        if (status.Module != BackendModules.Procman)
        {
            Volatile.Write(ref _statusFailure, "进程管理状态模块不匹配");
            return;
        }
        Volatile.Write(ref _latestStatus, status); Volatile.Write(ref _statusFailure, "");
        try { StatusUpdated?.Invoke(status); } catch (Exception ex) { Log.Warn("进程管理状态订阅失败：" + ex.Message); }
    }

    internal async Task<OperationResult> SyncFrameBackgroundAsync()
    {
        try
        {
            var result = BackendResults.Map(await InvokeAsync(BackendCommands.ProcmanFrameBackgroundSync, new { }), "帧格后台功能同步");
            if (_caller.SharedClient is { IsUsable: true }) await InvokeAsync(BackendCommands.ProcmanStatus, new { });
            return result;
        }
        catch (Exception ex) { Log.Error("帧格后台功能同步未完成", ex); return OperationResult.Fail(ex.Message); }
    }

    internal async Task ActivateSavedRulesAsync()
    {
        if (OfflineModeGuard.BlocksNormalAutomation) return;
        var settings = AppSettingsStore.Read();
        if (!File.Exists(Path.Combine(AppDataPaths.Root, "procman-rules.json")) && !File.Exists(Path.Combine(AppDataPaths.Root, "procman-changes.json"))
            && !File.Exists(Path.Combine(AppDataPaths.Root, "procman-background.json"))
            && !File.Exists(Path.Combine(AppDataPaths.Root, "frame-game-priority.json"))
            && !File.Exists(Path.Combine(AppDataPaths.Root, "frame-game-timer-ignore.json"))
            && !File.Exists(Path.Combine(AppDataPaths.Root, "timer-ignore-list.json"))
            && !settings.FrameModeActive
            && !(settings.TimerIgnoreExtraProcesses?.Any(n => !string.IsNullOrWhiteSpace(n)) ?? false)) return;
        try
        {
            var result = await InvokeAsync(BackendCommands.ProcmanActivate, new { });
            if (result.Status != BackendResultStatuses.Ok) Log.Warn("进程规则未恢复启动：" + result.Message);
        }
        catch (Exception ex) { Log.Error("进程规则启动未完成，证据保留", ex); }
    }

    internal async Task<ProcmanListResult> ListAsync(int offset, int limit, string? nameFilter, CancellationToken ct = default, string scope = "all")
    {
        var result = await _caller.InvokeAsync(BackendCommands.ProcmanList,
            new ProcmanListArgs { Offset = offset, Limit = limit, NameFilter = nameFilter, Scope = scope, SortOrder = "name" }, null, ct);
        return BackendResults.RequireData<ProcmanListResult>(result, "进程列表");
    }

    internal async Task<ProcmanDetailResult?> DetailAsync(ProcessIdentityArgs identity, CancellationToken ct = default)
    {
        try
        {
            var result = await _caller.InvokeAsync(BackendCommands.ProcmanDetail,
                new ProcmanDetailArgs { Identity = identity }, null, ct);
            if (result.Status != BackendResultStatuses.Ok || result.Data is not { } data) return null;
            return BackendJson.Deserialize<ProcmanDetailResult>(data);
        }
        catch (BackendContractException) { return null; }
        catch (IOException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    internal async Task<ProcmanSetResult?> SetAsync(ProcmanSetArgs args, CancellationToken ct = default)
    {
        var result = await _caller.InvokeAsync(BackendCommands.ProcmanSet, args, null, ct);
        if (result.Data is not { } data)
            return new ProcmanSetResult { IdentityOk = false, IdentityError = "修改未完成：" + result.Message };
        var mapped = BackendJson.Deserialize<ProcmanSetResult>(data);
        if (result.Status != BackendResultStatuses.Ok && string.IsNullOrEmpty(mapped.IdentityError) && !mapped.IdentityOk)
            _ = mapped; // 保留后端逐属性结果；整体失败信息已由消息承载
        return mapped;
    }

    internal async Task<ProcmanSetResult?> ResetAsync(ProcmanResetArgs args, CancellationToken ct = default)
    {
        var result = await _caller.InvokeAsync(BackendCommands.ProcmanReset, args, null, ct);
        return result.Data is { } data ? BackendJson.Deserialize<ProcmanSetResult>(data) : null;
    }

    internal async Task<ProcmanTopologyResult?> GetTopologyAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _caller.InvokeAsync(BackendCommands.ProcmanCpuTopology, new { }, null, ct);
            return result.Data is { } data ? BackendJson.Deserialize<ProcmanTopologyResult>(data) : null;
        }
        catch (BackendContractException) { return null; }
        catch (IOException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    internal async Task<BackendResult> RestoreAllAsync(CancellationToken ct = default)
    {
        // 全部还原同时撤销 CCD 调度；完成后按后端实际状态刷新只读镜像。
        var result = await _caller.InvokeAsync(BackendCommands.ProcmanRestoreAll, new { }, null, ct);
        await RefreshCcdStateAsync(ct);
        return result;
    }

    internal Task<BackendResult> ReconcileAsync(CancellationToken ct = default)
        => _caller.InvokeAsync(BackendCommands.ProcmanReconcile, new { }, null, ct);

    // ---------------- 单/双 CCD 调度（执行在进程管理后端） ----------------

    internal Task<OperationResult> ApplyDualCcdAsync(int gameCcdIndex, CancellationToken ct = default) =>
        CcdCommandAsync(BackendCommands.ProcmanCcdApply, new ProcmanCcdArgs { GameCcdIndex = gameCcdIndex }, ct);

    internal Task<OperationResult> RevertDualCcdAsync(CancellationToken ct = default) =>
        CcdCommandAsync(BackendCommands.ProcmanCcdRevert, new { }, ct);

    internal Task<OperationResult> ApplySingleCcdAsync(CancellationToken ct = default) =>
        CcdCommandAsync(BackendCommands.ProcmanSingleCcdApply, new { }, ct);

    internal Task<OperationResult> RevertSingleCcdAsync(CancellationToken ct = default) =>
        CcdCommandAsync(BackendCommands.ProcmanSingleCcdRevert, new { }, ct);

    /// <summary>只读刷新 CCD 调度镜像（procman.status）；读取失败时保留原镜像，不假定已撤销。</summary>
    internal async Task RefreshCcdStateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await InvokeAsync(BackendCommands.ProcmanStatus, new { }, ct);
            if (result.Status != BackendResultStatuses.Ok || result.Data is not { } data) return;
            var snapshot = BackendJson.Deserialize<ProcmanStatusSnapshot>(data);
            CpuFrameStatusState.DualCcdSchedulingActive = snapshot.DualCcdSchedulingActive;
            CpuFrameStatusState.SingleCcdExcludeActive = snapshot.SingleCcdExcludeActive;
        }
        catch (Exception ex) when (ex is BackendContractException or InvalidOperationException or IOException)
        { Log.Warn("CCD 调度状态读取未完成，保留原状态：" + ex.Message); }
    }

    // ---------------- 钟摆计划（Windows 11 / Windows 10 实验兼容） ----------------

    /// <summary>当前钟摆计划状态（来自后端权威快照；未收到状态时为 null）。</summary>
    internal OptimalClockSnapshot? ClockState => LatestStatus?.OptimalClock;

    /// <summary>启用并锁定指定分辨率；只有后端实测确认全局生效才成功。</summary>
    internal async Task<OperationResult> ApplyClockAsync(uint? resolution100ns, CancellationToken ct = default)
        => await ClockCommandAsync(BackendCommands.ProcmanClockApply, new OptimalClockArgs { Resolution100ns = resolution100ns }, "钟摆计划", ct);

    /// <summary>关闭并释放钟摆计划。</summary>
    internal async Task<OperationResult> ReleaseClockAsync(CancellationToken ct = default)
        => await ClockCommandAsync(BackendCommands.ProcmanClockRelease, new { }, "钟摆计划", ct);

    /// <summary>扫描本机最佳分辨率（可能耗时数十秒到数分钟）。</summary>
    internal async Task<OperationResult> ScanClockAsync(bool quick, OptimalClockScanMode scanMode, CancellationToken ct = default)
        => await ClockCommandAsync(BackendCommands.ProcmanClockScan, new OptimalClockArgs { Quick = quick, ScanMode = scanMode }, "钟摆计划扫描", ct);

    internal Task<OperationResult> ProbeClockCompatibilityAsync(CancellationToken ct = default)
        => ClockCommandAsync(BackendCommands.ProcmanClockScan, new OptimalClockArgs { CompatibilityOnly = true }, "钟摆计划兼容检测", ct);

    private async Task<OperationResult> ClockCommandAsync(string command, object args, string what, CancellationToken ct)
    {
        OperationResult mapped;
        try { mapped = BackendResults.Map(await InvokeAsync(command, args, ct), what); }
        catch (Exception ex) when (ex is BackendContractException or InvalidOperationException or IOException)
        {
            Log.Error(what + "未完成：" + command, ex);
            return OperationResult.Fail(what + "未完成：" + ex.Message);
        }
        try { await InvokeAsync(BackendCommands.ProcmanStatus, new { }, ct); } catch { }
        return mapped;
    }

    private async Task<OperationResult> CcdCommandAsync(string command, object args, CancellationToken ct)
    {
        OperationResult mapped;
        try { mapped = BackendResults.Map(await InvokeAsync(command, args, ct), command); }
        catch (Exception ex) when (ex is BackendContractException or InvalidOperationException or IOException)
        {
            Log.Error("CCD 调度操作未完成：" + command, ex);
            return OperationResult.Fail("CCD 调度操作未完成：" + ex.Message + "\n若有旧操作记录，请先在「核对未确认操作」中处理后重试。");
        }
        await RefreshCcdStateAsync(ct);
        return mapped;
    }
}
