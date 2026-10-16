using DeltaNFD.Services;

internal static class AdvancedStartupChecks
{
    internal static async Task RunAsync()
    {
        int count = 0;
        void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); count++; }
        // Tests use injected requests only. Never call RequestWindowsAsync on the test host.
        int prepares = 0, requests = 0;
        var lease = new Lease();
        Task<StartupPreparation> Prepare() { prepares++; return Task.FromResult(new StartupPreparation(lease, true, "ready")); }
        Task<StartupRequest> Request() { requests++; return Task.FromResult(new StartupRequest(StartupRequestState.Accepted, "accepted")); }
        var noConsent = await AdvancedStartupRestart.RunAsync(false, Prepare, Request, default);
        Check(!noConsent.ShouldExit && prepares == 0 && requests == 0, "no consent must not drain or restart");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var earlyCancel = await AdvancedStartupRestart.RunAsync(true, Prepare, Request, cancelled.Token);
        Check(!earlyCancel.ShouldExit && prepares == 0 && requests == 0, "cancel before preparation must do nothing");
        var rejectedLease = new Lease();
        var rejected = await AdvancedStartupRestart.RunAsync(true,
            () => Task.FromResult(new StartupPreparation(rejectedLease, false, "active installer")), Request, default);
        Check(!rejected.ShouldExit && requests == 0 && rejectedLease.Disposed, "failed handover must prevent restart and release gates");
        Check(rejected.Request.Message.Contains("active installer"), "preserve blocker detail");
        var lateLease = new Lease();
        using var lateCancel = new CancellationTokenSource();
        var late = await AdvancedStartupRestart.RunAsync(true, () =>
        {
            lateCancel.Cancel(); return Task.FromResult(new StartupPreparation(lateLease, true, "ready"));
        }, Request, lateCancel.Token);
        Check(!late.ShouldExit && requests == 0 && lateLease.Disposed, "cancel after drain must not request reboot");
        var accepted = await AdvancedStartupRestart.RunAsync(true, Prepare, Request, default);
        Check(accepted.ShouldExit && prepares == 1 && requests == 1, "one explicit request after successful drain");
        Check(ReferenceEquals(accepted.RetainedLease, lease) && !lease.Disposed, "hold gates until tool exits");
        accepted.RetainedLease!.Dispose();
        foreach (var state in new[] { StartupRequestState.Rejected, StartupRequestState.Unconfirmed })
        {
            var local = new Lease(); int calls = 0;
            var result = await AdvancedStartupRestart.RunAsync(true,
                () => Task.FromResult(new StartupPreparation(local, true, "ready")),
                () => { calls++; return Task.FromResult(new StartupRequest(state, "fixture")); }, default);
            Check(calls == 1, "must not retry a failed or uncertain reboot request");
            Check(result.ShouldExit == (state == StartupRequestState.Unconfirmed), "unknown request must exit with gates held");
            Check(local.Disposed == (state == StartupRequestState.Rejected), "rejected request must release gates");
            result.RetainedLease?.Dispose();
        }
        var faultLease = new Lease();
        try
        {
            await AdvancedStartupRestart.RunAsync(true,
                () => Task.FromResult(new StartupPreparation(faultLease, true, "ready")),
                () => throw new InvalidOperationException("fixture failure before request"), default);
            throw new Exception("expected injected failure");
        }
        catch (InvalidOperationException) { Check(faultLease.Disposed, "release gates on prelaunch error"); }
        var info = AdvancedStartupRestart.CreateStartInfo(@"C:\Windows\System32");
        Check(info.FileName == @"C:\Windows\System32\shutdown.exe", "use fixed system executable");
        Check(!info.UseShellExecute && info.CreateNoWindow, "no command shell or visible console");
        Check(info.ArgumentList.SequenceEqual(new[] { "/r", "/o", "/t", "0" }), "one-shot advanced startup, timeout zero avoids implicit force");
        Check(info.Arguments.Length == 0, "no string-built arguments or user input");
        Console.WriteLine($"Advanced startup checks passed: {count}; no real restart or boot changes executed.");
    }

    private sealed class Lease : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
