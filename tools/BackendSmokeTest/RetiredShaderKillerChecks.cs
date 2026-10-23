using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class RetiredShaderKillerChecks
{
    public static async Task RunAsync()
    {
        int passed=0;void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
        var id=Guid.NewGuid();string root=Path.Combine(Path.GetTempPath(),"DeltaNFD-retired-shaderkiller",id.ToString("N"));
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"isolated-test.marker"),id.ToString("D"));AppDataPaths.ConfigureIsolatedDataRoot(root,id);
        string module=BackendModules.ShaderKiller;string record=BackendUnconfirmedStore.RecordPathForTests(module);Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        using var self=Process.GetCurrentProcess();
        string Fixture(int pid,long start,bool unknown=false,object? children=null)=>JsonSerializer.Serialize(new {Module=module,Phase="unconfirmed",Command="shaderkiller.start",Pid=pid,StartTicks=start,Path=Path.Combine(root,"DeltaNFD.Backend.ShaderKiller.exe"),ChildrenUnknown=unknown,Children=children,CreatedUtc=DateTimeOffset.UtcNow});
        Check(!BackendModules.All.ContainsKey(module)&&BackendModules.Retired.ContainsKey(module),"module removed from active registry but legacy identity retained");
        try{await BackendClient.StartAsync(module,default);throw new Exception("retired module started");}catch(BackendContractException){Check(true,"retired worker cannot launch");}
        File.WriteAllText(record,Fixture(self.Id,self.StartTime.ToUniversalTime().Ticks));byte[] original=File.ReadAllBytes(record);
        Check(BackendUnconfirmedStore.PendingModules().Contains(module),"legacy pending record stays visible");
        var live=BackendUnconfirmedStore.ReviewAll().Single();Check(!live.CanAcknowledge&&!live.CanRestore,"live legacy owner prevents archive and restore");
        var pending=await BackendLifecycle.PrepareHandoverAsync("retired shader test",TimeSpan.FromSeconds(2));
        Check(pending.Contains(module)&&File.ReadAllBytes(record).SequenceEqual(original),"handover preserves original legacy evidence and blocker");
        File.WriteAllText(record,Fixture(int.MaxValue,1,true));Check(!BackendUnconfirmedStore.ReviewAll().Single().CanAcknowledge,"unknown children remain blocked");
        File.WriteAllText(record,"{bad");Check(!BackendUnconfirmedStore.ReviewAll().Single().CanAcknowledge,"corrupt legacy record remains blocked");
        File.WriteAllText(record,Fixture(int.MaxValue,1,children:new[]{new{Name="owned-test",Pid=self.Id,StartTicks=self.StartTime.ToUniversalTime().Ticks}}));
        Check(!BackendUnconfirmedStore.ReviewAll().Single().CanAcknowledge,"live legacy child prevents archive");
        File.WriteAllText(record,Fixture(int.MaxValue,1));original=File.ReadAllBytes(record);
        var exited=BackendUnconfirmedStore.ReviewAll().Single();Check(exited.CanAcknowledge&&!exited.CanRestore,"exited legacy task supports manual archive only");
        Check(BackendRecovery.CanKeepCurrentState(exited,null),"generic settings review can keep current legacy state without launching worker");
        string archive=await BackendLifecycle.AcknowledgeCurrentStateAsync(exited);
        Check(File.ReadAllBytes(archive).SequenceEqual(original)&&!File.Exists(record),"manual archive preserves exact legacy record bytes");
        Check(BackendUnconfirmedStore.PendingModules().Count==0,"only isolated legacy blocker resolved");
        Console.WriteLine($"Retired shaderkiller checks: {passed} passed; no real user records modified.");
    }
}
