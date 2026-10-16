namespace DeltaNFD.Services;

/// <summary>
/// 前端只读镜像：由 CPU/帧格后端的状态快照更新，反映后端进程内实际生效的双/单 CCD 调度状态。
/// 供状态展示与目标切换阻塞判断使用；不执行任何调度、不建立监控。
/// 只读镜像独立于前端本地的实验室/脱机调度状态，避免二者互相覆盖。
/// </summary>
internal static class CpuFrameStatusState
{
    internal static volatile bool DualCcdSchedulingActive;
    internal static volatile bool SingleCcdExcludeActive;
}
