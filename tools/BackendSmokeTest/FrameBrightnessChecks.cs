using System.Text.Json;
using DeltaNFD.Services;
using DeltaNFD.Execution.CpuFrame;

internal static class FrameBrightnessChecks
{
    private const string Scheme = "11111111-1111-1111-1111-111111111111";
    private const string Cpu = "54533251-82be-4824-96c1-47b60b740d00";
    private const string CpuSetting = "93b8b6dc-0698-4d1c-9ee4-0644e900c85d";
    private const string Display = "7516b95f-f776-4464-8c53-06167f40cc99";
    private static readonly string[] Brightness = ["aded5e82-b909-4619-9949-f5d71dac0bcb", "f1fbfde2-a960-4165-9f88-50667911ce96", "fbd9aa66-9553-4097-ba44-ed6e9d65eab8"];
    private sealed record Entry(string Subgroup, string Setting, long Ac, long Dc);
    private sealed class FakePower : IPowerService
    {
        internal List<Entry> Entries = [new(Cpu, CpuSetting, 10, 20), .. Brightness.Select((g,i) => new Entry(Display,g,i == 2 ? 1 : 100,i == 2 ? 1 : 80))];
        internal readonly List<string> Queries = [], Writes = [], Switches = [];
        internal bool HideBrightness, QueryFails, CpuWriteFails;
        internal Task<(int Code,string StdOut,string StdErr)> Run(string arguments, TimeSpan timeout)
        {
            var p = arguments.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if (p[0] is "/qh" or "/q")
            {
                Queries.Add(arguments);
                if (QueryFails) return Task.FromResult((5,"","fixture query failure"));
                var entries = Entries.Where(e => !HideBrightness || !Brightness.Contains(e.Setting));
                if (p.Length == 4) entries = entries.Where(e => e.Subgroup.Equals(p[2],StringComparison.OrdinalIgnoreCase) && e.Setting.Equals(p[3],StringComparison.OrdinalIgnoreCase));
                var text = $"Scheme {Scheme}\n" + string.Join("\n",entries.GroupBy(e=>e.Subgroup).Select(g=>
                    $"Subgroup {g.Key}\n"+string.Join("\n",g.Select(e=>$"Setting {e.Setting}\nAC 0x{e.Ac:X8}\nDC 0x{e.Dc:X8}\n"))));
                return Task.FromResult((0,text,""));
            }
            Writes.Add(arguments);
            if (p[0] == "/setactive")
            {
                if (p.Length != 2 || p[1] != Scheme) throw new Exception("Unexpected simulated activation");
                return Task.FromResult((0,"",""));
            }
            if (p[0] is not ("/setacvalueindex" or "/setdcvalueindex") || p.Length != 5 || p[1] != Scheme)
                throw new Exception("Unexpected simulated write");
            var i = Entries.FindIndex(e=>e.Subgroup.Equals(p[2],StringComparison.OrdinalIgnoreCase) && e.Setting.Equals(p[3],StringComparison.OrdinalIgnoreCase));
            if (i < 0) throw new Exception("Simulated setting missing");
            if (CpuWriteFails && p[3].Equals(CpuSetting,StringComparison.OrdinalIgnoreCase)) return Task.FromResult((5,"","fixture write failure"));
            var old = Entries[i];
            Entries[i] = p[0] == "/setacvalueindex" ? old with { Ac=long.Parse(p[4]) } : old with { Dc=long.Parse(p[4]) };
            return Task.FromResult((0,"",""));
        }
        internal void SetBrightness(long ac,long dc)
        { for (var i=0;i<Entries.Count;i++) if(Brightness.Contains(Entries[i].Setting)) Entries[i]=Entries[i] with {Ac=Entries[i].Setting==Brightness[2]?0:ac,Dc=Entries[i].Setting==Brightness[2]?0:dc}; }
        internal bool BrightnessEquals(long ac,long dc) => Entries.Where(e=>Brightness.Contains(e.Setting)).All(e=>e.Ac==(e.Setting==Brightness[2]?0:ac)&&e.Dc==(e.Setting==Brightness[2]?0:dc));
        internal bool HasBrightnessWrite => Writes.Any(w=>Brightness.Any(g=>w.Contains(g,StringComparison.OrdinalIgnoreCase)));
        internal bool HasBrightnessDirectQuery => Queries.Any(q=>q.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length == 4 && Brightness.Any(g=>q.Contains(g,StringComparison.OrdinalIgnoreCase)));
        public Task<List<PowerSchemeInfo>> GetSchemesAsync() => Task.FromResult(new List<PowerSchemeInfo> { new() { Guid=Scheme,Name="fixture",IsActive=true } });
        public Task<OperationResult> SetSchemeAsync(string scheme) { if(scheme != Scheme) throw new Exception("Unexpected simulated scheme"); Switches.Add(scheme); return Task.FromResult(OperationResult.Ok("fixture")); }
        public Task<OperationResult> ImportNoPowerSaveSchemeAsync() => throw new Exception("Unexpected import");
        public Task<OperationResult> EnsureNoPowerSaveSchemeNameAsync() => throw new Exception("Unexpected migration");
        public Task<bool> IsHibernateEnabledAsync() => throw new Exception("Unexpected hibernation read");
        public Task<OperationResult> SetHibernateAsync(bool enable) => throw new Exception("Unexpected hibernation write");
        public Task<List<HeteroPolicyInfo>> GetHeteroPoliciesAsync() => throw new Exception("Unexpected policy read");
        public Task<OperationResult> SetHeteroPolicyAsync(string setting,int value) => throw new Exception("Unexpected policy write");
        public Task<OperationResult> SetHeteroPolicyValuesAsync(string setting,int ac,int dc) => throw new Exception("Unexpected policy write");
    }

    internal static async Task RunAsync()
    {
        var count=0;
        void Check(bool value,string message) { if(!value) throw new Exception(message); count++; }
        var id=Guid.NewGuid();
        var root=Path.Combine(Path.GetTempPath(),"DeltaNFD-brightness-"+id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root,"isolated-test.marker"),id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root,id);
        (FakePower Fake,FrameTweaksService Service,string File) Fixture(string name,bool legacy=false,bool onlyBrightness=false,bool uppercase=false)
        {
            var path=Path.Combine(root,name); Directory.CreateDirectory(path);
            var fake=new FakePower();
            var service=new FrameTweaksService(path,fake.Run,()=>Scheme);
            var file=Path.Combine(path,"frame_power_scheme_snapshot.json");
            if(legacy)
            {
                var entries=fake.Entries.Where(e=>!onlyBrightness||Brightness.Contains(e.Setting));
                if(uppercase) entries=entries.Select(e=>e with {Subgroup=e.Subgroup.ToUpperInvariant(),Setting=e.Setting.ToUpperInvariant()});
                File.WriteAllText(file,JsonSerializer.Serialize(new {Scheme,Settings=entries.ToArray()}));
            }
            return(fake,service,file);
        }
        try
        {
            var fresh=Fixture("fresh");
            Check((await fresh.Service.SnapshotPowerSchemeContentAsync(Scheme)).Success,"new snapshot succeeds");
            using(var json=JsonDocument.Parse(File.ReadAllText(fresh.File)))
            {
                var entries=json.RootElement.GetProperty("Settings").EnumerateArray().ToArray();
                Check(entries.Length==1 && entries[0].GetProperty("Setting").GetString()==CpuSetting,"new snapshots contain CPU policy and exclude all three brightness policies");
            }
            foreach(var (ac,dc) in new(long,long)[] {(40,35),(45,10),(0,0),(99,1)})
            {
                fresh.Fake.SetBrightness(ac,dc); fresh.Fake.Writes.Clear();
                Check((await fresh.Service.VerifyAndRestorePowerSchemeContentAsync(Scheme)).Success,"brightness-only drift remains healthy");
                Check(fresh.Fake.BrightnessEquals(ac,dc),"manual AC/DC brightness and adaptive on/off changes are retained");
                Check(fresh.Fake.Writes.Count==0,"brightness-only change does not write or reactivate the scheme");
            }
            foreach(var uppercase in new[]{false,true})
            {
                var old=Fixture("legacy-"+uppercase,legacy:true,uppercase:uppercase);
                var original=File.ReadAllBytes(old.File);
                old.Fake.SetBrightness(40,35);
                Check((await old.Service.SnapshotPowerSchemeContentAsync(Scheme)).Success&&File.ReadAllBytes(old.File).SequenceEqual(original),"existing legacy snapshot is reused without migration writes");
                Check((await old.Service.VerifyAndRestorePowerSchemeContentAsync(Scheme)).Success&&old.Fake.Writes.Count==0,"legacy snapshot ignores brightness case-insensitively");
                old.Fake.Entries[0]=old.Fake.Entries[0] with {Ac=99,Dc=88};
                Check((await old.Service.VerifyAndRestorePowerSchemeContentAsync(Scheme)).Success,"mixed drift restores CPU without failing brightness recheck");
                Check(old.Fake.Entries[0] is {Ac:10,Dc:20}&&!old.Fake.HasBrightnessWrite,"CPU protection remains effective and never writes brightness");
                Check(old.Fake.BrightnessEquals(40,35),"CPU repair preserves current brightness");
                Check(old.Fake.Writes.Count==3&&old.Fake.Writes.Count(w=>w.StartsWith("/setactive"))==1,"only two CPU indices and one refresh are written");
                Check(!old.Fake.HasBrightnessDirectQuery&&File.ReadAllBytes(old.File).SequenceEqual(original),"legacy ignored entries never fall back to single-setting reads or change original backup");
                old.Fake.HideBrightness=true; old.Fake.Writes.Clear();
                Check((await old.Service.VerifyAndRestorePowerSchemeContentAsync(Scheme)).Success,"missing legacy brightness settings do not fail restoration");
                Check((await old.Service.GetIncompletePowerBackupsAsync()).Count==0&&!old.Fake.HasBrightnessDirectQuery,"missing ignored brightness does not create a manual recovery blocker");
            }
            var only=Fixture("legacy-only",legacy:true,onlyBrightness:true);
            only.Fake.QueryFails=true;
            Check((await only.Service.VerifyAndRestorePowerSchemeContentAsync(Scheme)).Success&&only.Fake.Queries.Count==0&&only.Fake.Writes.Count==0,"valid legacy brightness-only snapshot needs no power queries or writes");
            Check((await only.Service.GetIncompletePowerBackupsAsync()).Count==0,"brightness-only old snapshot does not block review");
            var nativeOnly=Fixture("legacy-native-only",legacy:true,onlyBrightness:true);
            var nativeService=new FrameTweaksService(Path.GetDirectoryName(nativeOnly.File)!,
                (_,_)=>throw new Exception("Ignored brightness must not issue power commands"),
                ()=>throw new Exception("Ignored brightness must not reactivate a plan"),nativePowerReads:true);
            Check((await nativeService.VerifyAndRestorePowerSchemeContentAsync(Scheme)).Success,
                "old brightness-only snapshot also bypasses the native power-read branch");
            Check((await nativeService.GetIncompletePowerBackupsAsync()).Count==0,
                "native review ignores excluded brightness without querying the host");
            var failed=Fixture("cpu-failure",legacy:true); failed.Fake.SetBrightness(40,35); failed.Fake.Entries[0]=failed.Fake.Entries[0] with {Ac=99}; failed.Fake.CpuWriteFails=true;
            var bytes=File.ReadAllBytes(failed.File);
            Check(!(await failed.Service.VerifyAndRestorePowerSchemeContentAsync(Scheme)).Success&&File.ReadAllBytes(failed.File).SequenceEqual(bytes),"real CPU restore failure remains failure and keeps original backup");
            Check(!failed.Fake.HasBrightnessWrite,"failure path never writes ignored brightness");
            var wrong=Fixture("wrong-scheme",legacy:true);
            Check(!(await wrong.Service.VerifyAndRestorePowerSchemeContentAsync(Guid.NewGuid().ToString("D"))).Success&&wrong.Fake.Queries.Count==0&&wrong.Fake.Writes.Count==0,"scheme identity gate is unchanged");
            var broken=Fixture("broken-metadata",legacy:true,onlyBrightness:true);
            File.WriteAllText(broken.File,File.ReadAllText(broken.File).Replace("\"Ac\":100","\"Ac\":null"));
            Check(!(await broken.Service.VerifyAndRestorePowerSchemeContentAsync(Scheme)).Success&&broken.Fake.Queries.Count==0,"ignored policy does not bypass invalid original metadata");
            var cancel=Fixture("cancelled",legacy:true); using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
            try { await cancel.Service.VerifyAndRestorePowerSchemeContentAsync(Scheme,cancelled.Token); throw new Exception("Cancellation ignored"); }
            catch(OperationCanceledException) { Check(cancel.Fake.Writes.Count==0,"cancellation precedes restoration writes"); }

            var periodic=Fixture("periodic");
            using(var engine=new CpuFrameEngine(periodic.Fake,periodic.Service))
            {
                Check((await engine.SetPowerLockAsync(true,Scheme)).Success,"actual engine can enable locking with brightness excluded");
                periodic.Fake.SetBrightness(40,35); periodic.Fake.Writes.Clear(); periodic.Fake.Switches.Clear();
                var guard=typeof(CpuFrameEngine).GetMethod("GuardPowerLockCoreAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                await (Task)guard.Invoke(engine,[AppSettingsStore.ReadRequired(),CancellationToken.None])!;
                Check(periodic.Fake.BrightnessEquals(40,35)&&periodic.Fake.Writes.Count==0&&periodic.Fake.Switches.Count==0,
                    "actual periodic guard preserves brightness without writing or reactivating the current scheme");
                Check((await engine.SetPowerLockAsync(false,"")).Success,"periodic guard session can unlock after manual brightness changes");
            }

            // Exercise the actual unlock path also used by exit restoration. No monitor is started,
            // and all plan operations are injected; unrelated process settings remain at defaults.
            foreach(var onlyBrightness in new[]{false,true})
            {
                var unlock=Fixture("unlock-"+onlyBrightness,legacy:true,onlyBrightness:onlyBrightness);
                var original=File.ReadAllBytes(unlock.File); unlock.Fake.SetBrightness(40,35);
                AppSettingsStore.UpdateRequired(s=>{s.FramePowerPreviousSchemeGuid=Scheme;s.FramePowerLockedSchemeGuid=Scheme;s.FramePowerRestorePending=true;});
                using(var engine=new CpuFrameEngine(unlock.Fake,unlock.Service))
                    Check((await engine.SetPowerLockAsync(false,"")).Success,"actual engine unlock accepts old snapshot without restoring brightness");
                Check(!unlock.Fake.HasBrightnessWrite&&unlock.Fake.Writes.Count==0,"actual unlock does not refresh an unchanged plan for brightness drift");
                Check(!File.Exists(unlock.File)&&!AppSettingsStore.ReadRequired().FramePowerRestorePending,"successful unlock archives snapshot and clears restore obligation");
                var archived=Directory.GetFiles(Path.Combine(Path.GetDirectoryName(unlock.File)!,"restored-frame-power")).Single();
                Check(File.ReadAllBytes(archived).SequenceEqual(original),"legacy snapshot is archived byte-for-byte after verified unlock");
            }
            Console.WriteLine($"Frame brightness checks: {count} passed; real snapshot/guard/unlock code, owned fixtures and fake power I/O only; no host changes.");
        }
        finally
        {
            var temp=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(root).StartsWith(temp,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe fixture path");
            Directory.Delete(root,true);
        }
    }
}
