using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.ViewModels;

// Compile the actual view model and lease implementation. Only system execution is replaced.
internal static class RuntimePageSessionChecks
{
    private static int _checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); _checks++; }
    private static async Task Rejected(Func<Task> action)
    {
        try { await action(); }
        catch (PageBackendUnavailableException) { _checks++; return; }
        throw new Exception("Departed or suspended page admitted an operation.");
    }

    private sealed class Guard : IRuntimeGuardService, IPageBackendProcess
    {
        internal readonly PageBackendLease Owner = PageBackendSessions.ConstructionOwner
            ?? throw new Exception("Guard was constructed outside the runtime page.");
        private readonly SemaphoreSlim _gate = PageBackendSessions.ModuleGate(BackendModules.Runtime);
        internal int Writes, Stops;
        internal bool Enabled;
        internal RuntimeGuardMode Mode;
        internal TaskCompletionSource? Entered, Hold;
        public string ModuleId => BackendModules.Runtime;
        public bool ExitConfirmed { get; private set; }
        public bool RequiresAttention { get; set; }
        public Task StopForPageAsync() { Stops++; ExitConfirmed = true; return Task.CompletedTask; }
        public Task<RuntimeGuardStatus> GetStatusAsync() => Task.FromResult(new RuntimeGuardStatus
        {
            InstalledRedists = [], IfeoApplied = Enabled, IfeoCount = Enabled ? 3 : 0,
            IfeoTotal = 3, IfeoManagedCount = Enabled ? 3 : 0, ScopeManagedCount = Enabled ? 3 : 0,
            Ue4PrereqFound = false, Ue4PrereqDenied = false, Mode = Mode
        });
        public Task<Vc14Scan> Scan14Async() => Task.FromResult(new Vc14Scan([], [], []));
        private async Task<OperationResult> Apply(Action action)
        {
            await PageBackendSessions.WaitGateAsync(_gate, Owner, default, ModuleId);
            try
            {
                Owner.Attach(this);
                using var admission = Owner.BeginCommand(this);
                Entered?.TrySetResult();
                if (Hold is { } hold) await hold.Task;
                action(); Writes++;
                return OperationResult.Ok("Isolated guard operation completed.");
            }
            finally { _gate.Release(); }
        }
        public Task<OperationResult> EnableAsync() => Apply(() => Enabled = true);
        public Task<OperationResult> DisableAsync() => Apply(() => Enabled = false);
        public Task<OperationResult> SelectModeAsync(RuntimeGuardMode mode) => Apply(() => Mode = mode);
        public Task<OperationResult> CleanupLegacyIfeoAsync(IReadOnlyList<RuntimeGuardLegacyEntry> entries) => Apply(() => Enabled = false);
        public Task<OperationResult> RepairLegacyUe4AclAsync(string path) => throw new NotSupportedException();
    }

    internal static async Task RunAsync()
    {
        _checks = 0;
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-runtime-page-session-" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var guards = new List<Guard>();
        ServiceLocator.RuntimeGuardFactory = () => { var guard = new Guard(); guards.Add(guard); return guard; };
        var manager = PageBackendSessions.Manager;
        manager.Navigate("dashboard"); manager.ObserveDelta(false);
        await Rejected(() => { _ = RuntimeViewModel.Current; return Task.CompletedTask; });
        Check(guards.Count == 0, "Getting a view model outside its page must not create a guard.");
        RuntimeViewModel? previous = null;
        Guard? previousGuard = null;
        for (var i = 0; i < 10; i++)
        {
            manager.Navigate("runtime");
            var vm = RuntimeViewModel.Current; var owner = manager.Current!; var guard = guards[^1];
            Check(ReferenceEquals(vm, RuntimeViewModel.Current), "Same page lease reuses its view model.");
            Check(ReferenceEquals(guard.Owner, owner), "View model guard belongs to the current lease.");
            Check(!ReferenceEquals(vm, previous), "Reentry must not reuse a departed view model.");
            await vm.LoadGuardAsync();
            Check((await vm.SetGuardAsync(true))?.Success == true && vm.GuardIsEnabled, "Reentered page can enable protection.");
            Check((await vm.SetGuardAsync(false))?.Success == true && !vm.GuardIsEnabled, "Reentered page can disable protection.");
            Check((await vm.ChangeGuardModeAsync(1))?.Success == true && vm.GuardMode == RuntimeGuardMode.Full, "Reentry can select protection mode.");
            Check((await vm.ForceCleanupGuardAsync())?.Success == true, "Reentry can explicitly clear protection.");
            if (previous is not null)
            {
                var writes = previousGuard!.Writes;
                Check((await previous.SetGuardAsync(true))?.Success == false, "Late old-page callback must remain rejected.");
                Check(previousGuard.Writes == writes && !vm.GuardIsEnabled, "Old callback cannot write through the new lease.");
            }
            manager.Navigate("dashboard");
            await owner.Stopping.WaitAsync(TimeSpan.FromSeconds(3));
            Check(guard.Stops == 1 && guard.ExitConfirmed, "Departure stops the page worker once.");
            previous = vm; previousGuard = guard;
        }

        manager.Navigate("runtime");
        var active = RuntimeViewModel.Current; var activeGuard = guards[^1]; var activeOwner = manager.Current!;
        await active.LoadGuardAsync();
        foreach (bool? running in new bool?[] { true, null })
        {
            manager.ObserveDelta(running); await activeOwner.Stopping.WaitAsync(TimeSpan.FromSeconds(3));
            var writes = activeGuard.Writes;
            Check((await active.SetGuardAsync(true))?.Success == false && activeGuard.Writes == writes, "Game/unknown state still blocks execution.");
            manager.ObserveDelta(false);
            Check(ReferenceEquals(active, RuntimeViewModel.Current) && activeGuard.Writes == writes, "Game exit resumes admission without replaying a write.");
        }

        activeGuard.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        activeGuard.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = active.SetGuardAsync(true);
        await activeGuard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        manager.Navigate("dashboard"); manager.Navigate("runtime");
        var fresh = RuntimeViewModel.Current; var freshGuard = guards[^1]; var freshOwner = manager.Current!;
        await fresh.LoadGuardAsync();
        var freshWrite = fresh.SetGuardAsync(true);
        Check(!activeOwner.Stopping.IsCompleted && !freshWrite.IsCompleted && freshGuard.Writes == 0, "New page waits for the already submitted old operation.");
        activeGuard.Hold.SetResult();
        Check((await inFlight.WaitAsync(TimeSpan.FromSeconds(3)))?.Success == true, "Submitted old request completes normally.");
        Check((await freshWrite.WaitAsync(TimeSpan.FromSeconds(3)))?.Success == true && !active.GuardIsBusy && !fresh.GuardIsBusy, "Draining and separate view-model busy states complete.");
        await activeOwner.Stopping.WaitAsync(TimeSpan.FromSeconds(3));
        Check(activeGuard.Stops == 1 && freshGuard.Stops == 0, "Old worker exit never closes the new worker.");
        var gate = PageBackendSessions.ModuleGate(BackendModules.Runtime);
        await gate.WaitAsync();
        var queued = fresh.SetGuardAsync(false);
        manager.Navigate("dashboard");
        Check((await queued.WaitAsync(TimeSpan.FromSeconds(3)))?.Success == false && freshGuard.Writes == 1, "Departure cancels queued operations without replay.");
        gate.Release(); await freshOwner.Stopping.WaitAsync(TimeSpan.FromSeconds(3));
        Check(BackendSessions.Snapshot().Count == 0, "Fixture starts no real backend process.");
        Console.WriteLine($"Runtime page session checks passed ({_checks}); actual view model and lease, isolated guard, no system writes.");
    }
}
