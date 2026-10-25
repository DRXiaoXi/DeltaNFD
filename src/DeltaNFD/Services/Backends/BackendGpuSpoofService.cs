using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;
/// <summary>
 
/// 前端侧显卡伪装服务：枚举/伪装/恢复/失效检测全部经显卡后端 EXE 完成，前端不含执行实现。
 
/// </summary>
internal sealed class BackendGpuSpoofService : IGpuSpoofService
{
    private readonly PageBackendLease? _owner = PageBackendSessions.ConstructionOwner;
    private readonly SemaphoreSlim _gate = PageBackendSessions.ModuleGate(BackendModules.Gpu);
    private readonly BackendCaller _caller = new(BackendModules.Gpu);
    private BackendClient? _client { get => _caller.SharedClient; set => _caller.SharedClient = value; }
    public IReadOnlyList<string> PresetNames => GpuSpoofPresets.Names;

    private async Task<BackendClient> EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsUsable: true })
            return _client;
        if (_client is not null)
        {
            if (_client.RequiresAttention)
                throw new BackendContractException("上次操作结果未确认，请先核对；未启动第二份后端。");
            await _client.QuiesceAsync(BackendSessions.StopTimeoutFor(BackendModules.Gpu));
            _client = null;
        }

        _client = await BackendClient.StartAsync(BackendModules.Gpu, cancellationToken, _owner);
        return _client;
    }

    private async Task<BackendResult> InvokeAsync(string command, object args, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await PageBackendSessions.WaitGateAsync(_gate, _owner, cancellationToken, BackendModules.Gpu);
        try
        {
            var client = await EnsureClientAsync(cancellationToken);
            return await client.InvokeAsync(command, args, progress, cancellationToken);
        }
        finally
        {
            try
            {
                await PageBackendSessions.FinishUnownedAsync(_client, _owner);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async Task<List<DisplayAdapterInfo>> GetAdaptersAsync()
    {
        var result = await InvokeAsync(BackendCommands.GpuSpoofStatus, new { }, null, CancellationToken.None);
        RequireOk(result, "枚举显示适配器");
        return result.Data is { } data ? BackendJson.Deserialize<List<DisplayAdapterInfo>>(data) : [];
    }

    public Task<OperationResult> SpoofAsync(DisplayAdapterInfo adapter, string fakeName) => SpoofByPathAsync(adapter.RegistryPath, fakeName);
    public Task<OperationResult> RestoreAsync(DisplayAdapterInfo adapter) => RestoreByPathAsync(adapter.RegistryPath);
    public async Task<OperationResult> SpoofByPathAsync(string registryPath, string fakeName)
    {
        var result = await InvokeAsync(BackendCommands.GpuSpoofApply, new GpuSpoofApplyArgs { RegistryPath = registryPath, FakeName = fakeName }, null, CancellationToken.None);
        return ToOperationResult(result, "写入显卡伪装");
    }

    public async Task<OperationResult> RestoreByPathAsync(string registryPath)
    {
        var result = await InvokeAsync(BackendCommands.GpuSpoofRestore, new GpuSpoofPathArgs { RegistryPath = registryPath }, null, CancellationToken.None);
        return ToOperationResult(result, "恢复显卡型号");
    }

    public Task<IReadOnlyList<GpuSpoofInvalidation>> CheckInvalidationAsync() => DeltaNFD.Inspection.InspectionScheduler.Run(() => DeltaNFD.Inspection.GpuSpoofReadWatch.Check());
    public async Task<GpuDriverNamePreview> PreviewDriverNameAsync(string registryPath)
    {
        var result = await InvokeAsync(BackendCommands.GpuDriverNamePreview, new GpuSpoofPathArgs { RegistryPath = registryPath }, null, CancellationToken.None);
        RequireOk(result, "核验驱动型号");
        return BackendResults.RequireData<GpuDriverNamePreview>(result, "核验驱动型号");
    }
    public async Task<OperationResult> RestoreDriverNameAsync(string registryPath, string fingerprint)
    {
        var result = await InvokeAsync(BackendCommands.GpuDriverNameRestore,
            new GpuDriverNameRestoreArgs { RegistryPath = registryPath, Fingerprint = fingerprint }, null, CancellationToken.None);
        return MapDriverNameResult(result);
    }
    internal static OperationResult MapDriverNameResult(BackendResult result)
    {
        if (result.Data is { } data)
        {
            var operation = BackendJson.Deserialize<OperationResult>(data);
            return new OperationResult
            {
                Success = operation.Success && result.Status is BackendResultStatuses.Ok or BackendResultStatuses.Partial,
                Message = operation.Message, IsPartial = operation.IsPartial, OutcomeUnknown = operation.OutcomeUnknown || result.PendingRestore,
                RequiresReboot = operation.RequiresReboot, RequiresLogoff = operation.RequiresLogoff,
                RequiresDisplayConfirm = operation.RequiresDisplayConfirm, IsSkipped = operation.IsSkipped && result.Status == BackendResultStatuses.Ok,
                DiagnosticsDirectory = operation.DiagnosticsDirectory,
            };
        }
        return result.PendingRestore ? new OperationResult { OutcomeUnknown = true, Message = result.Message }
            : BackendResults.Map(result, "按驱动重建显卡型号");
    }
    private static void RequireOk(BackendResult result, string what)
    {
        if (result.Status != BackendResultStatuses.Ok)
            throw new InvalidOperationException($"{what}失败：{result.Message}");
    }

    private static OperationResult ToOperationResult(BackendResult result, string what)
    {
        if (result.Data is { } data)
            return BackendJson.Deserialize<OperationResult>(data);
        return result.Status is BackendResultStatuses.Ok or BackendResultStatuses.Partial ? OperationResult.Ok(result.Message) : OperationResult.Fail($"{what}失败：{result.Message}");
    }
}
