using DeltaNFD.Execution.Procman;

internal static class FrameTimerPolicyChecks
{
    private sealed class Api : IFrameTimerPolicyApi
    {
        internal uint Control = 1, State = 1;
        internal int Reads, Writes;
        internal bool FailRead, FailWrite, Ignore, Corrupt;
        public (bool Ok, uint Control, uint State, string Error) Read()
        { Reads++; return FailRead ? (false, 0, 0, "fixture read rejected") : (true, Control, State, ""); }
        public (bool Ok, string Error) Write(uint control, uint state)
        {
            Writes++;
            if (FailWrite) return (false, "fixture Win32 87");
            if (!Ignore) { Control = control; State = Corrupt ? state & ~1u : state; }
            return (true, "");
        }
    }
    internal static void Run()
    {
        int count = 0;
        void Check(bool ok, string message) { if (!ok) throw new IOException(message); count++; }
        foreach (int build in new[] { 17763, 19041, 19045, 22000, 26200 })
            Check(FrameTimerPolicyProbeHost.Candidate(new Version(10, 0, build)), "Windows 10/11 candidate " + build);
        Check(!FrameTimerPolicyProbeHost.Candidate(new Version(10, 0, 17134)), "Pre-1809 unsupported");
        Check(!FrameTimerPolicyProbeHost.Candidate(new Version(6, 3, 9600)), "Older OS unsupported");
        var api = new Api();
        Check(FrameTimerPolicyProbeHost.Verify(api).Accepted && api.Control == 5 && api.State == 5, "Accept only after preserving EcoQoS and confirming ignore bit");
        Check(api.Reads == 2 && api.Writes == 1, "Bounded query/write/read");
        api = new() { FailRead = true };
        Check(!FrameTimerPolicyProbeHost.Verify(api).Accepted && api.Writes == 0, "No write after unknown initial state");
        api = new() { FailWrite = true };
        var failed = FrameTimerPolicyProbeHost.Verify(api);
        Check(!failed.Accepted && failed.Error.Contains("87") && api.Reads == 1, "OS rejection cannot become a success");
        api = new() { Ignore = true };
        Check(!FrameTimerPolicyProbeHost.Verify(api).Accepted, "Silent no-op rejected");
        api = new() { Corrupt = true };
        Check(!FrameTimerPolicyProbeHost.Verify(api).Accepted, "Changes to other owned bits rejected");
        Check(!FrameTimerPolicyProbeHost.TryRun(["--describe"], out _), "Other backend commands do not invoke native probe");
        Check(!FrameTimerPolicyProbeHost.TryRun([FrameTimerPolicyProbeHost.Argument, "123"], out _), "Probe accepts no target PID or extra arguments");
        Console.WriteLine($"Win10 timer-policy probe: {count} fake-API checks passed; no native worker, timer or target writes.");
    }
}
