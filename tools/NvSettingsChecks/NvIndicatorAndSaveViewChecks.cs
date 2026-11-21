using DeltaNFD.Inspection;
using DeltaNFD.Services;
using DeltaNFD.ViewModels;
using Microsoft.Win32;

internal static class NvIndicatorAndSaveViewChecks
{
    private static int _checks;
    private static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException("FAIL indicator/save: " + label);
        _checks++;
        Console.WriteLine("PASS indicator/save: " + label);
    }

    internal static async Task RunAsync()
    {
        Check(NvDlssIndicatorInspection.KeyPath == @"SOFTWARE\NVIDIA Corporation\Global\NGXCore" &&
            NvDlssIndicatorInspection.ValueName == "ShowDlssIndicator" && NvDlssIndicatorInspection.ReleaseEnabled == 1024,
            "fixed NVIDIA key and documented release DWORD");
        var absent = NvDlssIndicatorInspection.Classify(null, null);
        Check(absent is { Enabled: false, CanSet: true, StoredValue: null }, "absent value in existing key is default off, not an explicit DWORD");
        Check(NvDlssIndicatorInspection.Read(null) is { Enabled: null, CanSet: false }, "missing NGXCore key is unknown and not created");
        Check(NvDlssIndicatorInspection.Classify(RegistryValueKind.DWord, 1024) is { Enabled: true, CanSet: true, StoredValue: 1024 }, "release enable stores 1024");
        Check(NvDlssIndicatorInspection.Classify(RegistryValueKind.DWord, 0) is { Enabled: false, CanSet: true, StoredValue: 0 }, "explicit disable stores zero");
        foreach (var v in new[] { 1, 2, 1025, -1 })
            Check(NvDlssIndicatorInspection.Classify(RegistryValueKind.DWord, v) is { Enabled: null, CanSet: true } other
                && other.StoredValue == unchecked((uint)v), "nonstandard/debug value not falsely asserted on or off: " + v);
        foreach (var (kind, value) in new (RegistryValueKind, object)[] { (RegistryValueKind.String, "1024"), (RegistryValueKind.QWord, 1024L), (RegistryValueKind.DWord, "1024") })
            Check(NvDlssIndicatorInspection.Classify(kind, value) is { Enabled: null, CanSet: false }, "unexpected value kind is not overwritten: " + kind);

        var writes = new List<int>();
        int? actual = null;
        NvDlssIndicatorStatus Read() => actual is int raw ? NvDlssIndicatorInspection.Classify(RegistryValueKind.DWord, raw) : absent;
        void Write(int v) { actual = v; writes.Add(v); }
        OperationResult Commit(bool enable) => NvDlssIndicatorService.Commit(enable, Read, Write, Read);
        Check(Commit(false).IsSkipped && writes.Count == 0, "default off skip does not create a DWORD");
        Check(Commit(true).Success && actual == 1024 && writes.SequenceEqual([1024]), "enable writes precisely 1024 and verifies");
        Check(Commit(true).IsSkipped && writes.Count == 1, "already-verified setting does not repeat write");
        Check(Commit(false).Success && actual == 0 && writes.SequenceEqual([1024, 0]), "disable writes precise zero and verifies");
        var denied = NvDlssIndicatorService.Commit(true, () => throw new IOException("fixture preread failure"), Write, Read);
        Check(!denied.Success && !denied.OutcomeUnknown && writes.Count == 2, "read failure before write is a definite rejection");
        denied = NvDlssIndicatorService.Commit(true, () => NvDlssIndicatorInspection.Classify(RegistryValueKind.String, "other"), Write, Read);
        Check(!denied.Success && !denied.OutcomeUnknown && writes.Count == 2, "wrong type never gets overwritten by UI request");
        var unknown = NvDlssIndicatorService.Commit(true, Read, v => { Write(v); throw new IOException("fixture interrupted"); }, Read);
        Check(!unknown.Success && unknown.OutcomeUnknown && writes.Count == 3 && actual == 1024, "throw after attempted write cannot be called a definitive failure");
        unknown = NvDlssIndicatorService.Commit(false, Read, Write, () => throw new IOException("fixture reread failure"));
        Check(!unknown.Success && unknown.OutcomeUnknown && writes.Count == 4, "readback failure cannot report success");
        unknown = NvDlssIndicatorService.Commit(true, Read, v => writes.Add(v), Read);
        Check(!unknown.Success && unknown.OutcomeUnknown && writes.Count == 5, "unexpected persisted DWORD cannot verify enable");
        var noOffDword = NvDlssIndicatorService.Commit(false, () => new(true, true, "fixture", 1024), _ => actual = null, Read);
        Check(noOffDword.OutcomeUnknown, "missing value after a requested zero write does not masquerade as verified explicit zero");

        var serviceFake = new FakeNv();
        ServiceLocator.NvProfile = serviceFake;
        var containerPage = new NvSettingsViewModel();
        Check(containerPage.CanChangeDisplayContainer && containerPage.DisplayContainerButton == "禁用并停止", "service card reads state without modifying it");
        await containerPage.ChangeDisplayContainerAsync(_ => Task.FromResult(false));
        Check(serviceFake.ContainerWrites == 0 && containerPage.CanChangeDisplayContainer, "cancel does not write");
        var confirmContainer = new TaskCompletionSource<bool>();
        var firstContainer = containerPage.ChangeDisplayContainerAsync(restore =>
        {
            Check(!restore && !containerPage.CanChangeDisplayContainer && !containerPage.CanRefresh, "disable confirmation locks other edits");
            return confirmContainer.Task;
        });
        await containerPage.ChangeDisplayContainerAsync(_ => throw new Exception("duplicate confirmation opened"));
        confirmContainer.SetResult(true);
        await firstContainer;
        Check(serviceFake.ContainerWrites == 1 && serviceFake.LastContainerChange == new NvDisplayContainerChange(false, "running-fixture") &&
            containerPage.DisplayContainerButton == "恢复原设置" && containerPage.PendingCount == 0, "verified disable switches independent card to restore");
        await containerPage.ChangeDisplayContainerAsync(restore => Task.FromResult(restore));
        Check(serviceFake.ContainerWrites == 2 && serviceFake.LastContainerChange == new NvDisplayContainerChange(true, "stopped-fixture") &&
            containerPage.DisplayContainerButton == "禁用并停止" && serviceFake.GameWrites == 0, "restore never writes to game DRS");
        serviceFake.ContainerUnknown = true;
        await containerPage.ChangeDisplayContainerAsync(_ => Task.FromResult(true));
        Check(!containerPage.CanChangeDisplayContainer && containerPage.DisplayContainerText.Contains("未确认"), "uncertain response blocks immediate retries");

        var fake = new FakeNv();
        ServiceLocator.NvProfile = fake;
        var vm = new NvSettingsViewModel();
        var power = vm.Rows.Single(r => r.Id == 0x1057EB71);
        await vm.SelectOptionAsync(power, 1);
        vm.DlssIndicatorSelectedIndex = 1;
        Check(vm.PendingCount == 1 && vm.CanSaveDlssIndicator && fake.IndicatorWrites == 0, "the global selector is only a draft, not a DRS pending item");
        fake.Locked = true; // Same DB is now read-only; refresh will discard drafts and read authoritative values.
        await vm.LoadAsync();
        await vm.SelectOptionAsync(power, 1);
        vm.DlssIndicatorSelectedIndex = 1;
        Check(!vm.ApplyEnabled && vm.CanSaveDlssIndicator && await vm.ResetAllAsync() is null,
            "DRS lock blocks game save/reset but not the independent indicator save");
        fake.IndicatorGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = vm.SaveDlssIndicatorAsync();
        var oldReads = fake.EnvironmentReads;
        await vm.LoadAsync();
        Check(!vm.CanRefresh && !vm.CanEditOptions && fake.EnvironmentReads == oldReads && await vm.SaveDlssIndicatorAsync() is null
            && await vm.SetLockAsync(false) is null && fake.IndicatorWrites == 1, "in-flight indicator save serializes UI writes and refresh");
        fake.IndicatorGate.SetResult();
        var result = await save;
        Check(result?.Success == true && fake.Indicator && vm.DlssIndicatorSelectedIndex == 1 && !vm.CanSaveDlssIndicator
            && vm.DlssIndicatorResultText.Contains("indicator receipt") && power.PendingChange && fake.GameWrites == 0,
            "indicator receipt survives readback; it doesn't submit or discard the game's draft");
        fake.Locked = false;
        await vm.LoadAsync();
        vm.DlssIndicatorSelectedIndex = 0;
        await vm.SelectOptionAsync(power, 1);
        fake.GameGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        save = vm.ApplyChangesAsync();
        await vm.LoadAsync();
        Check(vm.IsApplying && fake.GameWrites == 1 && !vm.CanRefresh && await vm.ApplyChangesAsync() is null
            && await vm.ResetAllAsync() is null && await vm.SaveDlssIndicatorAsync() is null,
            "busy game save cannot overlap another save, reset, refresh or indicator write");
        fake.GameGate.SetResult();
        result = await save;
        Check(result?.Success == true && vm.StatusText == "game receipt" && !power.PendingChange && vm.CanSaveDlssIndicator && fake.Indicator,
            "verified game save replaces actual display without erasing receipt or unrelated indicator draft");
        await vm.ResetAllAsync();
        Check(fake.GameWrites == 2 && fake.IndicatorWrites == 1 && fake.Indicator && vm.CanSaveDlssIndicator,
            "game reset affects only DRS settings; the global indicator and its draft stay separate");
        await vm.SelectOptionAsync(power, 1);
        fake.GameResult = OperationResult.Fail("known pre-save refusal");
        result = await vm.ApplyChangesAsync();
        Check(result is { Success: false, OutcomeUnknown: false } && vm.ApplyEnabled && power.PendingChange
            && vm.StatusText == "known pre-save refusal", "pre-save failure retains unsubmitted choices and previous actual values");
        fake.GameResult = new() { OutcomeUnknown = true, Message = "unknown driver receipt" };
        result = await vm.ApplyChangesAsync();
        Check(result?.OutcomeUnknown == true && power.PendingChange && !vm.ApplyEnabled && vm.CanRefresh
            && vm.StatusText == "unknown driver receipt" && await vm.ApplyChangesAsync() is null,
            "unknown game save displays the receipt, retains the draft, and prevents blind resend");
        await vm.LoadAsync();
        Check(!power.PendingChange && vm.CanEditOptions, "successful explicit refresh reestablishes authoritative game state");
        fake.GameResult = null;
        fake.NvapiAvailable = false; fake.IndicatorResult = new() { OutcomeUnknown = true, Message = "unknown indicator receipt" };
        await vm.LoadAsync();
        vm.DlssIndicatorSelectedIndex = 0;
        Check(!vm.CanEditOptions && vm.CanSaveDlssIndicator, "indicator is independent of NVAPI/DRS availability");
        result = await vm.SaveDlssIndicatorAsync();
        Check(result?.OutcomeUnknown == true && vm.DlssIndicatorResultText == "unknown indicator receipt"
            && !vm.CanSaveDlssIndicator && vm.CanRefresh && fake.Indicator, "unknown indicator save can be inspected, not blindly resent");
        fake.ThrowIndicatorRead = true;
        await vm.LoadAsync();
        Check(vm.DlssIndicatorSelectedIndex == -1 && !vm.CanEditDlssIndicator && vm.DlssIndicatorStateText.Contains("读取失败"),
            "failed read clears stale active indicator state instead of treating it as off");
        Console.WriteLine($"DLSS indicator and NVIDIA save UI fixtures passed ({_checks} assertions); no registry or DRS writes.");
    }

    private sealed class FakeNv : INvProfileService
    {
        public bool Indicator, Locked, ThrowIndicatorRead;
        public bool NvapiAvailable = true;
        public int IndicatorWrites, GameWrites, EnvironmentReads;
        public TaskCompletionSource? IndicatorGate, GameGate;
        public OperationResult? IndicatorResult, GameResult;
        private readonly Dictionary<uint, (bool, uint)> _settings = NvSettingsCatalog.Definitions.ToDictionary(d => d.Id,
            d => (false, NvDriverSettingPolicy.MissingValue(d.Id)));
        public IReadOnlyList<NvSettingDef> GetSettingCatalog() => NvSettingsCatalog.Definitions;
        public Task<NvGpuEnvironmentInfo> GetEnvironmentAsync()
        {
            EnvironmentReads++;
            return Task.FromResult(new NvGpuEnvironmentInfo { IsNvidia = true, GpuName = "fixture", NvApiAvailable = NvapiAvailable,
                DrsFilesExist = true, DrsFilesLocked = Locked, DriverVersion = 62124 });
        }
        public Task<Dictionary<uint, (bool, uint)>?> GetGameSettingValuesAsync() => Task.FromResult<Dictionary<uint, (bool, uint)>?>(new(_settings));
        public int ContainerWrites;
        public bool ContainerDisabled, ContainerUnknown;
        public NvDisplayContainerChange? LastContainerChange;
        public Task<NvDisplayContainerStatus> ReadDisplayContainerAsync() => Task.FromResult(ContainerDisabled
            ? new NvDisplayContainerStatus("fixture stopped", "stopped-fixture", true)
            : new NvDisplayContainerStatus("fixture running", "running-fixture"));
        public Task<OperationResult> ChangeDisplayContainerAsync(NvDisplayContainerChange change)
        {
            ContainerWrites++;
            LastContainerChange = change;
            if (ContainerUnknown) return Task.FromResult(new OperationResult { OutcomeUnknown = true, Message = "fixture unknown" });
            ContainerDisabled = !change.Restore;
            return Task.FromResult(OperationResult.Ok("service receipt"));
        }
        public Task<NvDlssIndicatorStatus> ReadDlssIndicatorAsync() => ThrowIndicatorRead ? throw new IOException("fixture read failure")
            : Task.FromResult(NvDlssIndicatorInspection.Classify(RegistryValueKind.DWord, Indicator ? 1024 : 0));
        public async Task<OperationResult> SetDlssIndicatorAsync(bool enabled)
        {
            IndicatorWrites++;
            if (IndicatorGate is not null) await IndicatorGate.Task;
            if (IndicatorResult is not null) return IndicatorResult;
            Indicator = enabled;
            return OperationResult.Ok("indicator receipt");
        }
        public async Task<OperationResult> ApplyGameSettingsAsync(IReadOnlyList<(uint Id, uint? Value)> changes, bool rebarWarningAccepted = false)
        {
            GameWrites++;
            if (GameGate is not null) await GameGate.Task;
            if (GameResult is not null) return GameResult;
            foreach (var (id, v) in changes) _settings[id] = (v is not null, v ?? NvDriverSettingPolicy.MissingValue(id));
            return OperationResult.Ok("game receipt");
        }
        public Task<OperationResult> SetDrsFilesReadOnlyAsync(bool readOnly) => throw new Exception("No real lock write expected.");
    }
}
