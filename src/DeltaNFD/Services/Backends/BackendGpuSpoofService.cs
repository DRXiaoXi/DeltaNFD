using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>
/// 前端侧显卡伪装服务：枚举/伪装/恢复/失效检测全部经显卡后端 EXE 完成，前端不含执行实现。
/// </summary>
internal sealed class BackendGpuSpoofService : IGpuSpoofService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BackendClient? _client;

    public IReadOnlyList<string> PresetNames => GpuSpoofPresets.Names;

    private async Task<BackendClient> EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsUsable: true }) return _client;
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
        _client = await BackendClient.StartAsync(BackendModules.Gpu, cancellationToken);
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

    public async Task<List<DisplayAdapterInfo>> GetAdaptersAsync()
    {
        var result = await InvokeAsync(BackendCommands.GpuSpoofStatus, new { }, null, CancellationToken.None);
        RequireOk(result, "枚举显示适配器");
        return result.Data is { } data ? BackendJson.Deserialize<List<DisplayAdapterInfo>>(data) : [];
    }

    public Task<OperationResult> SpoofAsync(DisplayAdapterInfo adapter, string fakeName) =>
        SpoofByPathAsync(adapter.RegistryPath, fakeName);

    public Task<OperationResult> RestoreAsync(DisplayAdapterInfo adapter) =>
        RestoreByPathAsync(adapter.RegistryPath);

    public async Task<OperationResult> SpoofByPathAsync(string registryPath, string fakeName)
    {
        var result = await InvokeAsync(BackendCommands.GpuSpoofApply,
            new GpuSpoofApplyArgs { RegistryPath = registryPath, FakeName = fakeName }, null, CancellationToken.None);
        return ToOperationResult(result, "写入显卡伪装");
    }

    public async Task<OperationResult> RestoreByPathAsync(string registryPath)
    {
        var result = await InvokeAsync(BackendCommands.GpuSpoofRestore,
            new GpuSpoofPathArgs { RegistryPath = registryPath }, null, CancellationToken.None);
        return ToOperationResult(result, "恢复显卡型号");
    }

    public async Task<IReadOnlyList<GpuSpoofInvalidation>> CheckInvalidationAsync()
    {
        var result = await InvokeAsync(BackendCommands.GpuWatchEvaluate, new { }, null, CancellationToken.None);
        RequireOk(result, "检测伪装失效");
        return result.Data is { } data ? BackendJson.Deserialize<List<GpuSpoofInvalidation>>(data) : [];
    }

    private static void RequireOk(BackendResult result, string what)
    {
        if (result.Status != BackendResultStatuses.Ok)
            throw new InvalidOperationException($"{what}失败：{result.Message}");
    }

    private static OperationResult ToOperationResult(BackendResult result, string what)
    {
        if (result.Data is { } data) return BackendJson.Deserialize<OperationResult>(data);
        return result.Status is BackendResultStatuses.Ok or BackendResultStatuses.Partial
            ? OperationResult.Ok(result.Message)
            : OperationResult.Fail($"{what}失败：{result.Message}");
    }
}
