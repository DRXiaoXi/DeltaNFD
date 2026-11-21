using System.Text.Json;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;
using DeltaNFD.Inspection;
using DeltaNFD.Execution.OptimalClock;

internal static class WakeComparisonChecks
{
    internal static void Run()
    {
        int assertions=0;
        void Check(bool value,string title) { if (!value) throw new Exception(title); assertions++; }
        WakeBlock Row(uint? t,int n,double p=1) => new(n,t??10000,t??10000,1,1.5,.1,p,0,t.HasValue?5000u:0u);
        WakeReport Run(Func<uint?,int,WakeBlock?> fn,bool quick=false,uint? reference=null) => WakeComparison.Run(reference,"A",quick,fn,default);
        Check(Run((t,n)=>Row(t,n)).Verdict==WakeVerdict.Keep,"Different readback, identical performance must not win");
        Check(Run((t,n)=>Row(null,n) with {ApiReading=5000}).Verdict==WakeVerdict.Inconclusive,"Indistinguishable actual settings");
        Check(Run((t,n)=>Row(t,n,t is null?1:.99)).Verdict==WakeVerdict.Keep,"Negligible effect");
        var lowerBoundCalls = new List<uint>();
        var lowerQuick = Run((t,n)=>{if(t is uint v) lowerBoundCalls.Add(v);return Row(t,n,t==5000?.3:1);},true);
        Check(lowerQuick.Verdict==WakeVerdict.Shortlist && lowerQuick.Message.Contains("0.5000") && lowerBoundCalls.Min()==5000,"Quick scan can nominate 0.5000 without requesting any lower value");
        Check(Run((t,n)=>Row(t,n,t==5100?.3:1)).Candidate==5100,"0.5100 observer/0.5000 API can win only on fresh benefit");
        Check(Run((t,n)=>Row(t,n,t==5100?.3:1) with {After=10000}).Verdict==WakeVerdict.Inconclusive,"After-reading reversal invalidates candidate");
        Check(Run((t,n)=>Row(t,n,n==40 && t.HasValue?.3:1)).Verdict==WakeVerdict.Keep,"Screening benefit does not transfer to final");
        Check(Run((t,n)=>Row(t,n,t.HasValue?.4:1) with {Skipped=t.HasValue?1:0}).Verdict==WakeVerdict.Keep,"More skipped deadlines cannot win");
        Check(Run((t,n)=>Row(t,n,t.HasValue?.4:1) with {SleepMedian=t.HasValue?1.1:1}).Verdict==WakeVerdict.Keep,"Worse typical Sleep cannot win");
        Check(Run((t,n)=>Row(t,n,t.HasValue?.4:1) with {LateMedian=t.HasValue?.2:.1}).Verdict==WakeVerdict.Keep,"Worse typical deadline cannot win");
        int k=0; Check(Run((t,n)=>Row(t,n,++k%2==0?1.6:1)).Verdict==WakeVerdict.Inconclusive,"Unstable initial baseline");
        k=0; Check(Run((t,n)=>Row(t,n,t is null && n==96 && ++k%2==0?1.6:1)).Verdict==WakeVerdict.Inconclusive,"Unstable final baseline");
        k=0; Check(Run((t,n)=>Row(t,n,t.HasValue?.85: ++k%2==0?1.05:.87)).Verdict==WakeVerdict.Keep,"Cannot beat A/A variation");
        var windows = new List<int>();
        var recovered = Run((t,n)=>{ if(t is null) windows.Add(n); return Row(t,n,windows.Count==2 && t is null?1.6:1); });
        Check(windows.Take(6).SequenceEqual(new[]{40,40,40,96,96,96}) && recovered.Verdict==WakeVerdict.Keep,"Full run confirms a noisy baseline before comparing");
        int count=0; var noisy=Run((t,n)=>Row(t,n,++count%2==0?1.6:1));
        Check(count==6 && noisy.Verdict==WakeVerdict.Inconclusive && noisy.Message.Contains("尚未比较"),"Full retry bounded; no ranking from noise");
        count=0; Run((t,n)=>Row(t,n,++count%2==0?1.6:1),true); Check(count==3,"Quick remains short");
        int finalA=0; var mixed=Run((t,n)=>Row(t,n,t is null && n==96 && ++finalA==2?1.6:1));
        Check(mixed.Verdict==WakeVerdict.Inconclusive && mixed.Candidate is null && mixed.Message.Contains("无法判断") && mixed.Message.Contains("复测未见稳定优势"),"Mixed evidence keeps separate candidate outcomes");
        var finalistCalls=new List<uint?>(); var result=Run((t,n)=>{if(n==96)finalistCalls.Add(t);return Row(t,n,t==5100?.3:1);},reference:5800);
        Check(result.Candidate==5100 && finalistCalls.Take(8).SequenceEqual(new uint?[]{5800,5100,5100,5800,5100,5800,5800,5100}),"Fresh ABBA/BAAB against saved reference");
        Check(Run((t,n)=>{Check(n==40,"Quick never runs final");return Row(t,n);},true).Verdict==WakeVerdict.Shortlist,"Quick cannot recommend");
        Check(Run((t,n)=>null).Verdict==WakeVerdict.Inconclusive,"No baseline");
        Check(Run((t,n)=>Row(t,n) with {LateP95=double.NaN}).Verdict==WakeVerdict.Inconclusive,"Nonfinite sample invalid");
        foreach (var (report, lead) in new[] { (lowerQuick, "值得复测"), (result, "0.5100 ms 值得试用"),
            (Run((t,n)=>Row(t,n)), "建议保留"), (Run((t,n)=>null), "这次没测准") })
            Check(report.Message.StartsWith(lead, StringComparison.Ordinal) && report.Message.Contains("对照设置：A") && report.Message.Contains("未更改保存值"),
                "Player report leads with the outcome and distinguishes comparison from saving: " + report.Message);
        using var cancel=new CancellationTokenSource();int calls=0;try {WakeComparison.Run(null,"A",false,(t,n)=>{calls++;cancel.Cancel();return Row(t,n);},cancel.Token);throw new Exception("Expected cancellation");}catch(OperationCanceledException){Check(calls==1,"Cancel between blocks");}
        var live=new List<FakeChild>();
        NativeOptimalClockMeasurementApi Adapter(bool wrongEcho=false,bool corrupt=false) => new("fixture",psi=>{
            var arg=psi.ArgumentList[0];string line=arg==OptimalClockProbeHost.HoldArgument ? $"HELD:true:{(wrongEcho?5200:5100)}:5000:5000:" : corrupt?"WAKE:invalid":"WAKE:"+JsonSerializer.Serialize(Row(5100,40));
            var child=new FakeChild(line);live.Add(child);return child;});
        var adapter=Adapter();Check(adapter.MeasureWakeBlock(5100,40) is {Before:5100,ApiReading:5000} && live.Count==2 && live.All(c=>c.Disposed),"Both independent children exit");
        live.Clear(); Check(Adapter(true).MeasureWakeBlock(5100,40)==null && live.Count==1 && live[0].Disposed,"Wrong holder echo aborts before observer");
        live.Clear();try{Adapter(corrupt:true).MeasureWakeBlock(5100,40);throw new Exception("Expected malformed payload");}catch(JsonException){Check(live.All(c=>c.Disposed),"Malformed observer output releases both children");}
        live.Clear();Check(Adapter().MeasureWakeBlock(null,40) is {ApiReading:0} && live.Count==1,"Reference without request has no holder");
        // Exercise the actual command owner on Win10/DPC with simulated child processes and isolated settings.
        string root=Path.Combine(Path.GetTempPath(),"wake-report-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var marker=Guid.NewGuid(); File.WriteAllText(Path.Combine(root,"isolated-test.marker"),marker.ToString("D")); AppDataPaths.ConfigureIsolatedDataRoot(root,marker);
        var global=DpcPendulumInspection.Evaluate(new(10,0,19045,5070),new(true,@"D:\fixture\dpclat.exe","fixture"));
        var dpc=new BaseHolder();var combined=new DpcCombinedHolder(dpc,new BaseHolder()); uint? held=null; bool playing=false;var spawned=new List<FakeChild>();
        var fakeNative=new NativeOptimalClockMeasurementApi("fixture",psi=>{
            var args=psi.ArgumentList; string output; Action? close=null;
            if(args[0]==OptimalClockProbeHost.LoadArgument) output="LOAD:"+args[1];
            else if(args[0]==OptimalClockProbeHost.HoldArgument){held=uint.Parse(args[1]);close=()=>held=null;output=$"HELD:true:{held}:5000:5000:";}
            else {Check(args[0]==WakeDeadlineProbe.Argument && dpc.Holding,"Observer runs under admitted DPC base");output="WAKE:"+JsonSerializer.Serialize(Row(held,int.Parse(args[1]),held==5000?.35:1));}
            var child=new FakeChild(output,close);spawned.Add(child);return child;});
        using var engine=new ProcmanEngine(Path.Combine(root,"changes.json"),Path.Combine(root,"rules.json"),clockHolder:combined,clockMeasurement:fakeNative,clockCurrentResolution:()=>10000);
        engine.GlobalPrecisionForTests=()=>global; engine.FrameModeActiveForTests=()=>false; engine.ClockGameRunningForTests=()=>playing;
        engine.ExperimentalTimerFeaturesForTests=true; engine.BackgroundLoopDisabledForTests=true;
        AppSettingsStore.UpdateRequired(s=>{s.OptimalClockScanPlatform=global.PlatformId;s.OptimalClockScanEnvironment=global.ClockEnvironmentRevision;s.OptimalClockScanUtc=DateTimeOffset.Now;s.OptimalClockResolution100ns=10000;});
        var original=JsonSerializer.Serialize(AppSettingsStore.ReadRequired()); var validated=engine.CompareWakeAsync(false,default).GetAwaiter().GetResult();
        Check(validated.Success && validated.Message.Contains("0.5000") && validated.Message.Contains("基础 DPC"),"Engine reports measured candidate: "+validated.Message);
        Check(original==JsonSerializer.Serialize(AppSettingsStore.ReadRequired()) && !dpc.Holding && held==null && spawned.All(x=>x.Disposed),"Saved setting preserved; all holders and load released");
        playing=true;var oldCount=spawned.Count;Check(!engine.CompareWakeAsync(true,default).GetAwaiter().GetResult().Success && spawned.Count==oldCount,"Game blocks measurement before any child");
        Console.WriteLine($"PASS: {assertions} wake comparison assertions (simulated, no real clock request).");
    }
    private sealed class BaseHolder:IOptimalClockHolderApi
    {
        public bool Holding{get;private set;} public bool DpcProcess=>true; public uint Resolution100ns=>Holding?10000u:0u;
        public uint Granted100ns=>0;public uint Current100ns=>10000;public string Error=>"fixture";
        public bool Ensure(uint t){Holding=t==10000;return Holding;}public void Stop()=>Holding=false;
    }
    private sealed class FakeChild(string line,Action? onClose=null):IOptimalClockChildProcess
    {
        public bool HasExited{get;private set;} public bool Disposed{get;private set;}
        public void CloseInput(){HasExited=true;onClose?.Invoke();} public bool WaitForExit(int ms)=>HasExited;public void Kill()=>HasExited=true;
        public string? ReadReadyLine()=>line;public void Dispose()=>Disposed=true;
    }
}
