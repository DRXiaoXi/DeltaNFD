using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Inspection;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;
using Microsoft.Win32;

internal static class SystemSwitchChecks
{
    private static int _checks;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); _checks++; }
    private static HagsRuntimeStatus Runtime(params HagsAdapterStatus[] adapters) => new(true, adapters);

    internal static async Task RunAsync()
    {
        _checks = 0;
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-system-switch-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        try
        {
            var gpuOn = Runtime(new("GPU", true, true), new("IGPU", false, false));
            var gpuOff = Runtime(new HagsAdapterStatus("GPU", true, false));
            var hags = SystemSwitchInspection.EvaluateHags(null, gpuOn);
            Check(hags.State == BxState.On && !hags.PendingRestart && hags.Detail.Contains("系统默认"), "missing registry uses actual enabled state");
            Check(SystemSwitchInspection.EvaluateHags(null, gpuOff).State == BxState.Off, "actual disabled state");
            hags = SystemSwitchInspection.EvaluateHags(2, gpuOff);
            Check(hags.State == BxState.On && hags.PendingRestart && hags.Detail.Contains("当前已关闭"), "saved enable differs from live disabled, pending restart");
            hags = SystemSwitchInspection.EvaluateHags(1, gpuOn);
            Check(hags.State == BxState.Off && hags.PendingRestart && hags.Detail.Contains("当前已开启"), "saved disable differs from live enabled");
            Check(!SystemSwitchInspection.EvaluateHags(2, gpuOn).PendingRestart, "matching enable already active");
            Check(!SystemSwitchInspection.EvaluateHags(1, gpuOff).PendingRestart, "matching disable already active");
            Check(SystemSwitchInspection.EvaluateHags(null, Runtime(new HagsAdapterStatus("GPU", false, false))).State == BxState.NotApplicable, "unsupported is not mistaken for off");
            foreach (var runtime in new[] { Runtime(), new HagsRuntimeStatus(false, gpuOn.Adapters, "failure"),
                Runtime(new HagsAdapterStatus("GPU", null, null)), Runtime(new HagsAdapterStatus("GPU", true, null)) })
                Check(SystemSwitchInspection.EvaluateHags(2, runtime).State == BxState.Unknown, "incomplete driver read stays unknown even with configured enable");
            Check(SystemSwitchInspection.EvaluateHags(null, Runtime(new("GPU1", true, true), new("GPU2", true, false))).State == BxState.Mixed,
                "supported mixed adapters remain explicit");
            var basic = BxCatalog.Database["basic"].Single(i => i.Name == "HAGS");
            var inverse = BxCatalog.Database["pending"].Single(i => i.Name == "UpDisableHags");
            var mouse = BxCatalog.Database["basic"].Single(i => i.Name == "setsaccel");
            var reader = new BxInspection(hagsQuery: () => SystemSwitchInspection.EvaluateHags(null, gpuOn), mouseQuery: () => new(6, 10, 1));
            var states = await reader.GetItemStatesAsync([basic, inverse, mouse]);
            Check(states[basic] == BxState.On && states[inverse] == BxState.Off, "real batch reader keeps inverse HAGS entries coherent");
            Check(states[mouse] == BxState.Off, "mouse reader uses runtime, not host registry (host registry is not used by fixture)");
            reader = new BxInspection(hagsQuery: () => throw new IOException("fake"), mouseQuery: () => throw new IOException("fake"));
            states = await reader.GetItemStatesAsync([basic, mouse]);
            Check(states.Values.All(s => s == BxState.Unknown), "read failures never become optimized or disabled");
            OptimizationScan.SetItemState("basic", "HAGS", BxState.On);
            OptimizationScan.SetItemState("pending", "UpDisableHags", BxState.Off);
            DeltaNFD.Services.Backends.InspectionInvalidation.AfterCommand(DeltaNFD.Backends.BackendCommands.SystemBxApply,
                new BxApplyArgs { SectionId = "basic", Name = "HAGS", TurnOn = false });
            Check(!OptimizationScan.TryGetItemState("basic", "HAGS", out _) && !OptimizationScan.TryGetItemState("pending", "UpDisableHags", out _),
                "HKEY_LOCAL_MACHINE/HKLM aliases invalidate each other's cached state");
            OptimizationScan.SetItemState("basic", "setsaccel", BxState.On);
            DeltaNFD.Services.Backends.InspectionInvalidation.AfterCommand(DeltaNFD.Backends.BackendCommands.SystemTweaksRestore,
                new { tweak = SystemTweak.MouseAccelerationOff });
            Check(!OptimizationScan.TryGetItemState("basic", "setsaccel", out _), "deep mouse restore invalidates basic alias");

            var api = new FakeMouse();
            var writer = new MouseSettingsWriter(api);
            Check(writer.Apply(new(0, 0, 0), _ => Check(api.Writes == 0, "backup before first write")).Success && api.State == new MouseParameters(0, 0, 0), "verified immediate apply");
            Check(api.PersistedWrites == 1, "apply persists profile and notification through API");
            api = new(); writer = new(api);
            Check(!writer.Apply(new(0, 0, 0), _ => throw new IOException("save denied")).Success && api.Writes == 0, "failed backup blocks every native write");
            Check(!writer.Apply(new(-1, 0, 0)).Success && api.Writes == 0, "invalid target blocks write");
            api.ReadDenied = true;
            Check(!writer.Apply(new(0, 0, 0)).Success && api.Writes == 0, "unknown original blocks write");
            api = new() { IgnoreWrite = 1 }; writer = new(api);
            var failure = writer.Apply(new(0, 0, 0));
            Check(!failure.Success && !failure.OutcomeUnknown && api.State == new MouseParameters(6, 10, 1), "readback mismatch rolls back instead of reporting success");
            api = new() { RejectWrites = true }; writer = new(api);
            failure = writer.Apply(new(0, 0, 0));
            Check(!failure.Success && failure.OutcomeUnknown && failure.IsPartial, "failed rollback remains unconfirmed");
            api = new() { IgnoreWrite = 1 }; writer = new(api);
            failure = writer.Apply(new(0, 0, 0), rollbackProfile: () => throw new IOException("restore denied"));
            Check(!failure.Success && failure.OutcomeUnknown, "failed profile rollback is not disguised as restored");

            ResetDefaults(root);
            var registry = new FakeRegistry(); api = new(registry);
            Check(MouseSettingsMaintenance.ApplyBx(true, api, registry).Success && api.State.Acceleration == 0, "basic entry uses verified native setter");
            var bx = BxBackupStore.ReadAll();
            Check(bx.Count == 4 && bx.Any(e => e.Kind == "MouseRuntime"), "basic saves runtime and three profile originals");
            Check(MouseSettingsMaintenance.ApplyBx(true, api, registry).Success && BxBackupStore.ReadAll().Count == 4, "repeat apply preserves first originals");
            Check(MouseSettingsMaintenance.ApplyBx(false, api, registry).Success && api.State == new MouseParameters(6, 10, 1), "basic off applies documented default immediately");
            Check(MouseSettingsMaintenance.RestoreBx(bx, api, registry).Success && registry.Read("MouseSpeed").Data == "1", "global mouse restore verifies runtime and profile");
            BxBackupStore.RemoveIds(bx.Select(e => e.Id));
            Check(BxBackupStore.ReadAll().Count == 0, "successful restore can archive only its verified evidence");

            // The saved profile can differ from live values (or values can be absent).
            ResetDefaults(root); registry = new(); registry.Values["MouseSpeed"] = (0, null); api = new(registry);
            Check(MouseSettingsMaintenance.ApplyBx(true, api, registry).Success, "apply with absent registry value captures native original");
            bx = BxBackupStore.ReadAll();
            Check(MouseSettingsMaintenance.RestoreBx(bx, api, registry).Success && api.State.Acceleration == 1 && registry.Read("MouseSpeed").ValueKind == 0,
                "restore preserves missing value without losing runtime original");
            BxBackupStore.RemoveIds(bx.Select(e => e.Id));

            ResetDefaults(root); registry = new(); api = new(registry);
            var store = TweakBackupStore.Default;
            Check(MouseSettingsMaintenance.ApplyDeep(store, api, registry).Success && api.State.Acceleration == 0, "deep entry shares native setter");
            Check(store.GetAllStrict().Count == 4, "deep saves runtime and registry originals");
            Check(MouseSettingsMaintenance.ApplyBx(false, api, registry).Success, "second entry can change the setting");
            bx = BxBackupStore.ReadAll();
            Check(JsonSerializer.Deserialize<MouseParameters>(bx.Single(e => e.Kind == "MouseRuntime").Data!)!.Acceleration == 1,
                "basic alias inherits deep earliest original instead of backing up already modified state");
            Check(MouseSettingsMaintenance.RestoreBx(bx, api, registry).Success, "basic alias restoration");
            BxBackupStore.RemoveIds(bx.Select(e => e.Id));
            Check(MouseSettingsMaintenance.RestoreDeep(store, api, registry).Success && api.State.Acceleration == 1 && store.GetAllStrict().Count == 0,
                "deep restore verifies before removing originals");

            ResetDefaults(root); registry = new(); api = new(registry);
            Check(MouseSettingsMaintenance.ApplyBx(true, api, registry).Success, "basic-first setup");
            Check(MouseSettingsMaintenance.ApplyDeep(store, api, registry).Success, "deep alias setup");
            Check(JsonSerializer.Deserialize<MouseParameters>(store.GetStrict("State", "MouseAcceleration", "Runtime")!.Data)!.Acceleration == 1,
                "deep alias inherits basic earliest original");
            bx = BxBackupStore.ReadAll();
            registry.RestoreDenied = true;
            Check(!MouseSettingsMaintenance.RestoreBx(bx, api, registry).Success && BxBackupStore.ReadAll().Count == 4, "failed profile restore retains all basic evidence");
            registry.RestoreDenied = false;
            Check(MouseSettingsMaintenance.RestoreBx(bx, api, registry).Success, "profile restoration can be retried");
            BxBackupStore.RemoveIds(bx.Select(e => e.Id));
            Check(MouseSettingsMaintenance.RestoreDeep(store, api, registry).Success && api.State.Acceleration == 1, "both alias restore order preserves earliest original");

            ResetDefaults(root); registry = new(); api = new(registry) { IgnoreWrite = 1 };
            failure = MouseSettingsMaintenance.ApplyBx(true, api, registry);
            Check(!failure.Success && !failure.OutcomeUnknown && registry.Read("MouseSpeed").Data == "1" && BxBackupStore.ReadAll().Count == 4,
                "basic readback failure restores live/profile state and retains backups");

            ResetDefaults(root); registry = new(); api = new(registry);
            var legacy = registry.Values.Keys.Select(registry.Read).ToList();
            BxBackupStore.SaveMany(legacy);
            Check(MouseSettingsMaintenance.RestoreBx(legacy, api, registry).Success, "legacy complete registry-only backup restores immediately");
            try { MouseSettingsMaintenance.ReadLegacy(legacy.Take(2).ToList()); Check(false, "incomplete legacy backup accepted"); }
            catch (InvalidDataException) { Check(true, "incomplete legacy backup never guesses default values"); }
            File.WriteAllText(BxBackupStore.DefaultPath, "{");
            var writes = api.Writes;
            Check(!MouseSettingsMaintenance.ApplyBx(true, api, registry).Success && api.Writes == writes, "corrupt backup blocks new basic writes");
            ResetDefaults(root);
            registry = new(); registry.Values["MouseSpeed"] = (0, null); api = new(registry);
            Check(MouseSettingsMaintenance.ApplyDeep(store, api, registry).Success, "deep handles missing stored value with native backup");
            registry.RestoreDenied = true;
            Check(!MouseSettingsMaintenance.RestoreDeep(store, api, registry).Success && store.GetAllStrict().Count == 4, "failed deep profile restore retains all evidence");
            registry.RestoreDenied = false;
            Check(MouseSettingsMaintenance.RestoreDeep(store, api, registry).Success && api.State.Acceleration == 1 && registry.Read("MouseSpeed").ValueKind == 0,
                "deep retry restores missing value and original runtime");
            ResetDefaults(root); registry = new(); api = new(registry);
            store.SaveStrict(new RegistryValueBackup { Hive = "State", KeyPath = "MouseAcceleration", ValueName = "Runtime", Data = "{bad" });
            Check(!MouseSettingsMaintenance.ApplyDeep(store, api, registry).Success && api.Writes == 0, "corrupt runtime original blocks repeated deep apply");
            var beforeCorrupt = store.GetAllStrict().Single().Data;
            try { MouseSettingsMaintenance.RestoreDeep(store, api, registry); Check(false, "corrupt original accepted"); }
            catch (JsonException) { Check(api.Writes == 0 && store.GetAllStrict().Single().Data == beforeCorrupt, "corrupt restore evidence preserved without native write"); }
            ResetDefaults(root);
            Console.WriteLine($"System switch checks: {_checks} passed; native writes/profile updates simulated, real stores isolated. No host mouse/HAGS setting changed.");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new IOException("fixture escaped temp");
            Directory.Delete(root, true);
        }
    }

    private static void ResetDefaults(string root)
    { File.WriteAllText(BxBackupStore.DefaultPath, "{\"Entries\":[]}"); File.WriteAllText(Path.Combine(root, "backups.json"), "[]"); }

    internal static void ReadNative()
    {
        var time = Stopwatch.StartNew();
        var mouse = SystemSwitchInspection.ReadMouse();
        var runtime = SystemSwitchInspection.ReadHagsRuntime();
        var hags = SystemSwitchInspection.ReadHags();
        Console.WriteLine(JsonSerializer.Serialize(new { mouse, runtime, hags, elapsedMs = time.Elapsed.TotalMilliseconds }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static void CheckBackendMapping(string directory)
    {
        var assembly = System.Reflection.Assembly.LoadFrom(Path.Combine(directory, "DeltaNFD.Backend.SystemMaintenance.dll"));
        var type = assembly.GetTypes().Single(t => t.Name == "SystemCommands");
        foreach (var name in new[] { "Wrap", "MapBxResult" })
        {
            var method = type.GetMethod(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var unknown = new OperationResult { Success = false, IsPartial = true, OutcomeUnknown = true, Message = "fixture" };
            var mapped = (DeltaNFD.Backends.BackendResult)method.Invoke(null, ["fixture", unknown])!;
            if (!mapped.PendingRestore || !mapped.RequiresReview || !DeltaNFD.Services.Backends.BackendResults.Map(mapped, "fixture").OutcomeUnknown)
                throw new Exception("Unconfirmed mouse result lost at " + name);
            var reboot = (DeltaNFD.Backends.BackendResult)method.Invoke(null, ["fixture", OperationResult.Ok("saved", requiresReboot: true)])!;
            if (!DeltaNFD.Services.Backends.BackendResults.Map(reboot, "fixture").RequiresReboot) throw new Exception("Reboot flag lost at " + name);
        }
        Console.WriteLine("System switch backend mapping: 4 checks passed; reflection-only result conversion, no handler/system operation invoked.");
    }

    private sealed class FakeMouse(FakeRegistry? registry = null) : IMouseSettingsApi
    {
        public MouseParameters State = new(6, 10, 1);
        public int Writes, PersistedWrites, IgnoreWrite;
        public bool ReadDenied, RejectWrites;
        public MouseParameters Read() => ReadDenied ? throw new IOException("read denied") : State;
        public bool Set(MouseParameters value, bool persist)
        {
            Writes++;
            if (RejectWrites) return false;
            if (Writes == IgnoreWrite) return true;
            State = value;
            if (persist)
            {
                PersistedWrites++;
                if (registry is not null)
                {
                    registry.Values["MouseSpeed"] = ((int)RegistryValueKind.String, value.Acceleration.ToString());
                    registry.Values["MouseThreshold1"] = ((int)RegistryValueKind.String, value.Threshold1.ToString());
                    registry.Values["MouseThreshold2"] = ((int)RegistryValueKind.String, value.Threshold2.ToString());
                }
            }
            return true;
        }
    }
    private sealed class FakeRegistry : IMouseRegistryApi
    {
        public Dictionary<string, (int Kind, string? Data)> Values = new() { ["MouseSpeed"]=(1,"1"), ["MouseThreshold1"]=(1,"6"), ["MouseThreshold2"]=(1,"10") };
        public bool RestoreDenied;
        public BxBackupEntry Read(string name) => new() { Id = "Reg|HKEY_CURRENT_USER|" + MouseSettingsMaintenance.MousePath + "|" + name,
            Kind = "Reg", KeyPath = MouseSettingsMaintenance.MousePath, ValueName = name, ValueKind = Values[name].Kind, Data = Values[name].Data };
        public void Restore(BxBackupEntry entry)
        { if (RestoreDenied) throw new IOException("restore denied"); Values[entry.ValueName] = (entry.ValueKind, entry.Data); }
    }
}
