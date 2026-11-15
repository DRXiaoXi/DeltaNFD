using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class RuntimePendingRecoveryChecks
{
    internal static async Task RunAsync(bool withIpc)
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-runtime-review-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        var guard = new ReconcileSnapshot { Module = BackendModules.Runtime, Complete = true, Summary = "guard read-only verified",
            RecoveryScopes = [new() { Id = "guard", Description = "guard restore", Available = true }] };
        IReadOnlyList<VcRedistInfo> scan = [];
        var runner = new RuntimeInstallationReview(root);
        var record = Path.Combine(root, "runtime-repair-active.json");
        void WriteActive(string stage, int pid, long ticks) => File.WriteAllText(record, JsonSerializer.Serialize(new
        {
            Id = Guid.NewGuid().ToString("D"), Stage = stage, Pid = pid, StartTicks = ticks,
            Executable = Environment.ProcessPath!, OwnerPid = Environment.ProcessId,
            OwnerStartTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, CreatedUtc = DateTimeOffset.UtcNow,
        }));
        Task<IReadOnlyList<VcRedistInfo>> Scan() => Task.FromResult(scan);
        Task<ReconcileSnapshot> Guard() => Task.FromResult(guard);
        Task<RecoverOutcome> Review(Func<bool>? busy = null, Func<Task<IReadOnlyList<VcRedistInfo>>>? scanner = null)
            => RuntimeLegacyInstallationReview.ReviewAsync(runner, busy ?? (() => false), scanner ?? Scan, Guard);

        Check((await RuntimeLegacyInstallationReview.InspectAsync(guard, runner, () => true, Scan)).RecoveryScopes.Single(s => s.Id == "repair-review").Available == false,
            "MSI busy blocks review even without active record");
        WriteActive("prepared", 0, 0);
        var original = File.ReadAllBytes(record);
        Check(!(await Review()).Verified && File.ReadAllBytes(record).SequenceEqual(original), "prepared unknown launch remains blocked and preserved");
        File.WriteAllText(record, "{corrupt");
        Check(!(await Review()).Verified && File.ReadAllText(record) == "{corrupt", "corrupt activity cannot be silently discarded");
        WriteActive("running", int.MaxValue, 1);
        original = File.ReadAllBytes(record);
        Check(!(await Review(() => true)).Verified && File.ReadAllBytes(record).SequenceEqual(original), "exited launcher but MSI busy remains blocked");
        Check(!(await Review(scanner: () => throw new IOException("fixture scan failure"))).Verified && File.Exists(record), "scan failure retains activity and module blockade");
        var outcome = await Review();
        Check(outcome.Verified && outcome.Success && outcome.Scope == "repair-review" && !File.Exists(record), "confirmed ended transaction archived after read-only scan");
        Check(Directory.GetFiles(Path.Combine(root, "runtime-repair-history")).Any(p => File.ReadAllBytes(p).SequenceEqual(original)), "activity evidence archived byte-for-byte");
        Check(outcome.Message.Contains("不表示上次修复成功") && outcome.Message.Contains("不表示组件已回滚"), "transaction review does not claim repair or rollback success");
        Check((await Review()).Verified, "review remains usable when previous activity was already archived");

        var commandInfo = new ProcessStartInfo(Environment.ProcessPath!, "--procman-test-child") { UseShellExecute = false, CreateNoWindow = true };
        using var child = Process.Start(commandInfo)!;
        var evidence = BackendUnconfirmedStore.RecordPathForTests(BackendModules.Runtime);
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        void WriteEvidence(IReadOnlyList<BackendUnconfirmedStore.ChildProcess> children) => File.WriteAllText(evidence, JsonSerializer.Serialize(new
        {
            Module = "runtime", Phase = "unconfirmed", Command = BackendCommands.RuntimeCompatibilityRepair,
            Pid = int.MaxValue, StartTicks = 1L, Path = Environment.ProcessPath!, CreatedUtc = DateTimeOffset.UtcNow,
            Children = children, ChildrenUnknown = false,
        }));
        try
        {
            var ticks = child.StartTime.ToUniversalTime().Ticks;
            WriteActive("running", child.Id, ticks);
            original = File.ReadAllBytes(record);
            Check(!(await Review()).Verified && File.ReadAllBytes(record).SequenceEqual(original), "live system-tool fixture cannot be acknowledged or killed by review");
            WriteEvidence([new("fixture", child.Id, ticks / 10 * 10)]);
            var alive = BackendUnconfirmedStore.ReviewAll().Single();
            Check(alive.ChildrenAlive && !alive.CanRestore && !alive.CanAcknowledge,
                "WMI microsecond timestamp rounding cannot misclassify live installer as exited");
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
        WriteEvidence([]);
        var review = BackendUnconfirmedStore.ReviewAll().Single();
        var snapshot = await RuntimeLegacyInstallationReview.InspectAsync(guard, runner, () => false, Scan);
        Check(BackendRecovery.RecoveryChoices(review, snapshot).Select(s => s.Id).SequenceEqual(new[] { "repair-review" }), "installation record offers transaction review instead of guard rollback");
        Check(!BackendRecovery.CanKeepCurrentState(review, snapshot), "installation cannot bypass transaction scan by generic keep-current button");
        Check(BackendRecovery.RecoveryChoices(review with { Command = BackendCommands.RuntimeGuardEnable }, snapshot).Select(s => s.Id).SequenceEqual(new[] { "guard" }),
            "guard records retain original backup recovery path");
        if (withIpc)
        {
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", id.ToString("D"));
            var actual = await BackendRecovery.ReconcileAsync(BackendModules.Runtime, scope: "repair-review");
            Check(actual is { Complete: true } && actual.RecoveryScopes.Any(s => s.Id == "repair-review" && s.Available),
                "real backend exposes ended installation review: " + JsonSerializer.Serialize(actual));
            var recovered = await BackendRecovery.RestoreAsync(review, "repair-review");
            Check(recovered.Verified && !File.Exists(evidence) && !File.Exists(record), "real frontend/backend path archives both records and releases module");
            Check(Directory.GetFiles(Path.Combine(root, "official-backend-reviewed"), "*installation-reviewed*.outcome.json").Length == 1,
                "IPC review evidence records installation-reviewed, never restored");
        }
        else
        {
            var completed = await Review();
            BackendUnconfirmedStore.ArchiveAfterRecovery(review, "repair-review", completed.Verified, completed.Message, completed.VerifiedSnapshot);
            Check(!File.Exists(evidence) && !File.Exists(record), "verified transaction review can release both pending layers");
        }
        Console.WriteLine($"Runtime pending recovery: {checks} checks passed; isolated records and own child only; no installer/system repair invoked. Evidence: {root}");
    }
}
