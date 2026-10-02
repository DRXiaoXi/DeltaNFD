using System.Diagnostics;
using System.Numerics;
using DeltaNFD.Native;
using DeltaNFD.Services;

internal static class OfflineAuditChecks
{
    public static async Task RunAsync()
    {
        var fixtureParent = Path.Combine(Directory.GetCurrentDirectory(), "_buildcheck", "offline-audit-fixtures");
        var root = Path.Combine(fixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new OfflineModeStateStore(Path.Combine(root, "state.json"));
            var run = Guid.NewGuid();
            Require(store.TryUpdate(s => { s.OfflineModeEnabled = true; s.Status = OfflineModeStatus.WaitingForGame;
                s.SavedPreferences = new(); s.HelperRunId = run; }, out _), "reserve run");
            Require(OfflineHelperSession.TryRegister(store, run, 123, DateTime.UtcNow.Ticks, out _), "register before READY");
            var registered = store.Read();
            Require(!OfflineHelperSession.TryRegister(store, Guid.NewGuid(), 123, registered.HelperStartTimeUtcTicks!.Value, out _), "stale run rejected");
            store.TryUpdate(s => { s.CancelRequested = true; s.Status = OfflineModeStatus.RestorePending; }, out _);
            Require(!OfflineHelperSession.TryProgress(store, run, 123, registered.HelperStartTimeUtcTicks!.Value,
                OfflineModeStatus.Applying, "bad", true, OfflineModeStatus.Ready, out _), "completion cannot override cancellation");
            Require(store.Read().CancelRequested && store.Read().Status == OfflineModeStatus.RestorePending, "cancellation preserved");
            Require(new OfflineModeState { SavedPreferences = new() }.BlocksNormalAutomation,
                "partially restored preferences cannot open normal automation");
            Require(new OfflineModeState { HelperProcessId = 123 }.BlocksNormalAutomation,
                "unknown live helper cannot open normal automation");
            Require(!store.TryUpdate(s => s.Status = (OfflineModeStatus)99, out _), "unknown enum rejected");
            File.WriteAllText(store.PathName, "{\"Status\":99}");
            Require(!store.TryRead(out _, out _), "numeric invalid state fails closed");
            File.Delete(store.PathName);
            Directory.CreateDirectory(store.PathName);
            Require(!store.TryRead(out _, out _), "directory is not a missing state file");
            Directory.Delete(store.PathName);
            var deadlineHit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var deadline = new OfflineHelperDeadline(TimeSpan.FromMilliseconds(40), () => deadlineHit.TrySetResult(true)))
                await deadlineHit.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var ranCanceled = false;
            using (var deadline = new OfflineHelperDeadline(TimeSpan.FromMilliseconds(100), () => ranCanceled = true)) { }
            await Task.Delay(150);
            Require(!ranCanceled, "finished deadline cannot fire later");
            var completion = new OfflineModeStateStore(Path.Combine(root, "completion.json"));
            Require(completion.TryUpdate(s => { s.OfflineModeEnabled = true; s.SavedPreferences = new();
                s.Status = OfflineModeStatus.Applying; s.HelperRunId = Guid.NewGuid(); s.HelperProcessId = int.MaxValue;
                s.HelperStartTimeUtcTicks = 638000000000000000; s.HelperOperationCompleted = true;
                s.HelperCompletionStatus = OfflineModeStatus.Ready; }, out _), "completion fixture");
            using (var denyReplace = new FileStream(completion.PathName, FileMode.Open, FileAccess.Read, FileShare.Read))
                Require(!OfflineHelperCompletion.TryConfirmExited(completion), "completion write failure cannot report success");
            Require(completion.Read().HelperProcessId == int.MaxValue, "failed completion keeps helper evidence");
            Require(OfflineHelperCompletion.TryConfirmExited(completion) && completion.Read().HelperProcessId is null,
                "completion can retry after persistence recovers");

            await CheckIsolatedProcessesAsync(root);
            CheckCoordinatorStructure();
            Console.WriteLine("Offline audit checks passed: session/cancellation fencing, strict state, deadlines, isolated dual-CCD others, retained unknown identity, restore retry, no auto restart. No real game/DWM/power/task actions.");
        }
        finally
        {
            var prefix = Path.GetFullPath(fixtureParent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Require(Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "cleanup within temporary root");
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(root, true); break; }
                catch (IOException) when (attempt < 15) { await Task.Delay(200); }
                catch (UnauthorizedAccessException) when (attempt < 15) { await Task.Delay(200); }
            }
        }
    }

    private static async Task CheckIsolatedProcessesAsync(string root)
    {
        var real = await new CpuTopologyService().GetTopologyAsync();
        if (real.GroupCount != 1 || BitOperations.PopCount(real.AllMask) < 4 ||
            !OfflineCpuSetCatalog.TryRead(out var catalog, out _) ||
            !OfflineCpuSetCatalog.IsMaskUsable(catalog, 1, 0, real.AllMask))
        { Console.WriteLine("SKIP native scheduler: unsupported or reserved topology."); return; }
        var directory = Path.Combine(root, "children");
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.GetFiles(AppContext.BaseDirectory))
            File.Copy(path, Path.Combine(directory, Path.GetFileName(path)));
        var sourceExe = Path.Combine(directory, "BackendSmokeTest.exe");
        var targetExe = Path.Combine(directory, "OfflineAuditTarget.exe");
        var otherExe = Path.Combine(directory, "OfflineAuditOther.exe");
        File.Copy(sourceExe, targetExe);
        File.Copy(sourceExe, otherExe);
        using var target = Process.Start(new ProcessStartInfo(targetExe, "--offline-audit-child") { UseShellExecute = false, CreateNoWindow = true })!;
        using var other = Process.Start(new ProcessStartInfo(otherExe, "--offline-audit-child") { UseShellExecute = false, CreateNoWindow = true })!;
        var store = new OfflineModeStateStore(Path.Combine(root, "native-state.json"));
        OfflineCpuSetScheduler? scheduler = null;
        try
        {
            await Task.Delay(250);
            if (unchecked((ulong)target.ProcessorAffinity.ToInt64()) != real.AllMask ||
                unchecked((ulong)other.ProcessorAffinity.ToInt64()) != real.AllMask)
            { Console.WriteLine("SKIP native scheduler: parent already has restrictive hard affinity."); return; }
            var settingsPath = Path.Combine(root, "settings.json");
            var targetService = new GameTargetService(settingsPath);
            Require((await targetService.ChangeAsync(true, targetExe)).Success, "isolated exact target selected");
            var prefs = new OfflineModePreferences { DualCcdImmediateEnabled = true, DualCcdGameCcdIndex = 1 };
            store.TryUpdate(s => { s.OfflineModeEnabled = true; s.Status = OfflineModeStatus.Ready; s.SavedPreferences = prefs; }, out _);
            scheduler = new OfflineCpuSetScheduler(new SyntheticTopology(real.AllMask), targetService, store,
                () => [Process.GetProcessById(other.Id)]);
            var applied = await scheduler.ApplyOnceAsync(prefs);
            Require(applied.Success, "isolated soft apply: " + applied.Message);
            var records = store.Read().CpuSetChanges;
            Require(records.Any(r => r.ProcessId == other.Id && r.Purpose == "other-process"), "background is not tested as target-game");
            Require(records.Any(r => r.ProcessId == target.Id), "target also applied");
            var originalOtherPath = records.Single(r => r.ProcessId == other.Id).ExecutablePath;
            store.TryUpdate(s => s.CpuSetChanges.Single(r => r.ProcessId == other.Id).ExecutablePath = targetExe, out _);
            Require(!scheduler.RestoreRecordedAssignments().Success && store.Read().CpuSetChanges.Any(r => r.ProcessId == other.Id),
                "same live PID/time but wrong path is retained, not discarded as gone");
            store.TryUpdate(s => s.CpuSetChanges.Single(r => r.ProcessId == other.Id).ExecutablePath = originalOtherPath, out _);
            using (var blocked = new FileStream(store.PathName, FileMode.Open, FileAccess.Read, FileShare.Read))
                Require(!scheduler.RestoreRecordedAssignments().Success, "restored value but failed record deletion is reported");
            Require(scheduler.RestoreRecordedAssignments().Success && store.Read().CpuSetChanges.Count == 0,
                "retry recognizes original CPU sets and cleans applied/restore-failed record");
        }
        finally
        {
            if (scheduler is not null) _ = scheduler.RestoreRecordedAssignments();
            foreach (var process in new[] { target, other })
            {
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            }
        }
    }

    private static void CheckCoordinatorStructure()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), "src", "DeltaNFD", "Services", "OfflineModeCoordinator.cs");
        var code = File.ReadAllText(path);
        var disable = code[code.IndexOf("public async Task<OperationResult> DisableAsync", StringComparison.Ordinal)..
            code.IndexOf("public async Task<OperationResult> StartHelperAsync", StringComparison.Ordinal)];
        Require(!disable.Contains("ActivateFrameModeAsync") && !disable.Contains("EnsureOwnedTaskAsync"), "disable cannot reactivate frame/login tasks");
        Require(disable.Contains("s.GameAffinityRuleEnabled = false") && disable.Contains("s.GamePriorityEnabled = false") &&
            disable.Contains("s.CloseToTrayEnabled = false"), "disable does not silently resume automation");
        Require(code.Contains("GetNamedPipeClientProcessId") && code.Contains("\"START\""), "two-phase authenticated process handshake");
    }

    private sealed class SyntheticTopology(ulong allMask) : ICpuTopologyService
    {
        public Task<CpuTopology> GetTopologyAsync()
        {
            var bits = Enumerable.Range(0, 64).Where(i => (allMask & (1UL << i)) != 0).ToArray();
            var first = bits.Take(bits.Length / 2).Aggregate(0UL, (m, i) => m | (1UL << i));
            return Task.FromResult(new CpuTopology { Vendor = "fixture", CpuName = "synthetic, not physical CCD diagnosis",
                PhysicalCores = bits.Length, LogicalProcessors = bits.Length, GroupCount = 1, IsHybrid = false, PCoreCount = 0,
                ECoreCount = 0, HasHyperThreading = false, IsAmd = true, IsAmdMultiCcd = true, CcdCount = 2, AllMask = allMask,
                PCoreMask = 0, ECoreMask = 0, Ccds = [new CcdInfo { Index = 0, Group = 0, CoreCount = 1, LogicalCount = bits.Length / 2, Mask = first },
                    new CcdInfo { Index = 1, Group = 0, CoreCount = 1, LogicalCount = bits.Length - bits.Length / 2, Mask = allMask & ~first }] });
        }
        public Task<bool> IsGameRunningAsync() => throw new NotSupportedException();
        public Task<OperationResult> ApplyDualCcdSchedulingAsync(int i) => throw new NotSupportedException();
        public Task<OperationResult> RevertDualCcdSchedulingAsync() => throw new NotSupportedException();
        public Task<(bool Detected, int RemovedMb, string RawOutput)> DetectMemoryLimitAsync() => throw new NotSupportedException();
        public Task<OperationResult> RemoveMemoryLimitAsync() => throw new NotSupportedException();
        public Task<OperationResult> ExcludeCpu0FromGameAsync() => throw new NotSupportedException();
        public Task<OperationResult> RestoreGameFullCoresAsync() => throw new NotSupportedException();
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
