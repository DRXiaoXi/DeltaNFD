using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

public sealed partial class ProcmanEngine
{
    private CancellationTokenSource? _clockValueCancellation;
    private TaskCompletionSource? _clockValueFinished;
    private ClockSleepResult? _clockSleepResult;
    internal Func<bool>? ClockGameRunningForTests { get; set; }
    private bool ClockGameRunning => ClockGameRunningForTests?.Invoke()
        ?? (GameTargetService.Default.IsRunning() || DeltaForceLocator.IsGameRunning());

    /// <summary>A bounded experiment owns the same measurement/holder lane as scanning; it never edits auto results.</summary>
    public async Task<OperationResult> ClockValueAsync(ClockValueArgs args, CancellationToken token = default)
    {
        if (!PendulumFeatureEnabled || !OptimalClockSupported) return OperationResult.Fail("当前系统或版本未开放钟摆计划。");
        if (!Enum.IsDefined(args.Action)) return OperationResult.Fail("未知的钟摆操作，未执行。");
        var custom = args.Action is ClockValueAction.SaveCustom or ClockValueAction.TestCustomSleep;
        var testing = args.Action is ClockValueAction.TestCustomSleep or ClockValueAction.TestAutomaticSleep;
        if (!custom && args.Resolution100ns is not null) return OperationResult.Fail("自动值由后端读取，不接受额外目标值。");
        using var environmentLease = EnvironmentReadLease();
        using var targetLease = GameTargetService.Default.BeginOperation();
        PendulumGlobalStatus admitted;
        AppSettings original;
        uint target;
        CancellationTokenSource cancellation;
        TaskCompletionSource finished;
        Func<bool> targetCurrent;
        lock (_gate)
        {
            if (_optimalClockScanInProgress || _clockValueFinished is not null || _memoryStopping > 0 || _memoryDisposed)
                return OperationResult.Fail("钟摆检测或后端收尾正在进行，请等待完成。");
            if (FrameBackgroundActive || ClockGameRunning) return OperationResult.Fail("请先关闭帧格并退出目标游戏，再测试或更换分辨率。");
            if (OfflineModeGuard.BlocksNormalAutomation) return OperationResult.Fail("恢复状态不允许开始钟摆实验。");
            admitted = ReadGlobalPrecision();
            if (!admitted.Ready) return OperationResult.Fail(admitted.Message);
            if (admitted.ExperimentalWindows10 && _optimalClockHolder is not DpcCombinedHolder)
                return OperationResult.Fail("Win10 自定义和 Sleep 测试需要当前自备 DPC 联合持有路径。");
            original = AppSettingsStore.ReadRequired();
            if (!custom && !PendulumFramePolicy.AutomaticPrepared(admitted, original))
                return OperationResult.Fail("尚无与当前准备状态匹配的自动保存值，请先完成检测或扫描。");
            if (args.Action == ClockValueAction.TestAutomaticSleep && !PendulumFramePolicy.AutomaticOptimumPrepared(admitted, original))
                return OperationResult.Fail("尚未自动测得最优值；基础 DPC 1ms 记录不冒充自动最优值。");
            target = custom ? args.Resolution100ns ?? 0 : original.OptimalClockResolution100ns;
            if (target < 5000 || target > (admitted.ExperimentalWindows10 ? 10000u : 156250u))
                return OperationResult.Fail(admitted.ExperimentalWindows10 ? "Win10 自定义范围为 0.5000～1.0000ms。" : "自定义范围为 0.5000～15.6250ms。");
            if (!StopOptimalClock("单值操作前释放旧钟摆请求")) return OperationResult.Fail("旧请求释放未确认，未开始单值操作。");
            var captured = FrameTarget;
            targetCurrent = () => FrameTargetCurrent(captured);
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _clockValueCancellation = cancellation; _clockValueFinished = finished; _optimalClockScanInProgress = true;
        }
        var ct = cancellation.Token;
        var failure = "";
        ClockSleepResult? measured = null;
        bool Allowed()
        {
            ct.ThrowIfCancellationRequested();
            var current = ReadGlobalPrecision();
            if (_memoryStopping > 0 || _memoryDisposed || FrameBackgroundActive || ClockGameRunning || !targetCurrent()
                || OfflineModeGuard.BlocksNormalAutomation || !current.Ready || current.Fingerprint != admitted.Fingerprint
                || current.PlatformId != admitted.PlatformId || current.ClockEnvironmentRevision != admitted.ClockEnvironmentRevision
                || PendulumFramePolicy.CustomSource(current) != PendulumFramePolicy.CustomSource(admitted)) return false;
            var settings = AppSettingsStore.ReadRequired();
            return custom || settings.OptimalClockResolution100ns == original.OptimalClockResolution100ns
                && settings.OptimalClockScanUtc == original.OptimalClockScanUtc && settings.OptimalClockScanPlatform == original.OptimalClockScanPlatform;
        }
        try
        {
            try
            {
                await Task.Run(() =>
                {
                    lock (_gate)
                    {
                        if (!Allowed()) throw new IOException("准备或游戏状态已变化，未开始请求。");
                        _optimalClock.CustomResolution = custom;
                        if (admitted.ExperimentalWindows10 && _optimalClockHolder is DpcCombinedHolder combined)
                        {
                            combined.AlgorithmMode = args.Action == ClockValueAction.SaveCustom;
                            if (testing && !_optimalClock.Apply(10000, true)) throw new IOException(_optimalClock.State.Message);
                        }
                    }
                    if (testing)
                    {
                        if (admitted.ExperimentalWindows10 && _optimalClockHolder is DpcCombinedHolder before && !before.DpcHolding)
                            throw new IOException("DPC 基底已退出，未测量。");
                        measured = new ClockValueTester(_optimalClockMeasurement).Test(target, custom, ct);
                        if (admitted.ExperimentalWindows10 && _optimalClockHolder is DpcCombinedHolder after && !after.DpcHolding)
                            measured = measured with { TargetVerified = false, Message = "测量期间 DPC 基底退出，结果不能代表联合持有。" };
                    }
                    else if (args.Action == ClockValueAction.SaveCustom)
                    {
                        lock (_gate)
                        {
                            if (!Allowed() || !_optimalClock.Apply(target, true) || !_optimalClock.State.VerifiedGlobal
                                || Math.Abs((long)_optimalClockHolder.Current100ns - target) > 50
                                || _optimalClockHolder.Granted100ns == 0 || Math.Abs((long)_optimalClockHolder.Granted100ns - target) > 50)
                                throw new IOException("自定义值持有与独立等待核验未通过，未保存：" + _optimalClock.State.Message);
                        }
                    }
                    ct.ThrowIfCancellationRequested();
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) { failure = ex is OperationCanceledException ? "钟摆单值操作已取消。" : "钟摆单值操作未完成：" + ex.Message; }
            lock (_gate)
            {
                var released = StopOptimalClock("钟摆单值操作结束，临时请求已释放");
                if (!released) failure += " 临时请求释放未确认，保留恢复责任；未保存或切换分辨率。";
                try
                {
                    if (failure.Length == 0 && !Allowed()) failure = "准备、游戏状态或自动结果已变化，本次结果不保存。";
                    if (testing)
                    {
                        measured ??= new() { Custom = custom, Requested100ns = target, CompletedUtc = DateTimeOffset.UtcNow };
                        measured = measured with { ReleaseConfirmed = released,
                            TargetVerified = measured.TargetVerified && failure.Length == 0,
                            Message = failure.Length > 0 ? failure : measured.Message };
                        Volatile.Write(ref _clockSleepResult, measured);
                        _pendulumLog.Write("single-value-sleep", measured);
                        return measured.TargetVerified && released ? OperationResult.Ok(measured.Message) : OperationResult.Fail(measured.Message);
                    }
                    if (failure.Length > 0) return OperationResult.Fail(failure);
                    if (args.Action == ClockValueAction.SaveCustom)
                    {
                        var source = PendulumFramePolicy.CustomSource(admitted); var utc = DateTimeOffset.UtcNow;
                        AppSettingsStore.UpdateRequired(s => { s.OptimalClockCustomResolution100ns = target; s.OptimalClockCustomEnvironment = admitted.ClockEnvironmentRevision;
                            s.OptimalClockCustomVerifiedUtc = utc; s.OptimalClockCustomPlatform = source; s.OptimalClockUseCustomResolution = true; });
                        var read = AppSettingsStore.ReadRequired();
                        if (!read.OptimalClockUseCustomResolution || read.OptimalClockCustomResolution100ns != target
                            || read.OptimalClockCustomVerifiedUtc != utc || read.OptimalClockCustomPlatform != source
                            || read.OptimalClockCustomEnvironment != admitted.ClockEnvironmentRevision)
                            throw new IOException("自定义配置写后回读不匹配。");
                        _pendulumLog.Write("custom-value-saved", new { target, utc, source });
                        return OperationResult.Ok($"已验证并保存自定义 {target / 10000.0:0.0000}ms；开启帧格后使用，原自动扫描结果保留。");
                    }
                    AppSettingsStore.UpdateRequired(s => s.OptimalClockUseCustomResolution = false);
                    if (AppSettingsStore.ReadRequired().OptimalClockUseCustomResolution) throw new IOException("自动值选择回读未确认。");
                    return OperationResult.Ok($"已选择原自动保存值 {target / 10000.0:0.0000}ms；自定义记录保留。");
                }
                catch (Exception ex)
                {
                    var message = ex is OperationCanceledException ? "钟摆单值操作已取消。" : "钟摆单值结果未确认保存：" + ex.Message;
                    if (testing) Volatile.Write(ref _clockSleepResult, new() { Custom = custom, Requested100ns = target,
                        CompletedUtc = DateTimeOffset.UtcNow, ReleaseConfirmed = released, Message = message });
                    return OperationResult.Fail(message);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _optimalClockScanInProgress = false; _clockValueCancellation = null; _clockValueFinished = null;
                if (!_optimalClock.State.Holding && _optimalClockHolder is DpcCombinedHolder combined) combined.AlgorithmMode = false;
                _optimalClock.CustomResolution = AppSettingsStore.Read().OptimalClockUseCustomResolution;
            }
            cancellation.Dispose(); finished.TrySetResult();
        }
    }
}
