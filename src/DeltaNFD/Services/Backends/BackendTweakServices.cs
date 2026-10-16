using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

internal sealed class BackendSystemTweakService : ISystemTweakService
{
    private readonly BackendCaller _caller = new(BackendModules.System);
    private IReadOnlyList<SystemTweak>? _backups;
    private async Task<BackendResult> Call(string command, object? args = null) =>
        await _caller.InvokeAsync(command, args ?? new { }, null, CancellationToken.None);
    public async Task<List<TweakStatus>> GetStatusesAsync() =>
        BackendResults.RequireData<List<TweakStatus>>(await Call(BackendCommands.SystemTweaksStatus), "系统优化检测");
    public async Task<bool?> IsHyperVFullyDisabledAsync() =>
        BackendResults.RequireData<SystemTweakState>(await Call(BackendCommands.SystemTweaksHyperV), "Hyper-V 检测").Disabled;
    public async Task<OperationResult> DisableAsync(SystemTweak tweak) =>
        BackendResults.Map(await Call(BackendCommands.SystemTweaksApply, new { tweak }), "系统优化");
    public async Task<OperationResult> RestoreAsync(SystemTweak tweak) =>
        BackendResults.Map(await Call(BackendCommands.SystemTweaksRestore, new { tweak }), "系统恢复");
    public IReadOnlyList<SystemTweak> GetBackedUpTweaks() => _backups ?? throw new InvalidOperationException("请先异步读取后端恢复记录，不能将未知缓存视为无备份。");
    public async Task<IReadOnlyList<SystemTweak>> GetBackedUpTweaksAsync() =>
        _backups = BackendResults.RequireData<List<SystemTweak>>(await Call(BackendCommands.SystemTweaksBackups), "读取系统恢复记录");
    public async Task<string> GetVbsRuntimeSummaryAsync() =>
        BackendResults.RequireData<string>(await Call(BackendCommands.SystemTweaksVbs), "VBS 状态");
}

internal sealed class BackendAdvancedTweakService : IAdvancedTweakService
{
    private readonly BackendCaller _caller = new(BackendModules.System);
    public async Task<List<AdvancedTweakGroup>> GetGroupsAsync() =>
        BackendResults.RequireData<List<AdvancedTweakGroup>>(await _caller.InvokeAsync(BackendCommands.SystemAdvancedStatus, new { }, null, CancellationToken.None), "高级优化检测");
    public async Task<OperationResult> ApplyAsync(string groupId) =>
        BackendResults.Map(await _caller.InvokeAsync(BackendCommands.SystemAdvancedApply, new { groupId }, null, CancellationToken.None), "高级优化");
    public async Task<OperationResult> RevertAsync(string groupId) =>
        BackendResults.Map(await _caller.InvokeAsync(BackendCommands.SystemAdvancedRestore, new { groupId }, null, CancellationToken.None), "高级优化恢复");
}
