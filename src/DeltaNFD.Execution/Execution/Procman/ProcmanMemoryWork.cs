using DeltaNFD.Backends;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

public sealed partial class ProcmanEngine
{
    // Admission and completion use _gate; enumeration and native calls never hold it.
    // Manual/global/automatic work shares one slot. No queued replay after a mode change.
    private sealed class MemoryWork(ProcmanBackgroundConfig config, long revision, bool automatic, CancellationToken token)
    {
        internal readonly ProcmanBackgroundConfig Config = config;
        internal readonly long Revision = revision;
        internal readonly bool Automatic = automatic;
        internal readonly CancellationTokenSource Cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private MemoryWork? _memoryWork;
    private long _memoryRevision;
    private int _memoryStopping;
    private bool _memoryDisposed;

    // Caller holds _gate. Cancellation sources are also disposed under this lock.
    private MemoryWork? BeginMemoryWork(bool automatic, CancellationToken token)
    {
        if (_memoryWork is not null || _memoryStopping > 0 || _memoryDisposed || _frozen || token.IsCancellationRequested) return null;
        return _memoryWork = new(_backgroundConfig, _memoryRevision, automatic, token);
    }
    private void EndMemoryWork(MemoryWork work)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_memoryWork, work)) _memoryWork = null;
            work.Cancel.Dispose();
            work.Finished.TrySetResult();
        }
    }
    private bool MemoryWorkCurrent(MemoryWork work) => !work.Cancel.IsCancellationRequested
        && work.Revision == Volatile.Read(ref _memoryRevision) && !Volatile.Read(ref _frozen)
        && Volatile.Read(ref _backgroundLoadError).Length == 0;

    // Do not acquire _gate here: ordinary mode owns the cross-process property mutex
    // through its native call, while other engine operations acquire _gate first.
    private bool MemoryOperationAllowed(MemoryWork work)
    {
        if (!MemoryWorkCurrent(work)) return false;
        var policy = MemoryPolicy();
        return policy.BlockReason(BackendCommands.CpuFrameMemoryClean) is null
            && (!work.Automatic || (Volatile.Read(ref _automaticMemoryActive) && FrameAutomaticMemoryActive
                && !OfflineModeGuard.BlocksNormalAutomation && !policy.ManualMemoryCleanOnly
                && MemoryPressure() is { } pressure && pressure <= 100
                && pressure >= policy.MemoryThreshold(work.Config.MemoryPressurePercent)));
    }

    private Task ScheduleAutomaticMemory(CancellationToken token)
    {
        lock (_gate)
        {
            if (!_automaticMemoryActive || !FrameAutomaticMemoryActive || _frozen || token.IsCancellationRequested
                || OfflineModeGuard.BlocksNormalAutomation)
            { _memoryHighSince = null; return Task.CompletedTask; }
            var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
            if (_lastAutomaticCheck is { } check && now - check < TimeSpan.FromSeconds(30)) return Task.CompletedTask;
            var work = BeginMemoryWork(automatic: true, token);
            if (work is null) return Task.CompletedTask;
            _lastAutomaticCheck = now;
            _ = Task.Run(() =>
            {
                try { AutomaticMemoryTick(work, now); }
                catch (OperationCanceledException) when (work.Cancel.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        if (work.Revision == _memoryRevision)
                        {
                            _memoryHighSince = null;
                            _memoryFeedback = "内存回收未完成：" + ex.Message;
                            _automaticMemoryReport = new([], _memoryFeedback);
                        }
                    }
                }
                finally { EndMemoryWork(work); }
            });
            return work.Finished.Task;
        }
    }
    internal Task AutomaticMemoryOnceAsyncForTests(CancellationToken token = default) => ScheduleAutomaticMemory(token);
    internal void AutomaticMemoryOnceForTests(CancellationToken token = default) => ScheduleAutomaticMemory(token).GetAwaiter().GetResult();
}
