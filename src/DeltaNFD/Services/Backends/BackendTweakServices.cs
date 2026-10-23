using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;
internal sealed class BackendSystemTweakService : ISystemTweakService
{
    private readonly BackendCaller _caller = new(BackendModules.System);
    private IReadOnlyList<SystemTweak>? _backups;
    private async Task<BackendResult> Call(string command, object? args = null) => await _caller.InvokeAsync(command, args ?? new { }, null, CancellationToken.None);
    public Task<List<TweakStatus>> GetStatusesAsync() => OptimizationScan.ReadTweaksAsync();
    public Task<bool?> IsHyperVFullyDisabledAsync() => new DeltaNFD.Inspection.SystemTweakInspection().IsHyperVFullyDisabledAsync();
    public async Task<OperationResult> DisableAsync(SystemTweak tweak) => BackendResults.Map(await Call(BackendCommands.SystemTweaksApply, new { tweak }), "系统优化");
    public async Task<OperationResult> RestoreAsync(SystemTweak tweak) => BackendResults.Map(await Call(BackendCommands.SystemTweaksRestore, new { tweak }), "系统恢复");
    public IReadOnlyList<SystemTweak> GetBackedUpTweaks() => _backups ?? throw new InvalidOperationException("请先异步读取后端恢复记录，不能将未知缓存视为无备份。");
    public Task<IReadOnlyList<SystemTweak>> GetBackedUpTweaksAsync()
    {
        _backups = new DeltaNFD.Inspection.SystemTweakInspection().GetBackedUpTweaks();
        return Task.FromResult(_backups);
    }

    public Task<string> GetVbsRuntimeSummaryAsync() => new DeltaNFD.Inspection.SystemTweakInspection().GetVbsRuntimeSummaryAsync();
}

internal sealed class BackendAdvancedTweakService : IAdvancedTweakService
{
    private readonly BackendCaller _caller = new(BackendModules.System);
    public Task<List<AdvancedTweakGroup>> GetGroupsAsync() => SystemInspectionCache.Groups();
    public async Task<OperationResult> ApplyAsync(string groupId) => BackendResults.Map(await _caller.InvokeAsync(BackendCommands.SystemAdvancedApply, new { groupId }, null, CancellationToken.None), "高级优化");
    public async Task<OperationResult> RevertAsync(string groupId) => BackendResults.Map(await _caller.InvokeAsync(BackendCommands.SystemAdvancedRestore, new { groupId }, null, CancellationToken.None), "高级优化恢复");
}
