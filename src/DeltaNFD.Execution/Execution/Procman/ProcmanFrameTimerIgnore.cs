using System.Diagnostics;
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

    // The old independent preference is retained in settings for compatibility, but preparation
    // and a live verified clock now own this part of the frame lifecycle on both platforms.
    private bool FrameTimerIgnoreRequested => (ReleaseFeaturePolicy.FrameTimerIgnoreEnabled || ExperimentalTimerFeaturesForTests)
        && OptimalClockRequested && PendulumPrepared && PendulumClockReadyForIgnore();

    private bool PendulumClockReadyForIgnore()
    {
        try
        {
            var state = _optimalClock.State;
            if (!state.Holding || state.HasError || !_optimalClockHolder.Holding || state.Resolution100ns != OptimalClockResolution) return false;
            var current = _optimalClockHolder.Current100ns;
            if (_optimalClockHolder.DpcProcess && !_optimalClockHolder.DpcAssisted)
                return state.DpcProcessVerified && current is > 0 and <= 10000;
            return state.VerifiedGlobal && (!_optimalClockHolder.DpcAssisted || state.DpcProcessVerified)
                && current > 0 && Math.Abs((long)current - state.Resolution100ns) <= 50;
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
            var error = Volatile.Read(ref _frameTimerIgnoreError);
            return error.Length == 0 ? state : state with { HasError = true, Message = error + "；" + state.Message };
        }
    }

    private bool RestoreFrameTimerIgnore(string reason = "游戏定时器请求忽略状态已恢复")
    {
        var ok = _frameTimerIgnore.Sync([], _ => false, reason, FrameTimerIgnoreSupported);
        GameTargetService.Default.SetRestoreFailure("帧格忽略定时器请求", !ok);
        if (ok) Volatile.Write(ref _frameTimerIgnoreError, "");
        return ok;
    }


    private bool FrameTimerIgnoreTick(bool force = false)
    {
        lock (_gate)
        {
            if (_frozen || !FrameTimerIgnoreRequested)
            {
                _frameTimerPolicyResult = null;
                return RestoreFrameTimerIgnore(_frozen ? "进程管理监控已停止，游戏定时器请求忽略状态已恢复" : "帧格或该功能未开启，游戏定时器请求忽略状态已恢复");
            }
            var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
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
                        return RestoreFrameTimerIgnore("Win10 实验：本次策略能力自检未通过，未施加忽略位。" + _frameTimerPolicyResult.Error);
                }
                var ok = _frameTimerIgnore.Sync(targets, id =>
                {
                    return !_frozen && FrameTargetCurrent(target) && IsSelectedFrameTarget(id, target)
                        && FrameTimerIgnoreRequested && (_rulesEngineRunning || BackgroundLoopDisabledForTests);
                }, "等待 " + target.DisplayName + " 启动，未施加定时器请求忽略", FrameTimerIgnoreSupported);
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
