using DeltaNFD.Native;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.CpuFrame;

internal interface IFrameTimerApi
{
    TimerResolutionRequestResult Request();
    TimerResolutionRequestResult Release();
    double? QueryMilliseconds();
}

internal sealed class NativeFrameTimerApi : IFrameTimerApi
{
    public TimerResolutionRequestResult Request() => ProcessPerf.RequestTimerResolutionHighDetailed();
    public TimerResolutionRequestResult Release() => ProcessPerf.ReleaseTimerResolutionDetailed();
    public double? QueryMilliseconds() => ProcessPerf.QueryTimerResolutionMs();
}

/// <summary>
/// Owns at most one request from this backend. Querying a shared system value never creates
/// ownership and releasing our request does not restore a saved global value. The lock also
/// covers StopMonitor/Dispose, which can run before the automation queue has drained.
/// </summary>
internal sealed class FrameTimerResolution(IFrameTimerApi api)
{
    private readonly object _gate = new();
    private TimerResolutionSnapshot _snapshot = new();
    internal TimerResolutionSnapshot Status => Volatile.Read(ref _snapshot);
    internal bool NeedsRelease => Status.RequestHeld || Status.OwnershipUnknown;

    internal OperationResult Update(bool wanted, string idleReason = "当前后端未持有计时器请求。", Func<bool>? stillAllowed = null)
    {
        lock (_gate)
        {
            var state = Status;
            var gateError = "";
            try
            {
                if (wanted && stillAllowed is not null && !stillAllowed())
                { wanted = false; idleReason = "执行条件已变化，本后端未保持计时器请求。"; }
            }
            catch (Exception ex) { wanted = false; gateError = "计时器执行条件无法确认：" + ex.Message; }
            state = state with { Wanted = wanted, UpdatedUtc = DateTimeOffset.UtcNow };
            if (!wanted)
            {
                if (state.RequestHeld || state.OwnershipUnknown)
                {
                    try
                    {
                        var released = api.Release();
                        state = state with { ReleaseNtStatus = released.NtStatus,
                            SystemCurrentMilliseconds = Valid(released.SystemCurrentMilliseconds), SystemObservedUtc = DateTimeOffset.UtcNow };
                        if (!released.Succeeded)
                            return Save(state with { HasError = true, ReleasePending = true, TargetReached = false,
                                Message = released.DiagnosticText("释放计时器请求失败，仍需核对") }, false);
                        // A remaining 0.5ms system reading can belong to another process.
                        state = state with { RequestHeld = false, OwnershipUnknown = false };
                    }
                    catch (Exception ex)
                    {
                        return Save(state with { HasError = true, OwnershipUnknown = true, ReleasePending = true, TargetReached = false,
                            Message = "计时器释放结果未知，保留释放责任：" + ex.Message }, false);
                    }
                }
                return Save(state with { ReleasePending = false, TargetReached = false, HasError = gateError.Length > 0,
                    Message = gateError.Length > 0 ? gateError : idleReason }, gateError.Length == 0);
            }

            if (state.OwnershipUnknown || state.ReleasePending)
                return Save(state with { HasError = true, ReleasePending = true, TargetReached = false,
                    Message = "计时器上次释放尚未确认，请先停止并核对释放；未重复申请或恢复执行。" }, false);
            if (!state.RequestHeld)
            {
                try
                {
                    var requested = api.Request();
                    state = state with { RequestNtStatus = requested.NtStatus,
                        ReturnedActualMilliseconds = requested.Succeeded ? Valid(requested.ReturnedActualMilliseconds) : null,
                        SystemCurrentMilliseconds = Valid(requested.SystemCurrentMilliseconds), SystemObservedUtc = DateTimeOffset.UtcNow };
                    if (!requested.Succeeded)
                        return Save(state with { HasError = true, TargetReached = false, ReleasePending = false,
                            Message = requested.DiagnosticText("申请计时器失败") }, false);
                    state = state with { RequestHeld = true, ReleasePending = false };
                }
                catch (Exception ex)
                {
                    return Save(state with { OwnershipUnknown = true, HasError = true, ReleasePending = true, TargetReached = false,
                        Message = "计时器申请结果未知，未重复申请：" + ex.Message }, false);
                }
            }
            else
            {
                double? current;
                try { current = Valid(api.QueryMilliseconds()); } catch { current = null; }
                state = state with { SystemCurrentMilliseconds = current, SystemObservedUtc = DateTimeOffset.UtcNow };
            }
            var reached = state.ReturnedActualMilliseconds is > 0 and <= 0.5 && state.SystemCurrentMilliseconds is > 0 and <= 0.5;
            return Save(state with { TargetReached = reached, HasError = !reached,
                Message = $"已持有 0.5ms 计时器请求；返回实际 {Format(state.ReturnedActualMilliseconds)}，系统读数 {Format(state.SystemCurrentMilliseconds)}"
                    + (reached ? "。" : "；尚未确认达到目标，未重复增加请求。") }, reached);
        }
    }

    private OperationResult Save(TimerResolutionSnapshot next, bool success)
    {
        var previous = Status;
        Volatile.Write(ref _snapshot, next);
        if (next.HasError && (!previous.HasError || previous.Message != next.Message)) Log.Warn(next.Message);
        return success ? OperationResult.Ok(next.Message) : new OperationResult
        {
            Success = false, Message = next.Message,
            IsPartial = next.RequestHeld || next.OwnershipUnknown,
            OutcomeUnknown = next.OwnershipUnknown,
        };
    }
    private static double? Valid(double? value) => value is > 0 && double.IsFinite(value.Value) ? value : null;
    private static string Format(double? value) => value is { } ms ? $"{ms:0.####}ms" : "不可读";
}
