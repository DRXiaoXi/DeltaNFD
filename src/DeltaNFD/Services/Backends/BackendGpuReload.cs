using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>
/// 显卡设备重载的前端调用入口：执行在显卡后端（gpu.spoof.reload）。
/// 黑屏兜底（延迟自动恢复进程）与玩家亮屏确认仍由帧格流程在前端编排。
/// </summary>
internal static class BackendGpuReload
{
    private static readonly BackendCaller Caller = new(BackendModules.Gpu);

    public static async Task<OperationResult> ReloadAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var result = await Caller.InvokeAsync(BackendCommands.GpuSpoofReload,
            new GpuSpoofReloadArgs { InstanceId = instanceId }, null, cancellationToken);
        return BackendResults.Map(result, "gpu.spoof.reload");
    }
}
