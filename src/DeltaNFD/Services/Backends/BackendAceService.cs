using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>
/// 前端侧 ACE 服务的合同实现：所有扫描/诊断/清理都通过 ACE 后端 EXE 完成，
/// 前端不再持有任何清理实现。确认弹窗与结果展示仍在前端（AcePage/AceViewModel）。
/// </summary>
internal sealed class BackendAceService : IAceService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BackendClient? _client;

    /// <summary>
    /// 离开 ACE 页时调用：后端空闲（无在途命令、无未确认操作）则彻底关闭进程，
    /// 下次进页再由 <see cref="EnsureClientAsync"/> 按需拉起。
    /// 清理结果未知（RequiresAttention）或后端仍在跑时拒绝强杀，保持安全语义。
    /// </summary>
    public async Task StopBackendIfIdleAsync()
    {
        // 不阻塞在途命令：拿不到锁说明正在清理/检测，直接放弃关闭。
        if (!await _gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (_client is null)
            {
                return;
            }

            // 结果未知（清理可能写了一半）→ 绝不强杀，留给用户核对。
            if (_client.RequiresAttention)
            {
                return;
            }

            var client = _client;
            _client = null;
            await client.DisposeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BackendClient> EnsureClientAsync(CancellationToken cancellationToken)
    {
        BackendOperationGate.Check();
        if (_client is { IsUsable: true })
        {
            return _client;
        }

        if (_client is not null)
        {
            if (_client.RequiresAttention)
            {
                if (!_client.ExitConfirmed || !BackendUnconfirmedStore.PendingModules().Contains(BackendModules.Ace))
                    throw new BackendContractException("旧 ACE 清理尚未确认结束，请先核对；未启动第二份清理后端。");
                // Release only the exited executor. Disk evidence remains and still blocks CleanAsync.
                await _client.ReleaseExitedSessionAsync();
            }
            else await _client.QuiesceAsync(BackendSessions.StopTimeoutFor(BackendModules.Ace));
            _client = null;
        }

        _client = await BackendClient.StartAsync(BackendModules.Ace, cancellationToken);
        return _client;
    }

    public async Task<AceScanResult> ScanAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var client = await EnsureClientAsync(CancellationToken.None);
            var result = await client.InvokeAsync(BackendCommands.AceScan, new { }, null, CancellationToken.None);
            if (result.Status != BackendResultStatuses.Ok || result.Data is null)
            {
                throw new InvalidOperationException("ACE 扫描失败：" + result.Message);
            }

            return BackendJson.Deserialize<AceScanResult>(result.Data.Value);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult> CleanAsync(IProgress<string>? progress = null)
    {
        await _gate.WaitAsync();
        OperationResult? outcome = null;
        try
        {
            BackendOperationGate.Check();
            var blocker = AceCleanupReadiness.DescribeBlocker();
            if (blocker.Length > 0) return OperationResult.Fail(blocker);
            var client = await EnsureClientAsync(CancellationToken.None);
            var result = await client.InvokeAsync(BackendCommands.AceClean, new { }, progress, CancellationToken.None);
            var mapped = AceCleanupResults.ToOperationResult(result);
            outcome = mapped;
            await client.QuiesceAsync(BackendSessions.StopTimeoutFor(BackendModules.Ace));
            if (mapped.Success && AceCleanupReadiness.RequiresReview)
                return new OperationResult { Success = false, OutcomeUnknown = true, RequiresReboot = mapped.RequiresReboot,
                    Message = mapped.Message + "\n清理收尾尚未确认，请先核对旧记录。" };
            return mapped;
        }
        catch (Exception ex)
        {
            Log.Error("ACE 清理未完成，未重放清理请求", ex);
            var blocker = AceCleanupReadiness.DescribeBlocker();
            return new OperationResult { Success = false, OutcomeUnknown = AceCleanupReadiness.RequiresReview,
                IsPartial = outcome?.IsPartial == true, RequiresReboot = outcome?.RequiresReboot == true,
                Message = (outcome is null ? "" : outcome.Message + "\n")
                    + (blocker.Length > 0 ? blocker : "ACE 清理未完成：" + ex.Message) };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AceCoreCheckResult> CheckCoreFilesAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var client = await EnsureClientAsync(CancellationToken.None);
            var result = await client.InvokeAsync(BackendCommands.AceCoreCheck, new { }, null, CancellationToken.None);
            if (result.Status != BackendResultStatuses.Ok || result.Data is null)
            {
                throw new InvalidOperationException("ACE-CORE 检测失败：" + result.Message);
            }

            return BackendJson.Deserialize<AceCoreCheckResult>(result.Data.Value);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AceVirtualizationCheckResult> CheckVirtualizationAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var client = await EnsureClientAsync(CancellationToken.None);
            var result = await client.InvokeAsync(BackendCommands.AceVirtCheck, new { }, null, CancellationToken.None);
            if (result.Status != BackendResultStatuses.Ok || result.Data is null)
            {
                throw new InvalidOperationException("虚拟化环境检测失败：" + result.Message);
            }

            return BackendJson.Deserialize<AceVirtualizationCheckResult>(result.Data.Value);
        }
        finally
        {
            _gate.Release();
        }
    }

}
