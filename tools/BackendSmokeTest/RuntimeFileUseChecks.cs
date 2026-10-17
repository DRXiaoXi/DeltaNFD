using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class RuntimeFileUseChecks
{
    internal static async Task RunAsync()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-file-use-checks-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        int count = 0;
        void Check(bool yes, string message) { if (!yes) throw new Exception(message); count++; }
        Check(BackendModules.IsReadOnly(BackendModules.Runtime, BackendCommands.RuntimeFileUseScan), "file use scan must be read-only");
        Check(BackendModules.RequiresWriteIntent(BackendModules.Runtime, BackendCommands.RuntimeFileUseClose), "closing needs write intent");
        Check(EmergencyPolicyVerifier.CanBlock(BackendCommands.RuntimeFileUseClose), "close must be covered by emergency policy");
        var app = new RuntimeFileUseProcess(700001, 12345, "Fixture app", @"C:\ThirdParty\App.exe", true, "");
        var now = DateTimeOffset.UtcNow;
        RuntimeFileUseCloseArgs Args(RuntimeFileUseScan scan, bool force = false) => new()
        { SnapshotId = scan.SnapshotId, ProcessIds = [app.Pid], Confirmed = true, Force = force, ForceConfirmed = force };
        async Task<(Fake Env, RuntimeFileUseEngine Engine, RuntimeFileUseScan Scan)> Fresh()
        {
            var env = new Fake(app);
            var engine = new RuntimeFileUseEngine(env, () => now);
            return (env, engine, await engine.ScanAsync(default));
        }
        var good = await Fresh();
        Check((await good.Engine.CloseAsync(Args(good.Scan), default)).Success && good.Env.CloseCount == 1 && good.Env.KillCount == 0,
            "normal close must not force terminate");
        Check(!(await good.Engine.CloseAsync(Args(good.Scan), default)).Success && good.Env.CloseCount == 1, "snapshot must be one-use");
        var legacyOnly = new Fake(app) { OnlyFull = true };
        var fullEngine = new RuntimeFileUseEngine(legacyOnly, () => now);
        Check((await fullEngine.ScanAsync(default)).Processes.Count == 0, "V14-only scan must not claim legacy-only owner");
        var fullSnapshot = await fullEngine.ScanAsync(default, RuntimeFileUseScope.FullRepair);
        Check(fullSnapshot.Scope == RuntimeFileUseScope.FullRepair && fullSnapshot.Processes.Count == 1, "full repair finds old-CRT-only owner");
        Check((await fullEngine.CloseAsync(Args(fullSnapshot), default)).Success && legacyOnly.CloseCount == 1
            && legacyOnly.Scopes.Last() == RuntimeFileUseScope.FullRepair, "close rechecks the server-approved full scope, not the V14 default");
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<RuntimeFileUseScan>(System.Text.Json.JsonSerializer.Serialize(fullSnapshot));
        Check(roundTrip?.Scope == RuntimeFileUseScope.FullRepair, "scope preserved through backend protocol");
        var inspected = legacyOnly.Scopes.Count;
        Check(!(await fullEngine.ScanAsync(default, (RuntimeFileUseScope)999)).Success && legacyOnly.Scopes.Count == inspected,
            "unknown scan scope is refused before native inspection");
        var windows = Path.Combine(root, "fixture-windows");
        var system = Path.Combine(windows, "System32"); Directory.CreateDirectory(system);
        string CreateFile(string directory, string name)
        { Directory.CreateDirectory(directory); var path = Path.Combine(directory, name); File.WriteAllText(path, "fixture only"); return path; }
        var v14File = CreateFile(system, "vcruntime140.dll");
        var oldFile = CreateFile(system, "msvcr120.dll");
        var sxsFile = CreateFile(Path.Combine(windows, "WinSxS", "amd64_microsoft.vc80.crt_fixture"), "msvcr80.dll");
        var x86File = CreateFile(Path.Combine(windows, "WinSxS", "x86_microsoft.vc90.crt_fixture"), "msvcp90.dll");
        CreateFile(Path.Combine(windows, "WinSxS", "arm64_microsoft.vc90.crt_fixture"), "msvcp90.dll");
        CreateFile(Path.Combine(windows, "WinSxS", "amd64_other.component_fixture"), "msvcr80.dll");
        CreateFile(system, "unrelated.dll");
        Check(RuntimeFileUseNative.Files(RuntimeFileUseScope.Vc14, [system], windows).SequenceEqual(new[] { v14File }), "default files remain V14 only");
        Check(RuntimeFileUseNative.Files(RuntimeFileUseScope.FullRepair, [system], windows).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(new[] { v14File, oldFile, sxsFile, x86File }), "full resources cover fixed old CRT and SxS without ARM64, other components or arbitrary DLLs");
        var force = await Fresh();
        Check((await force.Engine.CloseAsync(Args(force.Scan, true), default)).Success && force.Env.KillCount == 1 && force.Env.CloseCount == 0,
            "explicit force consent should terminate only selected identity");
        var noConsent = await Fresh(); var noArgs = Args(noConsent.Scan); noArgs.Confirmed = false;
        Check(!(await noConsent.Engine.CloseAsync(noArgs, default)).Success && noConsent.Env.CloseCount == 0, "no consent must do nothing");
        noArgs = Args(noConsent.Scan, true); noArgs.ForceConfirmed = false;
        Check(!(await noConsent.Engine.CloseAsync(noArgs, default)).Success && noConsent.Env.KillCount == 0, "force needs second consent");
        var stale = await Fresh(); now += TimeSpan.FromMinutes(6);
        Check(!(await stale.Engine.CloseAsync(Args(stale.Scan, true), default)).Success && stale.Env.KillCount == 0, "expired snapshot");
        var unknown = await Fresh(); var unknownArgs = Args(unknown.Scan); unknownArgs.SnapshotId = Guid.NewGuid().ToString("N");
        Check(!(await unknown.Engine.CloseAsync(unknownArgs, default)).Success && unknown.Env.CloseCount == 0, "forged snapshot");
        var outside = await Fresh(); var outsideArgs = Args(outside.Scan); outsideArgs.ProcessIds = [app.Pid + 1];
        Check(!(await outside.Engine.CloseAsync(outsideArgs, default)).Success && outside.Env.CloseCount == 0, "unlisted process");
        var duplicates = await Fresh(); var duplicateArgs = Args(duplicates.Scan); duplicateArgs.ProcessIds = [app.Pid, app.Pid];
        Check(!(await duplicates.Engine.CloseAsync(duplicateArgs, default)).Success && duplicates.Env.CloseCount == 0, "duplicate IDs");
        var manyArgs = Args(duplicates.Scan); manyArgs.ProcessIds = Enumerable.Range(1, 17).ToList();
        Check(!(await duplicates.Engine.CloseAsync(manyArgs, default)).Success, "bounded process batch");
        var reused = await Fresh(); reused.Env.Current = app with { StartFileTime = app.StartFileTime + 1 };
        Check((await reused.Engine.CloseAsync(Args(reused.Scan, true), default)).Actions.Single().Status == "skipped" && reused.Env.KillCount == 0,
            "PID reuse must never terminate new process");
        var changed = await Fresh(); changed.Env.Current = app with { ImagePath = @"C:\Other\new.exe" };
        Check((await changed.Engine.CloseAsync(Args(changed.Scan, true), default)).Actions.Single().Status == "skipped" && changed.Env.KillCount == 0,
            "path change must not terminate");
        var stoppedUsing = await Fresh(); stoppedUsing.Env.Current = null;
        Check((await stoppedUsing.Engine.CloseAsync(Args(stoppedUsing.Scan, true), default)).Actions.Single().Status == "skipped" && stoppedUsing.Env.KillCount == 0,
            "no longer holding file must not terminate");
        var protectedNow = await Fresh(); protectedNow.Env.Current = app with { CanClose = false, ProtectionReason = "protected" };
        Check(!(await protectedNow.Engine.CloseAsync(Args(protectedNow.Scan, true), default)).Success && protectedNow.Env.KillCount == 0, "fresh protection blocks");
        var heldChanged = await Fresh(); heldChanged.Env.Held = app with { StartFileTime = app.StartFileTime + 1 };
        Check(!(await heldChanged.Engine.CloseAsync(Args(heldChanged.Scan, true), default)).Success && heldChanged.Env.KillCount == 0, "identity must be checked on retained handle");
        var heldProtected = await Fresh(); heldProtected.Env.Held = app with { CanClose = false };
        Check(!(await heldProtected.Engine.CloseAsync(Args(heldProtected.Scan, true), default)).Success && heldProtected.Env.KillCount == 0, "handle protection recheck");
        var busy = await Fresh(); busy.Env.Blocked = true;
        Check(!(await busy.Engine.CloseAsync(Args(busy.Scan, true), default)).Success && busy.Env.KillCount == 0, "installer busy blocks termination");
        var error = await Fresh(); error.Env.ScanFails = true;
        Check(!(await error.Engine.CloseAsync(Args(error.Scan, true), default)).Success && error.Env.KillCount == 0, "failed rescan must not terminate");
        var waiting = await Fresh(); waiting.Env.ExitConfirmed = false;
        Check(!(await waiting.Engine.CloseAsync(Args(waiting.Scan), default)).Success && waiting.Env.CloseCount == 1 && waiting.Env.KillCount == 0,
            "close timeout must not automatically force terminate");
        var noWindow = await Fresh(); noWindow.Env.HasWindow = false;
        Check((await noWindow.Engine.CloseAsync(Args(noWindow.Scan), default)).Actions.Single().Status == "still-running" && noWindow.Env.KillCount == 0,
            "console app needs separate force confirmation");
        var cancel = await Fresh(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await cancel.Engine.CloseAsync(Args(cancel.Scan, true), cancellation.Token); throw new Exception("cancel should throw"); }
        catch (OperationCanceledException) { Check(cancel.Env.KillCount == 0, "cancel before action"); }
        var protectedScan = new Fake(app with { CanClose = false }); var protectedEngine = new RuntimeFileUseEngine(protectedScan);
        var protectedReport = await protectedEngine.ScanAsync(default);
        Check(!(await protectedEngine.CloseAsync(Args(protectedReport, true), default)).Success && protectedScan.KillCount == 0, "protected original selection cannot be forged");
        Check(RuntimeFileUseEngine.SameIdentity(app, app with { ImagePath = app.ImagePath.ToUpperInvariant() }), "Windows paths are case insensitive");
        Check(!RuntimeFileUseEngine.SameIdentity(app with { StartFileTime = 0 }, app with { StartFileTime = 0 }), "unknown start time cannot be approved");
        string Safety(string image = @"C:\ThirdParty\App.exe", int pid = 700001, int session = 2, bool sameUser = true,
            bool known = true, bool critical = false, int type = 1) => RuntimeFileUseSafety.Reason(pid, image, session, 2, sameUser, known, critical, type, @"C:\Windows", @"C:\Tool");
        Check(Safety() == "", "ordinary current-user app can be selected");
        foreach (var image in new[] { @"C:\Windows\explorer.exe", @"C:\Windows\System32\svchost.exe", @"C:\Tool\Backends\backend.exe",
            @"C:\Other\DeltaNFD.Backend.Runtime.exe", @"C:\Other\DeltaOptimizer.exe", @"C:\Other\msiexec.exe", @"C:\Other\vc_redist.x64.exe",
            @"C:\Other\setup.exe", @"C:\Other\InstallerApp.exe", @"C:\Other\sfc.exe", "relative.exe" })
            Check(Safety(image).Length > 0, "protected executable: " + image);
        Check(Safety(pid: 4).Length > 0 && Safety(pid: Environment.ProcessId).Length > 0, "system and self PID protected");
        Check(Safety(session: 0).Length > 0 && Safety(session: 3).Length > 0 && Safety(sameUser: false).Length > 0, "other users/sessions/services protected");
        Check(Safety(known: false).Length > 0 && Safety(critical: true).Length > 0, "critical or unknown state protected");
        foreach (var type in new[] { 0, 3, 4, 1000 }) Check(Safety(type: type).Length > 0, "RM protected application type " + type);
        Check(Safety(@"C:\ToolOther\App.exe") == "", "directory protection uses full path boundary");
        Check(Safety(@"C:\ProgramData\Package Cache\bundle\random.exe").Length > 0, "renamed cached installer protected");
        Check(RuntimeFileUseSafety.InstallerMetadata("Microsoft Visual C++ Redistributable")
            && RuntimeFileUseSafety.InstallerMetadata("Product Installer") && RuntimeFileUseSafety.InstallerMetadata("安装程序"), "installer metadata protected");
        Console.WriteLine($"Runtime file-use checks: {count} passed. All close/terminate operations mocked; no user processes affected.");
    }

    private sealed class Fake(RuntimeFileUseProcess process) : IRuntimeFileUseEnvironment
    {
        internal RuntimeFileUseProcess? Current = process, Held;
        internal int CloseCount, KillCount;
        internal bool Blocked, ScanFails, OnlyFull, ExitConfirmed = true, HasWindow = true;
        internal List<RuntimeFileUseScope> Scopes = [];
        public Task<RuntimeFileUseScan> InspectAsync(CancellationToken token, RuntimeFileUseScope scope = RuntimeFileUseScope.Vc14)
        { Scopes.Add(scope); return Task.FromResult(new RuntimeFileUseScan(!ScanFails, "fixture", Current is null || OnlyFull && scope != RuntimeFileUseScope.FullRepair ? [] : [Current])); }
        public IRuntimeFileUseProcess Open(RuntimeFileUseProcess expected) => new FakeProcess(this, Held ?? Current!);
        public string? Blocker() => Blocked ? "installer busy" : null;
        private sealed class FakeProcess(Fake owner, RuntimeFileUseProcess identity) : IRuntimeFileUseProcess
        {
            public RuntimeFileUseProcess Identity => identity;
            public bool RequestClose() { owner.CloseCount++; return owner.HasWindow; }
            public void ForceEnd() => owner.KillCount++;
            public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken token) => Task.FromResult(owner.ExitConfirmed);
            public void Dispose() { }
        }
    }
}
