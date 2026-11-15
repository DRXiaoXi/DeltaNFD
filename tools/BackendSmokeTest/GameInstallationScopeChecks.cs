using DeltaNFD.Backends;
using DeltaNFD.Inspection;
using DeltaNFD.Services;
using Microsoft.Win32;

internal static class GameInstallationScopeChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        void Reject(Action action, string message)
        {
            try { action(); } catch (InvalidOperationException) { count++; return; }
            throw new Exception(message);
        }
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-multi-install-" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        string Put(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "owned fixture"); return path; }
        var first = Path.Combine(root, "游戏 A"); var second = Path.Combine(root, "游戏 B");
        var shared = Path.Combine(root, "shared", "AntiCheatExpert");
        foreach (var install in new[] { first, second })
            Put(Path.Combine(install, "DeltaForce", "Binaries", "Win64", DeltaForceLocator.GameProcessName + ".exe"));
        var inventory = GameInstallationScope.NormalizeInventory([first, second, Path.Combine(first, "DeltaForce"), first.ToUpperInvariant()]);
        Check(inventory.Length == 2, "parent/payload/case aliases are one installation");
        Check(GameInstallationScope.InventoryId(inventory) == GameInstallationScope.InventoryId(inventory.Reverse()), "inventory ordering is stable");
        var one = GameInstallationScope.Select(inventory, first, false);
        var all = GameInstallationScope.Select(inventory, null, true);
        var request = System.Text.Json.JsonSerializer.SerializeToElement(new GameInstallationArgs { Selection = one }, BackendJson.SerializerOptions);
        Check(!GameInstallationProtocol.IsSupported(request, 0), "old backend cannot receive a scoped request");
        Check(GameInstallationProtocol.IsSupported(request, 1), "supporting backend accepts scoped protocol");
        Check(!GameInstallationProtocol.IsSupported(request, 2), "unknown scoped protocol is refused");
        var nonce = Convert.ToBase64String(new byte[32]);
        var legacyPayload = System.Text.Json.JsonSerializer.SerializeToElement(new { nonce, module = "ace", moduleVersion = "0.9350.0", protocolMinor = 0,
            pid = 1, startTimeUtcTicks = 1, imagePath = first, imageSha256 = new string('0', 64) }, BackendJson.SerializerOptions);
        var legacyReady = BackendMessages.ParseReady(legacyPayload);
        Check(legacyReady.GameInstallationScopeVersion == 0, "legacy ready message defaults to no scope capability");
        var currentReady = legacyReady with { GameInstallationScopeVersion = 1 };
        Check(BackendMessages.ParseReady(System.Text.Json.JsonSerializer.SerializeToElement(currentReady, BackendJson.SerializerOptions)).GameInstallationScopeVersion == 1,
            "actual strict ready parser retains current scope capability");
        Reject(() => GameInstallationScope.Enter(null, true, inventory), "old mutation clients cannot silently choose all");
        Reject(() => GameInstallationScope.Enter(one, true, [first]), "removed installation invalidates selection");
        Reject(() => GameInstallationScope.Enter(one, true, [first, second, Path.Combine(root, "third")]), "new installation invalidates selection");
        Reject(() => GameInstallationScope.Enter(new() { InventoryId = one.InventoryId, Roots = [root] }, true, inventory), "arbitrary paths refused");
        Reject(() => GameInstallationScope.Enter(new() { InventoryId = one.InventoryId, Roots = [first], AllInstallations = true }, true, inventory), "all cannot hide an incomplete subset");
        Reject(() => GameInstallationScope.Enter(new() { InventoryId = one.InventoryId, Roots = [first, second] }, true, inventory), "single must mean exactly one installation");
        Reject(() => GameInstallationScope.Enter(new() { InventoryId = one.InventoryId, Roots = [first], SharedCleanupConfirmed = true }, true, inventory), "single cannot authorize shared writes");
        Reject(() => GameInstallationScope.Enter(new() { InventoryId = one.InventoryId, Roots = [first, first] }, true, inventory), "duplicate paths refused");
        using (GameInstallationScope.Enter(one, true, inventory))
        {
            Check(DeltaForceLocator.FindRoots().SequenceEqual([first]), "real locator sees selected installation only");
            Check(!GameInstallationScope.AllowsSharedCleanup, "single selection never includes shared cleanup");
            var copy = GameInstallationScope.CurrentSelection!;
            copy.Roots[0] = second;
            Check(DeltaForceLocator.FindRoots().Single() == first, "caller cannot mutate captured roots");
            Check((await Task.Run(() => DeltaForceLocator.FindRoots().Single())) == first, "selection survives Task.Run");
            Check((await InspectionScheduler.Run(() => DeltaForceLocator.FindRoots().Single())) == first, "selection reaches real read worker");
            using (GameInstallationScope.Enter(all, true, inventory))
            {
                Check(DeltaForceLocator.FindRoots().Count() == 2, "nested all selection enumerates both installations");
                Check(!GameInstallationScope.AllowsSharedCleanup, "all selection still requires shared confirmation");
            }
            Check(DeltaForceLocator.FindRoots().Single() == first, "nested disposal restores outer operation");
        }
        Check(GameInstallationScope.CurrentSelection is null, "operation scope does not persist");
        Check(await InspectionScheduler.Run(() => GameInstallationScope.CurrentSelection is null), "worker does not retain first caller selection");
        var selections = new[] { one, GameInstallationScope.Select(inventory, second, false) };
        var concurrent = await Task.WhenAll(selections.Select(selection => Task.Run(async () =>
        {
            using var scope = GameInstallationScope.Enter(selection, true, inventory);
            return await InspectionScheduler.Run(() => DeltaForceLocator.FindRoots().Single());
        })));
        Check(concurrent.SequenceEqual([first, second]), "queued concurrent inspections keep separate selections");
        using (GameInstallationScope.Enter(new() { InventoryId = all.InventoryId, Roots = all.Roots, AllInstallations = true,
            SharedCleanupConfirmed = true }, true, inventory))
            Check(GameInstallationScope.AllowsSharedCleanup, "all plus confirmation authorizes shared cleanup");

        var target = new GameTargetService(Path.Combine(root, "settings.json"));
        var firstPso = Path.Combine(first, "DeltaForce", "Saved", "PSOCache");
        var secondPso = Path.Combine(second, "DeltaForce", "Saved", "PSOCache");
        var firstFile = Put(Path.Combine(firstPso, "GameVer", "test.bin"));
        var secondFile = Put(Path.Combine(secondPso, "GameVer", "test.bin"));
        var local = Path.Combine(root, "local");
        var systemFile = Put(Path.Combine(local, "D3DSCache", "keep.bin"));
        var shader = new ShaderService(target, () => new(ShaderGpuDetectionState.Nvidia, "621.14"),
            gameRunning: () => false, localAppData: () => local);
        using (GameInstallationScope.Enter(one, true, inventory))
        {
            Check(DeltaForceLocator.FindPsoCachePaths().SequenceEqual([firstPso]), "PSOCache resolution is scoped");
            Check(GameCompatibilityService.ResolveGameExePaths().Single().StartsWith(first + Path.DirectorySeparatorChar), "FSO paths are scoped");
            var result = await shader.ClearPsoCacheAsync();
            Check(result.Success && !File.Exists(firstFile), "real shader service deletes selected fixture cache");
            Check(File.Exists(secondFile), "other installation shader cache stays");
            Check(File.Exists(systemFile), "621 single-install cleanup keeps system D3DSCache");
        }
        using (GameInstallationScope.Enter(new() { InventoryId = all.InventoryId, Roots = all.Roots,
            AllInstallations = true, SharedCleanupConfirmed = true }, true, inventory))
        {
            Check((await shader.ClearPsoCacheAsync()).Success, "all-directory shader cleanup succeeds");
            Check(!File.Exists(secondFile) && !File.Exists(systemFile), "confirmed all clears second PSOCache and linked system fixture");
        }
        Directory.CreateDirectory(Path.Combine(firstPso, "v1"));
        Directory.CreateDirectory(Path.Combine(firstPso, "v2"));
        Directory.CreateDirectory(Path.Combine(secondPso, "v1"));
        Directory.CreateDirectory(Path.Combine(secondPso, "v2"));
        using (GameInstallationScope.Enter(all, inventory: inventory))
        {
            var diagnosis = await new ShaderInspection(target, () => new(ShaderGpuDetectionState.NotPresent, "")).DiagnoseAsync();
            Check(diagnosis.Level == ShaderDiagLevel.Abnormal && diagnosis.Details.Any(s => s.Contains(first))
                && diagnosis.Details.Any(s => s.Contains(second)), "all-directory diagnosis reports both installations");
        }

        var firstAce = Path.Combine(first, "DeltaForce", "Binaries", "Win64", "AntiCheatExpert");
        var secondAce = Path.Combine(second, "DeltaForce", "Binaries", "Win64", "AntiCheatExpert");
        var firstAceFile = Put(Path.Combine(firstAce, "local.bin"));
        var secondAceFile = Put(Path.Combine(secondAce, "keep.bin"));
        var sharedFile = Put(Path.Combine(shared, "keep.sys"));
        var environment = new LocalEnvironment(shared);
        var ace = new AceService(new TweakBackupStore(Path.Combine(root, "backups.json")), target, () => false, environment);
        using (GameInstallationScope.Enter(one, true, inventory))
        {
            var result = await ace.CleanLocalPhasedAsync(null, CancellationToken.None);
            Check(result.Status == BackendResultStatuses.Ok && !File.Exists(firstAceFile), "local ACE service deletes selected fixture only");
            Check(File.Exists(secondAceFile) && File.Exists(sharedFile), "other installation and shared ACE stay");
            Check(environment.SharedCalls == 0, "no shared process/service/registry/reboot-delete API used");
            Check(environment.Deleted.SequenceEqual([firstAce]), "only selected fixed ACE root reached deletion");
            Put(firstAceFile);
            environment.Blocked = true;
            Check((await ace.CleanLocalPhasedAsync(null, CancellationToken.None)).Status == BackendResultStatuses.Failed
                && File.Exists(firstAceFile), "guard blocks local cleanup before deletion");
            environment.Blocked = false;
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check((await ace.CleanLocalPhasedAsync(null, cancelled.Token)).Status == BackendResultStatuses.Cancelled
                && File.Exists(firstAceFile), "cancelled local cleanup does not touch files");
            environment.Reparse = firstAce;
            Check((await ace.CleanLocalPhasedAsync(null, CancellationToken.None)).Status == BackendResultStatuses.Failed
                && File.Exists(firstAceFile), "reparse local root refused");
            var candidates = AceDefaultGameInspection.Read([], () => false, () => DeltaForceLocator.FindRoots(),
                () => [Path.Combine(second, "DeltaForce")]);
            Check(candidates.Count == 1 && candidates[0].Root == first, "manual alias cannot re-add unselected Delta installation");
            Put(Path.Combine(shared, "ACE-CORE-test.sys"));
            var core = AceCoreOwnershipInspection.Read(shared, () => false, (_, _) => []);
            Check(core.InstallationScopeLimited && !core.HasExcessCoreFiles && core.Level == AceCoreCheckLevel.NeedsReview,
                "single-install view cannot falsely report shared CORE surplus");
        }
        void Pe(string path)
        {
            var bytes = new byte[512]; bytes[0] = 0x4d; bytes[1] = 0x5a; bytes[0x3c] = 0x80;
            bytes[0x80] = 0x50; bytes[0x81] = 0x45;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
        }
        foreach (var install in inventory)
        {
            Pe(Path.Combine(install, "icreate", "icreate-df", "icreateDF.exe"));
            Pe(Path.Combine(install, "icreate", "recorder-release", "recorder.exe"));
        }
        var recording = new GameRecordingModification(Path.Combine(root, "recording"),
            () => GameInstallationScope.CurrentRoots ?? inventory, () => null);
        using (GameInstallationScope.Enter(one, true, inventory))
        {
            var snapshot = recording.Read();
            Check(snapshot.Complete && snapshot.Items.Count == 2 && snapshot.Items.All(i => i.InstallationRoot == first), "recording status is scoped");
            var item = snapshot.Items.Single(i => i.ComponentId == "icreate");
            var applied = recording.Change(new() { InstallationRoot = item.InstallationRoot, ComponentId = item.ComponentId, ExpectedFingerprint = item.Fingerprint });
            Check(applied.Success && new FileInfo(item.FilePath).Length == 0, "recording change applies to selected fixture");
            Check(new FileInfo(Path.Combine(second, "icreate", "icreate-df", "icreateDF.exe")).Length == 512, "other recording original stays");
        }
        using (GameInstallationScope.Enter(selections[1], true, inventory))
        {
            var snapshot = recording.Read();
            Check(snapshot.Complete && snapshot.Items.All(i => i.InstallationRoot == second) && snapshot.UnmatchedRecords == 1,
                "another installation recovery record does not lock selected page");
            var other = snapshot.Items.Single(i => i.ComponentId == "icreate");
            Check(recording.Change(new() { InstallationRoot = first, ComponentId = other.ComponentId, ExpectedFingerprint = other.Fingerprint }).Success == false,
                "recording engine rejects an installation outside operation scope");
        }
        var guarded = Path.Combine(root, "guarded", "AntiCheatExpert");
        var guardedFile = Put(Path.Combine(guarded, "keep.bin"));
        var guardedResult = AceLocalFileCleanup.Delete(guarded, () => throw new IOException("simulated game start"));
        Check(guardedResult.Problems.Count > 0 && File.Exists(guardedFile), "real local deleter stops when per-file guard refuses");
        var locked = Put(Path.Combine(root, "locked", "AntiCheatExpert", "locked.bin"));
        using (var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var lockedResult = AceLocalFileCleanup.Delete(Path.GetDirectoryName(locked)!, () => { });
            Check(lockedResult.Problems.Count > 0 && File.Exists(locked), "locked file is reported without reboot scheduling");
        }
        Console.WriteLine($"Multi-installation scope checks passed ({count} assertions). Only owned temporary fixtures and fake system APIs used. Fixture: {root}");
    }

    private sealed class LocalEnvironment(string shared) : IAceCleanupEnvironment
    {
        public bool Blocked;
        public string Reparse = "";
        public int SharedCalls;
        public List<string> Deleted = [];
        private Exception Forbidden() { SharedCalls++; return new Exception("shared mutation must not be called"); }
        public bool IsElevated => true;
        public string AceInstallDirectory => shared;
        public IReadOnlyList<string> GameAceDirectories() => throw Forbidden();
        public bool IsProcessRunning(string name) => Blocked;
        public IReadOnlyList<int> FindProcesses(string name) => throw Forbidden();
        public bool TerminateProcessTree(int pid) => throw Forbidden();
        public bool ServiceKeyExists(string name) => throw Forbidden();
        public Task<(int Code, string StdOut, string StdErr)> RunCommandAsync(string fileName, string arguments, TimeSpan timeout) => throw Forbidden();
        public bool RegistryKeyExists(RegistryHive hive, string path) => throw Forbidden();
        public void DeleteRegistryTree(RegistryHive hive, string path, bool throwOnMissingSubKey) => throw Forbidden();
        public IReadOnlyList<string> EnumerateSubKeyNames(RegistryHive hive, string path) => throw Forbidden();
        public bool HasAceRegistryOwnership(RegistryHive hive, string path) => throw Forbidden();
        public Task<OperationResult> BackupRegistryKeyAsync(RegistryHive hive, string path) => throw Forbidden();
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public bool IsReparsePoint(string path) => path == Reparse;
        public double GetDirectorySizeMb(string path) => throw Forbidden();
        public (double FreedMb, int RebootPending) DeleteDirectoryTree(string path) => throw Forbidden();
        public AceLocalDeleteResult DeleteLocalDirectoryTree(string path, Action checkpoint)
        { Deleted.Add(path); return AceLocalFileCleanup.Delete(path, checkpoint); }
    }
}
