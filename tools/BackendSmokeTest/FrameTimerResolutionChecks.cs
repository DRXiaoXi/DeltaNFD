using System.Diagnostics;
using System.Reflection;
using DeltaNFD.Execution.CpuFrame;
using DeltaNFD.Native;
using DeltaNFD.Services;

internal static class FrameTimerResolutionChecks
{
    private static int _count;
    private static void Check(bool ok, string why) { if (!ok) throw new IOException(why); _count++; }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly int Failed = unchecked((int)0xC0000001);

    internal static async Task RunAsync()
    {
        _count = 0;
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-frame-timer-" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        Process? child = null;
        try
        {
            var api = new FakeApi(); var timer = new FrameTimerResolution(api);
            Check(timer.Update(false).Success && !timer.NeedsRelease, "idle owns no external/manual timer request");
            Check(api.Requests == 0 && api.Releases == 0 && api.Queries == 0, "idle performs no timer calls or new polling");
            Check(timer.Update(true, stillAllowed: () => true).Success && timer.Status.RequestHeld && timer.Status.TargetReached, "accepted 0.5ms request with matching observation");
            Check(timer.Status.ReturnedActualMilliseconds == .5 && timer.Status.SystemCurrentMilliseconds == .5, "request output and system query remain separate");
            for (var i=0;i<5;i++) Check(timer.Update(true).Success, "holding request only reads current resolution");
            Check(api.Requests == 1 && api.Queries == 5, "ticks do not add reference-counted requests");
            api.Current = 1;
            Check(!timer.Update(true).Success && timer.Status.HasError && timer.Status.RequestHeld && !timer.Status.TargetReached, "coarser observation is not claimed as target reached");
            Check(api.Requests == 1, "coarser observation does not cause repeated native requests");
            api.Current = null;
            Check(!timer.Update(true).Success && timer.Status.SystemCurrentMilliseconds is null && timer.NeedsRelease, "unknown current resolution preserves release responsibility");
            api.ThrowQuery=true;
            Check(!timer.Update(true).Success && timer.Status.HasError, "query exception stays unknown");
            api.ThrowQuery=false; api.Current=.5;
            Check(timer.Update(true).Success && !timer.Status.HasError, "fresh query recovers observation without reapplying");
            Check(timer.Update(false).Success && !timer.NeedsRelease && api.Releases==1, "symmetric release");
            Check(timer.Status.SystemCurrentMilliseconds==.5 && !timer.Status.RequestHeld, "another owner's 0.5ms reading does not mean our release failed");
            Check(timer.Update(false).Success && api.Releases==1, "release is idempotent, never releases another request");

            api = new() { Actual=10000, Current=1 }; timer = new(api);
            Check(!timer.Update(true).Success && timer.NeedsRelease && timer.Status.ReturnedActualMilliseconds==1, "clamped successful API call does not claim 0.5ms");
            Check(timer.Update(true) is { Success:false,IsPartial:true,OutcomeUnknown:false }, "clamping is known partial activation, not unknown execution");
            Check(timer.Status.Message.Contains("尚未确认") && !timer.Status.TargetReached, "clamping is explicit");
            Check(timer.Update(false).Success && !timer.NeedsRelease, "clamped request is still correctly released");
            api = new() { Actual=0 }; timer = new(api);
            Check(!timer.Update(true).Success && timer.Status.ReturnedActualMilliseconds is null && timer.NeedsRelease, "zero actual result is unknown, but accepted request is owned");

            api=new() { RequestStatus=Failed }; timer=new(api);
            Check(!timer.Update(true).Success && !timer.NeedsRelease && timer.Status.HasError, "request failure cannot become success");
            Check(timer.Update(false).Success && api.Releases==0, "a rejected request creates no release/adoption");
            api=new(); timer=new(api); Check(timer.Update(true).Success, "release-failure fixture acquires");
            api.ReleaseStatus=Failed;
            Check(!timer.Update(false).Success && timer.NeedsRelease && timer.Status.ReleasePending, "failed release preserves ownership and pending state");
            var reads=api.Queries;
            Check(!timer.Update(true).Success && api.Requests==1 && api.Queries==reads, "unfinished release blocks silent resumption");
            api.ReleaseStatus=0;
            Check(timer.Update(false).Success && !timer.NeedsRelease && !timer.Status.ReleasePending, "explicit release retry can finish");
            Check(timer.Update(true).Success && api.Requests==2, "fresh explicit request works after verified release");

            api=new() { ThrowRequest=true }; timer=new(api);
            Check(!timer.Update(true).Success && timer.Status.OwnershipUnknown && timer.Status.ReleasePending, "throwing request retains unknown ownership");
            Check(timer.Update(true) is { Success:false,IsPartial:true,OutcomeUnknown:true }, "unknown ownership reaches the standard recovery result contract");
            Check(!timer.Update(true).Success && api.Requests==1, "unknown request is never replayed");
            Check(timer.Update(false).Success && !timer.NeedsRelease, "unknown request needs explicit acknowledged release");
            api=new(); timer=new(api); timer.Update(true); api.ThrowRelease=true;
            Check(!timer.Update(false).Success && timer.Status.OwnershipUnknown && timer.NeedsRelease, "throwing release stays pending");
            api.ThrowRelease=false;
            Check(timer.Update(false).Success && !timer.NeedsRelease, "release unknown can be reconciled");
            api=new(); timer=new(api);
            Check(timer.Update(true, stillAllowed:()=>false).Success && api.Requests==0 && !timer.Status.Wanted, "last-moment closed execution gate causes no request");
            Check(!timer.Update(true, stillAllowed:()=>throw new IOException("unknown")).Success && api.Requests==0, "unreadable gate fails closed");
            for(var i=0;i<12;i++) { Check(timer.Update(true).Success, "repeat-session acquire"); timer.Update(true); Check(timer.Update(false).Success, "repeat-session release"); }
            Check(api.Requests==12 && api.Releases==12, "repeated sessions have exactly balanced calls");

            // A stop racing a native request drains the owner lock and releases afterwards.
            api=new(); timer=new(api);
            var entered=Signal(); using var release=new ManualResetEventSlim();
            api.OnRequest=()=>{entered.SetResult(); release.Wait();};
            var acquire=Task.Run(()=>timer.Update(true));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopped=Task.Run(()=>timer.Update(false));
            await Task.Delay(30);
            Check(!stopped.IsCompleted && api.Releases==0, "stop cannot finish before in-flight request returns");
            release.Set(); await Task.WhenAll(acquire,stopped).WaitAsync(TimeSpan.FromSeconds(5));
            Check(stopped.Result.Success && !timer.NeedsRelease && api.Requests==api.Releases, "stop releases accepted racing request");
            Check(timer.Update(true,stillAllowed:()=>false).Success && api.Requests==1, "late work cannot reacquire behind a closed gate");

            var childDir=Path.Combine(root,"child");
            Copy(Path.GetDirectoryName(Environment.ProcessPath!)!,childDir);
            var childExe=Path.Combine(childDir,Path.GetFileName(Environment.ProcessPath!));
            child=Process.Start(new ProcessStartInfo(childExe){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=childDir,ArgumentList={"--frame-timer-idle"}})!;
            Check((await GameTargetService.Default.ChangeAsync(true,childExe)).Success, "own idle child is the only selected game fixture");
            void Settings(bool frame=true,bool parent=true,bool option=true) => AppSettingsStore.Update(s=>
            { s.FrameModeActive=frame;s.FrameResponseBoostEnabled=parent;s.FrameResponseBoostTimerEnabled=option; });
            CpuFrameEngine Engine(FakeApi fake)
            {
                var tweaks=new FrameTweaksService(root,(_,_)=>throw new IOException("powercfg forbidden"),()=>Guid.Empty.ToString());
                return new CpuFrameEngine(new PowerService(),tweaks,new NoDwm(),fake)
                { TickForTests=_=>Task.CompletedTask, DeltaReaderForTests=()=>false };
            }
            static Task Tick(CpuFrameEngine engine) => (Task)typeof(CpuFrameEngine).GetMethod("TickAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(engine,[CancellationToken.None])!;

            Settings(); api=new();
            using(var engine=Engine(api))
            {
                var result=await engine.ApplyBoostAsync(true);
                Check(result.Success && api.Requests==0 && !engine.Snapshot().TimerActive, "configuring before monitor starts does not claim activation");
                await engine.StartMonitorAsync(); await Tick(engine);
                Check(engine.Snapshot().Timer.RequestHeld && api.Requests==1, "first valid tick acquires for selected live child");
                foreach(var flags in new[]{(false,true,true),(true,false,true),(true,true,false)})
                {
                    Settings(flags.Item1,flags.Item2,flags.Item3); await Tick(engine);
                    Check(!engine.Snapshot().TimerActive, "latest frame/parent/option gate releases stale intent");
                    Settings(); await Tick(engine);
                    Check(engine.Snapshot().Timer.RequestHeld, "valid next session retains configured intent");
                }
                var resultStop=await engine.StopMonitorAsync();
                Check(resultStop.Success && !engine.Snapshot().TimerActive && !engine.Snapshot().MonitorRunning, "monitor stop releases its request");
                var count=api.Requests; await engine.ApplyBoostAsync(true);
                Check(api.Requests==count && !engine.Snapshot().TimerActive, "command after stop cannot reacquire without monitor");
                await engine.StartMonitorAsync(); await Tick(engine);
                Check(OfflineModeStateStore.Default.TryUpdate(s=>s.CancelRequested=true,out _), "isolated recovery pause enabled");
                await Tick(engine);
                Check(!engine.Snapshot().TimerActive, "recovery pause releases request");
                Check(OfflineModeStateStore.Default.TryUpdate(s=>s.CancelRequested=false,out _), "isolated recovery pause cleared");
                await Tick(engine); engine.Dispose();
                Check(!engine.Snapshot().TimerActive && api.Requests==api.Releases, "direct Dispose balances all successful sessions");
            }

            Settings(); api=new() { RequestStatus=Failed };
            using(var engine=Engine(api))
            {
                await engine.StartMonitorAsync(); var result=await engine.ApplyBoostAsync(true);
                Check(!result.Success && !engine.Snapshot().TimerActive && engine.Snapshot().Timer.HasError, "boost propagates request failure to caller");
            }
            api=new() { Actual=10000, Current=1 };
            using(var engine=Engine(api))
            {
                await engine.StartMonitorAsync(); var result=await engine.ApplyBoostAsync(true);
                Check(!result.Success && engine.Snapshot().TimerActive && !engine.Snapshot().Timer.TargetReached, "clamped request is partial instead of all-applied");
                Check((await engine.ApplyTimerAsync(false)).Success && !engine.Snapshot().TimerActive, "clamped request is releasable through real command");
            }
            api=new();
            using(var engine=Engine(api))
            {
                await engine.StartMonitorAsync(); await engine.ApplyBoostAsync(true); api.ReleaseStatus=Failed;
                Check(!(await engine.ApplyTimerAsync(false)).Success && engine.Snapshot().Timer.ReleasePending, "timer command propagates failed release");
                Check(!(await engine.StopMonitorAsync()).Success, "stop propagates failed timer cleanup");
                Check(!(await engine.RestoreAllAsync()).Success, "aggregate restore cannot hide timer failure");
                var recovery=await engine.RecoverAsync("timer");
                Check(!recovery.Success && !recovery.Verified, "recovery success and verification agree on failure");
                Check(engine.Reconcile().RecoveryScopes.Single(s=>s.Id=="timer").Available, "pending timer release remains recoverable");
                api.ReleaseStatus=0;
                Check((await engine.RecoverAsync("timer")).Verified && !engine.Snapshot().TimerActive, "successful explicit retry passes final recovery gate");
            }

            api=new();
            using(var engine=Engine(api))
            {
                await engine.StartMonitorAsync(); await engine.ApplyBoostAsync(true);
                child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await Tick(engine);
                Check(!engine.Snapshot().TimerActive && engine.Snapshot().Timer.Message.Contains("等待目标游戏"), "game exit releases and waits rather than reporting failure");
            }
            UiChecks();
            Console.WriteLine($"Frame timer passed: {_count} checks; fake native timer only, own idle child; no host timer/config writes.");
        }
        finally
        {
            if(child is not null) { if(!child.HasExited){child.Kill();await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));} child.Dispose(); }
            Directory.Delete(root,true);
        }
    }

    private static void UiChecks()
    {
        var now=DateTimeOffset.UtcNow;
        CpuFrameStatusSnapshot Snapshot(TimerResolutionSnapshot timer,bool running=true)=>new(){MonitorRunning=running,UpdatedUtc=now,Timer=timer,TimerActive=timer.RequestHeld||timer.OwnershipUnknown};
        var state=new TimerResolutionSnapshot { UpdatedUtc=now, RequestHeld=true, Wanted=true, TargetReached=true, Message="已持有，返回实际 0.5ms，系统读数 0.5ms" };
        Check(FrameStatusPresentation.Timer(true,true,Snapshot(state),now,"").Contains("返回实际"), "UI shows actual status rather than preference");
        Check(FrameStatusPresentation.Timer(true,true,Snapshot(state),now,"lost").Contains("待确认"), "connection loss cannot show cached activation");
        Check(FrameStatusPresentation.Timer(true,true,Snapshot(state),now.AddSeconds(11),"").Contains("待确认"), "stale heartbeat is unknown");
        var pending=state with { ReleasePending=true,HasError=true,Message="释放失败" };
        Check(FrameStatusPresentation.Timer(false,false,Snapshot(pending),now,"")=="释放失败", "turning options off cannot hide pending release");
        Check(FrameStatusPresentation.Timer(false,false,Snapshot(pending),now,"lost").Contains("待核对"), "lost connection retains release warning even with options off");
        Check(FrameStatusPresentation.Timer(false,true,Snapshot(state),now,"").Contains("等待后端释放"), "short handover interval does not claim already disabled");
        Check(FrameStatusPresentation.Timer(true,true,Snapshot(state with{UpdatedUtc=now.AddSeconds(-11)}),now,"").Contains("等待后端"), "fresh heartbeat does not disguise stale timer read");
        Check(FrameStatusPresentation.Timer(true,true,Snapshot(state,false),now,"").Contains("释放待核对"), "stopped monitor with held request is not active success");
        var old=new CpuFrameStatusSnapshot { UpdatedUtc=now,MonitorRunning=true,TimerActive=true };
        Check(FrameStatusPresentation.Timer(false,false,old,now,"").Contains("待核对"), "legacy held flag without details fails closed");
        Check(FrameStatusPresentation.Timer(false,false,Snapshot(new(){UpdatedUtc=now}),now,"")=="未启用", "verified no-owned-request can show disabled");
        Check(FrameStatusPresentation.Timer(false,false,null,now,"lost").Contains("待确认"), "disconnect before first snapshot cannot claim a disabled option means released");
    }

    private static void Copy(string source,string destination)
    {
        Directory.CreateDirectory(destination);
        foreach(var file in Directory.GetFiles(source)) File.Copy(file,Path.Combine(destination,Path.GetFileName(file)));
        foreach(var dir in Directory.GetDirectories(source))
        { if((File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0)throw new IOException("fixture link");Copy(dir,Path.Combine(destination,Path.GetFileName(dir))); }
    }
    private sealed class FakeApi : IFrameTimerApi
    {
        internal int Requests,Releases,Queries,RequestStatus,ReleaseStatus;
        internal uint Actual=5000;
        internal double? Current=.5;
        internal bool ThrowRequest,ThrowRelease,ThrowQuery;
        internal Action? OnRequest;
        public TimerResolutionRequestResult Request()
        { Requests++;OnRequest?.Invoke();if(ThrowRequest)throw new IOException("request unknown");return new(true,RequestStatus,5000,Actual,Current); }
        public TimerResolutionRequestResult Release()
        { Releases++;if(ThrowRelease)throw new IOException("release unknown");return new(true,ReleaseStatus,0,5000,Current); }
        public double? QueryMilliseconds()
        { Queries++;if(ThrowQuery)throw new IOException("query unknown");return Current; }
    }
    private sealed class NoDwm : IDwmWatchEnvironment
    {
        public TimeSpan Now=>TimeSpan.Zero;
        public DwmReading Read()=>throw new IOException("DWM read forbidden");
        public OperationResult Restart(DwmIdentity expected,Func<bool> allowed)=>throw new IOException("DWM write forbidden");
        public Task Delay(TimeSpan duration,CancellationToken token)=>throw new IOException("DWM wait forbidden");
    }
}
