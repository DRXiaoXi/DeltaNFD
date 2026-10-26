using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class FrameExitAdmissionChecks
{
    private static int _count;
    private static void Check(bool value, string reason) { if (!value) throw new IOException(reason); _count++; }
    private static bool Admitted()
    {
        try { BackendOperationGate.Check(); return true; } catch (InvalidOperationException) { return false; }
    }

    internal static async Task RunAsync()
    {
        _count = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int observes = 0, automates = 0;
        CancellationToken submitted = default;
        using var observation = new GameObservationState(async token =>
        {
            observes++; submitted = token; entered.TrySetResult(); await release.Task;
        }, _ => { automates++; return Task.CompletedTask; }, () => false);
        var bootstrap = observation.ConnectAsync(bootstrapAutomation: true);
        await entered.Task;
        var exit = new ExitOperationScope();
        var probe = new ProbeLease();
        exit.Hold(probe);
        exit.Hold(BackendOperationGate.BlockNewOperations());
        exit.Hold(observation.PauseConnections());
        Check(!submitted.IsCancellationRequested, "exit pause never cancels an already submitted shared IPC request");
        Check(!Admitted(), "exit starts by closing new backend admission");
        await observation.ConnectAsync();
        Check(observes == 1, "exit pause rejects additional connection attempts");
        exit.Commit(); exit.Dispose();
        Check(!Admitted() && probe.Disposals == 0, "successful exit finally cannot release any admission");
        release.TrySetResult(); await bootstrap.WaitAsync(TimeSpan.FromSeconds(3));
        Check(automates == 0 && !observation.IsConnecting, "delayed initial connection cannot bootstrap after exit commits");
        exit.Release(); exit.Release();
        Check(Admitted() && probe.Disposals == 1, "explicit exit failure rollback releases each lease exactly once");
        await observation.ConnectAsync();
        Check(observes == 2 && automates == 0, "failed exit permits explicit readonly reconnect without replaying automation");

        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resumed = new GameObservationState(async _ => { entered.TrySetResult(); await release.Task; },
            _ => { automates++; return Task.CompletedTask; }, () => false);
        var delayed = resumed.ConnectAsync(bootstrapAutomation: true);
        await entered.Task;
        using (var cancelledExit = new ExitOperationScope())
        {
            cancelledExit.Hold(BackendOperationGate.BlockNewOperations());
            cancelledExit.Hold(resumed.PauseConnections());
        }
        Check(Admitted(), "cancelled exit releases backend admission");
        release.TrySetResult(); await delayed;
        Check(automates == 0, "releasing failed-exit pause does not revive the old bootstrap generation");
        using (var outer = resumed.PauseConnections())
        {
            using (resumed.PauseConnections()) { }
            await resumed.ConnectAsync(bootstrapAutomation: true);
            Check(automates == 0, "nested pause remains closed until its last owner releases");
        }
        await resumed.ConnectAsync();
        Check(automates == 0, "readonly reconnect remains readonly after nested pauses");

        var operationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken operationToken = default;
        using var submittedOperation = new GameObservationState(_ => Task.CompletedTask, async token =>
        {
            operationToken = token; operationEntered.TrySetResult(); await operationRelease.Task;
        }, () => false);
        var inFlight = submittedOperation.ConnectAsync(bootstrapAutomation: true);
        await operationEntered.Task;
        using (submittedOperation.PauseConnections())
        {
            Check(!operationToken.IsCancellationRequested && !inFlight.IsCompleted,
                "exit pause does not cancel an already admitted automation request");
            operationRelease.TrySetResult(); await inFlight.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!submittedOperation.IsConnecting, "submitted request completes before the connection attempt settles");
        }

        var failureScope = new ExitOperationScope();
        failureScope.Hold(BackendOperationGate.BlockNewOperations());
        failureScope.Hold(new ProbeLease { ThrowOnDispose = true });
        var failed = false;
        try { failureScope.Release(); } catch (AggregateException) { failed = true; }
        Check(failed && Admitted(), "one component release failure cannot leak the backend gate");
        failureScope.Dispose();
        Console.WriteLine($"Frame exit admission checks: {_count} passed; delayed startup, committed/cancelled/failed exit and nested pauses, no backend or system writes.");
    }

    private sealed class ProbeLease : IDisposable
    {
        public int Disposals;
        public bool ThrowOnDispose;
        public void Dispose() { Disposals++; if (ThrowOnDispose) throw new IOException("isolated release failure"); }
    }
}
