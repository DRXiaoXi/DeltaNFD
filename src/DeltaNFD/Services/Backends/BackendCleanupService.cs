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

    public async Task<(double FreedMb, List<string> Skipped)> CleanShaderCachesAsync(IProgress<string>? progress = null)
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemCleanupShaderCache, new { }, progress, CancellationToken.None);
        var dto = BackendResults.RequireData<CleanupResultDto>(result, "清理着色器缓存");
        return (dto.FreedMb, dto.Skipped);
    }
}
