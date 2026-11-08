using System.Diagnostics;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

public sealed partial class ProcmanEngine
{
    private readonly FrameGameTimerIgnore _frameTimerIgnore;
    private string _frameTimerIgnoreError = "";
    private DateTime _lastFrameTimerIgnoreTick;
    internal Func<IReadOnlyList<ProcmanProcessIdentity>>? FrameTimerIgnoreTargetsForTests { get; set; }

    /// <summary>PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION 仅 Windows 11 起支持。</summary>
    internal static bool FrameTimerIgnoreSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    private bool FrameTimerIgnoreRequested => (ReleaseFeaturePolicy.FrameTimerIgnoreEnabled || ExperimentalTimerFeaturesForTests)
        && FrameBackgroundActive && AppSettingsStore.Read().FrameGameTimerIgnoreEnabled;

    private FrameTimerIgnoreSnapshot FrameTimerIgnoreState
    {
        get
        {
            var state = _frameTimerIgnore.Snapshot;
            var error = Volatile.Read(ref _frameTimerIgnoreError);
            return error.Length == 0 ? state : state with { HasError = true, Message = error + "；" + state.Message };
        }
    }

    private bool RestoreFrameTimerIgnore(string reason = "三角洲定时器请求忽略状态已恢复")
    {
        var ok = _frameTimerIgnore.Sync([], _ => false, reason, FrameTimerIgnoreSupported);
        GameTargetService.Default.SetRestoreFailure("帧格忽略定时器请求", !ok);
        if (ok) Volatile.Write(ref _frameTimerIgnoreError, "");
        return ok;
    }

    private IReadOnlyList<ProcmanProcessIdentity> ReadFrameTimerIgnoreTargets()
    {
        if (FrameTimerIgnoreTargetsForTests is { } read) return read();
        var result = new List<ProcmanProcessIdentity>();
        foreach (var process in Process.GetProcessesByName("DeltaForceClient-Win64-Shipping"))
        {
            using (process)
            {
                var identity = ProcmanIdentity.TryIdentify(process.Id);
                if (identity is null) throw new IOException($"无法核验三角洲 PID {process.Id} 的路径和启动时间。");
                result.Add(identity);
            }
        }
        return result;
    }

    private bool FrameTimerIgnoreTick(bool force = false)
    {
        lock (_gate)
        {
            if (_frozen || !FrameTimerIgnoreRequested)
                return RestoreFrameTimerIgnore(_frozen ? "进程管理监控已停止，三角洲定时器请求忽略状态已恢复" : "帧格或该功能未开启，三角洲定时器请求忽略状态已恢复");
            var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
            if (!force && now - _lastFrameTimerIgnoreTick < TimeSpan.FromSeconds(2)) return !FrameTimerIgnoreState.HasError;
            _lastFrameTimerIgnoreTick = now;
            try
            {
                var targets = ReadFrameTimerIgnoreTargets();
                var ok = _frameTimerIgnore.Sync(targets, _ =>
                {
                    var latest = AppSettingsStore.Read();
                    return !_frozen && FrameTimerIgnoreRequested && (_rulesEngineRunning || BackgroundLoopDisabledForTests)
                        && latest.FrameGameTimerIgnoreEnabled;
                }, "等待三角洲启动，未施加定时器请求忽略", FrameTimerIgnoreSupported);
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
}
