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

    public async Task<List<string>> ResolveServiceGroupAsync(BxServiceGroup group) =>
        BackendResults.RequireData<List<string>>(
            await Invoke(BackendCommands.SystemBxServiceGroupResolve, new BxServiceGroupArgs { GroupId = group.Id }), "服务组展开");

    public async Task<BxState> GetItemStateAsync(BxItem item) =>
        (await GetItemStatesAsync([item]))[item];

    public async Task<Dictionary<BxItem, BxState>> GetItemStatesAsync(IReadOnlyList<BxItem> items)
    {
        var refs = items.Select(i => new BxItemRef { SectionId = RequireSection(i), Name = i.Name }).ToList();
        var states = BackendResults.RequireData<List<BxItemStateDto>>(
            await Invoke(BackendCommands.SystemBxStatus, new BxStatusArgs { Items = refs }), "条目状态");
        var map = new Dictionary<(string Section, string Name), BxState>();
        foreach (var state in states)
        {
            map[(state.SectionId, state.Name)] = state.State;
        }

        var result = new Dictionary<BxItem, BxState>();
        foreach (var item in items)
        {
            result[item] = map.TryGetValue((RequireSection(item), item.Name), out var state) ? state : BxState.Unknown;
        }

        return result;
    }

    public async Task<BxState> GetServiceGroupStateAsync(BxServiceGroup group) =>
        BackendResults.RequireData<BxState>(
            await Invoke(BackendCommands.SystemBxServiceGroupStatus, new BxServiceGroupArgs { GroupId = group.Id }), "服务组状态");

    public async Task InvalidateAppxCacheAsync()
    {
        await Invoke(BackendCommands.SystemBxCacheInvalidate, new BxCacheInvalidateArgs { What = "appx" });
    }

    public async Task InvalidateTaskCacheAsync()
    {
        await Invoke(BackendCommands.SystemBxCacheInvalidate, new BxCacheInvalidateArgs { What = "task" });
    }

    public async Task<OperationResult> ApplyItemAsync(BxItem item, bool turnOn) =>
        BackendResults.Map(await Invoke(BackendCommands.SystemBxApply,
            new BxApplyArgs { SectionId = RequireSection(item), Name = item.Name, TurnOn = turnOn }), "扩展库应用");

    public async Task<OperationResult> ApplyServiceGroupAsync(BxServiceGroup group, bool turnOn) =>
        BackendResults.Map(await Invoke(BackendCommands.SystemBxServiceGroupApply,
            new BxServiceGroupApplyArgs { GroupId = group.Id, TurnOn = turnOn }), "服务组应用");

    public async Task<BxGearInfo?> GetGearInfoAsync(BxItem item)
    {
        var result = await Invoke(BackendCommands.SystemBxGearInfo, new BxItemArgs { SectionId = RequireSection(item), Name = item.Name });
        if (result.Status != BackendResultStatuses.Ok || result.Data is null || result.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            return null;
        }

        return BackendJson.Deserialize<BxGearInfo>(result.Data.Value);
    }

    public async Task<OperationResult> ApplyGearAsync(BxItem item, int gearIndex) =>
        BackendResults.Map(await Invoke(BackendCommands.SystemBxGearApply,
            new BxGearApplyArgs { SectionId = RequireSection(item), Name = item.Name, GearIndex = gearIndex }), "档位切换");

    public async Task<BxRestoreSummary> RestoreAllAsync() =>
        BackendResults.RequireData<BxRestoreSummary>(await Invoke(BackendCommands.SystemBxRestoreAll), "一键恢复");

    public async Task<BxBackupStatus> GetBackupStatusAsync() =>
        BackendResults.RequireData<BxBackupStatus>(await Invoke(BackendCommands.SystemBxBackups), "读取扩展库备份");

    /// <summary>条目必须带栏目 Id 才能让后端按内置数据库解析；缺失即为内部错误（不回退为任意路径）。</summary>
    private static string RequireSection(BxItem item)
    {
        if (string.IsNullOrWhiteSpace(item.SectionId))
            throw new InvalidOperationException($"条目「{item.Name}」缺少栏目 Id，无法由后端解析。");
        return item.SectionId;
    }
}
