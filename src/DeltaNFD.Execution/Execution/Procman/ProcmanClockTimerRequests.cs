using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

public sealed partial class ProcmanEngine
{
    private ProcmanProcessIdentity[] _clockScanTimerIgnoreTargets = [];
    internal Func<CancellationToken, TimerRequestSurvey>? ClockTimerSurveyForTests { get; set; }

    public static TimerRequestSurvey ReadTimerRequests(CancellationToken token)
    {
        var (observations, kernelPeriod) = TimerRequestTrace.Capture(token);
        var rows = new List<TimerRequestProcess>();
        foreach (var request in observations.GroupBy(row => row.Pid)
            .Select(group => group.OrderBy(row => row.Ignored).ThenBy(row => row.Period100ns).First())
            .OrderBy(row => row.Period100ns).ThenBy(row => row.Pid))
        {
            token.ThrowIfCancellationRequested();
            var identity = ProcmanIdentity.TryIdentify(request.Pid);
            var name = Path.GetFileName(request.Name);
            if (identity is not null && (identity.StartTimeUtcTicks > request.ObservedUtcTicks
                || (name.Length > 0 && !Path.GetFileName(identity.ExecutablePath).Equals(name, StringComparison.OrdinalIgnoreCase))))
                identity = null;
            var canIgnore = false;
            var ignored = request.Ignored;
            if (identity is not null && IsTimerIgnoreListIdentityAllowed(identity))
            {
                try
                {
                    using var pin = ProcmanCapabilities.Pin(identity, requireOrdinaryUser: true);
                    var policy = ProcmanCapabilities.ReadPowerThrottling(identity.Pid);
                    canIgnore = policy.Ok && FrameTimerIgnoreSupported;
                    ignored |= policy.Ok && new FrameTimerPowerState(policy.Control, policy.State).Forced;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            var label = identity is not null ? Path.GetFileName(identity.ExecutablePath) : name;
            rows.Add(new(request.Pid, request.Period100ns, label.Length > 0 ? label : $"PID {request.Pid}",
                identity?.ExecutablePath ?? "", identity?.StartTimeUtcTicks ?? 0, ignored, canIgnore));
        }
        return new(rows, kernelPeriod);
    }

    private string PrepareClockScanTimerRequests(CancellationToken token, IProgress<string>? progress)
    {
        progress?.Report("正在检测申请小于 0.5500 ms 的进程…");
        var survey = ClockTimerSurveyForTests?.Invoke(token) ?? ReadTimerRequests(token);
        var finer = survey.Processes.Where(row => row.Period100ns < PendulumFramePolicy.ManagedScanLast100ns).ToArray();
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            CheckBurstScanEnvironment();
            _clockScanTimerIgnoreTargets = finer.Where(row => row.CanIgnore && !row.AlreadyIgnored)
                .Select(row => new ProcmanProcessIdentity(row.Pid, new DateTime(row.StartedUtcTicks, DateTimeKind.Utc), row.Path)).ToArray();
            if (_clockScanTimerIgnoreTargets.Length > 0)
            {
                var targets = ReadTimerIgnoreListTargets().Concat(_clockScanTimerIgnoreTargets).Distinct().ToArray();
                _timerIgnoreList.Sync(targets, _ => _optimalClockScanInProgress && !token.IsCancellationRequested,
                    "没有需临时忽略的进程", FrameTimerIgnoreSupported);
            }

            var applied = _clockScanTimerIgnoreTargets.Count(_timerIgnoreList.IsAppliedTo);
            var already = finer.Count(row => row.AlreadyIgnored);
            var unresolved = finer.Length - applied - already;
            var summary = $"前置检测发现 {finer.Length} 个小于 0.5500 ms 的请求进程，临时忽略 {applied} 个，已忽略 {already} 个，未能忽略 {unresolved} 个。";
            if (survey.KernelPeriod100ns is > 0 and < PendulumFramePolicy.ManagedScanLast100ns)
                summary += $"Windows 另报告内核请求 {survey.KernelPeriod100ns / 10000d:0.0000} ms。";
            _pendulumLog.Write("scan-timer-request-preflight", new { finer, survey.KernelPeriod100ns, applied, already, unresolved, TimerIgnoreListState.Message });
            progress?.Report(summary);
            return summary;
        }
    }

    private bool FinishClockScanTimerRequests()
    {
        lock (_gate)
        {
            if (_clockScanTimerIgnoreTargets.Length == 0) return true;
            _clockScanTimerIgnoreTargets = [];
            var ok = _frozen || _memoryStopping > 0 || _memoryDisposed
                ? RestoreTimerIgnoreList()
                : TimerIgnoreListTick(force: true);
            _pendulumLog.Write("scan-timer-request-cleanup", new { ok, TimerIgnoreListState.Message });
            return ok;
        }
    }
}

