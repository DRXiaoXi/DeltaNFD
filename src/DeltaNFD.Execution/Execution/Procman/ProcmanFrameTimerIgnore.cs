using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

public sealed partial class ProcmanEngine
{
    private readonly FrameGameTimerIgnore _frameTimerIgnore;
    private string _frameTimerIgnoreError = "";
    private DateTime _lastFrameTimerIgnoreTick;
    internal Func<IReadOnlyList<ProcmanProcessIdentity>>? FrameTimerIgnoreTargetsForTests { get; set; }

    /// <summary>Win11 常规路径；Win10 1809+ 仅在隔离自检通过后试用。</summary>
    internal static bool FrameTimerIgnoreSupported => OperatingSystem.IsWindows() && FrameTimerPolicyProbeHost.Candidate(Environment.OSVersion.Version);
    internal Func<bool>? FrameTimerIgnoreWindows10ForTests { get; set; }
    internal Func<FrameTimerPolicyResult>? FrameTimerPolicyProbeForTests { get; set; }
    private bool FrameTimerIgnoreWindows10 => FrameTimerIgnoreWindows10ForTests?.Invoke()
        ?? (FrameTimerIgnoreSupported && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000));
    private FrameTimerPolicyResult? _frameTimerPolicyResult;
    private sealed record TimerPolicyObservation(uint Before, uint After, HashSet<ProcmanProcessIdentity> Targets, double? RequestlessMs);
    private TimerPolicyObservation? _frameTimerObservation;
    private DateTime _frameTimerRetryAfter;
    private string _frameTimerRetryReason = "";

    private bool FrameTimerIgnoreSelected => (ReleaseFeaturePolicy.FrameTimerIgnoreEnabled || ExperimentalTimerFeaturesForTests)
        && OptimalClockRequested && ReadPendulumPreparation() is { Prepared: true, UsesGameDefault: false };
    private bool FrameTimerIgnoreRequested => FrameTimerIgnoreSelected && PendulumClockReadyForIgnore();

    private bool PendulumClockReadyForIgnore()
    {
        try
        {
            var state = _optimalClock.State;
            if (!state.Holding || state.HasError || !_optimalClockHolder.Holding || state.Resolution100ns != OptimalClockResolution) return false;
            var current = _optimalClockHolder.Current100ns;
            if (_optimalClockHolder.DpcProcess && !_optimalClockHolder.DpcAssisted)
                return state.DpcProcessVerified && current is > 0 and <= 10000;
            if (!state.VerifiedGlobal || current == 0) return false;
            if (_optimalClockHolder.DpcAssisted)
                return state.DpcProcessVerified && Math.Abs((long)current - state.Resolution100ns) <= 50;
            // Match the ordinary holder's verification: a finer live clock is valid after
            // independent verification. The game still requires its own reachability/read-back checks.
            return current <= (ulong)state.Resolution100ns + OptimalClockController.ResolutionTolerance100ns;
        }
        catch { return false; }
    }

    private FrameTimerIgnoreSnapshot FrameTimerIgnoreState
    {
        get
        {
            var state = _frameTimerIgnore.Snapshot;
            if (FrameTimerIgnoreWindows10)
                state = state with { ExperimentalWindows10 = true, PolicyAccepted = _frameTimerPolicyResult?.Accepted,
                    Supported = state.Supported && _frameTimerPolicyResult?.Accepted == true };
            if (Volatile.Read(ref _frameTimerObservation) is { } sample && FullyMaintaining(state))
                state = state with { PostApplyReachabilityConfirmed = true, BeforePolicyClock100ns = sample.Before, AfterPolicyClock100ns = sample.After,
                    SystemCurrentMilliseconds = sample.After / 10000.0, RequestlessGranularityMilliseconds = sample.RequestlessMs };
            var error = Volatile.Read(ref _frameTimerIgnoreError);
            return error.Length == 0 ? state : state with { HasError = true, Message = error + "；" + state.Message };
        }
    }

    private static bool FullyMaintaining(FrameTimerIgnoreSnapshot state) => state.Maintaining && !state.HasError && !state.RestorePending
        && state.InstanceCount > 0 && state.AppliedCount == state.InstanceCount;

    private uint ReadClockForTimerIgnore()
    {
        try { return _optimalClockHolder.Current100ns; }
        catch { return 0; }
    }

    private bool RestoreFrameTimerIgnore(string reason = "游戏定时器请求忽略状态已恢复")
    {
        Volatile.Write(ref _frameTimerObservation, null);
        var ok = _frameTimerIgnore.Sync([], _ => false, reason, FrameTimerIgnoreSupported);
        GameTargetService.Default.SetRestoreFailure("帧格忽略定时器请求", !ok);
        if (ok) Volatile.Write(ref _frameTimerIgnoreError, "");
        return ok;
    }


    private bool FrameTimerIgnoreTick(bool force = false)
    {
        lock (_gate)
        {
            var selected = !_frozen && FrameTimerIgnoreSelected;
            var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
            if (!selected)
            {
                _frameTimerPolicyResult = null;
                _frameTimerRetryAfter = default; _frameTimerRetryReason = "";
                return RestoreFrameTimerIgnore(_frozen ? "进程管理监控已停止，游戏定时器请求忽略状态已恢复"
                    : "帧格或钟摆联动未启用，游戏定时器请求忽略状态已恢复");
            }
            if (now < _frameTimerRetryAfter)
                return RestoreFrameTimerIgnore(_frameTimerRetryReason);
            _frameTimerRetryReason = "";
            if (!PendulumClockReadyForIgnore())
            {
                _frameTimerPolicyResult = null;
                return RestoreFrameTimerIgnore("钟摆持有尚未核验就绪，未施加精度请求忽略");
            }
            if (!force && now - _lastFrameTimerIgnoreTick < TimeSpan.FromSeconds(2)) return !FrameTimerIgnoreState.HasError;
            _lastFrameTimerIgnoreTick = now;
            try
            {
                using var targetOperation = GameTargetService.Default.BeginOperation();
                var target = FrameTarget;
                var targets = ReadFrameGameTargets(target, FrameTimerIgnoreTargetsForTests);
                if (FrameTimerIgnoreWindows10 && targets.Count > 0)
                {
                    _frameTimerPolicyResult ??= FrameTimerPolicyProbeForTests?.Invoke() ?? FrameTimerPolicyProbeHost.RunIsolated();
                    if (!_frameTimerPolicyResult.Accepted)
                        return RestoreFrameTimerIgnore("Win10 策略能力自检未通过，未施加忽略位。" + _frameTimerPolicyResult.Error);
                }
                var before = ReadClockForTimerIgnore();
                var previous = Interlocked.Exchange(ref _frameTimerObservation, null);
                var revision = _frameTimerIgnore.ApplyWriteRevision;
                bool StillAllowed(ProcmanProcessIdentity id) => !_frozen && FrameTargetCurrent(target) && IsSelectedFrameTarget(id, target)
                    && FrameTimerIgnoreRequested && (_rulesEngineRunning || BackgroundLoopDisabledForTests);
                var ok = _frameTimerIgnore.Sync(targets, StillAllowed,
                    "等待 " + target.DisplayName + " 启动，未施加定时器请求忽略", FrameTimerIgnoreSupported);
                var observed = _frameTimerIgnore.Snapshot;
                var after = ReadClockForTimerIgnore();
                // A pre-apply probe can be fine only because the game itself was requesting
                // 0.5000 ms. Measure again without that request before claiming success.
                var wrote = revision != _frameTimerIgnore.ApplyWriteRevision;
                var reprobe = observed.AppliedCount > 0 && (previous is null || wrote || previous.After != after
                    || !previous.Targets.SetEquals(targets) || !FullyMaintaining(observed));
                if (!FullyMaintaining(observed) && (observed.AppliedCount > 0 || wrote))
                {
                    // A partial/unknown group could still be supplying the finer clock.
                    // Don't verify the replacement against a mix of ignored and non-ignored targets.
                    _frameTimerRetryAfter = (ClockForTests?.Invoke() ?? DateTime.UtcNow).AddSeconds(30);
                    _frameTimerRetryReason = "游戏忽略策略未全部确认，未保留本轮接管；30 秒后复核。";
                    RestoreFrameTimerIgnore(_frameTimerRetryReason);
                    Volatile.Write(ref _frameTimerIgnoreError, observed.Message);
                    return false;
                }
                if (observed.AppliedCount > 0)
                {
                    var reachable = reprobe ? _frameTimerIgnore.Probe.Probe(force: true) : observed.LegacyGlobalReachable;
                    after = ReadClockForTimerIgnore();
                    var holderReady = reachable == true && after != 0 && PendulumClockReadyForIgnore();
                    if (reprobe)
                        _pendulumLog.Write("frame-ignore-post-check", new { target.DisplayName, saved = OptimalClockResolution,
                            before, after, observed.InstanceCount, observed.AppliedCount, reachable,
                            requestlessMs = _frameTimerIgnore.Probe.GranularityMs, holderReady });
                    if (!holderReady)
                    {
                        _frameTimerRetryAfter = (ClockForTests?.Invoke() ?? DateTime.UtcNow).AddSeconds(30);
                        _frameTimerRetryReason = "忽略后未确认钟摆时钟仍可用；保留游戏原精度，30 秒后复核。";
                        _frameTimerIgnore.Probe.Invalidate();
                        return RestoreFrameTimerIgnore(_frameTimerRetryReason);
                    }
                    Volatile.Write(ref _frameTimerObservation, new TimerPolicyObservation(reprobe ? before : previous!.Before,
                        after, reprobe ? targets.ToHashSet() : previous!.Targets, _frameTimerIgnore.Probe.GranularityMs));
                }
                Volatile.Write(ref _frameTimerIgnoreError, "");
                GameTargetService.Default.SetRestoreFailure("帧格忽略定时器请求", _frameTimerIgnore.Snapshot.RestorePending);
                return ok;
            }
            catch (Exception ex)
            {
                RestoreFrameTimerIgnore("进程身份或配置无法核对，已恢复可确认的定时器忽略原值");
                Volatile.Write(ref _frameTimerIgnoreError, "定时器请求忽略未执行：" + ex.Message);
                return false;
            }
        }
    }

    internal bool FrameTimerIgnoreOnceForTests() => FrameTimerIgnoreTick(force: true);

    private bool FramePendulumTick(bool force = false)
    {
        var clock = OptimalClockTick(force);
        var ignore = FrameTimerIgnoreTick(force);
        return clock && ignore;
    }
    internal bool FramePendulumOnceForTests() => FramePendulumTick(force: true);
}
