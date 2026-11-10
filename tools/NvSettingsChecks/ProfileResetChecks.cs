using System.Reflection;
using System.Runtime.InteropServices;
using DeltaNFD.Native;
using DeltaNFD.Services;
using DeltaNFD.Backends;
using DeltaNFD.ViewModels;

internal static class ProfileResetChecks
{
    private static int _checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception("FAIL profile reset: " + message); _checks++; Console.WriteLine("PASS profile reset: " + message); }

    private static NvApiDrs.ProfileResetSnapshot Snapshot(bool user = true, bool predefined = true)
    {
        var settings = Enumerable.Range(0, user ? 5 : 0).Select(i => new NvDrsSetting
        {
            settingId = (uint)(0x70000000 + i), settingName = "unknown-to-catalog-" + i,
            settingType = i, settingLocation = 0, isCurrentPredefined = 0, isPredefinedValid = 1,
            // Identical buffers: this must STILL count as a user override, including explicit DWORD zero.
            currentValue = new byte[4100], predefinedValue = new byte[4100],
        }).ToArray();
        if (user) { BitConverter.TryWriteBytes(settings[1].currentValue.AsSpan(), 4096u);
            BitConverter.TryWriteBytes(settings[1].predefinedValue.AsSpan(), 4096u);
            settings[1].currentValue[4099] = 123; settings[3].currentValue[4000] = 88; }
        return new(true, new NvDrsProfile { profileName = "Delta Force", isPredefined = predefined ? 1u : 0u,
            numOfApps = 1, numOfSettings = (uint)settings.Length },
            [new NvDrsApplication { appName = "DeltaForceClient-Win64-Shipping.exe", isPredefined = predefined ? 1u : 0u,
                userFriendlyName = "Delta Force", fileInFolder = "", launcher = "", commandLine = "" }], settings);
    }

    public static async Task RunAsync()
    {
        var before = Snapshot(); var clean = Snapshot(false);
        Check(before.UserSettingCount == 5, "all raw setting types and equal-to-default overrides counted");
        var bytes = NvApiDrs.SerializeProfileBackup(before);
        var restored = System.Text.Json.JsonSerializer.Deserialize<NvApiDrs.ProfileResetSnapshot>(bytes, NvApiDrs.ProfileBackupJson)!;
        Check(restored.Settings.Length == 5 && restored.Settings[1].currentValue[4099] == 123
            && restored.Settings[3].currentValue[4000] == 88 && restored.Applications[0].appName == before.Applications[0].appName,
            "backup preserves hidden IDs, raw unions of every type, application and profile metadata");
        int backup = 0, reset = 0, save = 0, reads = 0;
        OperationResult Run(Func<bool>? gate = null, Action<byte[]>? persist = null,
            Func<NvApiDrs.ProfileResetSnapshot>? fresh = null, Func<int>? resetFn = null, Func<int>? saveFn = null,
            NvApiDrs.ProfileResetSnapshot? input = null)
        {
            backup = reset = save = reads = 0;
            return NvApiDrs.CommitProfileReset(input ?? before, persist ?? (_ => backup++), gate ?? (() => true),
                () => { reads++; return fresh is not null ? fresh() : save > 0 ? clean : input ?? before; },
                () => { reset++; return resetFn?.Invoke() ?? 0; }, () => { save++; return saveFn?.Invoke() ?? 0; });
        }
        var result = Run();
        Check(result.Success && !result.OutcomeUnknown && backup == 1 && reset == 1 && save == 1 && reads == 3,
            "backup, two conflict checks, official single-profile reset, save and fresh verification");
        result = Run(gate: () => false);
        Check(!result.Success && !result.OutcomeUnknown && backup + reset + save == 0, "running game or readonly database prevents backup and reset");
        result = Run(persist: _ => throw new IOException("disk full"));
        Check(!result.Success && !result.OutcomeUnknown && reset + save == 0, "backup failure cannot write GPU");
        result = Run(fresh: () => clean);
        Check(!result.Success && !result.OutcomeUnknown && backup == 1 && reset + save == 0, "configuration changed after backup cannot reset");
        var gates = 0; result = Run(gate: () => ++gates != 2);
        Check(!result.Success && !result.OutcomeUnknown && reset + save == 0, "game starting after backup prevents reset");
        gates = 0; result = Run(gate: () => ++gates != 3);
        Check(!result.Success && !result.OutcomeUnknown && reset == 1 && save == 0, "game starting before save discards in-memory reset");
        result = Run(fresh: () => reads == 2 ? clean : before);
        Check(!result.Success && !result.OutcomeUnknown && reset == 1 && save == 0, "concurrent setting changes before save are not overwritten");
        result = Run(resetFn: () => -1);
        Check(!result.Success && !result.OutcomeUnknown && save == 0, "API reset rejection never saves partial memory state");
        result = Run(saveFn: () => -1);
        Check(!result.Success && result.OutcomeUnknown && save == 1, "save failure retains unknown result");
        result = Run(saveFn: () => throw new IOException("driver lost"));
        Check(!result.Success && result.OutcomeUnknown, "save exception retains unknown result");
        result = Run(fresh: () => save == 1 ? throw new IOException("reload failure") : before);
        Check(!result.Success && result.OutcomeUnknown, "fresh read failure after save is not successful");
        result = Run(fresh: () => before);
        Check(!result.Success && result.OutcomeUnknown, "hidden user overrides remaining after save prevent success");
        var absent = new NvApiDrs.ProfileResetSnapshot(false, default, [], []);
        result = Run(input: absent);
        Check(result.Success && result.IsSkipped && backup + reset + save == 0, "missing game profile is a safe skip without creating one");
        result = Run(fresh: () => save > 0 ? absent : before);
        Check(!result.Success && result.OutcomeUnknown, "lost predefined application association is not mistaken for default");
        var custom = Snapshot(predefined: false);
        result = Run(input: custom, fresh: () => save > 0 ? absent : custom, resetFn: () => -169);
        Check(result.Success && save == 1, "official PROFILE_REMOVED success followed by absent custom profile is verified");
        result = Run(input: custom, fresh: () => save > 0 ? Snapshot(false, false) : custom);
        Check(!result.Success && result.OutcomeUnknown, "user-created profile surviving complete reset is unconfirmed");
        Check(BackendModules.IsAllowed(BackendModules.Gpu, BackendCommands.GpuDriverProfileReset)
            && !BackendModules.IsReadOnly(BackendModules.Gpu, BackendCommands.GpuDriverProfileReset)
            && !BackendModules.IsAllowed(BackendModules.Ace, BackendCommands.GpuDriverProfileReset), "GPU-only mutation command requires write intent");

        NativeChecks();
        await ViewModelChecks();
        Console.WriteLine($"TOTAL {_checks} profile reset checks passed; simulated driver only, no host GPU reads or writes.");
    }

    private static void NativeChecks()
    {
        using var native = new NativeFixture();
        var backedUp = 0;
        var result = NvApiDrs.ResetDeltaProfile(_ => backedUp++, () => true);
        Check(result.Success && native.ResetCalls == 1 && native.SaveCalls == 1 && backedUp == 1,
            "actual native adapter enumerates complete profile and clears unknown IDs");
        Check(native.Created >= 4 && native.Destroyed == native.Created,
            "verification uses new loaded sessions and destroys every session");
        native.Scenario = "driver-alias"; native.Database = Snapshot();
        result = NvApiDrs.ResetDeltaProfile(_ => { }, () => true);
        Check(result.Success, "driver-owned game aliases do not depend on editable profile title");
        foreach (var scenario in new[] { "reported-extra", "reported-short", "identical-duplicates", "grow-settings" })
        {
            native.Scenario = scenario; native.Database = Snapshot(); native.ResetCalls = native.SaveCalls = 0;
            byte[]? captured = null;
            result = NvApiDrs.ResetDeltaProfile(bytes => captured = bytes, () => true);
            Check(result.Success && native.ResetCalls == 1 && native.SaveCalls == 1 && captured is not null,
                "batch enumeration accepts stable " + scenario + " before normal transactional verification");
            var original = System.Text.Json.JsonSerializer.Deserialize<NvApiDrs.ProfileResetSnapshot>(captured!, NvApiDrs.ProfileBackupJson)!;
            Check(original.Settings.Single(s => s.settingType == 1).currentValue[4099] == 123 && original.Settings.Any(s => s.settingType == 3 && s.currentValue[4000] == 88),
                "batch backup preserves binary tail and wide strings for " + scenario);
            if (scenario == "identical-duplicates")
                Check(original.Settings.Length == 6 && original.UserSettingCount == 5, "identical duplicate raw records kept, user count uses unique settings");
            if (scenario == "grow-settings")
                Check(original.Settings.Length == 21 && native.LargestCapacity >= 32, "a full first array is reread with more room instead of accepting truncation");
        }
        foreach (var scenario in new[] { "global", "base", "metadata", "short-settings", "overflow-settings", "duplicate-settings",
            "conflicting-current", "conflicting-predefined", "conflicting-source", "count-zero", "limit-settings",
            "short-apps", "shared-app", "shared-user-app", "foreign-driver-profile", "bad-setting", "binary-overflow", "unsupported" })
        {
            native.Scenario = scenario; native.Database = Snapshot(); native.ResetCalls = native.SaveCalls = 0; backedUp = 0;
            result = NvApiDrs.ResetDeltaProfile(_ => backedUp++, () => true);
            Check(!result.Success && !result.OutcomeUnknown && native.ResetCalls + native.SaveCalls + backedUp == 0,
                "actual native reader rejects " + scenario + " before backup or reset");
        }
        native.Scenario = "none"; native.Database = new(false, default, [], []);
        result = NvApiDrs.ResetDeltaProfile(_ => throw new Exception("no backup expected"), () => true);
        Check(result.IsSkipped && native.SaveCalls == 0, "actual missing-profile path does not create or rename anything");
    }

    private sealed class NativeFixture : IDisposable
    {
        private readonly Dictionary<FieldInfo, object?> _original = [];
        private readonly Dictionary<IntPtr, NvApiDrs.ProfileResetSnapshot> _sessions = [];
        public NvApiDrs.ProfileResetSnapshot Database = Snapshot();
        public string Scenario = "none";
        public int ResetCalls, SaveCalls, Created, Destroyed, LargestCapacity;
        public NativeFixture()
        {
            Set("_initialized", true); Set("_available", true);
            foreach (var pair in new Dictionary<string, string> { ["_createSession"] = nameof(Create), ["_destroySession"] = nameof(Destroy),
                ["_loadSettings"] = nameof(Load), ["_saveSettings"] = nameof(Save), ["_findApp"] = nameof(Find),
                ["_getProfileInfo"] = nameof(Info), ["_getBaseProfile"] = nameof(Base), ["_getCurrentGlobalProfile"] = nameof(Global),
                ["_restoreProfile"] = nameof(Reset), ["_enumProfileSettings"] = nameof(Settings), ["_enumProfileApps"] = nameof(Apps) })
            {
                var f = typeof(NvApiDrs).GetField(pair.Key, BindingFlags.Static | BindingFlags.NonPublic)!;
                Set(pair.Key, Delegate.CreateDelegate(f.FieldType, this, GetType().GetMethod(pair.Value, BindingFlags.Instance | BindingFlags.NonPublic)!));
            }
        }
        private void Set(string name, object value)
        { var f = typeof(NvApiDrs).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!; _original.TryAdd(f, f.GetValue(null)); f.SetValue(null, value); }
        public void Dispose() { foreach (var (f, value) in _original) f.SetValue(null, value); }
        private int Create(ref IntPtr session) { session = new(++Created + 100); return 0; }
        private int Destroy(IntPtr session) { _sessions.Remove(session); Destroyed++; return 0; }
        private int Load(IntPtr session) { _sessions[session] = Database; return 0; }
        private int Save(IntPtr session) { SaveCalls++; Database = _sessions[session]; return 0; }
        private int Base(IntPtr session, ref IntPtr profile) { profile = new(1); return 0; }
        private int Global(IntPtr session, ref IntPtr profile) { profile = new(2); return 0; }
        private int Find(IntPtr session, string name, ref IntPtr profile, ref NvDrsApplication app)
        { profile = new(Scenario == "global" ? 2 : Scenario == "base" ? 1 : 42); return _sessions[session].Present ? 0 : -166; }
        private int Info(IntPtr session, IntPtr profile, ref NvDrsProfile info)
        {
            var ver = info.version; info = _sessions[session].Profile; info.version = ver;
            if (Scenario == "metadata") info.numOfSettings = 4097;
            if (info.numOfSettings > 0 && Scenario == "reported-extra") info.numOfSettings += 3;
            if (info.numOfSettings > 0 && Scenario == "reported-short") info.numOfSettings--;
            if (info.numOfSettings > 0 && Scenario == "grow-settings") info.numOfSettings = 1;
            if (Scenario is "driver-alias" or "shared-user-app" or "foreign-driver-profile")
            { info.numOfApps = 2; info.profileName = "Renamed game profile"; }
            return 0;
        }
        private int Reset(IntPtr session, IntPtr profile) { ResetCalls++; _sessions[session] = Snapshot(false); return 0; }
        private int Settings(IntPtr session, IntPtr profile, uint index, ref uint count, IntPtr buffer)
        {
            if (index != 0) throw new Exception("Settings enumeration must start from zero; one-item pagination repeats entries on real drivers.");
            var values = _sessions[session].Settings.ToArray();
            var capacity = count; LargestCapacity = Math.Max(LargestCapacity, (int)capacity);
            var stride = Marshal.SizeOf<NvDrsSetting>();
            for (var i = 0; i < capacity; i++)
            {
                var initial = Marshal.PtrToStructure<NvDrsSetting>(buffer + i * stride);
                if (initial.version != (uint)(stride | (1 << 16)) || BitConverter.ToUInt32(initial.currentValue, 0) != 4096
                    || BitConverter.ToUInt32(initial.predefinedValue, 0) != 4096)
                    throw new Exception("Every binary array element requires a version and capacity.");
            }
            if (Scenario == "unsupported") return -9;
            if (Scenario == "short-settings") return -1;
            if (Scenario == "overflow-settings") { count = capacity + 1; return 0; }
            if (Scenario == "limit-settings") { count = 4097; return 0; }
            if (Scenario == "count-zero") { count = 0; return 0; }
            if (values.Length > 0 && Scenario == "grow-settings")
                values = values.Concat(Enumerable.Range(0, 16).Select(i => { var s = values[0]; s.settingId = (uint)(0x71000000 + i); return s; })).ToArray();
            if (values.Length > 0 && Scenario is "identical-duplicates" or "conflicting-current" or "conflicting-predefined" or "conflicting-source")
            {
                var duplicate = values[0];
                duplicate.currentValue = duplicate.currentValue.ToArray(); duplicate.predefinedValue = duplicate.predefinedValue.ToArray();
                if (Scenario == "conflicting-current") duplicate.currentValue[4099] ^= 1;
                if (Scenario == "conflicting-predefined") duplicate.predefinedValue[4099] ^= 1;
                if (Scenario == "conflicting-source") duplicate.isCurrentPredefined ^= 1;
                values = values.Append(duplicate).ToArray();
            }
            count = Math.Min(capacity, (uint)values.Length);
            for (var i = 0; i < count; i++)
            {
                var setting = values[i]; setting.version = (uint)(stride | (1 << 16));
                if (Scenario == "duplicate-settings") setting.settingId = 1;
                if (Scenario == "bad-setting") setting.isCurrentPredefined = 7;
                if (Scenario == "binary-overflow" && setting.settingType == 1)
                {
                    setting.currentValue = setting.currentValue.ToArray();
                    BitConverter.TryWriteBytes(setting.currentValue.AsSpan(), 4097u);
                }
                Marshal.StructureToPtr(setting, buffer + i * stride, false);
            }
            return 0;
        }
        private int Apps(IntPtr session, IntPtr profile, uint index, ref uint count, ref NvDrsApplication app)
        {
            if (Scenario == "short-apps") return -7;
            if (index > 0)
            {
                if (index > 1 || Scenario is not ("driver-alias" or "shared-user-app" or "foreign-driver-profile")) return -7;
                app.appName = "DriverAlias.exe"; app.isPredefined = Scenario == "shared-user-app" ? 0u : 1u;
                return 0;
            }
            var version = app.version; app = _sessions[session].Applications[0]; app.version = version;
            if (Scenario == "shared-app") app.appName = "OtherGame.exe";
            if (Scenario == "foreign-driver-profile") app.isPredefined = 0;
            return 0;
        }
    }

    private static async Task ViewModelChecks()
    {
        var fake = new ResetFake(); ServiceLocator.NvProfile = fake;
        var vm = new NvSettingsViewModel();
        Check(vm.CanResetProfile && fake.Calls == 0, "opening N card page does not run reset");
        vm.Rows[0].SelectedIndex = 2; vm.OnRowSelectionChanged();
        var pending = vm.PendingCount;
        await vm.ResetProfileForFlickerAsync(() => Task.FromResult(false), () => true);
        Check(fake.Calls == 0 && vm.PendingCount == pending && !vm.IsApplying, "cancel preserves pending settings without backend writes");
        await vm.ResetProfileForFlickerAsync(() => Task.FromResult(true), () => false);
        Check(fake.Calls == 0, "inactive page cannot submit reset");
        var active = true;
        await vm.ResetProfileForFlickerAsync(() => { active = false; return Task.FromResult(true); }, () => active);
        Check(fake.Calls == 0, "leaving during confirmation cannot submit reset");
        vm.LockBusy = true;
        Check(!vm.CanResetProfile && await vm.ResetProfileForFlickerAsync(() => throw new Exception(), () => true) is null,
            "database lock operation disables reset admission");
        vm.LockBusy = false; fake.Locked = true; await vm.LoadAsync();
        Check(!vm.CanResetProfile && await vm.ResetProfileForFlickerAsync(() => throw new Exception(), () => true) is null,
            "readonly database cannot begin reset");
        fake.Locked = false; await vm.LoadAsync();
        var waiter = new TaskCompletionSource<bool>();
        var task = vm.ResetProfileForFlickerAsync(() => waiter.Task, () => true);
        Check(vm.IsApplying && !vm.CanResetProfile && !vm.CanEditOptions, "confirmation occupies mutation slot");
        Check(await vm.ResetProfileForFlickerAsync(() => throw new Exception(), () => true) is null, "double click cannot submit second reset");
        Check(await vm.SetLockAsync(true) is null, "database attributes cannot change while confirmation is active");
        waiter.SetResult(true); var result = await task;
        Check(result is { Success: true } && fake.Calls == 1 && vm.StatusText == "fixture reset verified" && vm.PendingCount == 0 && !vm.IsApplying,
            "successful reset refreshes rows, clears pending choices and preserves receipt");
        fake.Unknown = true; result = await vm.ResetProfileForFlickerAsync(() => Task.FromResult(true), () => true);
        Check(result is { Success: false, OutcomeUnknown: true } && vm.StatusText == "fixture unknown", "unknown backend result remains visible");
        fake.Throw = true; result = await vm.ResetProfileForFlickerAsync(() => Task.FromResult(true), () => true);
        Check(result is { Success: false, OutcomeUnknown: true } && !vm.IsApplying, "transport exception after submission remains unknown and releases UI");
        Check(vm.Rows.All(r => r.StateText == "未确认，待核对"), "transport exception clears stale displayed driver values");
        result = await vm.ResetProfileForFlickerAsync(() => throw new IOException("dialog failure"), () => true);
        Check(result is { Success: false, OutcomeUnknown: false } && !vm.IsApplying, "dialog failure does not claim driver changes");
    }
    private sealed class ResetFake : INvProfileService
    {
        public int Calls; public bool Locked, Unknown, Throw;
        public Task<OperationResult> ResetDeltaProfileAsync()
        { Calls++; if (Throw) throw new IOException("fixture disconnected"); return Task.FromResult(Unknown
            ? new OperationResult { Success = false, OutcomeUnknown = true, Message = "fixture unknown" } : OperationResult.Ok("fixture reset verified")); }
        public Task<NvGpuEnvironmentInfo> GetEnvironmentAsync() => Task.FromResult(new NvGpuEnvironmentInfo
        { IsNvidia = true, GpuName = "fixture", NvApiAvailable = true, DrsFilesExist = true, DrsFilesLocked = Locked, DriverVersion = 62114 });
        public IReadOnlyList<NvSettingDef> GetSettingCatalog() => NvSettingsCatalog.Definitions;
        public Task<Dictionary<uint, (bool HasOverride, uint Value)>?> GetGameSettingValuesAsync()
            => Task.FromResult<Dictionary<uint, (bool, uint)>?>(NvSettingsCatalog.Definitions.ToDictionary(d => d.Id, _ => (false, 0u)));
        public Task<OperationResult> ApplyGameSettingsAsync(IReadOnlyList<(uint Id, uint? Value)> changes, bool rebarWarningAccepted = false) => throw new Exception("unexpected managed-only write");
        public Task<OperationResult> SetDrsFilesReadOnlyAsync(bool readOnly) => throw new Exception("unexpected database attribute write");
    }
}
