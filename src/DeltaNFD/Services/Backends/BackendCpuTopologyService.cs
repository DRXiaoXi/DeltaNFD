namespace DeltaNFD.Services.Backends;

/// <summary>
/// CPU 拓扑 / 核心锁定（调度改善）的前端调用器：前端只读取拓扑并展示，不在本地施加调度。
/// 单/双 CCD 调度交由进程管理后端执行；拓扑读取、游戏状态与内存限制仍走 CPU/帧格后端。
/// 进程管理调用器在每次 CCD 命令后按 procman.status 刷新只读镜像，供 OfflineModeCoordinator 等同步读取。
/// </summary>
internal sealed class BackendCpuTopologyService : ICpuTopologyService
{
    private static BackendCpuFrameService Backend => BackendCpuFrameService.Instance;
    private static BackendProcmanService Procman => BackendProcmanService.Instance;

    public Task<CpuTopology> GetTopologyAsync() => Backend.GetTopologyAsync();

    public async Task<bool> IsGameRunningAsync()
    {
        var status = await Backend.GetStatusAsync();
        return status.GameRunning;
    }

    public Task<OperationResult> ApplyDualCcdSchedulingAsync(int gameCcdIndex) => Procman.ApplyDualCcdAsync(gameCcdIndex);

    public Task<OperationResult> RevertDualCcdSchedulingAsync() => Procman.RevertDualCcdAsync();

    public Task<(bool Detected, int RemovedMb, string RawOutput)> DetectMemoryLimitAsync()
        => Backend.DetectMemoryLimitAsync();

    public Task<OperationResult> RemoveMemoryLimitAsync() => Backend.RemoveMemoryLimitAsync();

    public Task<OperationResult> ExcludeCpu0FromGameAsync() => Procman.ApplySingleCcdAsync();

    public Task<OperationResult> RestoreGameFullCoresAsync() => Procman.RevertSingleCcdAsync();
}
