using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>
/// 前端运行库代理：防护、14.x 兼容性检测和定向修复，旧 AIO 命令不恢复。
/// </summary>
internal sealed class BackendRuntimeGuardService : IRuntimeGuardService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BackendClient? _client;

    private async Task<BackendClient> EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsUsable: true }) return _client;
        if (_client is not null)
        {
            if (_client.RequiresAttention) throw new BackendContractException("上次运行库操作结果未确认，请先核对安装器是否退出及恢复证据，禁止自动启动第二份后端。");
            await _client.DisposeAsync();
            _client = null;
        }
        _client = await BackendClient.StartAsync(BackendModules.Runtime, cancellationToken);
        return _client;
    }

    private async Task<BackendResult> InvokeAsync(string command, object args, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = await EnsureClientAsync(cancellationToken);
            return await client.InvokeAsync(command, args, progress, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeGuardStatus> GetStatusAsync()
    {
        var result = await InvokeAsync(BackendCommands.RuntimeStatus, new { }, null, CancellationToken.None);
        return RequireData<RuntimeGuardStatus>(result, "读取运行库防护状态");
    }

    public async Task<Vc14Scan> Scan14Async() => RequireData<Vc14Scan>(
        await InvokeAsync(BackendCommands.Vc14Scan, new { }, null, CancellationToken.None), "14.x 检测");
    public async Task<Vc14ReinstallPreview> Preview14RepairAsync() => RequireData<Vc14ReinstallPreview>(
        await InvokeAsync(BackendCommands.Vc14Preview, new { }, null, CancellationToken.None), "14.x 全量重装预览");
    public async Task<OperationResult> Repair14Async(string fingerprint, IProgress<string>? progress, CancellationToken token)
        => Map(await InvokeAsync(BackendCommands.Vc14Repair, new Vc14RepairArgs { Fingerprint = fingerprint }, progress, token), "14.x 全量重装");

    public async Task<OperationResult> EnableAsync()
    {
        var result = await InvokeAsync(BackendCommands.RuntimeGuardEnable, new { }, null, CancellationToken.None);
        return Map(result, "开启运行库拦截");
    }

    public async Task<OperationResult> DisableAsync()
    {
        var result = await InvokeAsync(BackendCommands.RuntimeGuardDisable, new { }, null, CancellationToken.None);
        return Map(result, "关闭运行库拦截");
    }

    public async Task<OperationResult> SelectModeAsync(RuntimeGuardMode mode)
    {
        var result = await InvokeAsync(BackendCommands.RuntimeGuardSelectMode, new RuntimeSelectModeArgs { Mode = mode }, null, CancellationToken.None);
        return Map(result, "保存防护范围");
    }

    public async Task<OperationResult> CleanupLegacyIfeoAsync(IReadOnlyList<RuntimeGuardLegacyEntry> confirmedEntries)
    {
        var result = await InvokeAsync(BackendCommands.RuntimeGuardCleanupLegacy,
            new RuntimeCleanupLegacyArgs { Entries = confirmedEntries.ToList() }, null, CancellationToken.None);
        return Map(result, "清理旧拦截");
    }

    public async Task<OperationResult> RepairLegacyUe4AclAsync(string expectedPath)
    {
        var result = await InvokeAsync(BackendCommands.RuntimeUe4RepairAcl,
            new RuntimeUe4RepairArgs { ExpectedPath = expectedPath }, null, CancellationToken.None);
        return Map(result, "修复 UE4 前置包 ACL");
    }

    private static T RequireData<T>(BackendResult result, string what) where T : notnull
    {
        if (result.Status != BackendResultStatuses.Ok || result.Data is null)
            throw new InvalidOperationException($"{what}失败：{result.Message}");
        return BackendJson.Deserialize<T>(result.Data.Value);
    }

    internal static OperationResult Map(BackendResult result, string what)
    {
        if (result.Data is { } data)
        {
            var operation = BackendJson.Deserialize<OperationResult>(data);
            return new OperationResult
            {
                Success = operation.Success && result.Status is BackendResultStatuses.Ok or BackendResultStatuses.Partial,
                Message = operation.Message, IsSkipped = operation.IsSkipped && result.Status == BackendResultStatuses.Ok,
                OutcomeUnknown = operation.OutcomeUnknown || result.PendingRestore,
                RequiresReboot = operation.RequiresReboot, RequiresLogoff = operation.RequiresLogoff,
                RequiresDisplayConfirm = operation.RequiresDisplayConfirm,
                IsPartial = operation.IsPartial,
                DiagnosticsDirectory = operation.DiagnosticsDirectory,
            };
        }
        if (result.PendingRestore || result.Code is "backend.timeout" or "backend.cancel-unconfirmed"
            or "backend.transport-unconfirmed" or "backend.disconnected")
            return new OperationResult { Success = false, OutcomeUnknown = true, Message = result.Message };
        return result.Status is BackendResultStatuses.Ok or BackendResultStatuses.Partial
            ? OperationResult.Ok(result.Message)
            : OperationResult.Fail($"{what}失败：{result.Message}");
    }
}
