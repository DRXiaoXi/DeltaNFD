using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>前端侧垃圾清理服务：扫描与清理全部经系统维护后端 EXE 完成。</summary>
internal sealed class BackendCleanupService : ICleanupService
{
    private readonly BackendCaller _caller = new(BackendModules.System);

    public async Task<List<CleanupTarget>> ScanAsync()
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemCleanupScan, new { }, null, CancellationToken.None);
        return BackendResults.RequireData<List<CleanupTarget>>(result, "扫描可清理项");
    }

    public async Task<(double FreedMb, List<string> Skipped)> CleanAsync(IProgress<string>? progress = null)
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemCleanupRun, new { }, progress, CancellationToken.None);
        var dto = BackendResults.RequireData<CleanupResultDto>(result, "清理系统垃圾");
        return (dto.FreedMb, dto.Skipped);
    }

    public async Task<ShaderCacheCleanupResult> CleanShaderCachesAsync(IProgress<string>? progress = null, CancellationToken token = default)
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemCleanupShaderCache, new { }, progress, token);
        if (result.Status is not (BackendResultStatuses.Ok or BackendResultStatuses.Partial) || result.Data is null)
            throw new InvalidOperationException($"清理着色器缓存失败：{result.Message}");
        return BackendJson.Deserialize<ShaderCacheCleanupResult>(result.Data.Value);
    }
}
