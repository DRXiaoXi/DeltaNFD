using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class FeedbackRepairChecks
{
    private static int _passed;
    private static void Check(bool result, string why)
    { if (!result) throw new Exception(why); _passed++; }
    private static void Reject(Action action, string why)
    { try { action(); } catch (IOException) { _passed++; return; } throw new Exception(why); }

    internal static async Task RunAsync()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-feedback-repair", id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        try
        {
            DurableRecords(root);
            CorruptRuntime(root);
            ProcessBoundaries();
            await OwnedPowerReviewAsync(root, id);
            Check(BackendClient.EvidenceCommand(BackendModules.CpuFrame, BackendCommands.CpuFrameMemoryLimitDetect,
                BackendCommands.CpuFramePowerLock, true) == BackendCommands.CpuFramePowerLock, "read poll retains power command");
            Check(BackendClient.EvidenceCommand(BackendModules.CpuFrame, BackendCommands.CpuFrameMemoryLimitDetect,
                null, true) == BackendCommands.CpuFrameMonitorStart, "unknown monitor obligation stays a monitor");
            Check(BackendClient.EvidenceCommand(BackendModules.CpuFrame, BackendCommands.CpuFramePowerLock,
                BackendCommands.CpuFrameMonitorStart, true) == BackendCommands.CpuFramePowerLock, "in-flight write retains its own command");
            Check(BackendClient.EvidenceCommand(BackendModules.CpuFrame, BackendCommands.CpuFrameMemoryLimitDetect,
                BackendCommands.CpuFrameMonitorStart, false) == BackendCommands.CpuFrameMemoryLimitDetect, "pure read unchanged");
            Console.WriteLine($"Feedback repair checks passed ({_passed} assertions): durable evidence, corrupt-runtime restart checkpoint and byte preservation, PID lifetime bounds, correct CPU obligation. Isolated data only; no real restart or optimization.");
        }
        finally
        {
            RuntimeCorruptRecordReview.BootForTests = null;
            RuntimeCorruptRecordReview.BusyForTests = null;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void DurableRecords(string root)
    {
        var path = Path.Combine(root, "writer", "record.json");
        BackendEvidenceFiles.WriteJson(path, new { Stage = "intent", Value = 1 });
        using (var parsed = JsonDocument.Parse(File.ReadAllBytes(path)))
            Check(parsed.RootElement.GetProperty("Value").GetInt32() == 1, "complete new JSON published");
        var bytes = File.ReadAllBytes(path);
        Reject(() => BackendEvidenceFiles.WriteJson(path, new { Value = 2 }), "new write overwrote evidence");
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "failed new publication preserves bytes");
        BackendEvidenceFiles.WriteJson(path, new { Value = 3 }, overwrite: true);
        using (var parsed = JsonDocument.Parse(File.ReadAllBytes(path)))
            Check(parsed.RootElement.GetProperty("Value").GetInt32() == 3, "complete replacement published");
        Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "owned temporary files cleaned");
        var directory = Path.Combine(root, "writer", "directory.json"); Directory.CreateDirectory(directory);
        Reject(() => BackendEvidenceFiles.WriteJson(directory, new { Value = 4 }), "directory occupied path accepted");
        Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "failed publication leaves no temp");
    }

    private static void CorruptRuntime(string root)
    {
        var path = BackendUnconfirmedStore.RecordPathForTests(BackendModules.Runtime);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var beforeBoot = Guid.NewGuid().ToString("D"); var afterBoot = Guid.NewGuid().ToString("D");
        var boot = beforeBoot; var busy = false;
        RuntimeCorruptRecordReview.BootForTests = () => boot;
        RuntimeCorruptRecordReview.BusyForTests = () => busy;
        File.WriteAllText(path, "{\"Module\":\"runtime\"}");
        Check(RuntimeCorruptRecordReview.Inspect() is null, "valid JSON never uses corruption bypass");
        var bytes = new byte[1024]; File.WriteAllBytes(path, bytes);
        var state = RuntimeCorruptRecordReview.Inspect()!;
        Check(state.CanPrepare && !state.CanPreserve, "zero-filled record offers checkpoint only");
        Check(!Directory.Exists(Path.Combine(root, "official-backend-corrupt-review")), "inspection is read-only");
        Reject(() => RuntimeCorruptRecordReview.Preserve(state), "corrupt evidence cleared before checkpoint/restart");
        RuntimeCorruptRecordReview.Prepare(state);
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "checkpoint preserves raw corrupted original");
        Check(Directory.GetFiles(Path.Combine(root, "official-backend-corrupt-review"), "*.original.bin").Single() is { } original
            && File.ReadAllBytes(original).SequenceEqual(bytes), "checkpoint retains byte-identical forensic copy");
        state = RuntimeCorruptRecordReview.Inspect()!;
        Check(!state.CanPrepare && !state.CanPreserve, "same boot cannot unblock corruption");
        Reject(() => RuntimeCorruptRecordReview.Preserve(state), "same boot preservation accepted");
        boot = afterBoot; busy = true;
        Check(!RuntimeCorruptRecordReview.Inspect()!.CanPreserve, "busy Installer/backend blocks after restart");
        busy = false; state = RuntimeCorruptRecordReview.Inspect()!;
        Check(state.CanPreserve && !state.CanPrepare, "new boot and idle state permits explicit preservation");
        File.WriteAllBytes(path, new byte[1025]);
        Reject(() => RuntimeCorruptRecordReview.Preserve(state), "stale consent cleared changed bytes");
        Check(RuntimeCorruptRecordReview.Inspect()!.CanPrepare, "changed bytes need their own checkpoint");
        File.WriteAllBytes(path, bytes);
        var inner = Path.Combine(root, "runtime-repair-active.json"); File.WriteAllText(inner, "independent transaction remains");
        var archived = RuntimeCorruptRecordReview.Preserve(state);
        Check(!File.Exists(path) && File.ReadAllBytes(archived).SequenceEqual(bytes), "explicit preservation archives exact bytes");
        Check(File.ReadAllText(inner) == "independent transaction remains", "independent runtime transaction untouched");
        using (var outcome = JsonDocument.Parse(File.ReadAllBytes(archived + ".outcome.json")))
            Check(!outcome.RootElement.GetProperty("Repaired").GetBoolean(), "archive is never claimed as repair");
        Check(RuntimeCorruptRecordReview.Inspect() is null, "no pending corruption after explicit archive");
        File.WriteAllBytes(path, bytes); boot = beforeBoot;
        var checkpoint = Directory.GetFiles(Path.Combine(root, "official-backend-corrupt-review"), "*.restart.json").Single();
        File.WriteAllText(checkpoint, "bad");
        Check(RuntimeCorruptRecordReview.Inspect() is null && File.Exists(path), "corrupt checkpoint fails closed");
        File.Delete(checkpoint); RuntimeCorruptRecordReview.BootForTests = () => null;
        Check(!RuntimeCorruptRecordReview.Inspect()!.CanPrepare && !RuntimeCorruptRecordReview.Inspect()!.CanPreserve, "unknown boot blocks both actions");
        File.Delete(path);
    }

    private static void ProcessBoundaries()
    {
        BackendProcessTree.Entry[] rows = [new("owned-child", 2, 1, 1500), new("PSAgent.exe", 3, 1, 3000),
            new("owned-grandchild", 4, 2, 2500)];
        var bounded = BackendProcessTree.Capture(1, 1000, rows, 2000);
        Check(bounded.Ok && bounded.Children.Select(c => c.Pid).Order().SequenceEqual(new[] { 2, 4 }),
            "confirmed root exit rejects late PPID reuse and retains real orphan grandchildren");
        var unbounded = BackendProcessTree.Capture(1, 1000, rows);
        Check(unbounded.Children.Count == 3, "unknown exit time does not guess unrelatedness");
        var unknown = BackendProcessTree.Capture(1, 1000, [new("unknown", 2, 1, 0)], 2000);
        Check(!unknown.Ok && unknown.Children.Count == 1, "unknown child identity remains blocking");
        Check(!BackendProcessTree.Capture(1, 1000, rows, 999).Ok, "invalid exit interval rejected");
        var reused = BackendProcessTree.Capture(1, 1000, [new("new-root", 1, 0, 4000), .. rows], 2000);
        Check(reused.Ok && reused.Children.Count == 2, "reused root lifetime cannot adopt later unrelated branch");
    }

    private static async Task OwnedPowerReviewAsync(string root, Guid id)
    {
        var backend = Path.Combine(root, "backends", "cpuframe"); Directory.CreateDirectory(backend);
        Check(Path.GetFileName(Environment.ProcessPath) == "BackendSmokeTest.exe", "uses only an owned test backend");
        foreach (var source in Directory.GetFiles(AppContext.BaseDirectory))
            File.Copy(source, Path.Combine(backend, Path.GetFileName(source) == "BackendSmokeTest.exe"
                ? "DeltaNFD.Backend.CpuFrame.exe" : Path.GetFileName(source)));
        Environment.SetEnvironmentVariable(BackendPaths.BackendRootEnvironmentVariable, Path.GetDirectoryName(backend));
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", id.ToString("D"));
        Environment.SetEnvironmentVariable("DELTANFD_TEST_RECOVERY_SCENARIO", "power-review");
        BackendUnconfirmedStore.DescendantsForTests = _ => ([], true);
        try
        {
            BackendUnconfirmedStore.PromoteIntent(BackendModules.CpuFrame, Guid.NewGuid(), "disconnected", int.MaxValue, 1,
                Environment.ProcessPath!, BackendCommands.CpuFrameRestore, [], false, Guid.NewGuid());
            var review = BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.CpuFrame);
            var path = BackendUnconfirmedStore.RecordPathForTests(BackendModules.CpuFrame);
            var original = File.ReadAllBytes(path);
            var records = await BackendRecovery.ReadPowerReviewAsync(review);
            Check(records.Count == 1 && File.ReadAllBytes(path).SequenceEqual(original), "read power review is usable while original record stays pending");
            try
            {
                await BackendRecovery.RestoreAsync(review, "all", confirmedPower: [records[0] with { Fingerprint = "stale" }]);
                throw new Exception("stale power confirmation accepted");
            }
            catch (InvalidOperationException) { Check(File.Exists(path), "stale recovery keeps original blocker"); }
            Check(File.ReadAllBytes(path).SequenceEqual(original), "failed power review leaves evidence bytes intact");
            var fresh = BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.CpuFrame);
            var outcome = await BackendRecovery.RestoreAsync(fresh, "all", confirmedPower: records);
            Check(outcome.Success && outcome.Verified && outcome.Scope == "all", "actual pipe confirms full recovery after explicit power review");
            Check(!File.Exists(path), "only full verified recovery completes the pending review");
            var archived = Directory.GetFiles(Path.Combine(root, "official-backend-reviewed"), "cpuframe-restored-*.json")
                .Single(p => !p.EndsWith(".outcome.json", StringComparison.Ordinal));
            Check(File.ReadAllBytes(archived).SequenceEqual(original), "successful power review archives exact original evidence");
        }
        finally
        {
            BackendUnconfirmedStore.DescendantsForTests = null;
            Environment.SetEnvironmentVariable(BackendPaths.BackendRootEnvironmentVariable, null);
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", null);
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", null);
            Environment.SetEnvironmentVariable("DELTANFD_TEST_RECOVERY_SCENARIO", null);
        }
    }
}
