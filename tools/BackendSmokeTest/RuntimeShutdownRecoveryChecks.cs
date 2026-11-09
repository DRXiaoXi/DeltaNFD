using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class RuntimeShutdownRecoveryChecks
{
    internal static async Task RunAsync(bool withIpc)
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-runtime-shutdown-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var record = Path.Combine(root, "runtime-repair-active.json");
        var checkpoint = Path.Combine(root, "runtime-repair-restart.json");
        var bootA = Guid.NewGuid().ToString("D"); var bootB = Guid.NewGuid().ToString("D");
        string? boot = bootA; var ownerAlive = false; var childAlive = false;
        var runner = new RuntimeInstallationReview(root, () => boot, (pid, _) => pid == 101 ? ownerAlive : childAlive);
        var guard = new ReconcileSnapshot { Module = BackendModules.Runtime, Complete = true, Summary = "fixture" };
        Task<IReadOnlyList<VcRedistInfo>> Scan() => Task.FromResult<IReadOnlyList<VcRedistInfo>>([]);
        Task<ReconcileSnapshot> Guard() => Task.FromResult(guard);
        Task<ReconcileSnapshot> Inspect(Func<bool>? busy = null) => RuntimeLegacyInstallationReview.InspectAsync(guard, runner, busy ?? (() => false), Scan);
        Task<RecoverOutcome> Review(bool prepare = false, Func<bool>? busy = null,
            Func<Task<IReadOnlyList<VcRedistInfo>>>? scan = null, Func<Task<ReconcileSnapshot>>? basis = null)
            => RuntimeLegacyInstallationReview.ReviewAsync(runner, busy ?? (() => false), scan ?? Scan, basis ?? Guard, prepare);
        bool Available(ReconcileSnapshot value, string scope = "repair-review") => value.RecoveryScopes.Any(s => s.Id == scope && s.Available);
        void Write(string stage = "prepared", string? recordedBoot = null, int pid = 0, long ticks = 0)
        {
            if (File.Exists(checkpoint)) File.Delete(checkpoint);
            File.WriteAllText(record, JsonSerializer.Serialize(new { Id = Guid.NewGuid().ToString("D"), Stage = stage,
                Pid = pid, StartTicks = ticks, Executable = Environment.ProcessPath!, OwnerPid = 101,
                OwnerStartTicks = 100L, CreatedUtc = DateTimeOffset.UtcNow, BootId = recordedBoot }));
            boot = bootA; ownerAlive = false; childAlive = false;
        }
        bool Preserved(byte[] bytes) => File.Exists(record) && File.ReadAllBytes(record).SequenceEqual(bytes);

        var nativeBoot = WindowsBootIdentity.Read();
        Check(Guid.TryParseExact(nativeBoot, "D", out var nativeId) && nativeId != Guid.Empty,
            "read-only native boot identity is available on this host");
        Check(WindowsBootIdentity.Read() == nativeBoot, "boot identity is stable within this boot");
        Write(recordedBoot: bootA); var original = File.ReadAllBytes(record);
        Check(!Available(await Inspect()) && Preserved(original), "same boot prepared record is not treated as ended");
        Check(!Available(await Inspect(), RuntimeLegacyInstallationReview.RestartScope), "new records already carry restart evidence; no redundant checkpoint");
        boot = bootB;
        Check(Available(await Inspect()) && Preserved(original), "cross-boot inspection offers review without writing or archiving");
        var result = await Review();
        Check(result.Success && result.Verified && !File.Exists(record), "cross-boot prepared transaction can be explicitly reviewed");
        Check(Directory.GetFiles(Path.Combine(root, "runtime-repair-history")).Any(p => File.ReadAllBytes(p).SequenceEqual(original)),
            "original interrupted activity is archived byte for byte");
        Check(result.Message.Contains("不表示上次修复成功") && result.Message.Contains("不表示组件已回滚"), "review cannot claim repair or rollback success");

        foreach (var missing in new[] { (0, 0L), (202, 0L), (0, 200L) })
        {
            Write("running", bootA, missing.Item1, missing.Item2); boot = bootB;
            Check((await Review()).Verified, "cross-boot incomplete running identity can be reviewed: " + missing);
        }
        Write(recordedBoot: bootA); boot = bootB; ownerAlive = true; original = File.ReadAllBytes(record);
        Check(!(await Review()).Verified && Preserved(original), "live original owner remains a blocker even with contradictory boot metadata");
        Write("running", bootA, 202, 200); boot = bootB; childAlive = true; original = File.ReadAllBytes(record);
        Check(!(await Review()).Verified && Preserved(original), "known live installer is never cleared by boot metadata");
        Write("prepared", bootA, 202, 200); boot = bootB; childAlive = true;
        Check(!(await Review()).Verified, "prepared record cannot hide a known live installer");
        Write(recordedBoot: bootA); boot = bootB; original = File.ReadAllBytes(record);
        Check(!(await Review(busy: () => true)).Verified && Preserved(original), "MSI busy after reboot remains blocked");
        Check(!Available(await Inspect(() => throw new IOException("fixture MSI query failure"))), "failed MSI query is not idle");
        Check(!(await Review(scan: () => throw new IOException("fixture scan failure"))).Verified && Preserved(original), "scan failure keeps interrupted activity");
        Check(!(await Review(scan: () => Task.FromResult<IReadOnlyList<VcRedistInfo>>(null!))).Verified && Preserved(original), "null scan cannot clear activity");
        Check(!(await Review(basis: () => Task.FromResult(new ReconcileSnapshot { Module = BackendModules.Runtime, Complete = false, Summary = "fixture incomplete" }))).Verified
            && Preserved(original), "incomplete reconciliation keeps activity");
        boot = null;
        Check(!(await Review()).Verified && Preserved(original), "unavailable boot identity never proves restart");
        boot = "invalid";
        Check(!(await Review()).Verified && Preserved(original), "invalid boot identity never proves restart");

        Write(); original = File.ReadAllBytes(record);
        Check(!Available(await Inspect()) && Available(await Inspect(), RuntimeLegacyInstallationReview.RestartScope), "legacy launch gap offers explicit checkpoint only");
        Check(!File.Exists(checkpoint) && Preserved(original), "inspection is fully read-only");
        result = await Review(prepare: true);
        Check(result.Success && !result.Verified && result.Scope == RuntimeLegacyInstallationReview.RestartScope
            && File.Exists(checkpoint) && Preserved(original), "checkpoint success retains original activity and is never verified recovery");
        Check(result.Message.Contains("不会自动重启") && result.Message.Contains("Windows 的“重启”"), "checkpoint asks user to restart at a convenient time");
        Check(!Available(await Inspect()) && !Available(await Inspect(), RuntimeLegacyInstallationReview.RestartScope), "checkpoint on same boot cannot unblock or continually renew itself");
        boot = bootB;
        Check(Available(await Inspect()) && Preserved(original), "legacy checkpoint proves later boot only for bound activity");
        Check((await Review()).Verified && !File.Exists(record) && File.Exists(checkpoint), "legacy cross-boot review archives activity and retains checkpoint evidence");

        Write(); ownerAlive = true;
        Check(!Available(await Inspect(), RuntimeLegacyInstallationReview.RestartScope), "live owner prevents checkpoint creation");
        ownerAlive = false;
        Check(!Available(await Inspect(() => true), RuntimeLegacyInstallationReview.RestartScope), "MSI busy prevents checkpoint creation");
        Check(!(await Review(prepare: true, scan: () => throw new IOException("fixture scan"))).Success && !File.Exists(checkpoint), "checkpoint scan failure performs no write");
        boot = null;
        Check(!Available(await Inspect(), RuntimeLegacyInstallationReview.RestartScope), "no boot identity means no checkpoint");
        Write(); await Review(prepare: true); File.AppendAllText(record, " "); boot = bootB;
        Check(!Available(await Inspect()), "checkpoint byte hash cannot be reused after activity changed");
        Write(); File.WriteAllText(checkpoint, "{corrupt"); boot = bootB; original = File.ReadAllBytes(record);
        Check(!Available(await Inspect()) && Preserved(original) && File.ReadAllText(checkpoint) == "{corrupt", "corrupt checkpoint retained and blocked");
        Write(); File.WriteAllText(record, "{corrupt");
        Check(!Available(await Inspect()) && !Available(await Inspect(), RuntimeLegacyInstallationReview.RestartScope), "corrupt activity offers no destructive escape");
        Write(recordedBoot: "not-a-boot-id"); boot = bootB; original = File.ReadAllBytes(record);
        Check(!Available(await Inspect()) && Preserved(original), "malformed recorded boot identity cannot enter fallback recovery");
        Write(); File.Delete(record); Directory.CreateDirectory(record);
        Check(!Available(await Inspect()) && !Available(await Inspect(), RuntimeLegacyInstallationReview.RestartScope), "directory in place of activity is preserved and blocked");
        Directory.Delete(record); Write(); Directory.CreateDirectory(checkpoint);
        Check(!Available(await Inspect()) && !Available(await Inspect(), RuntimeLegacyInstallationReview.RestartScope), "directory in place of checkpoint is preserved and blocked");
        Directory.Delete(checkpoint);
        Write(); original = File.ReadAllBytes(record); var busyCalls = 0;
        var busyRejected = false;
        try { result = await Review(prepare: true, busy: () => ++busyCalls > 1); }
        catch (IOException) { busyRejected = true; }
        Check(busyRejected && Preserved(original) && !File.Exists(checkpoint), "MSI becoming busy after initial inspection prevents checkpoint write");
        Write(recordedBoot: bootA); boot = bootB; original = File.ReadAllBytes(record); busyCalls = 0;
        Check(!(await Review(busy: () => ++busyCalls > 1)).Verified && Preserved(original), "MSI becoming busy before archive keeps transaction");
        Write(recordedBoot: bootA); boot = bootB;
        result = await Review(scan: () => { File.AppendAllText(record, " "); return Scan(); });
        Check(!result.Verified && File.Exists(record) && result.Message.Contains("已变化"), "activity mutation during scan prevents archival");
        Write(recordedBoot: bootA); boot = bootB; var scans = 0;
        result = await Review(scan: () => ++scans == 1 ? Scan() : throw new IOException("fixture post-scan failure"));
        Check(!result.Verified && !File.Exists(record), "post-archive scan failure keeps outer reconciliation unverified");
        Check((await Review()).Verified, "post-archive failure can be retried from current state without replaying installation");

        var review = new BackendUnconfirmedStore.Review(BackendModules.Runtime, "fixture", "fixture", true)
            { Command = BackendCommands.Vc14Repair, CanRestore = true };
        Write(); var snapshot = await Inspect();
        Check(BackendRecovery.RecoveryChoices(review, snapshot).Select(s => s.Id).SequenceEqual(new[] { RuntimeLegacyInstallationReview.RestartScope }), "frontend offers checkpoint without guard rollback for launch gap");
        Check(!BackendRecovery.CanKeepCurrentState(review, snapshot), "generic keep-current cannot bypass installation review");
        Check(BackendRecovery.IsRuntimeInstallation(review with { Command = BackendCommands.RuntimeSafeUninstall }), "interrupted safe-mode uninstall also uses transaction review");
        Check(BackendRecovery.RecoveryChoices(review with { Command = BackendCommands.RuntimeGuardEnable }, snapshot).Count == 0, "guard operation cannot use checkpoint or installation review");
        Check(BackendRecovery.RecoveryChoices(review with { Command = BackendCommands.Vc14ForceReplace }, snapshot).Count == 0, "DLL replacement remains manual file recovery");

        // Actual installer runner with a harmless self child; captures boot metadata and exercises timeout responsibility.
        var childRoot = Path.Combine(root, "owned-runner");
        var environment = new Vc14ReinstallEnvironment(dataRoot: childRoot, diagnosticsDirectory: Path.Combine(childRoot, "logs"), installerBusy: () => false);
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        var releaseFile = Path.Combine(root, "owned-child-release");
        foreach (var arg in new[] { "--runtime-shutdown-owned-child", releaseFile }) info.ArgumentList.Add(arg);
        var childRecord = Path.Combine(childRoot, "runtime-repair-active.json");
        try
        {
            // A release signal keeps the owned child alive while slow journal I/O completes.
            // The product deadline remains 25 ms; no product timeout or assertion is relaxed.
            var step = await environment.RunProcessAsync(info, null, default, TimeSpan.FromMilliseconds(25));
            Check(step.Unknown && File.Exists(childRecord), "timeout keeps runner responsibility without killing its child");
            using var json = JsonDocument.Parse(File.ReadAllBytes(childRecord));
            var row = json.RootElement;
            Check(row.GetProperty("BootId").GetString() == nativeBoot && row.GetProperty("Stage").GetString() == "running", "real runner persists native boot identity with child identity");
            using var ownedChild = Process.GetProcessById(row.GetProperty("Pid").GetInt32());
            Check(!ownedChild.HasExited, "timed-out owned child remains alive until explicitly released by the fixture");
        }
        finally
        {
            File.WriteAllText(releaseFile, "release owned child");
            if (File.Exists(childRecord))
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(childRecord));
                var pid = json.RootElement.GetProperty("Pid").GetInt32();
                if (pid > 0)
                    try { using var child = Process.GetProcessById(pid); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (ArgumentException) { }
            }
        }

        if (withIpc)
        {
            Write();
            // Real backend uses actual OS process identities, not the injected IDs above.
            using (var json = JsonDocument.Parse(File.ReadAllBytes(record)))
            {
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json.RootElement.GetRawText())!;
                fields["OwnerPid"] = JsonSerializer.SerializeToElement(int.MaxValue);
                File.WriteAllText(record, JsonSerializer.Serialize(fields));
            }
            var evidence = BackendUnconfirmedStore.RecordPathForTests(BackendModules.Runtime);
            Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
            File.WriteAllText(evidence, JsonSerializer.Serialize(new { Module = BackendModules.Runtime, Phase = "unconfirmed",
                Command = BackendCommands.Vc14Repair, Pid = int.MaxValue, StartTicks = 1L, Path = Environment.ProcessPath!,
                CreatedUtc = DateTimeOffset.UtcNow, ChildrenUnknown = false, Children = Array.Empty<object>() }));
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", id.ToString("D"));
            var current = BackendUnconfirmedStore.ReviewAll().Single();
            original = File.ReadAllBytes(record); var outer = File.ReadAllBytes(evidence);
            var actual = await BackendRecovery.ReconcileAsync(BackendModules.Runtime, scope: RuntimeLegacyInstallationReview.Scope);
            Check(actual is { Complete: true } && Available(actual, RuntimeLegacyInstallationReview.RestartScope), "real backend exposes legacy restart checkpoint scope");
            result = await BackendRecovery.RestoreAsync(current, RuntimeLegacyInstallationReview.RestartScope);
            Check(result.Success && !result.Verified && Preserved(original) && File.ReadAllBytes(evidence).SequenceEqual(outer), "real IPC checkpoint preserves both blocking layers");
            Check(BackendUnconfirmedStore.ReviewAll().Single().Fingerprint == current.Fingerprint, "checkpoint cannot acknowledge or stale outer evidence");
            // Simulate an older boot in our own checkpoint. This tests production IPC without restarting the computer.
            using (var json = JsonDocument.Parse(File.ReadAllBytes(checkpoint)))
            {
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json.RootElement.GetRawText())!;
                fields["BootId"] = JsonSerializer.SerializeToElement(bootA);
                File.WriteAllText(checkpoint, JsonSerializer.Serialize(fields));
            }
            actual = await BackendRecovery.ReconcileAsync(BackendModules.Runtime, scope: RuntimeLegacyInstallationReview.Scope);
            Check(actual is { Complete: true } && Available(actual), "production backend accepts bound checkpoint from simulated earlier boot");
            result = await BackendRecovery.RestoreAsync(current, RuntimeLegacyInstallationReview.Scope);
            Check(result.Verified && !File.Exists(evidence) && !File.Exists(record), "production recovery archives both pending layers after current scan");
            Check(Directory.GetFiles(Path.Combine(root, "runtime-repair-history")).Any(p => File.ReadAllBytes(p).SequenceEqual(original)),
                "production recovery preserves original transaction bytes");
            Check(Directory.GetFiles(Path.Combine(root, "official-backend-reviewed"), "*installation-reviewed*.outcome.json").Length == 1,
                "production review records transaction-reviewed rather than repaired or restored");
        }
        Console.WriteLine($"Runtime shutdown recovery: {count} checks passed; isolated records, simulated boot changes and own child only. No shutdown/install/system repair. Evidence: {root}");
    }
}
