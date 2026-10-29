using DeltaNFD.Execution.CpuFrame;
using DeltaNFD.Services;

internal static class DwmWatchdogChecks
{
    private static int _count;
    private static void Check(bool value, string why) { if (!value) throw new IOException(why); _count++; }
    private static readonly DwmIdentity Original = new(42, 12345, 1, @"C:\Windows\System32\dwm.exe");

    internal static async Task RunAsync()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-dwm-" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        try
        {
            var env = new Fake(); var watch = new DwmWatchdog(env);
            watch.Tick(false, "game", "off", () => true);
            Check(env.Reads == 0 && env.Restarts == 0 && !watch.Status.Monitoring, "disabled watcher does not read or terminate");
            watch.Tick(true, "game", "", () => true);
            Check(env.Restarts == 0 && watch.Status.CpuCorePercent is null, "game start samples instead of unconditional restart");
            env.Step(2, 2); Tick(watch);
            Check(env.Restarts == 0, "under three seconds is not a sample");
            env.Step(2, 0); Tick(watch);
            Check(env.Restarts == 0 && watch.Status.CpuCorePercent == 50, "one high average does not restart; uses one-core CPU denominator");
            env.Step(4, 0); Tick(watch); env.Step(4, 4); Tick(watch);
            Check(env.Restarts == 0, "normal window resets consecutive high evidence");
            env.Step(4, 4); Tick(watch);
            Check(env.Restarts == 1 && watch.Status.CheckingAfterRestart, "two sustained high windows request exactly one restart");
            Check(watch.Status.RestartedThisGame && watch.Status.RestartCount == 1, "request count is distinct from verified success");
            Verify(watch, env, high: false);
            Check(!watch.Status.CheckingAfterRestart && !watch.Status.HasError && watch.Status.Message.Contains("复查"), "new DWM and low CPU are required for success");
            Check(env.Delays == 0, "automatic state machine never sleeps or adds a timer");
            for (var i = 0; i < 6; i++) { env.Step(4, 4); Tick(watch); }
            Check(env.Restarts == 1, "60 second cooldown prevents repeated screen disruption");
            for (var i = 0; i < 15 && env.Restarts < 2; i++) { env.Step(4, 4); Tick(watch); }
            Check(env.Restarts == 2, "watcher continues after first successful repair rather than once per game");
            watch.Tick(false, "game", "game ended", () => true);
            Check(!watch.Status.Monitoring && watch.Status.HasError && watch.Status.Message.Contains("未完成"), "stop cancels pending automatic checks without claiming success");
            watch.Tick(false, "game", "off", () => true);
            Check(watch.Status.HasError, "repeated stop preserves incomplete verification explanation");

            env = new Fake { Memory = 600 }; watch = new(env); Tick(watch);
            for (var i = 0; i < 10; i++) { env.Step(4, .1); Tick(watch); }
            Check(env.Restarts == 0 && watch.Status.WorkingSetMiB == 600, "working set is reference only, not an automatic trigger");
            env.ReadError = true; env.Step(4, 4); Tick(watch);
            Check(watch.Status.HasError && env.Restarts == 0, "read failure is unknown, never abnormal CPU or restart permission");
            env.ReadError = false; env.Step(4, 4); Tick(watch);
            Check(env.Restarts == 0 && watch.Status.CpuCorePercent is null, "recovered read starts a new baseline");

            env = new(); watch = new(env); Tick(watch); env.Step(4, 4); Tick(watch);
            env.Current = Original with { Created = 99999 }; env.Cpu = 0; env.Step(4, 4); Tick(watch);
            Check(env.Restarts == 0 && watch.Status.CpuCorePercent is null, "reused PID with a different creation time resets samples");
            env.Step(20, 20); Tick(watch);
            Check(env.Restarts == 0, "long stalled tick is not treated as continuous load evidence");
            env.Step(-30, 0); Tick(watch);
            Check(env.Restarts == 0 && watch.Status.CpuCorePercent is null, "clock discontinuity resets samples");

            env = new(); watch = new(env); Tick(watch);
            for (var i = 0; i < 10; i++) { env.Step(4, 4); watch.Tick(true, "game", "", () => false); }
            Check(env.Restarts == 0, "execution gate rejects automatic restart even after high samples");
            var checks = 0;
            env.Step(4, 4); watch.Tick(true, "game", "", () => ++checks < 3);
            Check(env.Restarts == 0 && checks == 3, "gate is checked again at termination, not just sampling");

            env = new(); watch = new(env); Tick(watch);
            for (var i = 0; i < 60; i++) { env.Step(4, 4); Tick(watch); }
            Check(env.Restarts == 2 && watch.Status.HasError && watch.Status.Message.Contains("暂停自动"), "two unsuccessful post-checks open circuit instead of endless restart");
            env = new() { Replace = false }; watch = new(env); High(watch, env);
            env.Step(31, 0); Tick(watch);
            Check(watch.Status.HasError && !watch.Status.CheckingAfterRestart && env.Restarts == 1, "same old DWM for 30 seconds reports failure");
            env = new(); watch = new(env); High(watch, env); env.ReadError = true;
            env.Step(31, 0); Tick(watch);
            Check(watch.Status.HasError && !watch.Status.CheckingAfterRestart, "post-restart query denial cannot become success");
            env = new(); watch = new(env); High(watch, env);
            for (var i = 0; i < 12; i++) { env.Current = env.Current with { Created = env.Current.Created + 1 }; env.Step(4, 0); Tick(watch); }
            Check(watch.Status.HasError && !watch.Status.CheckingAfterRestart, "replacement churn has an overall verification deadline");

            env = new(); watch = new(env);
            var result = await watch.RestartNowAsync(() => true, CancellationToken.None);
            Check(result.Success && env.Restarts == 1 && env.Time >= TimeSpan.FromSeconds(11), "manual restart waits for replacement, stability and CPU sample");
            result = await watch.RestartNowAsync(() => true, CancellationToken.None);
            Check(!result.Success && env.Restarts == 1 && result.Message.Contains("冷却"), "manual requests cannot bypass cooldown");
            env = new() { Replace = false }; watch = new(env);
            result = await watch.RestartNowAsync(() => true, CancellationToken.None);
            Check(!result.Success && env.Time >= TimeSpan.FromSeconds(30), "manual no-replacement reports timeout");
            env = new() { FailRestart = true }; watch = new(env);
            result = await watch.RestartNowAsync(() => true, CancellationToken.None);
            Check(!result.Success && !watch.Status.CheckingAfterRestart, "native rejection does not claim restart");
            env = new(); watch = new(env);
            result = await watch.RestartNowAsync(() => false, CancellationToken.None);
            Check(!result.Success && env.Reads == 0 && env.Restarts == 0, "manual blocked request has no process access");
            using var cancel = new CancellationTokenSource();
            env = new(); watch = new(env); env.OnDelay = cancel.Cancel;
            result = await watch.RestartNowAsync(() => true, cancel.Token);
            Check(!result.Success && env.Restarts == 1 && watch.Status.HasError && !watch.Status.CheckingAfterRestart, "cancellation retains unverified outcome and does not repeat termination");

            Check(NativeDwmWatchEnvironment.IsExpected(Original, Original, 1, Original.Image), "matching identity accepted");
            foreach (var changed in new[] { Original with { Pid = 43 }, Original with { Created = 999 }, Original with { Session = 2 }, Original with { Image = @"C:\temp\dwm.exe" } })
                Check(!NativeDwmWatchEnvironment.IsExpected(Original, changed, 1, Original.Image), "PID/time/session/path change rejects termination");
            Check(!NativeDwmWatchEnvironment.IsExpected(Original, Original, 2, Original.Image), "other desktop session cannot be selected");
            Check(!NativeDwmWatchEnvironment.IsExpected(Original, Original, 1, "dwm.exe"), "unknown system folder cannot fall back to relative path");

            // Real engine serialization/stop with a fake DWM and no production tick/system writes.
            env = new();
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            env.Hold = waiting.Task;
            var tweaks = new FrameTweaksService(root, (_, _) => throw new IOException("no powercfg"), () => Guid.Empty.ToString());
            using var engine = new CpuFrameEngine(new PowerService(), tweaks, env);
            engine.TickForTests = _ => Task.CompletedTask; engine.DeltaReaderForTests = () => false;
            await engine.StartMonitorAsync();
            var command = engine.SerializeAutomationAsync(() => engine.RestartDwmAsync());
            Check(engine.Snapshot().Dwm.CheckingAfterRestart && !command.IsCompleted, "engine snapshot shows submitted manual verification");
            var drained = false;
            var stop = engine.SerializeAutomationAsync(() => { drained = true; return Task.FromResult(true); }, stopMonitor: true);
            Check(!drained && !command.IsCompleted && engine.Snapshot().Dwm.CheckingAfterRestart, "stop does not erase a submitted command's verification");
            waiting.SetResult();
            Check((await command.WaitAsync(TimeSpan.FromSeconds(5))).Success, "submitted manual check can finish during graceful close");
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Check(drained && !engine.Snapshot().MonitorRunning && !engine.Snapshot().Dwm.Monitoring && env.Restarts == 1, "stop drains command without repeating restart");

            var now = DateTimeOffset.UtcNow;
            var healthy = new CpuFrameStatusSnapshot { MonitorRunning = true, UpdatedUtc = now,
                Dwm = new() { Monitoring = true, UpdatedUtc = now, Message = "持续监测中", CpuCorePercent = 10, WorkingSetMiB = 300 } };
            Check(FrameStatusPresentation.Dwm(true, true, healthy, now, "").Contains("单核口径"), "UI shows actual one-core CPU and memory snapshot");
            Check(FrameStatusPresentation.Dwm(false, true, healthy, now, "") == "未启用", "unselected preference is not active");
            Check(FrameStatusPresentation.Dwm(true, false, healthy, now, "").Contains("开启一键帧格"), "preference alone does not claim monitoring");
            Check(FrameStatusPresentation.Dwm(true, true, healthy, now.AddSeconds(11), "").Contains("待确认"), "stale transport is not active monitoring");
            Check(FrameStatusPresentation.Dwm(true, true, healthy, now, "disconnected").Contains("待确认"), "disconnected backend cannot show cached success");
            var oldSample = new CpuFrameStatusSnapshot { MonitorRunning = true, UpdatedUtc = now,
                Dwm = healthy.Dwm with { UpdatedUtc = now.AddSeconds(-11) } };
            Check(FrameStatusPresentation.Dwm(true, true, oldSample, now, "").Contains("采样状态待确认"), "fresh heartbeat cannot disguise a stalled DWM sampling tick");

            Console.WriteLine($"DWM watchdog passed: {_count} checks; fake processes/clock only, no real DWM termination.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Tick(DwmWatchdog watch) => watch.Tick(true, "game", "", () => true);
    private static void High(DwmWatchdog watch, Fake env)
    { Tick(watch); env.Step(4, 4); Tick(watch); env.Step(4, 4); Tick(watch); }
    private static void Verify(DwmWatchdog watch, Fake env, bool high)
    { env.Step(2, 0); Tick(watch); env.Step(8, 0); Tick(watch); env.Step(4, high ? 4 : 0); Tick(watch); }

    private sealed class Fake : IDwmWatchEnvironment
    {
        internal DwmIdentity Current = Original;
        internal TimeSpan Time;
        internal double Cpu, Memory = 100;
        internal int Reads, Restarts, Delays;
        internal bool ReadError, FailRestart, Replace = true;
        internal Action? OnDelay;
        internal Task? Hold;
        public TimeSpan Now => Time;
        internal void Step(double seconds, double cpu) { Time += TimeSpan.FromSeconds(seconds); Cpu += cpu; }
        public DwmReading Read()
        { Reads++; if (ReadError) throw new IOException("fixture denied"); return new(Current, TimeSpan.FromSeconds(Cpu), Memory); }
        public OperationResult Restart(DwmIdentity expected, Func<bool> allowed)
        {
            if (!allowed() || expected != Current || FailRestart) return OperationResult.Fail("fixture refused");
            Restarts++; if (Replace) { Current = Current with { Pid = Current.Pid + 1, Created = Current.Created + 1 }; Cpu = 0; }
            return OperationResult.Ok("fixture accepted");
        }
        public async Task Delay(TimeSpan duration, CancellationToken token)
        {
            if (Hold is { } hold) { Hold = null; await hold.WaitAsync(token); }
            Delays++; Time += duration; OnDelay?.Invoke(); token.ThrowIfCancellationRequested();
        }
    }
}
