using System.Reflection;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class GameObservationChecks
{
    private static int _count;
    private static void Check(bool ok, string why) { if (!ok) throw new IOException(why); _count++; }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static async Task RunAsync()
    {
        _count = 0;
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-game-observation-" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", id.ToString("D"));
        var now = DateTimeOffset.UtcNow;
        CpuFrameStatusSnapshot Sample(bool? running = false, bool monitor = false, bool custom = false, int seconds = 0) => new()
        {
            UpdatedUtc = now.AddSeconds(seconds), DeltaForceObservedUtc = now.AddSeconds(seconds),
            DeltaForceRunning = running, MonitorRunning = monitor, IsCustomTarget = custom, GameRunning = true,
        };
        var observes = 0; var automates = 0; var blocked = false; var notifications = 0;
        using var state = new GameObservationState(_ => { observes++; return Task.CompletedTask; },
            _ => { automates++; throw new InvalidOperationException("fixture retained recovery record"); }, () => blocked, () => now);
        state.Changed += () => notifications++;
        Check(state.DeltaRunning is null && state.TargetRunning is null, "initial state is unknown, not running or stopped");
        await state.ConnectAsync(bootstrapAutomation: true);
        Check(observes == 1 && automates == 1 && state.AutomationFailure.Contains("retained"), "automation refusal retained with reason");
        state.Accept(Sample());
        Check(state.DeltaRunning == false && state.TargetRunning == false && notifications > 0,
            "subscription survives refusal and accepts stopped game independently of automation");
        state.Accept(Sample(true));
        Check(state.DeltaRunning == true, "later game start still locks maintenance pages");
        state.Accept(Sample(seconds: -1));
        Check(state.DeltaRunning == true, "late older snapshot cannot unlock pages");
        state.Accept(Sample(custom: true));
        Check(state.DeltaRunning == false && state.TargetRunning is null, "custom target cannot mask Delta or claim unobserved target stopped");
        state.Accept(Sample(monitor: true, custom: true));
        Check(state.TargetRunning == true && state.AutomationFailure == "", "actual running monitor clears previous startup failure");
        state.Unavailable("fixture disconnected");
        Check(state.DeltaRunning is null && state.Detail.Contains("disconnected"), "disconnect invalidates old observation and gives cause");
        await state.ConnectAsync();
        Check(observes == 2 && automates == 1, "manual reconnect is read-only and never retries automation");
        state.Accept(Sample());
        Check(state.DeltaRunning == false, "subscription recovers on same state object");
        now = now.AddSeconds(11);
        Check(state.DeltaRunning is null && state.TargetRunning is null, "stale status fails closed");
        Check(GameObservationState.ReadDelta(Sample(seconds: 5), now) is null, "future timestamps fail closed");
        state.Accept(Sample(seconds: 5));
        Check(state.Latest is null && state.DeltaRunning is null, "future snapshot cannot poison ordering of subsequent observations");
        state.Accept(Sample());
        Check(state.DeltaRunning == false, "valid heartbeat immediately recovers from invalid future timestamp");
        Check(GameObservationState.ReadDelta(new() { DeltaForceRunning = false, DeltaForceObservedUtc = now }, now) is null,
            "missing heartbeat timestamp cannot unlock");
        state.Accept(new() { Module = "gpu", UpdatedUtc = now });
        Check(state.DeltaRunning is null && state.Detail.Contains("不匹配"), "wrong module refused with reason");
        state.Accept(Sample()); blocked = true;
        await state.ConnectAsync(bootstrapAutomation: true);
        Check(observes == 2 && automates == 1 && state.DeltaRunning is null, "offline/recovery gate starts neither observation nor automation");
        blocked = false;

        var release = Signal(); var enters = 0; var starts = 0;
        using var pending = new GameObservationState(async _ => { enters++; await release.Task; },
            _ => { starts++; return Task.CompletedTask; }, () => false);
        var connect = pending.ConnectAsync(bootstrapAutomation: true);
        await pending.ConnectAsync();
        Check(enters == 1 && pending.IsConnecting, "parallel reconnect shares a single connection attempt");
        pending.Dispose(); release.SetResult(); await connect;
        pending.Accept(Sample());
        Check(starts == 0 && pending.Latest is null, "dispose rejects late status and automation continuation");
        using var failing = new GameObservationState(_ => throw new IOException("fixture pipe failure"), _ => Task.CompletedTask, () => false, () => now);
        await failing.ConnectAsync();
        Check(failing.Detail.Contains("pipe failure") && !failing.IsConnecting, "connection failure remains actionable and permits retry");
        failing.Accept(Sample());
        Check(failing.DeltaRunning == false, "late valid heartbeat recovers even after initial query failure");

        Check(FrameStatusPresentation.Execution(null, now, "blocked").Contains("未启动"), "configured flag is not rendered as active execution");
        Check(FrameStatusPresentation.Execution(Sample(monitor: true), now, "").Contains("运行中"), "fresh actual monitor rendered running");
        Check(FrameStatusPresentation.Power(true, "", Sample(), now).Contains("尚未确认生效"), "inactive power flag does not invent a recovery record");
        Check(FrameStatusPresentation.Game(null, "三角洲").Contains("待确认"), "unknown game status never rendered waiting");
        Check(BackendModules.IsMutating(BackendModules.CpuFrame, BackendCommands.CpuFrameMonitorStart)
            && BackendModules.IsReadOnly(BackendModules.CpuFrame, BackendCommands.CpuFrameStatus), "mutation/query contract unchanged");

        await CheckRealBackendAsync();
        Console.WriteLine($"Game observation checks: {_count} passed; isolated retained evidence + real read-only backend; no host policy/process writes.");
    }

    private static async Task CheckRealBackendAsync()
    {
        BackendUnconfirmedStore.BeginIntent(new(BackendModules.CpuFrame, BackendCommands.CpuFramePowerLock,
            Guid.NewGuid(), Guid.NewGuid(), "fixture", int.MaxValue, 1, Environment.ProcessPath!));
        var path = BackendUnconfirmedStore.RecordPathForTests(BackendModules.CpuFrame);
        var bytes = File.ReadAllBytes(path);
        await using (var service = new BackendCpuFrameService())
        {
            var received = Signal(); var statusCount = 0;
            service.StatusUpdated += _ => { statusCount++; received.TrySetResult(); };
            await service.EnsureObservationAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(service.Latest is { MonitorRunning: false, DeltaForceObservedUtc: not null }, "retained record permits real readonly status without starting automatic writes");
            Check(File.ReadAllBytes(path).SequenceEqual(bytes), "readonly startup preserves retained evidence byte-for-byte");
            var refused = false;
            try { await service.EnsureMonitorAsync(); } catch (InvalidOperationException) { refused = true; }
            Check(refused && service.AutomationError.Contains("待核对"), "same retained record still refuses automatic optimization");
            var previousCount = statusCount;
            await Task.Delay(2200);
            Check(statusCount > previousCount && service.Latest?.MonitorRunning == false, "real heartbeat continues after refused monitor start");
            await service.EnsureObservationAsync();
            Check(File.ReadAllBytes(path).SequenceEqual(bytes), "readonly retry does not clear or rewrite evidence");

            // Delayed callbacks from a retired status pump must not restore an obsolete snapshot.
            var type = typeof(BackendCpuFrameService);
            var generation = (int)type.GetField("_pumpGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
            var current = service.Latest;
            type.GetMethod("AcceptSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service,
                [new CpuFrameStatusSnapshot { UpdatedUtc = DateTimeOffset.UtcNow.AddSeconds(1), DeltaForceRunning = true }, generation - 1]);
            type.GetMethod("InvalidateObservation", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [generation - 1]);
            Check(ReferenceEquals(current, service.Latest), "retired generation cannot publish or invalidate current observation");
        }
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "graceful readonly shutdown preserves unresolved recovery evidence");
        Console.WriteLine("Synthetic recovery evidence retained for inspection: " + path);
    }
}
