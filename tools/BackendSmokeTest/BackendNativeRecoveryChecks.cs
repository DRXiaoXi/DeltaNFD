using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class BackendNativeRecoveryChecks
{
    internal static async Task RunAsync()
    {
        var count=0;
        void Check(bool ok,string message) { if(!ok)throw new Exception(message);count++; }
        void Reject(Action action,string message)
        { try {action();}catch(InvalidDataException){count++;return;}throw new Exception(message); }
        var id=Guid.NewGuid();var root=Path.Combine(Path.GetTempPath(),"DeltaNFD-native-recovery-"+id.ToString("N"));
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"isolated-test.marker"),id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root,id);
        try
        {
            IReadOnlyList<BackendProcessTree.Entry> Read(params BackendNativeProcessSnapshot.Row[] rows) =>
                BackendNativeProcessSnapshot.Read(()=>rows,pid=>pid==100?100:pid==200?200:300,()=>1000);
            var tree=Read(new("root",100,1),new("powercfg.exe",200,100),new("child",300,200));
            var captured=BackendProcessTree.Capture(100,100,tree);
            Check(captured.Ok&&captured.Children.Count==2,"native metadata follows child and grandchild identities");
            tree=BackendNativeProcessSnapshot.Read(()=>[new("root",100,1),new("protected unrelated",4,0)],pid=>pid==100?100:0,()=>1000);
            Check(BackendProcessTree.Capture(100,100,tree) is {Ok:true,Children.Count:0},"unreadable unrelated identity does not invent a child");
            tree=BackendNativeProcessSnapshot.Read(()=>[new("child",200,100)],_=>0,()=>1000);
            Check(!BackendProcessTree.Capture(100,100,tree).Ok,"unreadable related identity remains unknown");
            tree=BackendNativeProcessSnapshot.Read(()=>[new("child",200,100)],_=>throw new UnauthorizedAccessException("fixture"),()=>1000);
            Check(!BackendProcessTree.Capture(100,100,tree).Ok,"access failure cannot be an empty successful tree");
            tree=BackendNativeProcessSnapshot.Read(()=>[new("child",200,100)],_=>1100,()=>1000);
            Check(!BackendProcessTree.Capture(100,100,tree).Ok,"PID reuse or birth during snapshot is not assigned the old row's parent");
            tree=BackendNativeProcessSnapshot.Read(()=>[new("reused root",100,1),new("old child",200,100),new("new child",300,100)],pid=>pid==100?500:pid==200?400:600,()=>1000);
            captured=BackendProcessTree.Capture(100,100,tree);
            Check(captured.Ok&&captured.Children.Count==1&&captured.Children[0].Pid==200,"existing PID-reuse upper bound is preserved");
            Reject(()=>Read(),"empty snapshot must fail");
            Reject(()=>Read(new("a",100,1),new("duplicate",100,1)),"duplicate PID must fail");
            Reject(()=>Read(new BackendNativeProcessSnapshot.Row("invalid",-1,1)),"invalid PID must fail");
            Reject(()=>BackendNativeProcessSnapshot.Read(()=>Enumerable.Repeat(new BackendNativeProcessSnapshot.Row("many",100,1),65537).ToArray(),_=>100,()=>1000),"over-budget snapshot must fail");

            var path=BackendUnconfirmedStore.RecordPathForTests(BackendModules.CpuFrame);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            void Fixture(IReadOnlyList<BackendUnconfirmedStore.ChildProcess>? children=null)
            {
                File.WriteAllText(path,JsonSerializer.Serialize(new
                {
                    Module=BackendModules.CpuFrame,Phase="unconfirmed",Command=BackendCommands.CpuFramePowerUnlock,
                    RequestId=Guid.NewGuid().ToString("D"),SessionId=Guid.NewGuid().ToString("D"),
                    Pid=int.MaxValue,StartTicks=1L,Path=Environment.ProcessPath,CreatedUtc=DateTimeOffset.UtcNow,
                    Reason="disconnected",Children=children??[],ChildrenUnknown=true,
                }));
            }
            BackendUnconfirmedStore.Review Current()=>BackendUnconfirmedStore.ReviewAll().Single(r=>r.Module==BackendModules.CpuFrame);
            Fixture();var original=File.ReadAllBytes(path);var stale=Current();
            Check(!stale.CanAcknowledge&&!stale.CanRestore,"old unknown-child record initially remains blocked");
            BackendUnconfirmedStore.ProcessSnapshotForTests=()=>throw new InvalidDataException("fixture incomplete enumeration");
            Check(!BackendUnconfirmedStore.RefreshChildren(BackendModules.CpuFrame)&&File.ReadAllBytes(path).SequenceEqual(original),"failed enumeration preserves exact evidence and blocker");
            BackendUnconfirmedStore.ProcessSnapshotForTests=()=>[new("unrelated",123,0,100)];
            Check(BackendUnconfirmedStore.RefreshChildren(BackendModules.CpuFrame),"complete native snapshot rechecks legacy unknown record without WMI");
            var ready=Current();
            Check(ready.CanAcknowledge&&ready.CanRestore&&!ready.ChildrenUnknown,"confirmed old parent exit and complete child check make recovery available");
            Check(ready.Fingerprint!=stale.Fingerprint&&File.Exists(path),"recheck changes fingerprint but does not automatically archive the operation");
            Check(Directory.GetFiles(Path.Combine(root,"official-backend-reviewed"),"cpuframe-children-recheck-*.json").Any(p=>File.ReadAllBytes(p).SequenceEqual(original)),"original failed-query record is archived byte-for-byte");
            var rejected=false;try {BackendUnconfirmedStore.AcknowledgeCurrentState(stale);}catch(IOException){rejected=true;}
            Check(rejected&&File.Exists(path),"pre-refresh confirmation cannot clear the refreshed record");
            var snapshot=new ReconcileSnapshot {Module=BackendModules.CpuFrame,Complete=true,Summary="fixture",RecoveryScopes=[new RecoveryScope {Id="power",Description="fixture",Available=true}]};
            Check(BackendRecovery.RecoveryChoices(ready,snapshot) is [{Id:"all"}],"old power-unlock request exposes only the existing complete-backup recovery scope");
            Check(BackendRecovery.CanKeepCurrentState(ready,snapshot),"retain-current remains an explicit separate user decision");
            var kept=BackendUnconfirmedStore.AcknowledgeCurrentState(ready);
            Check(!File.Exists(path)&&File.Exists(kept),"explicit retain-current archives the synthetic record only after full recheck");

            using var self=Process.GetCurrentProcess();var start=self.StartTime.ToUniversalTime().Ticks;
            Fixture([new("known child",self.Id,start)]);
            Check(BackendUnconfirmedStore.RefreshChildren(BackendModules.CpuFrame),"known living child can be re-enumerated");
            Check(Current() is {ChildrenAlive:true,CanAcknowledge:false,CanRestore:false},"known living child remains blocked even if its parent is gone");
            Fixture();BackendUnconfirmedStore.ProcessSnapshotForTests=()=>[new("unknown child",self.Id,int.MaxValue,0)];
            original=File.ReadAllBytes(path);
            Check(!BackendUnconfirmedStore.RefreshChildren(BackendModules.CpuFrame)&&File.ReadAllBytes(path).SequenceEqual(original),"related unknown start time never unlocks the old record");
            BackendUnconfirmedStore.ProcessSnapshotForTests=null;
            var native=BackendNativeProcessSnapshot.Read();
            Check(native.Any(p=>p.Pid==self.Id&&p.StartTicks/10==start/10),"actual Windows native snapshot reads this test's process creation identity");

            // One owned no-window child. It waits on its private redirected stdin and exits on EOF.
            var info=new ProcessStartInfo(Environment.ProcessPath!) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            if(Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet",StringComparison.OrdinalIgnoreCase))info.ArgumentList.Add(typeof(BackendNativeRecoveryChecks).Assembly.Location);
            info.ArgumentList.Add("--backend-process-child-fixture");
            using var child=Process.Start(info)??throw new Exception("Owned child failed to start");
            try
            {
                Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))=="owned-process-ready","owned native-test child is ready");
                captured=BackendUnconfirmedStore.CaptureDescendants(self.Id,start);
                Check(captured.Ok&&captured.Children.Any(p=>p.Pid==child.Id&&p.StartTicks>0),"real native enumeration sees the owned child and its creation identity");
            }
            finally
            {
                child.StandardInput.Close();
                try {await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}
                catch(TimeoutException){child.Kill(entireProcessTree:true);await child.WaitForExitAsync();}
            }
            Check(child.HasExited,"only the owned child is stopped through stdin closure");
            File.Delete(path); // Synthetic record, never a real user's blocker.
            Console.WriteLine($"Native recovery checks: {count} passed; injected failure/legacy evidence plus own no-window child, no system policy or real recovery writes.");
        }
        finally
        {
            BackendUnconfirmedStore.ProcessSnapshotForTests=null;
            var temp=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(root).StartsWith(temp,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe fixture path");
            Directory.Delete(root,true);
        }
    }
}
