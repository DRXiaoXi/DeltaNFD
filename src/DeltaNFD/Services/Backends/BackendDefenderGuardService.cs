using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>
/// Defender 预检/收尾的前端 IPC 实现：只读查询、排除项增删与实时保护恢复都由系统维护后端执行。
/// 条目名称 + 操作文本仅用于后端判定是否需要引导，不构成执行指令。
/// </summary>
internal sealed class BackendDefenderGuardService : IDefenderGuardService
{
    private readonly BackendCaller _caller = new(BackendModules.System);

    private Task<BackendResult> Invoke(string command, object? args = null) =>
        _caller.InvokeAsync(command, args ?? new { }, null, CancellationToken.None);

    public async Task<DefenderPreflightReport?> PreflightAsync(IReadOnlyList<DefenderBatchItem> batchItems)
    {
        var result = await Invoke(BackendCommands.SystemDefenderPreflight, new DefenderPreflightArgs { Items = batchItems.ToList() });
        if (result.Status != BackendResultStatuses.Ok || result.Data is null ||
            result.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            return null;
        }

        return BackendJson.Deserialize<DefenderPreflightReport>(result.Data.Value);
    }

    public async Task<(bool RealTimeOn, bool TamperOn)> RefreshProtectionStateAsync()
    {
        var state = BackendResults.RequireData<DefenderProtectionState>(
            await Invoke(BackendCommands.SystemDefenderProtectionState), "Defender 防护状态");
        return (state.RealTimeOn, state.TamperOn);
    }

    public async Task AddSessionExclusionsAsync()
    {
        var result = await Invoke(BackendCommands.SystemDefenderExclusionsAdd);
        if (result.Status != BackendResultStatuses.Ok) throw new InvalidOperationException(result.Message);
    }

    public async Task<DefenderPostApplyResult> PostApplyAsync() =>
        BackendResults.RequireData<DefenderPostApplyResult>(await Invoke(BackendCommands.SystemDefenderPostApply), "Defender 收尾");
}
