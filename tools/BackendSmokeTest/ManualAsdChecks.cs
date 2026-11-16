using System.Text.Json;
using System.Xml.Linq;
using DeltaNFD.Backends;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

internal static class ManualAsdChecks
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
        void Reject(Action work, string why) { try { work(); } catch { checks++; return; } throw new Exception(why); }
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-manual-asd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var game = Path.Combine(root, "game"); var pso = Path.Combine(game, "PSOCache"); var system = Path.Combine(root, "D3DSCache");
        Directory.CreateDirectory(pso); Directory.CreateDirectory(system);
        var store = new ManualAsdStore(Path.Combine(root, "software", "session.json"));
        var context = new ManualAsdContext(true, new string('A',64), new string('B',64),
            GameInstallationScope.Select([game], null, true, true), [pso], system);
        ManualAsdContext Read() => context;
        ManualAsdSession Start() => ManualAsdStart.Run(store, Read, () => false,
            (c,t) => ShaderCacheCleanup.CleanMixedRoots(c.PsoPaths,[c.SystemRoot],()=>false,t));
        void FileAt(string path, long bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); NvphFixtureFiles.Raw(path,bytes); }
        try
        {
            foreach (var version in new[] { "621.00", "621.99", "622.00", "700.00" })
                Check(ManualAsdPolicy.Supported(ShaderGpuDetectionState.Nvidia,version), "621+ supported");
            foreach (var version in new[] { "620.99", "", "unknown", "621.100", "-621.00" })
                Check(!ManualAsdPolicy.Supported(ShaderGpuDetectionState.Nvidia,version), "unsupported version rejected");
            Check(!ManualAsdPolicy.Supported(ShaderGpuDetectionState.NotPresent,"621.14"),"other GPU unsupported");
            Check(store.Read() is null,"no implicit session");
            Reject(()=>ManualAsdInspection.Finish(store,"missing",Read),"cannot finish before start");
            FileAt(Path.Combine(pso,"old.nvph"),1); FileAt(Path.Combine(system,"old.nvph"),ManualAsdPolicy.ThresholdBytes+1);
            var outside=Path.Combine(root,"vendor-cache","keep.nvph"); FileAt(outside,1);
            var session=Start();
            Check(session.Stage==ManualAsdStage.Warmup && session.CleanedUtc is not null,"only complete cleanup enters warmup");
            Check(!Directory.EnumerateFileSystemEntries(pso).Any()&&!Directory.EnumerateFileSystemEntries(system).Any(),"both owned cache roots emptied");
            Check(File.Exists(outside),"unrelated vendor cache untouched");
            Check(new ManualAsdStore(store.FilePath).Read()?.Id==session.Id,"waiting progress survives service recreation");
            Reject(()=>ManualAsdInspection.Finish(store,Guid.NewGuid().ToString("N"),Read),"old request cannot finish current session");
            var no=ManualAsdInspection.Finish(store,session.Id,Read);
            Check(no.Stage==ManualAsdStage.NotDetected && no.MatchedBytes==0,"missing cache does not activate");
            FileAt(Path.Combine(system,"threshold.nvph"),ManualAsdPolicy.ThresholdBytes);
            Check(ManualAsdInspection.Finish(store,session.Id,Read).Stage==ManualAsdStage.NotDetected,"exactly100MiB is not greater than100MiB");
            FileAt(Path.Combine(system,"large.bin"),ManualAsdPolicy.ThresholdBytes+1);
            Check(ManualAsdInspection.Finish(store,session.Id,Read).Stage==ManualAsdStage.NotDetected,"wrong extension cannot activate");
            FileAt(Path.Combine(system,"nested","large.NVPH"),ManualAsdPolicy.ThresholdBytes+1);
            var active=ManualAsdInspection.Finish(store,session.Id,Read);
            Check(active.Stage==ManualAsdStage.Activated&&active.MatchedBytes==ManualAsdPolicy.ThresholdBytes+1,"nested NVPH100MiB+1 activates");
            Check(active.Message.Contains("ASD已激活")&&active.CheckedUtc is not null,"explicit result includes time and label");
            Reject(()=>ManualAsdInspection.Finish(store,session.Id,Read),"completed session cannot be replayed");
            Check(ManualAsdInspection.Read(store,Read)?.Stage==ManualAsdStage.Activated,"status reads saved manual result");
            store.Invalidate(); Check(store.Read()?.Stage==ManualAsdStage.Cancelled,"later cleanup invalidates manual result");
            session=Start(); var before=store.Read();
            foreach (var changed in new[] {context with{Supported=false},context with{DriverKey=new string('C',64)},
                context with{Owner=new string('D',64)},context with{SystemRoot=Path.Combine(root,"other")},
                context with{Selection=GameInstallationScope.Select([Path.Combine(root,"other-game")],null,true,true)} })
            {
                Reject(()=>ManualAsdInspection.Finish(store,session.Id,()=>changed),"changed context never activates");
                Check(store.Read()?.Stage==before?.Stage,"context rejection preserves waiting record");
            }
            var calls=0; FileAt(Path.Combine(system,"fresh.nvph"),ManualAsdPolicy.ThresholdBytes+1);
            Reject(()=>ManualAsdInspection.Finish(store,session.Id,()=>++calls==1?context:context with{DriverKey=new string('C',64)}),"driver changed during read cannot activate");
            Check(store.Read()?.Stage==ManualAsdStage.Warmup,"post-read rejection does not save success");
            using(var cancelled=new CancellationTokenSource()) {cancelled.Cancel();Reject(()=>ManualAsdInspection.Finish(store,session.Id,Read,cancelled.Token),"cancelled read cannot activate");}
            ManualAsdInspection.Cancel(store,session.Id); Check(File.Exists(Path.Combine(system,"fresh.nvph")),"cancel does not delete cache");
            Reject(()=>ManualAsdInspection.Finish(store,session.Id,Read),"cancelled session cannot finish");
            foreach(var bad in new[] {new ShaderCacheCleanupResult{Partial=true},new ShaderCacheCleanupResult{VerificationComplete=true,RemainingFiles=1},
                new ShaderCacheCleanupResult{VerificationComplete=true,FailedItems=1},new ShaderCacheCleanupResult{VerificationComplete=true,SkippedLinks=1},
                new ShaderCacheCleanupResult{VerificationComplete=true,GameStarted=true},new ShaderCacheCleanupResult{VerificationComplete=true,Cancelled=true}})
            {
                Reject(()=>ManualAsdStart.Run(store,Read,()=>false,(_,_)=>bad),"incomplete cleanup rejected");
                Check(store.Read()?.Stage==ManualAsdStage.Interrupted,"interrupted cleanup cannot masquerade as warmup");
                Reject(()=>ManualAsdInspection.Finish(store,store.Read()!.Id,Read),"large preexisting file after partial cleanup cannot activate");
            }
            Reject(()=>ManualAsdStart.Run(store,Read,()=>false,(_,_)=>new(){VerificationComplete=true}),"fake successful cleanup with residual file rejected");
            Check(store.Read()?.Stage==ManualAsdStage.Interrupted,"independent empty baseline guards false cleanup success");
            session=Start(); store.Save(session with{Stage=ManualAsdStage.Cleaning,CleanedUtc=null});
            FileAt(Path.Combine(system,"crash.nvph"),ManualAsdPolicy.ThresholdBytes+1);
            Reject(()=>ManualAsdInspection.Finish(store,session.Id,Read),"power loss during cleanup never authorizes a result");
            var deleteCalls=0; var invalidStore=new ManualAsdStore(Path.Combine(outside,"bad.json"));
            Reject(()=>ManualAsdStart.Run(invalidStore,Read,()=>false,(_,_)=>{deleteCalls++;return new(){VerificationComplete=true};}),"journal failure occurs before cleanup");
            Check(deleteCalls==0,"no cleanup without durable intent");
            Reject(()=>ManualAsdStart.Run(store,Read,()=>false,(_,_)=>{deleteCalls++;return new(){VerificationComplete=true};},expectedContextId:new string('0',64)),"different user/cache/driver confirmation rejected before cleanup");
            Check(deleteCalls==0,"mismatching confirmation never calls deletion");
            Reject(()=>ManualAsdStart.Run(store,Read,()=>true,(_,_)=>throw new Exception()),"game running blocks start");
            Reject(()=>ManualAsdStart.Run(store,()=>context with{Selection=GameInstallationScope.Select([game],game,false)},()=>false,(_,_)=>throw new Exception()),"single/shared scope rejected before cleanup");
            using(var gate=ShaderCacheOperationGate.Acquire()) Reject(()=>Start(),"other cleanup/validation excludes start");
            File.WriteAllText(store.FilePath,"{invalid");Reject(()=>store.Read(),"corrupt saved record cannot give green result");
            var ready=new BackendReady(Convert.ToBase64String(new byte[32]),BackendModules.System,"0.9400.0",0,123,123,Path.Combine(root,"backend.exe"),new string('a',64));
            var json=JsonSerializer.Serialize(ready,BackendJson.SerializerOptions);
            Check(BackendMessages.ParseReady(JsonDocument.Parse(json).RootElement).ManualAsdVersion==0,"legacy backend is unsupported by default");
            Check(BackendMessages.ParseReady(JsonSerializer.SerializeToElement(ready with{ManualAsdVersion=1},BackendJson.SerializerOptions)).ManualAsdVersion==1,"version1 explicitly advertised");
            foreach(var bad in new[]{"-1","2","null","true","\"1\""})
                Reject(()=>BackendMessages.ParseReady(JsonDocument.Parse(json.Replace("\"manualAsdVersion\":0","\"manualAsdVersion\":"+bad)).RootElement),"invalid capability rejected");
            var legacyJson=json.Replace(",\"manualAsdVersion\":0","");
            Check(BackendMessages.ParseReady(JsonDocument.Parse(legacyJson).RootElement).ManualAsdVersion==0,"missing capability remains unsupported");
            session=Start();
            var lockedPath=Path.Combine(system,"locked.nvph");FileAt(lockedPath,ManualAsdPolicy.ThresholdBytes+1);
            using(var locked=new FileStream(lockedPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {
                Check(ManualAsdInspection.Finish(store,session.Id,Read).Stage==ManualAsdStage.Activated,"content lock does not block the size-only check while the game remains open");
                store.Save(session);
                FileAt(Path.Combine(system,"positive.nvph"),ManualAsdPolicy.ThresholdBytes+1);
                Check(ManualAsdInspection.Finish(store,session.Id,Read).Stage==ManualAsdStage.Activated,"one observed positive metadata entry suffices");
            }
            session=Start();
            Check(Path.GetFullPath(system).StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"owned fixture deletion remains within its generated root");
            DriverPriorityStore.RejectLinks(system); Directory.Delete(system,true); File.WriteAllText(system,"not a directory");
            Reject(()=>ManualAsdInspection.Finish(store,session.Id,Read),"invalid system-cache root is a read failure, not an absence result");
            Check(store.Read()?.Stage==ManualAsdStage.Warmup,"metadata read failure remains retryable");
            File.Delete(system);Directory.CreateDirectory(system);
            DirectoryInfo? repo=null;
            foreach(var origin in new[]{AppContext.BaseDirectory,Environment.CurrentDirectory})
            {
                for(var candidate=new DirectoryInfo(origin);candidate is not null;candidate=candidate.Parent)
                    if(File.Exists(Path.Combine(candidate.FullName,"src/DeltaNFD.Contracts/ManualAsdContracts.cs"))) {repo=candidate;break;}
                if(repo is not null)break;
            }
            Check(repo is not null,"current source root found");
            var client=File.ReadAllText(Path.Combine(repo!.FullName,"src/DeltaNFD/Services/Backends/BackendClient.cs"));
            var capabilityCheck=client.IndexOf("command == BackendCommands.SystemManualAsdStart",StringComparison.Ordinal);
            Check(capabilityCheck>=0&&capabilityCheck<client.IndexOf("BackendUnconfirmedStore.BeginIntent",StringComparison.Ordinal),"old backend rejected before durable send intent");
            var inspectionSource=File.ReadAllText(Path.Combine(repo.FullName,"src/DeltaNFD.Inspection/ShaderInspection.cs"));
            Check(!inspectionSource.Contains("NvphLayoutProbe.Read(")&&!inspectionSource.Contains("D3dsCacheInspection.Combine("),"ordinary diagnosis no longer calls structural/ASD association probes");
            var page=XDocument.Load(Path.Combine(repo.FullName,"src/DeltaNFD/Views/ShaderPage.xaml"));
            foreach(var action in new[]{"StartManualAsd_Click","FinishManualAsd_Click","CancelManualAsd_Click"})
                Check(page.Descendants().Count(x=>(string?)x.Attribute("Click")==action)==1,"exactly one action: "+action);
            var ui=File.ReadAllText(Path.Combine(repo.FullName,"src/DeltaNFD/ViewModels/ShaderViewModel.ManualAsd.cs"));
            Check(ui.Contains("上次手动判定")&&ui.Contains("state.Error.Length > 0"),"saved manual result is labelled as history and corrupt progress offers restart");
            Check(BackendModules.RequiresWriteIntent(BackendModules.System,BackendCommands.SystemManualAsdStart),"start requires write intent");
            Console.WriteLine($"Manual ASD: {checks} checks passed. Only owned sparse fixture caches and own journal changed; no player caches/GPU/timer/game writes. Evidence: {root}");
        }
        finally { /* Retain only this owned fixture tree for failure and journal evidence. */ }
    }
}
