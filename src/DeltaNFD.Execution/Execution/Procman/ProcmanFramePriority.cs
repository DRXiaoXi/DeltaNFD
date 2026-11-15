using System.Diagnostics;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

public sealed partial class ProcmanEngine
{
    private readonly FrameGamePriority _framePriority;
    private FrameNvidiaGpuShare _frameNvidiaGpuShare;
    private DateTime _lastNvidiaGpuTick;
    private int NvidiaGpuShareHeldCount => _frameNvidiaGpuShare.Snapshot is { Maintaining: true } or { RestorePending: true } ? 1 : 0;
    private string _framePriorityError = "";
    private DateTime _lastFramePriorityTick;
    internal Func<IReadOnlyList<ProcmanProcessIdentity>>? FramePriorityTargetsForTests { get; set; }
    private bool FramePriorityRequested => FrameBackgroundActive && (AppSettingsStore.Read().FrameGamePriorityEnabled || AppSettingsStore.Read().FrameGameGpuPriorityEnabled);
    private bool FrameNvidiaGpuRequested => FrameBackgroundActive && AppSettingsStore.Read().FrameNvidiaGpuShareEnabled;
    private FrameGamePrioritySnapshot FramePriorityState
    {
        get
        {
            var state = _framePriority.Snapshot with { GpuProtocolVersion = 1 };
            var error = Volatile.Read(ref _framePriorityError);
            return error.Length == 0 ? state : state with { HasError = true, Message = error + "；" + state.Message };
        }
    }
    private bool RestoreFrameProcessPriorities(string reason)
    {
        var ok = _framePriority.Sync([], new(), _ => false, reason);
        GameTargetService.Default.SetRestoreFailure("帧格进程优先级", !ok);
        if (ok) Volatile.Write(ref _framePriorityError, "");
        return ok;
    }
    private bool RestoreFramePriorities(string reason = "游戏进程优先级原值已恢复")
    {
        var ok = RestoreFrameProcessPriorities(reason);
        var nvidia = _frameNvidiaGpuShare.Sync(false, 10, () => false, resetBlock: true);
        GameTargetService.Default.SetRestoreFailure("帧格 NVIDIA 前后台比例", !nvidia || _frameNvidiaGpuShare.Snapshot.RestorePending);
        if (!nvidia) Volatile.Write(ref _framePriorityError, _frameNvidiaGpuShare.Snapshot.Message);
        return ok && nvidia;
    }
    private bool FramePriorityTick(bool force = false)
    {
        var process = FrameProcessPriorityTick(force);
        var nvidia = FrameNvidiaGpuTick(force);
        return process && nvidia;
    }
    private bool FrameNvidiaGpuTick(bool force)
    {
        lock (_gate)
        {
            var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
            if (!force && now - _lastNvidiaGpuTick < TimeSpan.FromSeconds(2)) return !_frameNvidiaGpuShare.Snapshot.HasError;
            _lastNvidiaGpuTick = now;
            try
            {
                using var operation = GameTargetService.Default.BeginOperation();
                var settings = AppSettingsStore.Read(); var target = FrameTarget;
                bool Allowed() => !_frozen && FrameBackgroundActive && FrameTargetCurrent(target)
                    && AppSettingsStore.Read().FrameNvidiaGpuShareEnabled
                    && AppSettingsStore.Read().FrameNvidiaGpuBackgroundPercent == settings.FrameNvidiaGpuBackgroundPercent
                    && (_rulesEngineRunning || BackgroundLoopDisabledForTests)
                    && ReadFrameGameTargets(target, FramePriorityTargetsForTests).Count > 0;
                var requested = settings.FrameNvidiaGpuShareEnabled && FrameBackgroundActive && !_frozen;
                var result = _frameNvidiaGpuShare.Sync(requested && Allowed(), settings.FrameNvidiaGpuBackgroundPercent, Allowed,
                    resetBlock: !requested);
                GameTargetService.Default.SetRestoreFailure("帧格 NVIDIA 前后台比例", _frameNvidiaGpuShare.Snapshot.RestorePending);
                return result;
            }
            catch
            {
                var result = _frameNvidiaGpuShare.Sync(false, 10, () => false);
                GameTargetService.Default.SetRestoreFailure("帧格 NVIDIA 前后台比例", !result || _frameNvidiaGpuShare.Snapshot.RestorePending);
                return result;
            }
        }
    }
    private bool FrameProcessPriorityTick(bool force = false)
    {
        lock (_gate)
        {
            if (_frozen || !FramePriorityRequested)
                return RestoreFrameProcessPriorities(_frozen ? "进程管理监控已停止，游戏优先级原值已恢复" : "帧格或该功能未开启，游戏优先级原值已恢复");
            var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
            if (!force && now - _lastFramePriorityTick < TimeSpan.FromSeconds(2)) return !FramePriorityState.HasError;
            _lastFramePriorityTick = now;
            try
            {
                using var targetOperation = GameTargetService.Default.BeginOperation();
                var target = FrameTarget;
                var settings = AppSettingsStore.Read();
                var config = new FrameGamePriorityConfig { ProcessEnabled = settings.FrameGamePriorityEnabled,
                    GpuEnabled = settings.FrameGameGpuPriorityEnabled, Gpu = settings.FrameGameGpuPriority,
                    Cpu = settings.FrameGameCpuPriority, Io = settings.FrameGameIoPriority, Memory = settings.FrameGameMemoryPriority };
                var targets = ReadFrameGameTargets(target, FramePriorityTargetsForTests);
                var rules = _rules.EnabledRules();
                var ok = _framePriority.Sync(targets, config, id =>
                {
                    var latest = AppSettingsStore.Read();
                    return !_frozen && FrameTargetCurrent(target) && IsSelectedFrameTarget(id, target)
                        && FramePriorityRequested && (_rulesEngineRunning || BackgroundLoopDisabledForTests)
                        && latest.FrameGameCpuPriority == config.Cpu && latest.FrameGameIoPriority == config.Io && latest.FrameGameMemoryPriority == config.Memory
                        && latest.FrameGamePriorityEnabled == config.ProcessEnabled && latest.FrameGameGpuPriorityEnabled == config.GpuEnabled && latest.FrameGameGpuPriority == config.Gpu;
                }, "等待 " + target.DisplayName + " 启动，未锁定进程优先级", (id, property) => !(property == "priority" && settings.GamePriorityEnabled)
                    && !rules.Any(rule => rule.Capability == property
                        && string.Equals(rule.ExecutablePath, id.ExecutablePath, StringComparison.OrdinalIgnoreCase)));
                Volatile.Write(ref _framePriorityError, "");
                GameTargetService.Default.SetRestoreFailure("帧格进程优先级", _framePriority.Snapshot.RestorePending);
                return ok;
            }
            catch (Exception ex)
            {
                RestoreFrameProcessPriorities("进程身份或配置无法核对，已恢复可确认的优先级原值");
                Volatile.Write(ref _framePriorityError, "进程优先级锁定未执行：" + ex.Message);
                return false;
            }
        }
    }
    internal bool FramePriorityOnceForTests() => FramePriorityTick(force: true);
    internal void NvidiaGpuApiForTests(INvidiaGpuShareApi api, string path, Func<IDisposable> lease)
    { _frameNvidiaGpuShare.Dispose(); _frameNvidiaGpuShare = new(path, api, lease); }
}
