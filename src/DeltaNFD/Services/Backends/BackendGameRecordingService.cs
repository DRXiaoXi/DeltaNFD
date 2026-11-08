using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

internal sealed class BackendGameRecordingService : IGameRecordingService
{
    private readonly BackendCaller _caller = new(BackendModules.System);

    public async Task<GameRecordingSnapshot> GetStatusAsync() => BackendResults.RequireData<GameRecordingSnapshot>(
        await _caller.InvokeAsync(BackendCommands.SystemGameRecordingStatus, new { }, null, CancellationToken.None), "读取录制组件状态");

    public async Task<OperationResult> ChangeAsync(GameRecordingArgs args) => BackendResults.Map(
        await _caller.InvokeAsync(BackendCommands.SystemGameRecordingChange, args, null, CancellationToken.None), "修改录制组件");
}
