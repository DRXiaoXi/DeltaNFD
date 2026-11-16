using DeltaNFD.Inspection;
using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

public sealed partial class ProcmanEngine
{
    public bool DpcCleanupPending { get { try { return DpcPendulumInspection.ReadJournal() is not null
        || _optimalClockHolder is DpcCombinedHolder { HasCleanupResponsibility: true }; } catch { return true; } } }
    public bool DpcJournalReadable { get { try { _ = DpcPendulumInspection.ReadJournal(); return true; } catch { return false; } } }
    public OperationResult ConfigureDpcPendulum(DpcPendulumConfigureArgs args)
    {
        if (!PendulumGlobalInspection.ExperimentalWindows10 || !Environment.Is64BitProcess) return OperationResult.Fail("自备 DPC 仅用于 Windows 10 x64 1809+；Win11 保持原路径。");
        if (!args.Confirmed) return OperationResult.Fail("尚未确认使用玩家自备的第三方 DPC，未保存。");
        if (!ElevationHelper.IsElevated || OfflineModeGuard.BlocksNormalAutomation) return OperationResult.Fail("权限或恢复状态不允许配置 DPC。");
        if (DeltaForceLocator.IsGameRunning()) return OperationResult.Fail("请先退出三角洲，再配置DPC来源。");
        lock (_gate)
        {
            if (_optimalClockScanInProgress || FrameBackgroundActive) return OperationResult.Fail("请先关闭帧格并等待检测结束，再选择或清除 DPC 文件。");
            if (!StopOptimalClock()) return OperationResult.Fail("旧 DPC 释放未确认，请先核对常驻记录。");
            try
            {
                var path = args.ExePath.Length == 0 ? "" : DpcPendulumInspection.CanonicalExe(args.ExePath);
                using var file = path.Length == 0 ? null : DpcPendulumInspection.OpenVerified(path);
                AppSettingsStore.UpdateRequired(s => { s.OptimalClockDpcExePath = path;
                    s.OptimalClockDpcAcceptedSha = path.Length == 0 ? "" : DpcPendulumSource.ExeSha256;
                    s.OptimalClockScanUtc = null; s.OptimalClockScanPlatform = "";
                    s.OptimalClockCustomVerifiedUtc = null; s.OptimalClockCustomPlatform = ""; });
                var read = AppSettingsStore.ReadRequired();
                if (read.OptimalClockDpcExePath != path || read.OptimalClockScanUtc is not null)
                    throw new IOException("DPC 配置回读不匹配。");
                return OperationResult.Ok(path.Length == 0 ? "已清除 DPC 文件选择；原件未删除。" : "原版 DPC 文件已核验并记录，尚未运行。请检测并保存 1.0000ms 持有方式。");
            }
            catch (Exception ex) { return OperationResult.Fail("DPC 文件配置未完成：" + ex.Message); }
        }
    }
    private OperationResult ProbeWin10Dpc(PendulumGlobalStatus admitted, bool only, CancellationToken token)
    {
        lock (_gate)
        {
            if (_optimalClockHolder is DpcCombinedHolder combined) combined.AlgorithmMode = false;
            token.ThrowIfCancellationRequested();
            var latest = ReadGlobalPrecision();
            if (!latest.Ready || latest.PlatformId != admitted.PlatformId || latest.ClockEnvironmentRevision != admitted.ClockEnvironmentRevision) return OperationResult.Fail("DPC 文件或系统身份已变化，未启动检测。");
            var accepted = _optimalClock.Apply(10000, supported: true);
            var status = _optimalClock.State;
            _pendulumLog.Write("dpc-process-probe", new { accepted, status });
            if (!StopOptimalClock("DPC 临时检测结束，已正常关闭本工具启动的程序"))
                return OperationResult.Fail("DPC 临时检测释放未确认，未保存；请关闭帧格并核对：" + _optimalClock.State.Message);
            if (!accepted || !status.DpcProcessVerified) return OperationResult.Fail(status.Message);
            token.ThrowIfCancellationRequested();
            if (only) return OperationResult.Ok("DPC 进程、原版服务和 ≤1ms 读数检测通过；临时程序已关闭。没有保存持有方式，没有测量 Sleep。");
            latest = ReadGlobalPrecision();
            if (!latest.Ready || latest.PlatformId != admitted.PlatformId || latest.ClockEnvironmentRevision != admitted.ClockEnvironmentRevision) return OperationResult.Fail("DPC 身份已变化，未保存。");
            var utc = DateTimeOffset.UtcNow;
            AppSettingsStore.UpdateRequired(s => { s.OptimalClockUseCustomResolution = false; s.OptimalClockResolution100ns = 10000; s.OptimalClockScanUtc = utc;
                s.OptimalClockScanPlatform = admitted.PlatformId; s.OptimalClockScanEnvironment = admitted.ClockEnvironmentRevision; s.OptimalClockBestTypical100ns = 0; s.OptimalClockBestStable100ns = 0;
                s.OptimalClockScanLoadThreads = 0; s.OptimalClockSafetyMarginMs = 0;
                s.OptimalClockStableConditions = "自备 DPC EXE 固定1ms；核对进程/服务/当前值，未测量Sleep"; });
            var saved = AppSettingsStore.ReadRequired();
            if (saved.OptimalClockResolution100ns != 10000 || saved.OptimalClockScanUtc != utc || saved.OptimalClockScanPlatform != admitted.PlatformId || saved.OptimalClockScanEnvironment != admitted.ClockEnvironmentRevision)
                return OperationResult.Fail("DPC 保存回读失败，未确认保存。");
            _optimalClock.RecordKernelSelection(10000, utc);
            _pendulumLog.Write("dpc-process-selection-saved", new { admitted.PlatformId, utc });
            return OperationResult.Ok("已保存 DPC 原版 1.0000ms 持有方式。开启帧格后运行原程序，关闭帧格或退出工具时正常关闭；未验证 Sleep 或游戏效果。");
        }
    }

    private OperationResult ScanWin10DpcAlgorithms(PendulumGlobalStatus admitted, DpcCombinedHolder holder,
        bool quick, OptimalClockScanKind kind, CancellationToken token)
    {
        try
        {
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                holder.AlgorithmMode = false;
                var latest = ReadGlobalPrecision();
                if (!latest.Ready || latest.PlatformId != admitted.PlatformId || latest.ClockEnvironmentRevision != admitted.ClockEnvironmentRevision) return OperationResult.Fail("DPC来源已变化，未开始算法扫描。");
                if (!_optimalClock.Apply(10000, true)) return OperationResult.Fail(_optimalClock.State.Message);
            }
            var measurement = new DpcAssistedMeasurement(_optimalClockMeasurement, () => holder.DpcHolding, token, _pendulumLog.Write);
            var scanner = new OptimalClockScanner(measurement, _pendulumLog.Write);
            var result = scanner.Scan(quick ? OptimalClockScanOptions.Quick : new(), kind, token);
            _pendulumLog.Write("dpc-assisted-scan-result", result);
            if (!result.Success || result.Mode != OptimalClockMode.Global || result.BestLock is not { } best
                || best.Resolution100ns is < OptimalClockScanner.CriticalFloor100ns or > 10000)
                return OperationResult.Fail("DPC辅助算法扫描未得到可保存的跨进程稳定值，原保存保持：" + result.Message);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                var beforeHold = ReadGlobalPrecision();
                if (!beforeHold.Ready || beforeHold.PlatformId != admitted.PlatformId || beforeHold.ClockEnvironmentRevision != admitted.ClockEnvironmentRevision || OfflineModeGuard.BlocksNormalAutomation)
                    return OperationResult.Fail("来源或恢复状态已变化，未进行候选联合持有。");
                holder.AlgorithmMode = true;
                // Validate the actual two-owner hold before saving, not just the scanner's short-lived candidate.
                if (!_optimalClock.Apply(best.Resolution100ns, true) || !_optimalClock.State.DpcProcessVerified || !_optimalClock.State.VerifiedGlobal)
                    return OperationResult.Fail("候选联合持有复验未通过，未保存：" + _optimalClock.State.Message);
                if (!StopOptimalClock("DPC辅助扫描结束，DPC和额外请求已释放")) return OperationResult.Fail("联合持有释放未确认，未保存。");
                token.ThrowIfCancellationRequested();
                var latest = ReadGlobalPrecision();
                if (!latest.Ready || latest.PlatformId != admitted.PlatformId || latest.ClockEnvironmentRevision != admitted.ClockEnvironmentRevision || OfflineModeGuard.BlocksNormalAutomation)
                    return OperationResult.Fail("来源或恢复状态已变化，未保存算法结果。");
                var profile = admitted.PlatformId + DpcPendulumSource.AlgorithmProfileSuffix;
                AppSettingsStore.UpdateRequired(s => {
                    s.OptimalClockUseCustomResolution = false;
                    s.OptimalClockResolution100ns = best.Resolution100ns; s.OptimalClockScanUtc = result.CompletedUtc;
                    s.OptimalClockScanPlatform = profile; s.OptimalClockScanEnvironment = admitted.ClockEnvironmentRevision; s.OptimalClockBestTypical100ns = result.BestTypical?.Resolution100ns ?? 0;
                    s.OptimalClockBestStable100ns = result.BestStable?.Resolution100ns ?? 0; s.OptimalClockScanLoadThreads = result.LoadThreads;
                    s.OptimalClockScanMode = kind == OptimalClockScanKind.StableLatency ? OptimalClockScanMode.StableLatency : OptimalClockScanMode.LowestLatency;
                    s.OptimalClockSafetyMarginMs = result.SafetyMarginMs; s.OptimalClockStableConditions = result.StableConditions;
                });
                var saved = AppSettingsStore.ReadRequired();
                if (saved.OptimalClockScanPlatform != profile || saved.OptimalClockResolution100ns != best.Resolution100ns || saved.OptimalClockScanUtc != result.CompletedUtc || saved.OptimalClockScanEnvironment != admitted.ClockEnvironmentRevision)
                    return OperationResult.Fail("算法保存回读不匹配，未确认保存。");
                _optimalClock.RecordScan(result);
                _pendulumLog.Write("dpc-assisted-selection-saved", new { profile, best.Resolution100ns, kind });
                return OperationResult.Ok($"已保存DPC辅助算法{(kind == OptimalClockScanKind.LowestLatency ? 1 : 2)}结果 {best.Milliseconds:0.0000}ms；开启帧格后同时运行DPC与额外请求。" + result.Message);
            }
        }
        finally
        {
            lock (_gate)
            {
                // The common caller also verifies release on errors; do not hide a pending cleanup.
                if (!StopOptimalClock("DPC辅助扫描已结束")) throw new IOException("DPC辅助扫描释放未确认，记录保留：" + _optimalClock.State.Message);
                holder.AlgorithmMode = false;
            }
        }
    }

    private bool SelectDpcSavedMode(PendulumGlobalStatus global)
    {
        if (_optimalClockHolder is not DpcCombinedHolder holder) return true;
        var algorithm = AppSettingsStore.Read().OptimalClockUseCustomResolution
            || AppSettingsStore.Read().OptimalClockScanPlatform == global.PlatformId + DpcPendulumSource.AlgorithmProfileSuffix;
        if (holder.AlgorithmMode == algorithm) return true;
        if (!StopOptimalClock("切换DPC持有方式前释放旧请求")) return false;
        holder.AlgorithmMode = algorithm;
        return true;
    }
}
