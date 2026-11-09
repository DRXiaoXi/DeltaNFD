using System.Text.Json;
using System.Xml.Linq;
using DeltaNFD.Backends;
using DeltaNFD.Inspection;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;
using Microsoft.Win32;

internal static class LowMemorySupportChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); count++; }
        const ulong gb = 1024UL * 1024 * 1024;
        foreach (var total in new ulong?[] { null, 0, 8 * gb, 16 * gb - 1, 16 * gb, 16 * gb + 1, 24 * gb, 32 * gb })
        {
            var low = total is > 0 and <= 16 * gb;
            Check(new LowMemoryStatus(total).IsLowCapacity == low, "raw capacity boundary");
            foreach (var tweak in new[] { SystemTweak.MemoryCompression, SystemTweak.PageCombining })
                Check(LowMemoryPolicy.ShowUpTweak(tweak, total) == !low, "hide memory-disabling choices only on known <=16GB");
            Check(!LowMemoryPolicy.ShowUpTweak(SystemTweak.HardwareGpuScheduling, total), "keep HAGS deduplicated");
            Check(!LowMemoryPolicy.ShowUpTweak(SystemTweak.SysMainMemorySupport, total), "restore-only SysMain never becomes a disabling row");
            Check(LowMemoryPolicy.ShowUpTweak(SystemTweak.Prefetch, total), "keep prefetch choice available");
        }
        Check(BxCatalog.Sections[0].Id == "pending" && BxCatalog.Sections.Select(s => s.Id).Distinct().Count() == BxCatalog.Sections.Length,
            "UP recommendations first without duplicate sections");
        var healthy = new LowMemoryStatus(16 * gb, 2, true, true, true, true);
        Check(healthy.Ready && !healthy.NeedsAttention, "recommended configuration is healthy");
        foreach (var action in Enum.GetValues<LowMemoryAction>())
        {
            var status = Bad(healthy, action);
            Check(status.Complete && status.NeedsAttention && status.Needs(action) == true, "any single mismatch triggers warning");
            var unknown = status with { Error = "read failed" };
            Check(!unknown.Complete && unknown.NeedsAttention, "unknown independent read does not erase known warning");
            Check(status.Fingerprint != healthy.Fingerprint, "confirmation fingerprint covers the setting");
            var env = new RepairFake(status);
            var result = await LowMemoryRepairEngine.RunAsync(env, new(action, status.Fingerprint), CancellationToken.None);
            Check(result.Success && result.RequiresReboot && env.Writes == 1 && env.Status.Ready, "repair exactly the selected mismatch and verify");
            env = new(status) { Fail = true };
            result = await LowMemoryRepairEngine.RunAsync(env, new(action, status.Fingerprint), CancellationToken.None);
            Check(!result.Success && env.Writes == 1, "step failure is not converted to success");
            env = new(status) { LeaveUnchanged = true };
            result = await LowMemoryRepairEngine.RunAsync(env, new(action, status.Fingerprint), CancellationToken.None);
            Check(!result.Success && result.OutcomeUnknown, "successful submission still requires matching post-read");
        }
        foreach (var status in new[] { healthy with { TotalBytes = 32 * gb }, healthy with { TotalBytes = null },
            healthy with { CompressionEnabled = null }, healthy with { Error = "denied" } })
        {
            var env = new RepairFake(status);
            Check(!(await LowMemoryRepairEngine.RunAsync(env, new(LowMemoryAction.MemoryCompression, status.Fingerprint), CancellationToken.None)).Success
                && env.Writes == 0, "high/unknown/incomplete state cannot write");
        }
        var bad = Bad(healthy, LowMemoryAction.SysMain);
        var dependency = bad with { CompressionEnabled = null, CombiningEnabled = null, Error = "MMAgent unavailable" };
        var dependencyEnv = new RepairFake(dependency);
        var dependencyResult = await LowMemoryRepairEngine.RunAsync(dependencyEnv, new(LowMemoryAction.SysMain, dependency.Fingerprint), CancellationToken.None);
        Check(dependencyResult.Success && dependencyEnv.Writes == 1 && !dependencyEnv.Status.Complete && !dependencyEnv.Status.Ready,
            "known SysMain can be enabled before unavailable MMAgent without claiming complete repair");
        Check(!(await LowMemoryRepairEngine.RunAsync(new RepairFake(dependency), new(LowMemoryAction.MemoryCompression, dependency.Fingerprint), CancellationToken.None)).Success,
            "unavailable MMAgent original still cannot be overwritten");
        var stale = new RepairFake(bad);
        Check(!(await LowMemoryRepairEngine.RunAsync(stale, new(LowMemoryAction.SysMain, "stale"), CancellationToken.None)).Success && stale.Writes == 0,
            "stale confirmation cannot write");
        var game = new RepairFake(bad) { BlockOnSecond = true };
        Check(!(await LowMemoryRepairEngine.RunAsync(game, new(LowMemoryAction.SysMain, bad.Fingerprint), CancellationToken.None)).Success && game.Writes == 0,
            "game recheck before mutation");
        var cancelled = new RepairFake(bad);
        try { await LowMemoryRepairEngine.RunAsync(cancelled, new(LowMemoryAction.SysMain, bad.Fingerprint), new(true)); Check(false, "cancellation missing"); }
        catch (OperationCanceledException) { Check(cancelled.Writes == 0, "cancelled before mutation"); }
        Check((int)SystemTweak.WSearchOff == 6 && (int)SystemTweak.SysMainMemorySupport == 7, "existing wire enum identities retained");
        Check(BackendModules.IsAllowed(BackendModules.System, BackendCommands.SystemLowMemoryApply)
            && BackendModules.IsMutating(BackendModules.System, BackendCommands.SystemLowMemoryApply)
            && BackendModules.RequiresWriteIntent(BackendModules.System, BackendCommands.SystemLowMemoryApply)
            && BackendModules.RetainsStaticBackup(BackendModules.System, BackendCommands.SystemLowMemoryApply), "repair has write-intent and static-backup gates");
        var wire = JsonSerializer.Deserialize<LowMemoryStatus>(JsonSerializer.Serialize(bad))!;
        Check(wire.Fingerprint == bad.Fingerprint && wire.NeedsAttention, "status survives backend serialization");
        Check(LowMemoryInspection.EvaluatePrefetch([0, 0, 0, 0], false) == true, "all prefetch overrides disabled");
        Check(LowMemoryInspection.EvaluatePrefetch([null, null, null, null], false) == false, "missing prefetch overrides mean defaults, not disabled");
        Check(LowMemoryInspection.EvaluatePrefetch([0, 0, 0, 0], null) is null, "unknown prelaunch remains unknown");
        for (var i = 0; i < 4; i++)
        {
            var values = new int?[] { 0, 0, 0, 0 }; values[i] = 1;
            Check(LowMemoryInspection.EvaluatePrefetch(values, false) == false, "each prefetch component is checked");
        }
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-low-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), "owned low-memory fixtures");
        try
        {
            var sequence = 0;
            TweakBackupStore Store() => new(Path.Combine(root, (++sequence) + ".json"));
            foreach (var start in new[] { 2, 3, 4 })
            foreach (var running in new[] { false, true })
            foreach (var delayed in new int?[] { null, 0, 1 })
            {
                var io = new MemoryFake { Service = new(start, running, delayed) };
                var original = io.Service; var store = Store(); var control = new MemorySupportControl(store, io);
                Check((await control.EnableAsync(LowMemoryAction.SysMain)).Success && io.Service.Start == 2 && io.Service.Running, "enable SysMain automatic/running");
                var first = store.GetStrict("State", "LowMemory", "SysMain")!.Data;
                Check(JsonSerializer.Deserialize<MemoryServiceOriginal>(first) == original, "backup exact startup/running/delayed state");
                Check((await control.EnableAsync(LowMemoryAction.SysMain)).Success && store.GetStrict("State", "LowMemory", "SysMain")!.Data == first,
                    "repeated enable preserves first original");
                Check((await control.RestoreSysMainAsync()).Success && io.Service == original, "restore exact SysMain state");
                Check(store.GetStrict("State", "LowMemory", "SysMain") is null, "remove only verified recovery record");
            }
            foreach (var action in new[] { LowMemoryAction.MemoryCompression, LowMemoryAction.PageCombining })
            {
                var io = new MemoryFake(); var store = Store(); var control = new MemorySupportControl(store, io);
                Check((await control.EnableAsync(action)).Success && io.Mm == true, "enable MMAgent feature");
                var name = action == LowMemoryAction.MemoryCompression ? "MemoryCompression" : "PageCombining";
                Check(store.GetStrict("State", "MMAgent", name)?.Data == "0", "original MMAgent false retained for existing restore path");
            }
            var partialIo = new MemoryFake { Service = new(4, false, null), BlockAfterConfigure = true };
            var partialStore = Store(); var partialControl = new MemorySupportControl(partialStore, partialIo);
            var partial = await partialControl.EnableAsync(LowMemoryAction.SysMain);
            Check(!partial.Success && partial.OutcomeUnknown && partialIo.StartWrites == 0 && partialStore.GetStrict("State", "LowMemory", "SysMain") is not null,
                "game starts after configure: no next write and original retained");
            partialIo.BlockAfterConfigure = false;
            Check((await partialControl.RestoreSysMainAsync()).Success && partialIo.Service == new MemoryServiceOriginal(4, false, null),
                "interrupted SysMain setup can restore original");
            var corruptStore = Store(); corruptStore.SaveStrict(new() { Hive = "State", KeyPath = "LowMemory", ValueName = "SysMain", Data = "{}" });
            var corruptIo = new MemoryFake(); var corrupt = new MemorySupportControl(corruptStore, corruptIo);
            Check(!(await corrupt.EnableAsync(LowMemoryAction.SysMain)).Success && corruptIo.ConfigWrites == 0, "corrupt old backup blocks enable");
            Check(!(await corrupt.RestoreSysMainAsync()).Success && corruptIo.ConfigWrites == 0, "corrupt old backup blocks restore");
            var failStore = Store(); var failIo = new MemoryFake { Service = new(4, false, null), FailStart = true };
            var failControl = new MemorySupportControl(failStore, failIo);
            Check(!(await failControl.EnableAsync(LowMemoryAction.SysMain)).Success && failStore.GetStrict("State", "LowMemory", "SysMain") is not null,
                "service-start failure retains recovery evidence");
            failIo.FailStart = false;
            Check((await failControl.RestoreSysMainAsync()).Success, "failure can be restored without deleting evidence first");
            var unknownStore = Store(); var unknownIo = new MemoryFake { Mm = null };
            Check(!(await new MemorySupportControl(unknownStore, unknownIo).EnableAsync(LowMemoryAction.MemoryCompression)).Success
                && unknownIo.MmWrites == 0 && unknownStore.GetAllStrict().Count == 0, "unknown MMAgent state never writes or invents an original");
            var invalidMmStore = Store(); invalidMmStore.SaveStrict(new() { Hive = "State", KeyPath = "MMAgent", ValueName = "MemoryCompression", Data = "corrupt" });
            var invalidMmIo = new MemoryFake();
            Check(!(await new MemorySupportControl(invalidMmStore, invalidMmIo).EnableAsync(LowMemoryAction.MemoryCompression)).Success
                && invalidMmIo.MmWrites == 0, "unverifiable existing MMAgent original blocks enable");
            var blockedIo = new MemoryFake { BlockInitially = true }; var blockedStore = Store();
            var blockedResult = await new MemorySupportControl(blockedStore, blockedIo).EnableAsync(LowMemoryAction.SysMain);
            Check(!blockedResult.Success && !blockedResult.OutcomeUnknown && blockedIo.ConfigWrites == 0,
                "known pre-write blocker does not create an unconfirmed mutation");
        }
        finally
        {
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(Path.Combine(root, "isolated-test.marker"))) throw new IOException("fixture cleanup boundary mismatch");
            Directory.Delete(root, true);
        }
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "src/DeltaNFD/Views/LowMemoryRecoveryPage.xaml"))) repo = repo.Parent;
        Check(repo is not null, "production sources located");
        var basePath = repo!.FullName;
        var home = XDocument.Load(Path.Combine(basePath, "src/DeltaNFD/Views/DashboardPage.xaml"));
        Check(home.Descendants().Any(x => (string?)x.Attribute("Click") == "LowMemoryRecovery_Click"), "homepage links hidden recovery page");
        var page = XDocument.Load(Path.Combine(basePath, "src/DeltaNFD/Views/LowMemoryRecoveryPage.xaml"));
        Check(page.Descendants().Any(x => x.Name.LocalName == "ProgressBar") && page.ToString().Contains("手动重启"), "progress and manual restart guidance");
        var vm = File.ReadAllText(Path.Combine(basePath, "src/DeltaNFD/ViewModels/BxDbViewModel.cs"));
        Check(vm.Contains("LowMemoryPolicy.ShowUpTweak(s.Tweak, totalMemory)"), "actual UP list uses capacity policy");
        var nav = File.ReadAllText(Path.Combine(basePath, "src/DeltaNFD/MainWindow.PageSessions.cs"));
        Check(nav.Contains("typeof(LowMemoryRecoveryPage) ? \"system\""), "hidden page owns system backend lease");
        Console.WriteLine($"Low-memory guidance: {count} checks passed. Native/service/MMAgent writes simulated; own backup stores only.");
    }
    private static LowMemoryStatus Bad(LowMemoryStatus s, LowMemoryAction a) => a switch
    {
        LowMemoryAction.SysMain => s with { SysMainStart = 4, SysMainRunning = false },
        LowMemoryAction.MemoryCompression => s with { CompressionEnabled = false },
        LowMemoryAction.PageCombining => s with { CombiningEnabled = false },
        _ => s with { PrefetchDisabled = false },
    };
    private sealed class RepairFake(LowMemoryStatus status) : ILowMemoryRepairEnvironment
    {
        public LowMemoryStatus Status = status;
        public int Writes, Gates;
        public bool Fail, LeaveUnchanged, BlockOnSecond;
        public string? Blocker() => BlockOnSecond && ++Gates == 2 ? "game started" : null;
        public Task<LowMemoryStatus> ReadAsync() => Task.FromResult(Status);
        public Task<OperationResult> ApplyAsync(LowMemoryAction action)
        {
            Writes++;
            if (!Fail && !LeaveUnchanged) Status = action switch
            {
                LowMemoryAction.SysMain => Status with { SysMainStart = 2, SysMainRunning = true },
                LowMemoryAction.MemoryCompression => Status with { CompressionEnabled = true },
                LowMemoryAction.PageCombining => Status with { CombiningEnabled = true },
                _ => Status with { PrefetchDisabled = true },
            };
            return Task.FromResult(Fail ? OperationResult.Fail("fixture failure") : OperationResult.Ok("fixture", requiresReboot: true));
        }
    }
    private sealed class MemoryFake : IMemorySupportIo
    {
        public MemoryServiceOriginal Service = new(4, false, null);
        public bool? Mm = false;
        public bool BlockAfterConfigure, FailStart, BlockInitially;
        public int ConfigWrites, StartWrites, MmWrites;
        public string? Blocker() => BlockInitially || BlockAfterConfigure && ConfigWrites > 0 ? "game started" : null;
        public MemoryServiceOriginal ReadSysMain() => Service;
        public Task<int> ConfigureSysMainAsync(int start, bool delayed) { ConfigWrites++; Service = Service with { Start = start, Delayed = delayed ? 1 : 0 }; return Task.FromResult(0); }
        public Task<int> SetSysMainRunningAsync(bool running) { StartWrites++; if (FailStart) return Task.FromResult(1); Service = Service with { Running = running }; return Task.FromResult(0); }
        public void RestoreDelayed(int? value) => Service = Service with { Delayed = value };
        public Task<bool?> ReadMmAsync(string property) => Task.FromResult(Mm);
        public Task<int> EnableMmAsync(string property) { MmWrites++; Mm = true; return Task.FromResult(0); }
    }
}
