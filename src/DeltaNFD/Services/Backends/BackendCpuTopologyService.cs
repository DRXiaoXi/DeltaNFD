namespace DeltaNFD.Services.Backends;

/// <summary>
/// CPU 拓扑 / 核心锁定（实验室）的前端调用器：所有会写系统状态的调度（双单 CCD、亲和性规则、
/// 内存限制移除）都交由 CPU/帧格后端执行；前端只读取拓扑并展示，不再在本地施加调度。
/// 成功应用/撤销后立即更新只读镜像，供 OfflineModeCoordinator 等同步读取。
/// </summary>
internal sealed class BackendCpuTopologyService : ICpuTopologyService
{
    private static BackendCpuFrameService Backend => BackendCpuFrameService.Instance;

    public Task<CpuTopology> GetTopologyAsync() => Backend.GetTopologyAsync();

    public async Task<bool> IsGameRunningAsync()
    {
        var status = await Backend.GetStatusAsync();
        return status.GameRunning;
    }

    public async Task<OperationResult> ApplyDualCcdSchedulingAsync(int gameCcdIndex)
    {
        var result = await Backend.ApplyDualCcdAsync(gameCcdIndex);
        if (result.Success)
        {
            CpuFrameStatusState.DualCcdSchedulingActive = true;
            CpuFrameStatusState.SingleCcdExcludeActive = false;
        }
        return result;
    }

    public async Task<OperationResult> RevertDualCcdSchedulingAsync()
    {
        var result = await Backend.RevertDualCcdAsync();
        if (result.Success) CpuFrameStatusState.DualCcdSchedulingActive = false;
        return result;
    }

    public Task<(bool Detected, int RemovedMb, string RawOutput)> DetectMemoryLimitAsync()
        => Backend.DetectMemoryLimitAsync();

    public Task<OperationResult> RemoveMemoryLimitAsync() => Backend.RemoveMemoryLimitAsync();

    public async Task<OperationResult> ExcludeCpu0FromGameAsync()
    {
        var result = await Backend.ApplySingleCcdAsync();
        if (result.Success)
        {
            CpuFrameStatusState.SingleCcdExcludeActive = true;
            CpuFrameStatusState.DualCcdSchedulingActive = false;
        }
        return result;
    }

    public async Task<OperationResult> RestoreGameFullCoresAsync()
    {
        var result = await Backend.RevertSingleCcdAsync();
        if (result.Success) CpuFrameStatusState.SingleCcdExcludeActive = false;
        return result;
    }
}
