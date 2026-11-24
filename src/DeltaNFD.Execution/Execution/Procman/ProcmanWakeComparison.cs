using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

public sealed partial class ProcmanEngine
{
    public async Task<OperationResult> CompareWakeAsync(bool quick, CancellationToken token)
    {
        if (!PendulumFeatureEnabled || !OptimalClockSupported || _optimalClockMeasurement is not NativeOptimalClockMeasurementApi api)
            return OperationResult.Fail("当前系统或版本未开放钟摆对照测量。");
        using var environment = EnvironmentReadLease();
        var global = ReadGlobalPrecision();
        var dpc = global.ExperimentalWindows10 ? _optimalClockHolder as DpcCombinedHolder : null;
        if (!global.Ready || (global.ExperimentalWindows10 && (dpc is null || global.Win10Dpc is null)))
            return OperationResult.Fail("计时环境尚未就绪，请先完成本页准备。");
        var settings = AppSettingsStore.ReadRequired();
        uint? Reference(AppSettings s) => PendulumFramePolicy.Prepared(global,s) && (dpc is null || s.OptimalClockUseCustomResolution ||
            s.OptimalClockScanPlatform == global.PlatformId + DpcPendulumSource.AlgorithmProfileSuffix) ? PendulumFramePolicy.Resolution(s) : null;
        var reference = Reference(settings);
        var name = (dpc is null ? "" : "基础 DPC 1 ms") + (reference is uint t ? $" 已保存请求 {t/10000d:0.0000} ms" : dpc is null ? "系统默认（无额外请求）" : "（无额外请求）");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = cancellation.Token;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Guard()
        {
            ct.ThrowIfCancellationRequested();
            if (_memoryStopping > 0 || _memoryDisposed || FrameBackgroundActive || ClockGameRunning || OfflineModeGuard.BlocksNormalAutomation)
                throw new InvalidOperationException("请关闭帧格、退出游戏，并确认恢复流程已结束。");
        }
        void CheckIdentity()
        {
            var now = ReadGlobalPrecision();
            if (!now.Ready || now.Fingerprint != global.Fingerprint || now.PlatformId != global.PlatformId || now.ClockEnvironmentRevision != global.ClockEnvironmentRevision || Reference(AppSettingsStore.ReadRequired()) != reference)
                throw new InvalidOperationException("计时环境或基准设置已变化，结果无效。");
        }
        lock (_gate)
        {
            if (_optimalClockScanInProgress || _clockValueFinished is not null) return OperationResult.Fail("已有钟摆计时测试在运行。");
            try { Guard(); } catch (Exception e) { return OperationResult.Fail(ct.IsCancellationRequested ? "测量已取消。" : e.Message); }
            if (!StopOptimalClock("对照测量前释放旧请求")) return OperationResult.Fail("旧请求释放未确认，未开始测量。");
            _clockValueFinished=finished; _clockValueCancellation=cancellation; _optimalClockScanInProgress=true;
        }
        try
        {
            _pendulumLog.Write("comparison-start", new { quick, reference, name, loadThreads = OptimalClockScanOptions.DefaultLoadThreads, hz = 144 });
            WakeReport? report=null; string? failure=null;
            try
            {
                report = await Task.Run(() => {
                    Guard(); CheckIdentity();
                    if (dpc is not null) lock (_gate)
                    {
                        dpc.AlgorithmMode=false; _optimalClock.CustomResolution=false;
                        if (!_optimalClock.Apply(10000,true) || !dpc.DpcHolding) throw new IOException("基础 DPC 1 ms 未就绪。");
                    }
                    using var load = api.StartLoad(OptimalClockScanOptions.DefaultLoadThreads) ?? throw new IOException("未能启动测量负载。");
                    return WakeComparison.Run(reference,name,quick,(target,n) => {
                        Guard();
                        if (!load.EnsureActive() || dpc?.DpcHolding == false) throw new IOException("负载或基础 DPC 已退出。");
                        var row=api.MeasureWakeBlock(target,n);
                        Guard();
                        if (!load.IsActive || dpc?.DpcHolding == false) throw new IOException("采样期间负载或基础 DPC 已退出。");
                        return row;
                    },ct,_pendulumLog.Write);
                },ct).ConfigureAwait(false);
                Guard(); CheckIdentity();
            }
            catch (Exception ex) { failure=ex is OperationCanceledException ? "测量已取消" : ex.Message; }
            lock (_gate)
            {
                if (!StopOptimalClock("钟摆对照测量结束")) return OperationResult.Fail("释放未确认，报告无效："+_optimalClockError);
                if (failure is not null || report is null || ct.IsCancellationRequested) return OperationResult.Fail((failure ?? "测量已取消")+"；未修改保存值。");
                _pendulumLog.Write("comparison-report",report);
                return OperationResult.Ok(report.Message);
            }
        }
        finally
        {
            try { lock (_gate) {
                _clockValueFinished=null; _clockValueCancellation=null; _optimalClockScanInProgress=false;
                if (!_optimalClock.State.Holding && dpc is not null) dpc.AlgorithmMode=false;
                _optimalClock.CustomResolution = settings.OptimalClockUseCustomResolution;
            } } finally { finished.TrySetResult(); }
        }
    }
}
