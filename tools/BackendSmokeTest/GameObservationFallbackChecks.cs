using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class GameObservationFallbackChecks
{
    internal static async Task RunAsync()
    {
        var id = Guid.NewGuid(); var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-game-fallback-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var count = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); count++; }
        var path = BackendUnconfirmedStore.RecordPathForTests(BackendModules.CpuFrame);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var corrupt = new byte[256]; File.WriteAllBytes(path, corrupt);
        await using var backend = new BackendCpuFrameService();
        using var state = new GameObservationState(backend.EnsureObservationAsync, _ => throw new Exception("Never retry execution"), () => false);
        await state.ConnectAsync();
        Check(state.DeltaRunning is null && state.Failure.Contains("后端操作记录损坏") && state.Failure.Contains(path), "Corrupt evidence gives an actionable path, not raw JSON syntax.");
        Check(File.ReadAllBytes(path).SequenceEqual(corrupt) && BackendSessions.Snapshot().Count == 0, "Corrupt record remains byte-identical, no executor started.");
        Check(BackendUnconfirmedStore.ReviewAll() is [{ CanAcknowledge: false, CanRestore: false }], "Fallback does not grant recovery authority for corrupt evidence.");
        var now = DateTimeOffset.UtcNow;
        state.AcceptFallback(false, now);
        Check(state.DeltaRunning == false && state.Latest is null && state.TargetRunning is null, "Read-only confirmation separates Delta page state from actual backend execution.");
        state.AcceptFallback(true, now);
        Check(state.DeltaRunning == true, "Fallback still restricts pages when Delta runs.");
        state.AcceptFallback(null, now);
        Check(state.DeltaRunning is null, "Probe failure never assumes game has exited.");
        state.AcceptFallback(false, now.AddSeconds(-11));
        Check(state.DeltaRunning is null, "Stale fallback cannot unlock pages.");
        state.AcceptFallback(false, now.AddSeconds(5));
        Check(state.DeltaRunning is null, "Future fallback cannot unlock pages.");
        using (state.PauseConnections())
        {
            state.AcceptFallback(false, now);
            Check(!state.CanUseReadOnlyFallback && state.DeltaRunning is null, "Exit/handover pause revokes fallback callbacks.");
        }
        state.Accept(new() { UpdatedUtc = now, DeltaForceRunning = true, DeltaForceObservedUtc = now });
        state.AcceptFallback(false, now);
        Check(!state.CanUseReadOnlyFallback && state.DeltaRunning == true && state.Latest is not null, "Actual heartbeat overrides fallback and ends the fallback period.");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepts = 0; var reads = 0;
        using (var probe = new DeltaObservationFallback(() => { Interlocked.Increment(ref reads); entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return false; }, (_, _) => Interlocked.Increment(ref accepts)))
        {
            probe.SetEnabled(true); probe.SetEnabled(true); probe.SetEnabled(true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            probe.SetEnabled(false); release.SetResult();
            await Task.Delay(150);
            Check(reads == 1 && accepts == 0, "Repeated enable uses one probe; disabled slow probe cannot publish late.");
        }
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DeltaObservationFallback? reentrant = null;
        reentrant = new(() => false, (_, _) => { reentrant!.SetEnabled(false); observed.TrySetResult(); });
        using (reentrant) { reentrant.SetEnabled(true); await observed.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        Check(true, "Probe consumer can stop probing without lock inversion.");
        var blocked = true;
        using var offline = new GameObservationState(_ => Task.CompletedTask, _ => Task.CompletedTask, () => blocked);
        offline.AcceptFallback(false, now);
        Check(!offline.CanUseReadOnlyFallback && offline.DeltaRunning is null, "Offline recovery still blocks fallback.");
        blocked = false;
        offline.Dispose(); offline.AcceptFallback(false, now);
        Check(!offline.CanUseReadOnlyFallback && offline.DeltaRunning is null, "Disposed observation ignores late fallback.");
        Console.WriteLine($"Game observation fallback checks passed ({count}); corrupt evidence preserved, no real backend or policy writes.");
    }
}
