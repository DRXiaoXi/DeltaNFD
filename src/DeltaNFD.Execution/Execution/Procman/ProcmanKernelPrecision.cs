using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

public sealed partial class ProcmanEngine
{
    /// <summary>Win10: select a confirmed numerical resolution, never interpret Sleep as admission.</summary>
    private OperationResult ProbeWin10KernelPrecision(PendulumGlobalStatus admitted, bool compatibilityOnly, CancellationToken token)
    {
        uint selected = 0;
        var detail = "";
        foreach (var requested in new uint[] { 5000, 10000 })
        {
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var preparation = ReadGlobalPrecision();
                if (!preparation.Ready || preparation.PlatformId != admitted.PlatformId)
                    return OperationResult.Fail("Win10 准备状态已变化，驱动检测已停止：" + preparation.Message);
                var accepted = _optimalClock.Apply(requested, supported: true);
                var status = _optimalClock.State;
                detail = status.Message;
                _pendulumLog.Write("kernel-precision-probe", new { requested, accepted, status });
                if (!StopOptimalClock("Win10 驱动检测结束，已释放临时请求"))
                    return OperationResult.Fail("驱动请求释放未确认，检测结果未保存：" + Volatile.Read(ref _optimalClockError));
                if (accepted && status.KernelResolutionVerified) { selected = requested; break; }
            }
        }
        if (selected == 0) return OperationResult.Fail(detail);
        token.ThrowIfCancellationRequested();
        if (compatibilityOnly)
            return OperationResult.Ok($"Win10 驱动分辨率检测通过：{selected / 10000.0:0.0000}ms 请求与回读已确认，临时请求已释放；未检测 Sleep、未保存持有值。");
        lock (_gate)
        {
            var latest = ReadGlobalPrecision();
            if (!latest.Ready || !latest.ExperimentalWindows10 || latest.PlatformId != admitted.PlatformId
                || OfflineModeGuard.BlocksNormalAutomation)
                return OperationResult.Fail("系统或后端状态已变化，驱动检测结果未保存。");
            var completed = DateTimeOffset.UtcNow;
            AppSettingsStore.UpdateRequired(s => {
                s.OptimalClockUseCustomResolution = false;
                s.OptimalClockResolution100ns = selected;
                s.OptimalClockScanUtc = completed;
                s.OptimalClockScanPlatform = admitted.PlatformId;
                s.OptimalClockBestTypical100ns = 0;
                s.OptimalClockBestStable100ns = 0;
                s.OptimalClockScanLoadThreads = 0;
                s.OptimalClockSafetyMarginMs = 0;
                s.OptimalClockStableConditions = "仅驱动授予与分辨率回读，不检测 Sleep";
            });
            var saved = AppSettingsStore.ReadRequired();
            if (saved.OptimalClockResolution100ns != selected || saved.OptimalClockScanPlatform != admitted.PlatformId
                || saved.OptimalClockScanUtc != completed)
                return OperationResult.Fail("驱动持有值保存后回读不匹配，未授权新的持有值。");
            _optimalClock.RecordKernelSelection(selected, completed);
            _pendulumLog.Write("kernel-precision-saved", new { admitted.PlatformId, selected, completed });
        }
        return OperationResult.Ok($"已保存 Win10 驱动分辨率 {selected / 10000.0:0.0000}ms；随帧格开启并持续核对，关闭帧格释放。未检测 Sleep 或游戏效果。");
    }
}
