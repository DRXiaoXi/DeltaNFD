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
        internal readonly List<Vc14Baseline> Baselines = [];
        internal TaskCompletionSource? Entered, Hold;
        internal Vc14Scan? Scan = new([], [], []);
        internal Exception? ScanError;
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
        public Task<Vc14Scan> Scan14Async() => ScanError is { } error ? Task.FromException<Vc14Scan>(error) : Task.FromResult(Scan!);
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
        public Task<Vc14ReinstallPreview> Preview14RepairAsync(Vc14RepairMode mode = Vc14RepairMode.Full, bool allowTemporaryInstaller = false, Vc14Baseline baseline = Vc14Baseline.V142)
        { Baselines.Add(baseline); return Task.FromResult(new Vc14ReinstallPreview(new Vc14Scan([], [], []), [], "fixture", true, "") { Baseline = baseline }); }
        public Task<RuntimeHighFpsPreview> PreviewHighFpsAsync(bool strongMode, Vc14Baseline baseline = Vc14Baseline.V142)
        { Baselines.Add(baseline); return Task.FromResult(new RuntimeHighFpsPreview(true, "", "fixture", strongMode, []) { Baseline = baseline }); }
        public Task<Vc14ForceReplacePreview> PreviewForceReplace14Async(CancellationToken token, Vc14Baseline baseline = Vc14Baseline.V142)
        { Baselines.Add(baseline); return Task.FromResult(new Vc14ForceReplacePreview(true, "", "fixture", [], "", 0, 0, 0, 0) { Baseline = baseline }); }
        public Task<OperationResult> Repair14Async(string fingerprint, IProgress<string>? progress, CancellationToken token, Vc14RepairMode mode = Vc14RepairMode.Full, bool allowTemporaryInstaller = false, Vc14Baseline baseline = Vc14Baseline.V142)
        { Baselines.Add(baseline); return Task.FromResult(OperationResult.Ok("fixture: no system writes")); }
        public Task<OperationResult> RepairHighFpsAsync(string fingerprint, bool strongMode, IProgress<string>? progress, CancellationToken token, Vc14Baseline baseline = Vc14Baseline.V142)
        { Baselines.Add(baseline); return Task.FromResult(OperationResult.Ok("fixture: no system writes")); }
        public Task<OperationResult> ForceReplace14Async(string fingerprint, IProgress<string>? progress, CancellationToken token, Vc14Baseline baseline = Vc14Baseline.V142)
        { Baselines.Add(baseline); return Task.FromResult(OperationResult.Ok("fixture: no system writes")); }
    }

    internal static async Task RunAsync()
    {
        NvidiaDriverSeriesChecks.Run();
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
            if (i == 0) CheckObservableProperties(vm);
            Check(ReferenceEquals(guard.Owner, owner), "View model guard belongs to the current lease.");
            Check(!ReferenceEquals(vm, previous), "Reentry must not reuse a departed view model.");
            await vm.LoadGuardAsync();
            if (i == 0)
            {
                await CheckVersionAlertPolicyAsync(vm, guard);
                vm.SetRuntimePolicyChecking(true);
                Check(!vm.GuardCleanupEnabled && !vm.CanChangeRuntimeDetection, "Checking policy disables ordinary runtime controls.");
                vm.SetRuntimePolicyReason("fixture driver 596.49");
                vm.SetRuntimePolicyChecking(false);
                Check(vm.RuntimePolicyBlocked && !vm.GuardCleanupEnabled && !vm.GuardModeEnabled
                    && !vm.CanChangeRuntimeDetection && vm.CanReviewRuntimeRecovery,
                    "Active runtime policy disables ordinary controls and retains review.");
                var before = guard.Writes;
                Check(await vm.SetGuardAsync(true) is null && guard.Writes == before, "Active policy cannot enable protection.");
                guard.Enabled = true;
                await vm.LoadGuardAsync();
                Check(vm.GuardToggleEnabled && vm.GuardIsEnabled, "Existing protection can still be turned off.");
                Check((await vm.SetGuardAsync(false))?.Success == true && !vm.GuardIsEnabled,
                    "Existing protection is actually released through the isolated guard.");
                vm.SetRuntimePolicyReason(null);
                Check(vm.RuntimeOperationsAvailable && vm.GuardCleanupEnabled && vm.CanChangeRuntimeDetection,
                    "Policy withdrawal restores ordinary controls.");
                vm.SetRuntimeDriverUnknown(true);
                Check(vm.RuntimeDriverUnknown && vm.RuntimeOperationsAvailable && vm.GuardCleanupEnabled,
                    "Unreadable driver is shown as unknown and does not expand the disable condition.");
                vm.SetRuntimeDriverUnknown(false);
                Check(vm.BaselineDriverChecking && !vm.CanUseBaselineActions && vm.GuardToggleEnabled,
                    "Initial driver check disables only baseline actions, not protection.");
                vm.CompleteBaselineDriverCheck(false, false);
                vm.BaselineIndex = 1;
                Check(vm.SelectedBaseline == Vc14Baseline.V144 && vm.CanUseBaselineActions, "Non-596 can select 14.44.");
                vm.BeginBaselineDriverCheck();
                Check(!vm.CanUseBaselineActions && vm.GuardToggleEnabled, "Driver recheck does not disable protection.");
                vm.CompleteBaselineDriverCheck(true, false);
                Check(!vm.Hide144Baseline && vm.BaselineIndex == 1 && vm.SelectedBaseline == Vc14Baseline.V144,
                    "Confirmed 596 keeps a previously selected 14.44 available and selected.");
                vm.BaselineIndex = 1;
                Check(vm.BaselineIndex == 1 && vm.SelectedBaseline == Vc14Baseline.V144 && vm.ContinueInstallLabel.Contains("14.44"),
                    "596 users can select 14.44 for continuation.");
                await vm.PreviewRepairAsync(); await vm.PreviewRepairAsync(Vc14RepairMode.InstallOnly);
                await vm.PreviewHighFpsAsync(false); await vm.PreviewForceReplaceAsync(default);
                await vm.RepairAsync("fixture", default); await vm.RepairHighFpsAsync("fixture", false, default);
                await vm.ForceReplaceAsync("fixture", default);
                Check(guard.Baselines.Count == 7 && guard.Baselines.All(b => b == Vc14Baseline.V144),
                    "All actual 596 view-model preview, continuation and execution paths pass selected 14.44.");
                vm.BeginBaselineDriverCheck(); vm.CompleteBaselineDriverCheck(false, true);
                Check(!vm.Hide144Baseline && vm.BaselineDriverNotice.Contains("无法完整读取") && vm.CanUseBaselineActions,
                    "Unknown driver warns without treating it as a confirmed 596.");
                vm.BaselineIndex = 1;
                Check(vm.SelectedBaseline == Vc14Baseline.V144, "Unknown driver keeps the optional baseline available.");
                vm.BaselineIndex = 0;
            }
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
    private static async Task CheckVersionAlertPolicyAsync(RuntimeViewModel vm, Guard guard)
    {
        var before = guard.Scan;
        var writes = guard.Writes;
        var product = new Vc14Entry("{E}", "Microsoft Visual C++ v14 X64 Minimum Runtime - 14.51.36247", "14.51.36247", "x64", "Minimum", "{U}");
        var evaluated = new Vc14Scan([product], [new(product.Name, product.Version, product.Architecture, Vc14Severity.Evaluation, "待长期评估")], []);
        RuntimeDetectionPreference.Shared.SetEnabled(true);
        guard.Scan = evaluated;
        await vm.LoadGuardAsync();
        Check(vm.HasEvaluation && !vm.HasAbnormal && !vm.HasWarning && vm.EvaluationText.Contains("14.51"), "Verified evaluated version appears only in blue information state.");
        guard.Scan = evaluated with { Issues = ["注册版本与 MSI 缓存版本不一致"] };
        await vm.LoadGuardAsync();
        Check(!vm.HasAbnormal && vm.HasEvaluation && vm.HasWarning
            && vm.WarningText.Contains("不一致"), "Scan issues return to yellow while retaining the specific blue evaluation.");
        guard.Scan = evaluated;
        await vm.LoadGuardAsync();
        Check(!vm.HasAbnormal && vm.HasEvaluation, "Successful recheck clears the warning and retains evaluation information.");
        guard.ScanError = new IOException("无法读取版本");
        await vm.LoadGuardAsync();
        Check(!vm.HasAbnormal && !vm.HasEvaluation && vm.HasWarning && vm.WarningText.Contains("无法读取版本"), "Backend failure returns to yellow and clears stale blue state.");
        guard.ScanError = null; guard.Scan = null;
        await vm.LoadGuardAsync();
        Check(!vm.HasAbnormal && !vm.HasEvaluation && vm.HasWarning && vm.WarningText.Contains("未返回"), "Missing scan result returns to yellow, not a missing-runtime claim.");
        guard.Scan = evaluated; await vm.LoadGuardAsync();
        await vm.SetRuntimeDetectionEnabledAsync(false);
        Check(!vm.HasAbnormal && !vm.HasWarning && !vm.HasEvaluation, "Disabling detection clears all three alert channels.");
        guard.Scan = before;
        await vm.SetRuntimeDetectionEnabledAsync(true);
        Check(guard.Writes == writes, "Severity changes and rechecks never trigger runtime repair writes.");
    }

    private static void CheckObservableProperties(RuntimeViewModel vm)
    {
        // Exercise the actual setters without invoking a backend or running repair operations.
        var dependencies = new Dictionary<string, string[]>
        {
            [nameof(vm.RepairIsBusy)] = [nameof(vm.CanReviewRuntimeRecovery), nameof(vm.CanChangeRuntimeDetection), nameof(vm.GuardToggleEnabled), nameof(vm.GuardModeEnabled), nameof(vm.GuardCleanupEnabled), nameof(vm.CanUseBaselineActions)],
            [nameof(vm.BaselineIndex)] = [nameof(vm.SelectedBaseline), nameof(vm.SelectedBaselineLabel), nameof(vm.SelectedBaselineVersion), nameof(vm.SelectedBaselineDescription), nameof(vm.ContinueInstallLabel)],
            [nameof(vm.BaselineDriverChecking)] = [nameof(vm.CanUseBaselineActions)],
            [nameof(vm.Hide144Baseline)] = [nameof(vm.SelectedBaseline), nameof(vm.SelectedBaselineLabel), nameof(vm.SelectedBaselineVersion), nameof(vm.SelectedBaselineDescription), nameof(vm.ContinueInstallLabel)],
            [nameof(vm.AbnormalText)] = [nameof(vm.HasAbnormal)],
            [nameof(vm.WarningText)] = [nameof(vm.HasWarning)],
            [nameof(vm.EvaluationText)] = [nameof(vm.HasEvaluation)],
            [nameof(vm.ManualRepairGuidanceText)] = [nameof(vm.HasManualRepairGuidance)],
            [nameof(vm.GuardIsEnabled)] = [nameof(vm.GuardToggleText), nameof(vm.GuardToggleEnabled), nameof(vm.GuardModeEnabled)],
            [nameof(vm.GuardIsBusy)] = [nameof(vm.CanReviewRuntimeRecovery), nameof(vm.GuardToggleEnabled), nameof(vm.GuardModeEnabled), nameof(vm.GuardCleanupEnabled), nameof(vm.CanUseBaselineActions), nameof(vm.GuardToggleText)],
            [nameof(vm.GuardIsPartial)] = [nameof(vm.GuardToggleText)],
        };
        string[] names = [nameof(vm.RepairIsBusy), nameof(vm.BaselineIndex), nameof(vm.BaselineDriverChecking), nameof(vm.Hide144Baseline),
            nameof(vm.BaselineDriverNotice), nameof(vm.RepairStatusText), nameof(vm.AbnormalText), nameof(vm.AbnormalTitle),
            nameof(vm.WarningText), nameof(vm.EvaluationText), nameof(vm.DetectionStatusText), nameof(vm.ManualRepairGuidanceText), nameof(vm.InstalledText),
            nameof(vm.GuardIsEnabled), nameof(vm.GuardIsBusy), nameof(vm.GuardIsPartial), nameof(vm.GuardStatusText)];
        foreach (var name in names)
        {
            var property = typeof(RuntimeViewModel).GetProperty(name)!;
            var old = property.GetValue(vm);
            object next = old switch { bool b => !b, int => 1, _ => "fixture-property-value" };
            var events = new List<string>();
            System.ComponentModel.PropertyChangingEventHandler changing = (_, e) =>
            {
                events.Add("before:" + e.PropertyName);
                Check(Equals(property.GetValue(vm), old), name + " must still expose its old value during PropertyChanging.");
            };
            System.ComponentModel.PropertyChangedEventHandler changed = (_, e) =>
            {
                events.Add(e.PropertyName!);
                Check(Equals(property.GetValue(vm), next), name + " must expose its new value during dependent notifications.");
            };
            vm.PropertyChanging += changing; vm.PropertyChanged += changed;
            try
            {
                property.SetValue(vm, next);
                var expected = new[] { "before:" + name }.Concat(dependencies.GetValueOrDefault(name) ?? []).Append(name);
                Check(events.SequenceEqual(expected), name + " must preserve changing/hook/dependent/changed notification order.");
                events.Clear();
                property.SetValue(vm, next);
                Check(events.Count == 0, name + " same-value assignment must not notify or repeat its hook.");
            }
            finally
            {
                vm.PropertyChanging -= changing; vm.PropertyChanged -= changed;
                property.SetValue(vm, old);
            }
            Check(Equals(property.GetValue(vm), old), name + " must restore the original value.");
        }
    }
}
