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

    internal Task<BackendResult> InvokeAsync(string command, object args, CancellationToken ct = default)
        => _caller.InvokeAsync(command, args, null, ct);

    internal async Task<OperationResult> SyncFrameBackgroundAsync()
    {
        try { return BackendResults.Map(await InvokeAsync(BackendCommands.ProcmanFrameBackgroundSync, new { }), "帧格后台功能同步"); }
        catch (Exception ex) { Log.Error("帧格后台功能同步未完成", ex); return OperationResult.Fail(ex.Message); }
    }

    internal async Task ActivateSavedRulesAsync()
    {
        if (OfflineModeGuard.BlocksNormalAutomation) return;
        if (!File.Exists(Path.Combine(AppDataPaths.Root, "procman-rules.json")) && !File.Exists(Path.Combine(AppDataPaths.Root, "procman-changes.json"))
            && !File.Exists(Path.Combine(AppDataPaths.Root, "procman-background.json")) && !AppSettingsStore.Read().FrameModeActive) return;
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

    internal Task<BackendResult> RestoreAllAsync(CancellationToken ct = default)
        => _caller.InvokeAsync(BackendCommands.ProcmanRestoreAll, new { }, null, ct);

    internal Task<BackendResult> ReconcileAsync(CancellationToken ct = default)
        => _caller.InvokeAsync(BackendCommands.ProcmanReconcile, new { }, null, ct);
}
