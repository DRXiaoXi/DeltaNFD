using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>
/// 前端调用某个官方后端模块的通用封装：按需启动、串行调用、自动重启已退出的后端。
/// 各模块的前端服务组合本类实现具体合同接口。
/// </summary>
internal sealed class BackendCaller(string moduleId, PageBackendLease? pageOwner = null) : IAsyncDisposable
{
    private sealed class ModuleSession
    {
        public BackendClient? Client;
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ModuleSession> Sessions = new(StringComparer.Ordinal);
    private readonly PageBackendLease? _owner = pageOwner ?? PageBackendSessions.ConstructionOwner;
    private readonly ModuleSession _session = Sessions.GetOrAdd(moduleId + ":" + ((pageOwner ?? PageBackendSessions.ConstructionOwner)?.Id.ToString() ?? "shared"), _ => new ModuleSession());
    private SemaphoreSlim _gate => PageBackendSessions.ModuleGate(ModuleId);
    private BackendClient? _client { get => _session.Client; set => _session.Client = value; }
    internal BackendClient? SharedClient { get => _client; set => _client = value; }
    internal static void ReleasePage(Guid page)
    {
        foreach (var key in Sessions.Keys.Where(k => k.EndsWith(":" + page))) Sessions.TryRemove(key, out _);
    }

    public string ModuleId { get; } = moduleId;

    internal async Task WarmAsync(CancellationToken token = default)
    {
        await PageBackendSessions.WaitGateAsync(_gate, _owner, token, ModuleId);
        try { _ = await EnsureClientAsync(token); } finally { _gate.Release(); }
    }

    private async Task<BackendClient> EnsureClientAsync(CancellationToken cancellationToken)
    {
        BackendOperationGate.Check();
        if (_client is { IsUsable: true }) return _client;
        if (_client is not null)
        {
            if (_client.RequiresAttention) throw new BackendContractException("上次官方后端请求结果未知，禁止自动启动第二份后端。");
            await _client.QuiesceAsync(ModuleId == BackendModules.System ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30));
            if (_client.RequiresAttention) throw new BackendContractException("后端收尾仍有恢复义务，请核对操作记录。");
            _client = null;
        }
        _client = await BackendClient.StartAsync(ModuleId, cancellationToken, _owner);
        return _client;
    }

    public async Task<BackendResult> InvokeAsync(string command, object args, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        BackendOperationGate.Check();
        await PageBackendSessions.WaitGateAsync(_gate, _owner, cancellationToken, ModuleId);
        try
        {
            var client = await EnsureClientAsync(cancellationToken);
            var result = await client.InvokeAsync(command, args, progress, cancellationToken);
            InspectionInvalidation.AfterCommand(command, args);
            return result;
        }
        catch (Exception ex) when (ex is not PageBackendUnavailableException)
        {
            BackendAttention.Report(ModuleId);
            throw;
        }
        finally
        {
            try { await PageBackendSessions.FinishUnownedAsync(_client, _owner); } finally { _gate.Release(); }
        }
    }

    /// <summary>
    /// 恢复核对专用调用（只读 reconcile / 恢复 recover）：当上次会话结果未知但原进程已确认退出时，
    /// 先解除共享会话占用再启动新后端。原进程仍运行或身份无法确认时抛出，不会另起重复执行者。
    /// </summary>
    public async Task<BackendResult> InvokeReconcileAsync(string command, object args, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        BackendOperationGate.Check();
        if (command != BackendModules.ReconcileCommand(ModuleId) && command != BackendModules.RecoverCommand(ModuleId))
            throw new BackendContractException("仅允许核对或恢复命令。");
        await PageBackendSessions.WaitGateAsync(_gate, _owner, cancellationToken, ModuleId);
        try
        {
            if (_client is { RequiresAttention: true })
            {
                await _client.QuiesceAsync(BackendModules.TimeoutFor(ModuleId, command));
                await _client.ReleaseExitedSessionAsync();
                _client = null;
            }
            var client = await EnsureClientAsync(cancellationToken);
            return await client.InvokeAsync(command, args, progress, cancellationToken);
        }
        finally
        {
            try { await PageBackendSessions.FinishUnownedAsync(_client, _owner); } finally { _gate.Release(); }
        }
    }

    /// <summary>丢弃当前模块会话（记录已归档或已恢复后调用），让下一次调用重新启动干净后端。</summary>
    public async Task<bool> ResetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var client = _client;
            if (client is not null)
            {
                try { await client.QuiesceAsync(TimeSpan.FromSeconds(30)); }
                catch (BackendContractException) { /* 保留会话登记与证据，不能强杀恢复进程。 */ }
                if (client.ExitConfirmed) _client = null;
                return client.ExitConfirmed && !client.RequiresAttention;
            }
            return true;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
        if (_client is not null)
        {
            await _client.QuiesceAsync(TimeSpan.FromSeconds(30));
            _client = null;
        }
        }
        finally { _gate.Release(); }
    }
}

/// <summary>后端结果 → 合同类型的通用映射。</summary>
internal static class BackendResults
{
    public static T RequireData<T>(BackendResult result, string what) where T : notnull
    {
        if (result.Status != BackendResultStatuses.Ok || result.Data is null)
            throw new InvalidOperationException($"{what}失败：{result.Message}");
        return BackendJson.Deserialize<T>(result.Data.Value);
    }

    public static OperationResult Map(BackendResult result, string what)
    {
        if (result.Status is BackendResultStatuses.Failed or BackendResultStatuses.Cancelled)
            return OperationResult.Fail($"{what}未完成：{result.Message}");
        if (result.Data is { } data) return BackendJson.Deserialize<OperationResult>(data);
        return result.Status is BackendResultStatuses.Ok or BackendResultStatuses.Partial
            ? OperationResult.Ok(result.Message)
            : OperationResult.Fail($"{what}失败：{result.Message}");
    }
}
