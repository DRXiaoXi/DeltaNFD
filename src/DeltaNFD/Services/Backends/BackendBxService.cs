using DeltaNFD.Backends;
using DeltaNFD.Services.TweakDb;

namespace DeltaNFD.Services.Backends;

/// <summary>
/// 扩展优化库的前端 IPC 实现：所有执行都在系统维护后端完成。
/// 只发送「栏目 Id + 条目名 / 服务组 Id」，后端按自己的内置数据库解析；绝不发送注册表路径或脚本。
/// </summary>
internal sealed class BackendBxService : IBxService
{
    private readonly BackendCaller _caller = new(BackendModules.System);

    private Task<BackendResult> Invoke(string command, object? args = null) =>
        _caller.InvokeAsync(command, args ?? new { }, null, CancellationToken.None);

    public Task<List<string>> ResolveServiceGroupAsync(BxServiceGroup group) => SystemInspectionCache.ResolvedServices(group);

    public async Task<BxState> GetItemStateAsync(BxItem item) =>
        (await GetItemStatesAsync([item]))[item];

    public Task<Dictionary<BxItem,BxState>> GetItemStatesAsync(IReadOnlyList<BxItem> items) => OptimizationScan.ReadItemsAsync(items);

    public Task<BxState> GetServiceGroupStateAsync(BxServiceGroup group) => OptimizationScan.ReadGroupAsync(group);

    public Task InvalidateAppxCacheAsync() => new DeltaNFD.Inspection.BxInspection().InvalidateAppxCacheAsync();

    public Task InvalidateTaskCacheAsync() => new DeltaNFD.Inspection.BxInspection().InvalidateTaskCacheAsync();

    public async Task<OperationResult> ApplyItemAsync(BxItem item, bool turnOn) =>
        BackendResults.Map(await Invoke(BackendCommands.SystemBxApply,
            new BxApplyArgs { SectionId = RequireSection(item), Name = item.Name, TurnOn = turnOn }), "扩展库应用");

    public async Task<OperationResult> ApplyServiceGroupAsync(BxServiceGroup group, bool turnOn) =>
        BackendResults.Map(await Invoke(BackendCommands.SystemBxServiceGroupApply,
            new BxServiceGroupApplyArgs { GroupId = group.Id, TurnOn = turnOn }), "服务组应用");

    public Task<BxGearInfo?> GetGearInfoAsync(BxItem item) => SystemInspectionCache.Gear(item);

    public async Task<OperationResult> ApplyGearAsync(BxItem item, int gearIndex) =>
        BackendResults.Map(await Invoke(BackendCommands.SystemBxGearApply,
            new BxGearApplyArgs { SectionId = RequireSection(item), Name = item.Name, GearIndex = gearIndex }), "档位切换");

    public async Task<BxRestoreSummary> RestoreAllAsync() =>
        BackendResults.RequireData<BxRestoreSummary>(await Invoke(BackendCommands.SystemBxRestoreAll), "一键恢复");

    public Task<BxBackupStatus> GetBackupStatusAsync() => SystemInspectionCache.Backups();

    /// <summary>条目必须带栏目 Id 才能让后端按内置数据库解析；缺失即为内部错误（不回退为任意路径）。</summary>
    private static string RequireSection(BxItem item)
    {
        if (string.IsNullOrWhiteSpace(item.SectionId))
            throw new InvalidOperationException($"条目「{item.Name}」缺少栏目 Id，无法由后端解析。");
        return item.SectionId;
    }
}
