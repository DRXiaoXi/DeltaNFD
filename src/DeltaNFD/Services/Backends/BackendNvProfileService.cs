using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;
/// <summary>
 
/// 前端侧 NVIDIA 驱动设置服务：环境/覆盖读取与写入全部经显卡后端 EXE 完成；
 
/// 设置目录为合同层静态展示数据，无需 IPC。
 
/// </summary>
internal sealed class BackendNvProfileService : INvProfileService
{
    private readonly PageBackendLease? _owner = PageBackendSessions.ConstructionOwner;
    private readonly SemaphoreSlim _gate = PageBackendSessions.ModuleGate(BackendModules.Gpu);
    private readonly BackendCaller _caller = new(BackendModules.Gpu);
    private BackendClient? _client { get => _caller.SharedClient; set => _caller.SharedClient = value; }

    public IReadOnlyList<NvSettingDef> GetSettingCatalog() => NvSettingsCatalog.Definitions;
    public async Task<OperationResult> SetShaderCacheSizeAsync(string scope, uint? value) => Map(await InvokeAsync(BackendCommands.GpuShaderCacheApply, new NvShaderCacheApplyArgs { Scope = scope, Value = value }, CancellationToken.None), "设置着色器缓存大小");
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

    private async Task<BackendResult> InvokeAsync(string command, object args, CancellationToken cancellationToken)
    {
        await PageBackendSessions.WaitGateAsync(_gate, _owner, cancellationToken, BackendModules.Gpu);
        try
        {
            var client = await EnsureClientAsync(cancellationToken);
            return await client.InvokeAsync(command, args, null, cancellationToken);
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

    public Task<NvGpuEnvironmentInfo> GetEnvironmentAsync() => DeltaNFD.Inspection.NvEnvironmentInspection.ReadAsync();
    public async Task<Dictionary<uint, (bool HasOverride, uint Value)>?> GetGameSettingValuesAsync()
    {
        var ids = NvSettingsCatalog.Definitions.Select(d => d.Id).ToList();
        var result = await InvokeAsync(BackendCommands.GpuDriverRead, new NvDriverRestoreArgs { Ids = ids }, CancellationToken.None);
        if (result.Status != BackendResultStatuses.Ok)
            throw new InvalidOperationException("读取驱动设置失败：" + result.Message);
        if (result.Data is not { } data)
            return null;
        var wire = BackendJson.Deserialize<Dictionary<string, NvSettingValueDto>>(data);
        var mapped = new Dictionary<uint, (bool HasOverride, uint Value)>();
        foreach (var pair in wire)
        {
            if (uint.TryParse(pair.Key, System.Globalization.NumberStyles.HexNumber, null, out var id))
                mapped[id] = (pair.Value.HasOverride, pair.Value.Value);
        }

        return mapped;
    }

    public async Task<OperationResult> ApplyGameSettingsAsync(IReadOnlyList<(uint Id, uint? Value)> changes)
    {
        var args = new NvDriverApplyArgs
        {
            Changes = changes.Select(c => new NvSettingChangeDto { Id = c.Id, Value = c.Value }).ToList(),
        };
        var result = await InvokeAsync(BackendCommands.GpuDriverApply, args, CancellationToken.None);
        return Map(result, "应用驱动设置");
    }

    public async Task<OperationResult> SetDrsFilesReadOnlyAsync(bool readOnly)
    {
        var result = await InvokeAsync(BackendCommands.GpuDriverLock, new NvDriverReadOnlyArgs { ReadOnly = readOnly }, CancellationToken.None);
        return Map(result, readOnly ? "锁定驱动配置数据库" : "解除只读锁定");
    }

    private static OperationResult Map(BackendResult result, string what)
    {
        if (result.Data is { } data)
            return BackendJson.Deserialize<OperationResult>(data);
        return result.Status is BackendResultStatuses.Ok or BackendResultStatuses.Partial ? OperationResult.Ok(result.Message) : OperationResult.Fail($"{what}失败：{result.Message}");
    }
}
