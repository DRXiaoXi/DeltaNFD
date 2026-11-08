using DeltaNFD.Services;
using DeltaNFD.Execution.OptimalClock;

namespace DeltaNFD.Execution.Procman;

public sealed partial class ProcmanEngine
{
    private readonly OptimalClockController _optimalClock;
    private readonly PendulumClockHistory _pendulumHistory;
    private readonly PendulumDiagnosticLog _pendulumLog;
    private string _lastClockGate = "";
    internal Func<PendulumGlobalStatus>? GlobalPrecisionForTests { get; set; }
    private PendulumGlobalStatus ReadGlobalPrecision() => GlobalPrecisionForTests?.Invoke() ?? DeltaNFD.Inspection.PendulumGlobalInspection.Read();
    private readonly IOptimalClockMeasurementApi _optimalClockMeasurement;
    private string _optimalClockError = "";
    private DateTime _lastOptimalClockTick;
    private bool _optimalClockStopped = true;
    private bool _optimalClockScanInProgress;

    /// <summary>Windows 11 常规路径；Windows 10 1809+ 实验路径仍须实测准入。</summary>
    internal static bool OptimalClockSupported => DeltaNFD.Inspection.PendulumGlobalInspection.Supported;

    private static bool ScanMatchesPlatform(PendulumGlobalStatus global)
        => global.ExperimentalWindows10
            ? AppSettingsStore.Read().OptimalClockScanPlatform == global.PlatformId
            : AppSettingsStore.Read().OptimalClockScanPlatform.Length == 0;

    private bool PendulumFeatureEnabled => ReleaseFeaturePolicy.PendulumEnabled || ExperimentalTimerFeaturesForTests;
    private bool OptimalClockRequested => PendulumFeatureEnabled && FrameBackgroundActive;
    private static uint OptimalClockResolution => AppSettingsStore.Read().OptimalClockResolution100ns;

    private OptimalClockSnapshot OptimalClockState
    {
        get
        {
            var state = _optimalClock.State;
            var error = Volatile.Read(ref _optimalClockError);
            return new OptimalClockSnapshot
            {
                Supported = OptimalClockSupported && state.Supported,
                Enabled = state.Enabled,
                Holding = state.Holding,
                Resolution100ns = state.Resolution100ns,
                Granted100ns = state.Granted100ns,
                Current100ns = state.Current100ns,
                VerifiedGlobal = state.VerifiedGlobal,
                RequestlessMilliseconds = state.RequestlessMilliseconds,
                Mode = state.Mode.ToString(),
                HasError = state.HasError || error.Length > 0,
                BestTypical100ns = state.BestTypical100ns,
                BestStable100ns = state.BestStable100ns,
                BestLock100ns = state.BestLock100ns,
                BestTypicalMedianMs = state.BestTypicalMedianMs,
                BestStableMedianMs = state.BestStableMedianMs,
                ScanRequestlessBaselineMs = state.ScanRequestlessBaselineMs,
                ScanLoadThreads = state.ScanLoadThreads,
                ScanMode = state.ScanKind == OptimalClockScanKind.StableLatency
                    ? OptimalClockScanMode.StableLatency : OptimalClockScanMode.LowestLatency,
                ScanSafetyMarginMs = state.SafetyMarginMs,
                ScanStableConditions = state.StableConditions,
                ScanUtc = state.ScanUtc,
                Message = error.Length == 0 ? state.Message : error + "；" + state.Message,
                UpdatedUtc = state.UpdatedUtc,
                Notices = _pendulumHistory.Snapshot,
                DiagnosticHistoryError = _pendulumHistory.Error,
                DiagnosticLogPath = _pendulumLog.CurrentPath,
                DiagnosticLogError = _pendulumLog.Error,
                GlobalVerifiedUtc = state.GlobalVerifiedUtc,
                ExperimentalWindows10 = DeltaNFD.Inspection.PendulumGlobalInspection.ExperimentalWindows10,
            };
        }
    }

    /// <summary>停止并释放钟摆计划请求（幂等）。</summary>
    private bool StopOptimalClock(string reason = "钟摆计划已释放")
    {
        try
        {
            // 即使内部状态误标为 stopped，也必须执行幂等 Stop，避免验证异常后的持有进程泄漏。
            if (!_optimalClock.Stop())
                throw new InvalidOperationException(_optimalClock.State.Message);
            _optimalClockStopped = true;
            _optimalClock.SetWaiting(reason, OptimalClockSupported);
            _frameTimerIgnore.Probe.Invalidate();
            GameTargetService.Default.SetRestoreFailure("钟摆计划", false);
            Volatile.Write(ref _optimalClockError, "");
            return true;
        }
        catch (Exception ex)
        {
            _optimalClockStopped = !_optimalClock.State.Holding;
            Volatile.Write(ref _optimalClockError, "钟摆计划释放失败：" + ex.Message);
            GameTargetService.Default.SetRestoreFailure("钟摆计划", true);
            return false;
        }
    }

    /// <summary>启用并锁定指定分辨率；只有实测确认全局生效才返回成功。</summary>
    public OperationResult ApplyOptimalClock(uint? resolution100ns)
    {
        if (!PendulumFeatureEnabled) return OperationResult.Fail("当前公测版本未开放钟摆计划。");
        lock (_gate)
        {
            if (!OptimalClockSupported)
                return OperationResult.Fail("此系统版本不在钟摆计划支持范围（Windows 10 1809 起）。");
            if (!OptimalClockRequested) return OperationResult.Fail("钟摆计划随帧格开启，请先开启帧格；不再提供独立锁定开关。");
            var global = ReadGlobalPrecision();
            if (!global.Ready) return OperationResult.Fail(global.Message);
            if (AppSettingsStore.Read().OptimalClockScanUtc is null || !ScanMatchesPlatform(global))
                return OperationResult.Fail("请先在钟摆计划扫描本机最佳值，再由帧格自动锁定。");
            var target = resolution100ns ?? OptimalClockResolution;
            try
            {
                var ok = _optimalClock.Apply(target, supported: true);
                _optimalClockStopped = !_optimalClock.State.Holding;
                _frameTimerIgnore.Probe.Invalidate();
                GameTargetService.Default.SetRestoreFailure("钟摆计划", !ok && _optimalClock.State.Holding);
                if (ok)
                {
                    Volatile.Write(ref _optimalClockError, "");
                    AppSettingsStore.Update(s => s.OptimalClockResolution100ns = target);
                    _ = StartRulesEngine();
                    return OperationResult.Ok(_optimalClock.State.Message);
                }
                return OperationResult.Fail(_optimalClock.State.Message);
            }
            catch (Exception ex)
            {
                var released = StopOptimalClock();
                var message = "钟摆计划验证异常：" + ex.Message;
                if (!released) message += "；请求释放未确认，保留释放责任：" + Volatile.Read(ref _optimalClockError);
                else message += "；已确认释放请求";
                Volatile.Write(ref _optimalClockError, message);
                return OperationResult.Fail(message);
            }
        }
    }

    /// <summary>关闭并释放钟摆计划。</summary>
    public OperationResult ReleaseOptimalClock()
    {
        bool stopLoop;
        lock (_gate)
        {
            if (OptimalClockRequested)
                return OperationResult.Fail("钟摆计划由帧格统一控制，请关闭帧格以释放精度锁定。");
            var result = StopOptimalClock();
            if (!result) return OperationResult.Fail("钟摆计划释放失败，恢复记录已保留。");
            stopLoop = _rules.EnabledRules().Count == 0 && !_automaticMemoryActive && !FramePriorityRequested
                && !FrameTimerIgnoreRequested && !TimerIgnoreListRequested;
        }
        // 不在 _gate 内等待规则线程退出；规则线程可能正等待同一把锁。
        return stopLoop
            ? StopRulesEngineAsync().GetAwaiter().GetResult()
            : OperationResult.Ok("钟摆计划已释放");
    }

    /// <summary>扫描本机最优分辨率（三级逼近 + 重复 + 双赢家），扫描期间暂停现有锁定。</summary>
    public async Task<OperationResult> ScanOptimalClockAsync(bool quick, CancellationToken token = default)
        => await ScanOptimalClockAsync(quick, OptimalClockScanKind.LowestLatency, token);

    /// <summary>按指定检测模式扫描（最低延迟 / 稳定延迟）。</summary>
    public async Task<OperationResult> ScanOptimalClockAsync(bool quick, OptimalClockScanKind scanKind, CancellationToken token = default,
        bool compatibilityOnly = false)
    {
        if (!PendulumFeatureEnabled) return OperationResult.Fail("当前公测版本未开放钟摆计划。");
        if (!OptimalClockSupported)
            return OperationResult.Fail("此系统版本不在钟摆计划支持范围（Windows 10 1809 起）。");
        var global = ReadGlobalPrecision();
        _pendulumLog.Write("scan-admission", new { global, quick, compatibilityOnly, algorithm = scanKind.ToString() });
        if (!global.Ready) return OperationResult.Fail(global.Message);
        lock (_gate)
        {
            if (_optimalClockScanInProgress) return OperationResult.Fail("钟摆计划扫描已在进行中。");
            if (!StopOptimalClock("扫描前释放现有钟摆计划请求"))
                return OperationResult.Fail("无法确认释放旧请求，已取消扫描：" + Volatile.Read(ref _optimalClockError));
            _optimalClockScanInProgress = true;
        }
        try
        {
            var options = quick ? OptimalClockScanOptions.Quick : new OptimalClockScanOptions();
            var scanner = new OptimalClockScanner(_optimalClockMeasurement, _pendulumLog.Write);
            var result = await Task.Run(() =>
            {
                if (global.ExperimentalWindows10 || compatibilityOnly)
                {
                    var compatibility = PendulumGlobalProbe.Verify(_optimalClockMeasurement, token, _pendulumLog.Write);
                    if (!compatibility.Success) return OptimalClockScanResult.Failed(compatibility.Message);
                    if (compatibilityOnly) return new OptimalClockScanResult
                    {
                        Success = true, Mode = OptimalClockMode.Global,
                        Message = "本机跨进程能力检测通过；没有进行 CPU 负载扫描，没有保存或授权新的全局锁定值。此结果只适用于当前系统。",
                    };
                }
                return scanner.Scan(options, scanKind, token);
            }, token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!StopOptimalClock("扫描后确认探针请求已释放"))
                    return OperationResult.Fail("扫描结果未保存，探针释放未确认：" + Volatile.Read(ref _optimalClockError));
            }
            if (!result.Success)
            {
                _pendulumLog.Write("scan-not-saved", new { result.Message });
                return OperationResult.Fail(result.Message);
            }
            if (compatibilityOnly)
            {
                _pendulumLog.Write("compatibility-only-complete", new { result.Message, global.PlatformId });
                return OperationResult.Ok(result.Message);
            }
            if (result.Mode != OptimalClockMode.Global)
            {
                _pendulumLog.Write("scan-self-only", new { result.Message, result.BestLock });
                return OperationResult.Fail("本次只验证了请求进程自身，未确认对其他进程生效；结果未保存，未启用全局锁定。请查看钟摆日志。");
            }
            var lockValue = result.BestLock;
            if (lockValue is null || lockValue.Resolution100ns < 5100)
                return OperationResult.Fail("扫描未得到安全侧的稳定锁定值，原设置保持不变。");
            lock (_gate)
            {
                var latestGlobal = ReadGlobalPrecision();
                if (!latestGlobal.Ready || latestGlobal.ExperimentalWindows10 != global.ExperimentalWindows10
                    || latestGlobal.PlatformId != global.PlatformId || OfflineModeGuard.BlocksNormalAutomation)
                    return OperationResult.Fail("全局精度前置条件已变化，扫描结果未保存；请重新核对。");
                _optimalClock.RecordScan(result);
                // 只保存通过安全侧稳定性复测的锁定值，不回退到典型/稳定展示赢家。
                AppSettingsStore.Update(s => {
                    s.OptimalClockResolution100ns = lockValue.Resolution100ns;
                    s.OptimalClockScanUtc = result.CompletedUtc;
                    s.OptimalClockScanPlatform = global.PlatformId;
                    s.OptimalClockBestTypical100ns = result.BestTypical?.Resolution100ns ?? 0;
                    s.OptimalClockBestStable100ns = result.BestStable?.Resolution100ns ?? 0;
                    s.OptimalClockScanLoadThreads = result.LoadThreads;
                    s.OptimalClockScanMode = result.ScanKind == OptimalClockScanKind.StableLatency
                        ? OptimalClockScanMode.StableLatency : OptimalClockScanMode.LowestLatency;
                    s.OptimalClockSafetyMarginMs = result.SafetyMarginMs;
                    s.OptimalClockStableConditions = result.StableConditions;
                });
                Volatile.Write(ref _optimalClockError, "");
                _pendulumLog.Write("scan-saved", new { global.PlatformId, result.BestLock, result.ScanKind });
            }
            return OperationResult.Ok(result.Message);
        }
        catch (Exception ex)
        {
            _pendulumLog.Write("scan-error", new { error = ex.ToString() });
            lock (_gate)
            {
                var released = StopOptimalClock();
                var message = ex is OperationCanceledException ? "钟摆计划扫描已取消" : "钟摆计划扫描未完成：" + ex.Message;
                if (!released) message += "；探针释放未确认：" + Volatile.Read(ref _optimalClockError);
                Volatile.Write(ref _optimalClockError, message);
                return OperationResult.Fail(message);
            }
        }
        finally
        {
            lock (_gate)
            {
                _optimalClockScanInProgress = false;
                if (OptimalClockRequested && !_frozen) _ = StartRulesEngine();
            }
        }
    }

    /// <summary>两秒节拍：启用中保持持有进程存活；未启用/冻结时释放。</summary>
    private bool OptimalClockTick(bool force = false)
    {
        lock (_gate)
        {
            if (_optimalClockScanInProgress) return true;
            if (_frozen || !OptimalClockRequested || !OptimalClockSupported)
                return StopOptimalClock(_frozen ? "进程管理监控已停止，钟摆计划已释放" : "钟摆计划未启用，已释放");
            var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
            if (!force && now - _lastOptimalClockTick < TimeSpan.FromSeconds(2)) return !OptimalClockState.HasError;
            _lastOptimalClockTick = now;
            try
            {
                var global = ReadGlobalPrecision();
                var settings = AppSettingsStore.Read();
                var gate = $"{global.Ready}/{global.PlatformId}/{settings.OptimalClockScanUtc}/{ScanMatchesPlatform(global)}";
                if (gate != _lastClockGate)
                {
                    _pendulumLog.Write("frame-clock-admission", new { global, settings.OptimalClockScanUtc, settings.OptimalClockScanPlatform });
                    _lastClockGate = gate;
                }
                if (!global.Ready || settings.OptimalClockScanUtc is null || !ScanMatchesPlatform(global)
                    || OptimalClockResolution < OptimalClockScanner.CriticalFloor100ns)
                {
                    var reason = !global.Ready ? global.Message : "帧格已开启；请先在钟摆计划扫描本机最佳值，完成后自动锁定。";
                    return StopOptimalClock(reason);
                }
                var ok = _optimalClock.Ensure(enabled: true, resolution100ns: OptimalClockResolution, supported: true);
                _optimalClockStopped = !_optimalClock.State.Holding;
                _frameTimerIgnore.Probe.Invalidate();
                GameTargetService.Default.SetRestoreFailure("钟摆计划", !ok && _optimalClock.State.Holding);
                if (ok) Volatile.Write(ref _optimalClockError, "");
                return ok;
            }
            catch (Exception ex)
            {
                StopOptimalClock("钟摆计划监督失败，已释放");
                Volatile.Write(ref _optimalClockError, "钟摆计划未执行：" + ex.Message);
                return false;
            }
        }
    }

    internal bool OptimalClockOnceForTests() => OptimalClockTick(force: true);
}
