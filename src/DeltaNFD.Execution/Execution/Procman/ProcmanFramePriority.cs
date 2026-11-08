using System.Diagnostics;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

public sealed partial class ProcmanEngine
{
    private readonly FrameGamePriority _framePriority;
    private string _framePriorityError = "";
    private DateTime _lastFramePriorityTick;
    internal Func<IReadOnlyList<ProcmanProcessIdentity>>? FramePriorityTargetsForTests { get; set; }
    private bool FramePriorityRequested => FrameBackgroundActive && AppSettingsStore.Read().FrameGamePriorityEnabled;
    private FrameGamePrioritySnapshot FramePriorityState
    {
        get
        {
            var state = _framePriority.Snapshot;
            var error = Volatile.Read(ref _framePriorityError);
            return error.Length == 0 ? state : state with { HasError = true, Message = error + "；" + state.Message };
        }
    }
    private bool RestoreFramePriorities(string reason = "三角洲 CPU、IO 和内存优先级原值已恢复")
    {
        var ok = _framePriority.Sync([], new(), _ => false, reason);
        GameTargetService.Default.SetRestoreFailure("帧格进程优先级", !ok);
        if (ok) Volatile.Write(ref _framePriorityError, "");
        return ok;
    }
    private IReadOnlyList<ProcmanProcessIdentity> ReadFramePriorityTargets()
    {
        if (FramePriorityTargetsForTests is { } read) return read();
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
    private bool FramePriorityTick(bool force = false)
    {
        lock (_gate)
        {
            if (_frozen || !FramePriorityRequested)
                return RestoreFramePriorities(_frozen ? "进程管理监控已停止，三角洲优先级原值已恢复" : "帧格或该功能未开启，三角洲优先级原值已恢复");
            var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
            if (!force && now - _lastFramePriorityTick < TimeSpan.FromSeconds(2)) return !FramePriorityState.HasError;
            _lastFramePriorityTick = now;
            try
            {
                var settings = AppSettingsStore.Read();
                var config = new FrameGamePriorityConfig { Cpu = settings.FrameGameCpuPriority, Io = settings.FrameGameIoPriority, Memory = settings.FrameGameMemoryPriority };
                var targets = ReadFramePriorityTargets();
                var rules = _rules.EnabledRules();
                var ok = _framePriority.Sync(targets, config, id =>
                {
                    var latest = AppSettingsStore.Read();
                    return !_frozen && FramePriorityRequested && (_rulesEngineRunning || BackgroundLoopDisabledForTests)
                        && latest.FrameGameCpuPriority == config.Cpu && latest.FrameGameIoPriority == config.Io && latest.FrameGameMemoryPriority == config.Memory;
                }, "等待三角洲启动，未锁定进程优先级", (id, property) => !(property == "priority" && settings.GamePriorityEnabled)
                    && !rules.Any(rule => rule.Capability == property
                        && string.Equals(rule.ExecutablePath, id.ExecutablePath, StringComparison.OrdinalIgnoreCase)));
                Volatile.Write(ref _framePriorityError, "");
                GameTargetService.Default.SetRestoreFailure("帧格进程优先级", _framePriority.Snapshot.RestorePending);
                return ok;
            }
            catch (Exception ex)
            {
                RestoreFramePriorities("进程身份或配置无法核对，已恢复可确认的优先级原值");
                Volatile.Write(ref _framePriorityError, "进程优先级锁定未执行：" + ex.Message);
                return false;
            }
        }
    }
    internal bool FramePriorityOnceForTests() => FramePriorityTick(force: true);
}
