using System.Reflection;
using DeltaNFD.Native;
using DeltaNFD.Services;

internal static class NvApplicationSaveChecks
{
    private static readonly string Exe = DeltaForceLocator.GameProcessName + ".exe";
    private const uint V1 = NvDriverSettingPolicy.RebarId, V2 = NvDriverSettingPolicy.RebarPolicyId, Power = 0x1057EB71;

    internal static void Run()
    {
        var checks = 0;
        void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("FAIL DRS save: " + label);
            checks++;
            Console.WriteLine("PASS DRS save: " + label);
        }
        using var f = new Fixture();
        OperationResult Apply(params (uint Id, uint? Value)[] changes) =>
            NvApiDrs.ApplyApplicationSettings(Exe, changes, 62124, false, () => true);
        var data = NvApiDrs.GetApplicationSettings(Exe, [V1, V2])!;
        Check(data[V1] == (false, NvDriverSettingPolicy.RebarV1NotSpecified) && data[V2] == (false, 1u),
            "missing V1 is not Off; missing V2 is Auto");
        Check(f.Saves == 0 && f.NameLookups == 0 && f.AppCreates == 0 && f.ActiveSessions == 0 && f.Database.Name == "DeltaOptimizer DeltaForce",
            "reading a bound legacy profile never renames, saves, or attaches an application");
        f.Reset(); f.Database.Values[V1] = 0; f.Database.Values[V2] = 2;
        var result = Apply((V1, 1));
        Check(result.Success && f.Database.Values[V1] == 1 && f.Database.Values[V2] == 1
            && f.StageIds.SequenceEqual([V2, V1]), "native save switches from forced V2 to effective V1 (not V2 Off)");
        Check(f.Saves == 1 && f.Trace.SequenceEqual(["create 101", "load 101", "save 101", "destroy 101", "create 102", "load 102", "destroy 102"])
            && f.ActiveSessions == 0, "written session is destroyed, a new session reloads the DB and re-resolves the EXE");
        f.Reset(); f.Database.Values[V1] = 1;
        result = Apply((V2, 0));
        Check(result.Success && f.Database.Values[V1] == 1 && f.Database.Values[V2] == 0 && f.StageIds.SequenceEqual([V2]),
            "V2 Off clearly means override V1 without deleting its selection");
        f.Reset();
        result = NvApiDrs.ApplyApplicationSettings(Exe, [(V1, 1)], 61655, true, () => true);
        Check(result.Success && f.StageIds.SequenceEqual([V1]) && !f.Database.Values.ContainsKey(V2),
            "legacy driver and missing V2 can save V1 without writing the unsupported V2 ID");
        f.Reset(); f.Database.Values[V2] = 2;
        result = NvApiDrs.ApplyApplicationSettings(Exe, [(V1, 1)], 61655, true, () => true);
        Check(!result.Success && !result.OutcomeUnknown && f.Saves == 0 && f.StageIds.Count == 0 && f.Database.Values[V2] == 2,
            "older driver never writes implicit V2 Auto if V2 is explicitly different");
        f.Reset(); f.Database.AppName = null; f.Database.InheritedV2 = 0;
        data = NvApiDrs.GetApplicationSettings(Exe, [V1, V2])!;
        Check(data[V2] == (false, 0u) && f.NameLookups == 0 && f.Saves == 0, "real inherited V2 Off is different from absence");
        result = Apply((V1, null), (V2, null));
        Check(result.IsSkipped && f.Saves == 0 && f.AppCreates == 0 && f.NameLookups == 0, "unbound target has no managed overrides to delete and is not attached on restore");
        result = Apply((V1, 1));
        Check(result.Success && f.AppCreates == 1 && f.Saves == 1 && f.Database.AppName == Exe && f.Database.Values[V2] == 1,
            "genuine missing app is associated with an empty owned profile and verified with the new session");
        f.Reset(); f.Database.AppName = null; f.Database.Exists = false;
        result = Apply((Power, 5));
        Check(result.Success && f.ProfileCreates == 1 && f.AppCreates == 1 && f.Database.AppName == Exe && f.Database.Values[Power] == 5,
            "new profile must acquire and keep the actual EXE association");
        foreach (var (scenario, unbound) in new[] { ("find-error", false), ("find-ambiguous-profile", false),
            ("wrong-app", false), ("global-handle", false), ("named-error", true), ("occupied-name", true),
            ("forged-name", true), ("create-app-error", true), ("app-not-linked", true), ("app-wrong-profile", true), ("set-error", false) })
        {
            f.Reset(scenario);
            if (unbound) f.Database.AppName = null;
            result = Apply((Power, 5));
            Check(!result.Success && !result.OutcomeUnknown && f.Saves == 0 && !f.Database.Values.ContainsKey(Power)
                && f.ActiveSessions == 0, "known pre-save refusal is not a saved result: " + scenario);
            if (scenario is "find-error" or "find-ambiguous-profile")
            {
                Check(f.NameLookups == 0 && f.ProfileCreates == 0, "query error never falls back to a profile name: " + scenario);
                try { NvApiDrs.GetApplicationSettings(Exe, [V2]); throw new Exception("Read unexpectedly accepted a query error."); }
                catch (InvalidOperationException ex) { Check(ex.Message.Contains("查询目标 EXE"), "read does not present query error as no override: " + scenario); }
            }
        }
        f.Reset("create-app-error"); f.Database.Exists = false; f.Database.AppName = null;
        result = Apply((Power, 5));
        Check(!result.Success && f.Saves == 0 && f.ProfileCreates == 1 && f.AppCreates == 1 && !f.Database.Exists,
            "failed first-time EXE registration discards the new profile rather than saving an orphan");
        f.Reset(); var finalGateCalls = 0;
        result = NvApiDrs.ApplyApplicationSettings(Exe, [(Power, 5)], 62124, false, () => { finalGateCalls++; return false; });
        Check(!result.Success && !result.OutcomeUnknown && finalGateCalls == 1 && f.Saves == 0 && f.StageIds.SequenceEqual([Power]),
            "recheck of write eligibility happens before the only DRS save");
        foreach (var scenario in new[] { "save-error", "save-throws", "fresh-load-error", "lost-binding", "fresh-setting-error", "fresh-mismatch" })
        {
            f.Reset(scenario);
            result = Apply((Power, 5));
            Check(!result.Success && result.OutcomeUnknown && f.Saves == 1 && f.ActiveSessions == 0,
                "save was attempted once and only a known verified success can be reported: " + scenario);
        }
        const string custom = @"C:\fixture\Games\DeltaTarget.exe";
        f.Reset(); f.Database.Name = GameTargetService.CustomProfileName(custom); f.Database.AppName = custom.ToUpperInvariant();
        result = NvApiDrs.ApplyApplicationSettings(custom, [(V1, 1), (V2, 1)], 62124, false, () => true);
        Check(result.Success && f.Database.Values[V1] == 1 && f.Database.Values[V2] == 1,
            "custom exact full-path profile uses V1 + Auto just like the standard target");
        foreach (var registered in new[] { "DeltaTarget.exe", @"C:\fixture\Other\DeltaTarget.exe" })
        {
            f.Reset(); f.Database.Name = GameTargetService.CustomProfileName(custom); f.Database.AppName = registered;
            result = NvApiDrs.ApplyApplicationSettings(custom, [(Power, 5)], 62124, false, () => true);
            Check(!result.Success && !result.OutcomeUnknown && f.ProfileCreates + f.AppCreates + f.Saves == 0,
                "an existing custom basename or other-directory match is never reused or changed: " + registered);
        }
        Console.WriteLine($"NVIDIA DRS save fixtures passed ({checks} assertions); no NVAPI calls on the host.");
    }

    private sealed class Profile
    {
        public string Name = "DeltaOptimizer DeltaForce";
        public string? AppName = Exe;
        public bool Exists = true;
        public uint? InheritedV2;
        public Dictionary<uint, uint> Values = [];
        public Profile Copy() => new() { Name = Name, AppName = AppName, Exists = Exists, InheritedV2 = InheritedV2, Values = new(Values) };
    }

    // Swap only the private native delegates. Every load copies persisted fixture data and uses different handles.
    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<FieldInfo, object?> _old = [];
        private readonly Dictionary<IntPtr, Profile> _sessions = [];
        private int _lastHandle = 100;
        private string _scenario = "";
        public Profile Database = new();
        public int Saves, NameLookups, ProfileCreates, AppCreates;
        public int ActiveSessions => _sessions.Count;
        public List<string> Trace { get; } = [];
        public List<uint> StageIds { get; } = [];

        public Fixture()
        {
            Swap("_initialized", true); Swap("_available", true);
            foreach (var (field, method) in new (string, string)[]
            {
                ("_createSession", nameof(Create)), ("_destroySession", nameof(Destroy)), ("_loadSettings", nameof(Load)), ("_saveSettings", nameof(Save)),
                ("_findApp", nameof(FindApp)), ("_findProfile", nameof(FindProfile)), ("_createProfile", nameof(CreateProfile)), ("_createApp", nameof(CreateApp)),
                ("_getProfileInfo", nameof(Info)), ("_getBaseProfile", nameof(Base)), ("_getCurrentGlobalProfile", nameof(Global)),
                ("_getSetting", nameof(Get)), ("_setSetting", nameof(Set)), ("_deleteSetting", nameof(Delete)),
            })
            {
                var native = Field(field);
                Swap(field, Delegate.CreateDelegate(native.FieldType, this, GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!));
            }
        }
        private static FieldInfo Field(string name) => typeof(NvApiDrs).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
        private void Swap(string name, object value) { var f = Field(name); _old.Add(f, f.GetValue(null)); f.SetValue(null, value); }
        public void Dispose() { foreach (var (field, old) in _old) field.SetValue(null, old); }
        public void Reset(string scenario = "")
        {
            if (_sessions.Count != 0) throw new InvalidOperationException("Fixture session leaked.");
            _scenario = scenario; _lastHandle = 100; Database = new(); Saves = NameLookups = ProfileCreates = AppCreates = 0;
            Trace.Clear(); StageIds.Clear();
        }
        private static IntPtr AppHandle(IntPtr session) => new(session.ToInt32() + 1000);
        private int Create(ref IntPtr session) { session = new(++_lastHandle); Trace.Add("create " + session); return 0; }
        private int Destroy(IntPtr session) { Trace.Add("destroy " + session); _sessions.Remove(session); return 0; }
        private int Load(IntPtr session)
        {
            Trace.Add("load " + session);
            if (_scenario == "fresh-load-error" && Saves > 0) return -1;
            _sessions.Add(session, Database.Copy()); return 0;
        }
        private int Save(IntPtr session)
        {
            Trace.Add("save " + session); Saves++;
            if (_scenario == "save-error") return -1;
            if (_scenario == "save-throws") throw new IOException("fixture save interruption");
            Database = _sessions[session].Copy(); return 0;
        }
        private int Base(IntPtr session, ref IntPtr profile) { profile = new(1); return 0; }
        private int Global(IntPtr session, ref IntPtr profile) { profile = new(2); return 0; }
        private int FindApp(IntPtr session, string name, ref IntPtr profile, ref NvDrsApplication app)
        {
            var state = _sessions[session];
            if (_scenario == "find-error") return -1;
            if (_scenario == "find-ambiguous-profile") return -163;
            if (state.AppName is null || _scenario == "lost-binding" && Saves > 0) return -166;
            app.appName = _scenario == "wrong-app" ? "Other.exe" : state.AppName;
            profile = _scenario switch { "global-handle" => new IntPtr(2), "app-wrong-profile" => new IntPtr(9999), _ => AppHandle(session) };
            return 0;
        }
        private int FindProfile(IntPtr session, string name, ref IntPtr profile)
        {
            NameLookups++;
            if (_scenario == "named-error") return -1;
            var state = _sessions[session];
            if (!state.Exists || state.Name != name) return -163;
            profile = AppHandle(session); return 0;
        }
        private int Info(IntPtr session, IntPtr profile, ref NvDrsProfile info)
        {
            if (profile != AppHandle(session)) return -1;
            var state = _sessions[session]; info.profileName = state.Name;
            info.isPredefined = _scenario == "forged-name" ? 1u : 0u;
            info.numOfApps = state.AppName is not null || _scenario == "occupied-name" ? 1u : 0u;
            return 0;
        }
        private int CreateProfile(IntPtr session, ref NvDrsProfile info, ref IntPtr profile)
        { ProfileCreates++; _sessions[session] = new() { Name = info.profileName, AppName = null }; profile = AppHandle(session); return 0; }
        private int CreateApp(IntPtr session, IntPtr profile, ref NvDrsApplication application)
        {
            AppCreates++;
            if (_scenario == "create-app-error") return -1;
            if (_scenario != "app-not-linked") _sessions[session].AppName = application.appName;
            return 0;
        }
        private int Get(IntPtr session, IntPtr profile, uint id, ref NvDrsSetting setting, ref uint reserved)
        {
            if (profile != AppHandle(session) && profile != new IntPtr(2)) throw new InvalidOperationException("Bad fixture profile handle.");
            if (_scenario == "fresh-setting-error" && Saves > 0) return -1;
            var state = _sessions[session];
            if (profile == AppHandle(session) && state.Values.TryGetValue(id, out var value))
            {
                BitConverter.TryWriteBytes(setting.currentValue, _scenario == "fresh-mismatch" && Saves > 0 ? value + 1 : value);
                return 0;
            }
            if (id == V2 && state.InheritedV2 is uint inherited)
            { setting.settingLocation = 1; BitConverter.TryWriteBytes(setting.currentValue, inherited); return 0; }
            return -160;
        }
        private int Set(IntPtr session, IntPtr profile, ref NvDrsSetting setting, uint reserved1, uint reserved2)
        {
            StageIds.Add(setting.settingId);
            if (_scenario == "set-error" || profile != AppHandle(session)) return -1;
            _sessions[session].Values[setting.settingId] = BitConverter.ToUInt32(setting.currentValue, 0); return 0;
        }
        private int Delete(IntPtr session, IntPtr profile, uint id)
        { StageIds.Add(id); return _sessions[session].Values.Remove(id) ? 0 : -160; }
    }
}
