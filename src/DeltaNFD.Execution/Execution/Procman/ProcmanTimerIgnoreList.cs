using System.Diagnostics;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

/// <summary>
/// 「忽略列表」：用户手动指定的进程，其定时器分辨率请求由系统忽略（与三角洲那项各自独立）。
/// 与三角洲功能共用同一套原值记录/回读/归属锁/探针门禁实现，只是目标判定换成「映像名在列表里」。
/// 安全边界：只作用于当前用户会话里能核验的普通进程——系统进程、服务、Windows 目录、反作弊、本工具自身一律拒绝。
/// </summary>
public sealed partial class ProcmanEngine
{
    private readonly FrameGameTimerIgnore _timerIgnoreList;
    private string _timerIgnoreListError = "";
    private DateTime _lastTimerIgnoreListTick;
    internal Func<IReadOnlyList<ProcmanProcessIdentity>>? TimerIgnoreListTargetsForTests { get; set; }

    /// <summary>列表里的映像名（去重、去空、忽略大小写）；设置里显式 null 按空处理。</summary>
    internal static IReadOnlyList<string> TimerIgnoreListNames()
    {
        var list = AppSettingsStore.Read().TimerIgnoreExtraProcesses;
        if (list is null) return [];
        return list.Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>恢复记录的静态身份/路径校验。新施加还必须由 NativeFrameTimerIgnoreApi 在固定句柄上验证当前用户、会话与非服务身份。</summary>
    internal static bool IsTimerIgnoreListIdentityAllowed(ProcmanProcessIdentity identity)
        => identity.Pid > 0 && identity.StartTimeUtcTicks > 0
            && Path.IsPathFullyQualified(identity.ExecutablePath)
            && !ProcmanProcessProtection.IsAceProtected(identity.ExecutablePath)
            && !ProcmanIdentity.IsProtectedPath(identity.ExecutablePath);

    /// <summary>是否属于列表目标（名称命中且身份允许）。</summary>
    internal static bool IsTimerIgnoreListTarget(ProcmanProcessIdentity identity)
    {
        if (!IsTimerIgnoreListIdentityAllowed(identity)) return false;
        var name = Path.GetFileName(identity.ExecutablePath);
        return TimerIgnoreListNames().Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private bool TimerIgnoreListRequested => !_frozen && TimerIgnoreListNames().Count > 0;

    private FrameTimerIgnoreSnapshot TimerIgnoreListState
    {
        get
        {
            var state = _timerIgnoreList.Snapshot;
            var error = Volatile.Read(ref _timerIgnoreListError);
            return error.Length == 0 ? state : state with { HasError = true, Message = error + "；" + state.Message };
        }
    }

    private bool RestoreTimerIgnoreList(string reason = "忽略列表的定时器请求已恢复")
    {
        var ok = _timerIgnoreList.Sync([], _ => false, reason, FrameTimerIgnoreSupported);
        GameTargetService.Default.SetRestoreFailure("忽略列表忽略定时器请求", !ok);
        if (ok) Volatile.Write(ref _timerIgnoreListError, "");
        return ok;
    }

    private IReadOnlyList<ProcmanProcessIdentity> ReadTimerIgnoreListTargets()
    {
        if (TimerIgnoreListTargetsForTests is { } read) return read();
        var result = new List<ProcmanProcessIdentity>();
        foreach (var name in TimerIgnoreListNames())
        {
            var baseName = Path.GetFileNameWithoutExtension(name);
            if (baseName.Length == 0) continue;
            foreach (var process in Process.GetProcessesByName(baseName))
            {
                using (process)
                {
                    // 读不到身份（已退出/受保护）就跳过：不猜路径，也不拿 PID 硬写。
                    var identity = ProcmanIdentity.TryIdentify(process.Id);
                    if (identity is null || !IsTimerIgnoreListTarget(identity)) continue;
                    result.Add(identity);
                }
            }
        }
        return result;
    }

    private bool TimerIgnoreListTick(bool force = false)
    {
        lock (_gate)
        {
            if (_frozen || !TimerIgnoreListRequested)
                return RestoreTimerIgnoreList(_frozen
                    ? "进程管理监控已停止，忽略列表的定时器请求已恢复"
                    : "忽略列表为空，定时器请求已恢复");
            var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
            if (!force && now - _lastTimerIgnoreListTick < TimeSpan.FromSeconds(2)) return !TimerIgnoreListState.HasError;
            _lastTimerIgnoreListTick = now;
            try
            {
                var targets = ReadTimerIgnoreListTargets();
                var ok = _timerIgnoreList.Sync(targets, _ =>
                {
                    var latest = AppSettingsStore.Read();
                    return !_frozen && (_rulesEngineRunning || BackgroundLoopDisabledForTests)
                        && (latest.TimerIgnoreExtraProcesses?.Count ?? 0) > 0;
                }, "忽略列表里的进程尚未运行，未施加定时器请求忽略", FrameTimerIgnoreSupported);
                Volatile.Write(ref _timerIgnoreListError, "");
                GameTargetService.Default.SetRestoreFailure("忽略列表忽略定时器请求", _timerIgnoreList.Snapshot.RestorePending);
                return ok;
            }
            catch (Exception ex)
            {
                RestoreTimerIgnoreList("进程身份或配置无法核对，已恢复可确认的忽略列表原值");
                Volatile.Write(ref _timerIgnoreListError, "忽略列表的定时器请求忽略未执行：" + ex.Message);
                return false;
            }
        }
    }

    internal bool TimerIgnoreListOnceForTests() => TimerIgnoreListTick(force: true);
}
