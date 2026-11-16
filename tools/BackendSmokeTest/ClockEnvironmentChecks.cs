using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

internal static class ClockEnvironmentChecks
{
    private const string Entry = "{11111111-1111-1111-1111-111111111111}";
    private const string Parent = "{22222222-2222-2222-2222-222222222222}";
    private const string Sid = "S-1-5-21-111-222-333-1001";
    private static readonly string Machine = new('A', 64), Identity = new('B', 64), Inheritance = new('C', 64);
    private static int _checks;
    private static void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); _checks++; }
    private static void Throws(Action action, string detail)
    { try { action(); } catch (Exception) { _checks++; return; } throw new Exception(detail); }
    private sealed class World : IClockEnvironment
    {
        public ClockEnvironmentSystem State = new(Machine, "AuthenticAMD", "1", new(Entry, "yes", "no", Inheritance, false),
            [new(@"ACPI\PNP0103\0", Identity, "HPET", 0x2000, 0)], true, "");
        public string DynamicTick = "yes";
        public string? Block;
        public int Writes, Reads;
        public int FailWrite;
        public List<string> Calls = [];
        public bool FailAfterWrite, IgnoreWrite, ForbidSourceWrites;
        public Action<int>? AfterRead;
        public Func<int, bool>? BeforeWrite;
        public string? Blocker(bool restoring) => Block;
        public ClockEnvironmentSystem Read() { Reads++; AfterRead?.Invoke(Reads); return State; }
        public void WriteSource(string entry, string key, string value)
        {
            if (ForbidSourceWrites) throw new Exception("BCD writes are forbidden in device-only checks");
            Check(key is "useplatformclock" or "useplatformtick", "only two source fields can be written");
            Check(entry == Entry, "write bound to exact boot object");
            Calls.Add("bcd:" + key + "=" + value);
            Write(() => State = State with { Bcd = key == "useplatformclock" ? State.Bcd! with { Clock = value } : State.Bcd! with { Tick = value } });
        }
        public void SetDevice(string id, string identity, bool disabled)
        {
            Check(ClockEnvironmentPolicy.ValidDevice(id) && identity == Identity, "device operation uses exact known HPET identity");
            Calls.Add("device:" + (disabled ? "disable:" : "enable:") + id);
            Write(() => State = State with { Devices = State.Devices.Select(d => d.InstanceId == id ? d with { Problem = disabled ? 22u : 0u } : d).ToArray() });
        }
        private void Write(Action write)
        {
            Writes++; BeforeWrite?.Invoke(Writes);
            if (Writes == FailWrite && !FailAfterWrite) throw new IOException("fixture denied");
            if (!IgnoreWrite) write();
            if (Writes == FailWrite && FailAfterWrite) throw new IOException("fixture outcome unknown");
        }
    }
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFDClockEnvironmentChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ParserChecks(); ContractChecks(); PolicyChecks(); DeviceOnlyChecks(root);
            var index = 0;
            (World W, ClockEnvironmentStore S, ClockEnvironmentEngine E) Make()
            {
                var world = new World(); var store = new ClockEnvironmentStore(Path.Combine(root, (++index).ToString()), Machine, Sid);
                return (world, store, new(store, world));
            }
            OperationResult Apply((World W, ClockEnvironmentStore S, ClockEnvironmentEngine E) f, ClockEnvironmentAction action) => f.E.Change(new(action, f.E.Read().Fingerprint, true));
            foreach (var action in new[] { ClockEnvironmentAction.RestoreDefaultSource, ClockEnvironmentAction.DisableHpet })
            {
                var f = Make(); var before = f.W.State;
                Check(Apply(f, action).Success, action + " succeeds");
                Check(f.W.DynamicTick == "yes", action + " retains dynamic tick");
                var b = f.S.Read().Single();
                Check(b.Phase == "Applied" && b.Attempted && b.Steps.All(s => s.Verified), "durable per-field result");
                Check(f.E.Read().PendingRestart, "same boot remains pending after success");
                Check(!Apply(f, action).Success, "reapply never replaces live originals");
                f.W.State = f.W.State with { BootId = "2" };
                Check(!f.E.Read().PendingRestart && !f.E.Read().Incomplete, "new boot and matching state confirms");
                var restore = action == ClockEnvironmentAction.DisableHpet ? ClockEnvironmentAction.RestoreHpet : ClockEnvironmentAction.RestoreSource;
                Check(Apply(f, restore).Success, "restore original succeeds");
                Check(f.S.Read().Single().Restored && f.E.Read().PendingRestart, "restored evidence retained and restart required");
                Check(f.W.State.Bcd == before.Bcd && JsonSerializer.Serialize(f.W.State.Devices) == JsonSerializer.Serialize(before.Devices), "restore exactly reproduces original state");
                Check(f.W.DynamicTick == "yes", "restore never writes dynamic tick");
                var revision = f.E.Read().Revision;
                f.W.State = action == ClockEnvironmentAction.RestoreDefaultSource
                    ? f.W.State with { BootId = "3", Bcd = f.W.State.Bcd! with { Tick = "yes" } }
                    : f.W.State with { BootId = "3", Devices = [f.W.State.Devices[0] with { Problem = 10 }] };
                var history = f.E.Read();
                Check(!history.Incomplete && !history.PendingRestart, "restored historical evidence does not own later external changes");
                Check(history.Revision == revision, "restored evidence retains proof invalidation without perpetual ownership");
            }
            foreach (var value in new[] { "", "yes", "no" })
            {
                var f = Make(); f.W.State = f.W.State with { Bcd = f.W.State.Bcd! with { Tick = value } };
                Check(Apply(f, ClockEnvironmentAction.RestoreDefaultSource).Success, "all valid BCD values including absence");
                var saved = f.S.Read().Single();
                Check(saved.Steps.Single(s => s.Key == "useplatformtick").Original == value, "absence distinct from explicit no");
                f.W.State = f.W.State with { BootId = "2" };
                Check(Apply(f, ClockEnvironmentAction.RestoreSource).Success && f.W.State.Bcd!.Tick == value, "restore preserves absence/no/yes");
            }
            {
                var f = Make(); f.W.State = f.W.State with { Devices = [.. f.W.State.Devices, new(@"ACPI\PNP0103\1", Identity, "already disabled", 0x2000, 22)] };
                Check(Apply(f, ClockEnvironmentAction.DisableHpet).Success
                    && f.S.Read().Single().Steps.Count(s => !ClockEnvironmentPolicy.IsBcdKey(s.Key)) == 2
                    && f.W.Calls.Count(c => c.StartsWith("device:")) == 1, "pre-disabled device recorded without submitting a device write");
                f.W.State = f.W.State with { BootId = "2" };
                Check(Apply(f, ClockEnvironmentAction.RestoreHpet).Success && f.W.State.Devices.Single(d => d.InstanceId.EndsWith("1")).Disabled, "restore never enables pre-disabled device");
            }
            foreach (var bad in new[] { "intel", "vendor", "boot", "unreadable", "non-disableable", "problem", "none", "inherited" })
            {
                var f = Make(); var state = f.W.State;
                f.W.State = bad switch
                {
                    "intel" => state with { CpuVendor = "GenuineIntel" }, "vendor" => state with { CpuVendor = "" }, "boot" => state with { BootId = "" },
                    "unreadable" => state with { DevicesReadable = false }, "non-disableable" => state with { Devices = [state.Devices[0] with { Status = 0 }] },
                    "problem" => state with { Devices = [state.Devices[0] with { Problem = 10 }] }, "none" => state with { Devices = [] },
                    "inherited" => state with { Bcd = state.Bcd! with { InheritedOverride = true } }, _ => state,
                };
                var action = bad == "inherited" ? ClockEnvironmentAction.RestoreDefaultSource : ClockEnvironmentAction.DisableHpet;
                Check(!Apply(f, action).Success && f.W.Writes == 0 && f.S.Read().Count == 0, bad + " cannot acquire write authority");
            }
            {
                var f = Make(); var status = f.E.Read(); f.W.State = f.W.State with { Bcd = f.W.State.Bcd! with { Tick = "yes" } };
                Check(!f.E.Change(new(ClockEnvironmentAction.RestoreDefaultSource, status.Fingerprint, true)).Success && f.W.Writes == 0, "stale frontend fingerprint rejected");
                status = f.E.Read(); Check(!f.E.Change(new(ClockEnvironmentAction.DisableHpet, status.Fingerprint, false)).Success, "explicit confirmation required");
                Check(!f.E.Change(new((ClockEnvironmentAction)99, status.Fingerprint, true)).Success && f.W.Writes == 0, "unknown action rejected");
            }
            {
                var f = Make(); f.S.BeforeSaveForTests = () => throw new IOException("fixture disk denied");
                Check(!Apply(f, ClockEnvironmentAction.DisableHpet).Success && f.W.Writes == 0, "backup failure prevents hardware write");
            }
            {
                var f = Make(); f.W.Block = "fixture game running";
                Check(!Apply(f, ClockEnvironmentAction.DisableHpet).Success && f.W.Writes == 0, "game/frame/recovery blocker prevents write");
            }
            {
                var f = Make(); var selected = f.E.Read();
                f.W.AfterRead = count => { if (count == 3) f.W.State = f.W.State with { Devices = [f.W.State.Devices[0] with { Identity = new('D', 64) }] }; };
                Check(!f.E.Change(new(ClockEnvironmentAction.DisableHpet, selected.Fingerprint, true)).Success && f.W.Writes == 0,
                    "device identity changing after journaling prevents submission");
            }
            {
                var f = Make(); using var cancellation = new CancellationTokenSource();
                f.W.BeforeWrite = _ => { cancellation.Cancel(); return true; };
                var result = f.E.Change(new(ClockEnvironmentAction.RestoreDefaultSource, f.E.Read().Fingerprint, true), cancellation.Token);
                Check(!result.Success && result.IsPartial && f.S.Read().Single().Attempted, "cancellation after entering write retains recovery responsibility");
                Check(f.W.Writes == 1 && f.W.DynamicTick == "yes", "cancelled batch stops later writes without touching dynamic tick");
                f.W.BeforeWrite = null;
                Check(Apply(f, ClockEnvironmentAction.RestoreSource).Success, "cancelled batch can be explicitly rolled back");
            }
            foreach (var after in new[] { false, true })
            {
                var f = Make(); f.W.FailWrite = 2; f.W.FailAfterWrite = after;
                var r = Apply(f, ClockEnvironmentAction.RestoreDefaultSource);
                Check(!r.Success && r.OutcomeUnknown && r.RequiresReboot && f.S.Read().Single().Phase == "Writing", "interrupted batch retains original and write attempt");
                Check(f.E.Read().Incomplete && f.E.Read().CanRestoreSource, "interruption permits trusted rollback");
                f.W.FailWrite = 0;
                Check(Apply(f, ClockEnvironmentAction.RestoreSource).Success && f.W.State.Bcd!.Clock == "yes" && f.W.State.Bcd.Tick == "no", "partial/unknown batch restores both originals");
            }
            {
                var f = Make(); f.W.IgnoreWrite = true;
                Check(!Apply(f, ClockEnvironmentAction.DisableHpet).Success && !f.S.Read().Single().Settled, "exit success without actual change is not success");
                f.W.IgnoreWrite = false; Check(Apply(f, ClockEnvironmentAction.RestoreHpet).Success, "no-op rollback can settle an attempted change");
            }
            foreach (var change in new[] { "identity", "missing", "abnormal", "machine", "user" })
            {
                var f = Make(); Check(Apply(f, ClockEnvironmentAction.DisableHpet).Success, "prepare restore identity fixture");
                var state = f.W.State with { BootId = "2" };
                f.W.State = change switch
                {
                    "identity" => state with { Devices = [state.Devices[0] with { Identity = new('D', 64) }] },
                    "missing" => state with { Devices = [] }, "abnormal" => state with { Devices = [state.Devices[0] with { Problem = 10 }] },
                    "machine" => state with { Machine = new('D', 64) }, _ => state,
                };
                var engine = change == "user" ? new ClockEnvironmentEngine(new(f.SDirectory(root, index), Machine, "S-1-5-21-444-555-666-1002"), f.W) : f.E;
                var count = f.W.Writes; var status = engine.Read();
                Check(!engine.Change(new(ClockEnvironmentAction.RestoreHpet, status.Fingerprint, true)).Success && f.W.Writes == count, change + " does not authorize stale restore");
            }
            {
                var f = Make(); Apply(f, ClockEnvironmentAction.RestoreDefaultSource); f.W.State = f.W.State with { BootId = "2", Bcd = f.W.State.Bcd! with { Tick = "yes" } };
                Check(!f.E.Read().CanRestoreSource && !Apply(f, ClockEnvironmentAction.RestoreSource).Success, "external source edit preserved rather than overwritten");
            }
            {
                var f = Make(); Apply(f, ClockEnvironmentAction.RestoreDefaultSource); f.W.State = f.W.State with { BootId = "2", CpuVendor = "GenuineIntel" };
                Check(Apply(f, ClockEnvironmentAction.RestoreSource).Success, "restore remains available when AMD admission no longer holds");
            }
            {
                var f = Make(); using var read1 = f.S.Lease(false); using var read2 = f.S.Lease(false);
                Check(!Apply(f, ClockEnvironmentAction.DisableHpet).Success && f.W.Writes == 0, "active readers block system change");
            }
            {
                var f = Make(); using var writer = f.S.Lease(true);
                Throws(() => f.S.Lease(false).Dispose(), "writer blocks scan/hold admission");
            }
            {
                var f = Make(); var directory = Path.Combine(root, index.ToString()); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "hpet.json"), "{");
                Throws(() => f.S.Read(), "corrupt original never treated as absent");
                Check(!f.E.Change(new(ClockEnvironmentAction.DisableHpet, new('A', 64), true)).Success && f.W.Writes == 0, "corrupt record prevents new operation");
            }
            {
                var f = Make(); Apply(f, ClockEnvironmentAction.DisableHpet); f.W.State = f.W.State with { BootId = "2" }; Apply(f, ClockEnvironmentAction.RestoreHpet);
                f.W.State = f.W.State with { BootId = "3" };
                var prior = f.S.Read().Single().Digest; var writes = f.W.Writes;
                f.S.BeforeSaveForTests = () => throw new IOException("fixture new-cycle disk failure after archive");
                Check(!Apply(f, ClockEnvironmentAction.DisableHpet).Success && f.W.Writes == writes && f.S.Read().Single().Digest == prior,
                    "failed new cycle preserves previous restored record after archive");
                f.S.BeforeSaveForTests = null;
                Check(Apply(f, ClockEnvironmentAction.DisableHpet).Success, "new explicit cycle retries matching archive after disk failure");
                Check(Directory.GetFiles(Path.Combine(root, index.ToString(), "archive")).Length == 1, "prior originals retained in archive");
            }
            SourceBoundaryChecks();
            Console.WriteLine($"Clock environment checks passed ({_checks} assertions). No real BCD/device/timer writes.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static string SDirectory(this (World W, ClockEnvironmentStore S, ClockEnvironmentEngine E) fixture, string root, int index) => Path.Combine(root, index.ToString());
    private static void ParserChecks()
    {
        var normal = ClockEnvironmentInspection.ParseBcd("identifier " + Entry + "\nuseplatformclock Yes\nuseplatformtick No\n");
        Check(normal.Id == Entry && normal.Clock == "yes" && normal.Tick == "no", "parse stable GUID and normalized boolean values");
        var explicitFalse = ClockEnvironmentInspection.ParseBcd("identifier " + Entry + "\nuseplatformclock False\n");
        Check(explicitFalse.Clock == "no" && explicitFalse.Tick == "", "false is normalized to no while missing tick remains absent");
        Check(WindowsClockEnvironment.SourceArguments(Entry, "useplatformclock", "no").SequenceEqual(["/set", Entry, "useplatformclock", "false"]),
            "real command explicitly submits useplatformclock false against the selected GUID");
        Check(WindowsClockEnvironment.SourceArguments(Entry, "useplatformtick", "").SequenceEqual(["/deletevalue", Entry, "useplatformtick"]),
            "real command removes useplatformtick rather than setting false");
        Check(WindowsClockEnvironment.SourceArguments(Entry, "useplatformclock", "yes").SequenceEqual(["/set", Entry, "useplatformclock", "true"]),
            "restoring an original yes submits the equivalent true boolean");
        Throws(() => WindowsClockEnvironment.SourceArguments(Entry, "other", "no"), "command builder refuses unrelated keys");
        foreach (var text in new[] { "access denied", "identifier {current}", "identifier " + Entry + "\nuseplatformclock maybe", "identifier " + Entry + "\nuseplatformclock Yes\nuseplatformclock No" })
            Throws(() => ClockEnvironmentInspection.ParseBcd(text), "bad BCD is never default");
        var query = new Dictionary<string, string> { ["{current}"] = "identifier " + Entry + "\ninherit " + Parent + "\nuseplatformclock Yes", [Parent] = "identifier " + Parent + "\nuseplatformtick Yes" };
        var parsed = ClockEnvironmentInspection.ReadBcd(id => query[id]);
        Check(parsed.InheritedOverride && parsed.Clock == "yes", "inherited source override prevents false automatic-source promise");
        query[Parent] = "identifier " + Parent + "\ninherit " + Entry;
        Throws(() => ClockEnvironmentInspection.ReadBcd(id => query[id]), "cyclic BCD inheritance rejected");
        Check(ClockEnvironmentPolicy.ValidDevice(@"ACPI\PNP0103\0") && !ClockEnvironmentPolicy.ValidDevice(@"PCI\PNP0103\0")
            && !ClockEnvironmentPolicy.ValidDevice(@"ACPI\PNP0103\0 & command") && !ClockEnvironmentPolicy.ValidDevice(@"ACPI\PNP0103\0\other"), "exact HPET identifier whitelist");
    }
    private static void ContractChecks()
    {
        var value = new BackendReady(Convert.ToBase64String(new byte[32]), BackendModules.System, "0.9400.0", 0, 123, 1,
            Path.GetFullPath("fixture.exe"), new('a', 64), ClockEnvironmentVersion: 4);
        var element = JsonSerializer.SerializeToElement(value, BackendJson.SerializerOptions);
        Check(BackendMessages.ParseReady(element).ClockEnvironmentVersion == ClockEnvironmentPolicy.Version, "device-only capability four round-trips");
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(element.GetRawText())!; fields.Remove("clockEnvironmentVersion");
        Check(BackendMessages.ParseReady(JsonSerializer.SerializeToElement(fields)).ClockEnvironmentVersion == 0, "legacy backend missing capability is zero");
        fields["clockEnvironmentVersion"] = JsonSerializer.SerializeToElement(1);
        Check(BackendMessages.ParseReady(JsonSerializer.SerializeToElement(fields)).ClockEnvironmentVersion != ClockEnvironmentPolicy.Version,
            "device-only legacy capability is recognized but cannot authorize current requests");
        fields["clockEnvironmentVersion"] = JsonSerializer.SerializeToElement(2);
        Check(BackendMessages.ParseReady(JsonSerializer.SerializeToElement(fields)).ClockEnvironmentVersion != ClockEnvironmentPolicy.Version,
            "old delete-both backend is recognized but cannot authorize current requests");
        fields["clockEnvironmentVersion"] = JsonSerializer.SerializeToElement(3);
        Check(BackendMessages.ParseReady(JsonSerializer.SerializeToElement(fields)).ClockEnvironmentVersion != ClockEnvironmentPolicy.Version,
            "explicit-false backend cannot authorize device-only requests");
        foreach (var invalid in new[] { "5", "-1", "null", "true", "\"1\"" })
        { fields["clockEnvironmentVersion"] = JsonDocument.Parse(invalid).RootElement.Clone(); Throws(() => BackendMessages.ParseReady(JsonSerializer.SerializeToElement(fields)), "invalid capability rejected"); }
        Check(BackendModules.IsReadOnly(BackendModules.System, BackendCommands.SystemClockEnvironmentRead), "query is read-only");
        Check(BackendModules.RequiresWriteIntent(BackendModules.System, BackendCommands.SystemClockEnvironmentChange)
            && BackendModules.RetainsStaticBackup(BackendModules.System, BackendCommands.SystemClockEnvironmentChange), "change uses intent and persistent backup contract");
    }
    private static void PolicyChecks()
    {
        var global = new PendulumGlobalStatus { Supported = true, Readable = true, Configured = true, BootConfirmed = true, ClockEnvironmentRevision = "changed" };
        var automatic = new AppSettings { OptimalClockScanUtc = DateTimeOffset.UtcNow, OptimalClockResolution100ns = 6000 };
        Check(!PendulumFramePolicy.AutomaticPrepared(global, automatic), "old automatic proof cannot authorize changed environment");
        automatic.OptimalClockScanEnvironment = "changed"; Check(PendulumFramePolicy.AutomaticPrepared(global, automatic), "fresh proof matches environment");
        var custom = new AppSettings { OptimalClockUseCustomResolution = true, OptimalClockCustomResolution100ns = 6000,
            OptimalClockCustomVerifiedUtc = DateTimeOffset.UtcNow, OptimalClockCustomPlatform = PendulumFramePolicy.CustomSource(global) };
        Check(!PendulumFramePolicy.Prepared(global, custom), "old custom proof invalidated without erasing value");
        custom.OptimalClockCustomEnvironment = "changed"; Check(PendulumFramePolicy.Prepared(global, custom), "reverified custom value accepted");
        Check(!PendulumFramePolicy.Prepared(global with { PendingRecovery = true }, custom), "pending restart/recovery prevents saved-value admission");
    }
    private static void SourceBoundaryChecks()
    {
        var root = Directory.GetCurrentDirectory();
        foreach (var path in new[] { "src/DeltaNFD.Execution/Execution/ClockEnvironmentService.cs", "src/DeltaNFD.Inspection/ClockEnvironmentInspection.cs",
            "src/DeltaNFD.Shared/Services/ClockEnvironmentStore.cs", "src/DeltaNFD/Views/OptimalClockPage.Environment.cs" })
        {
            var text = File.ReadAllText(Path.Combine(root, path));
            Check(!text.Contains("disabledynamictick", StringComparison.OrdinalIgnoreCase), "new feature has no dynamic-tick write/restore key: " + path);
        }
        var client = File.ReadAllText(Path.Combine(root, "src/DeltaNFD/Services/Backends/BackendClient.cs"));
        Check(client.IndexOf("backend.clock-environment-unsupported", StringComparison.Ordinal) < client.IndexOf("var invoke = new BackendInvoke(command, argsElement)", StringComparison.Ordinal), "old-backend refusal precedes intent and sending");
        var ui = File.ReadAllText(Path.Combine(root, "src/DeltaNFD/Views/OptimalClockPage.xaml"));
        Check(ui.Contains("DefaultClockSource_Click") && ui.Contains("RestoreClockSource_Click") && ui.Contains("DisableHpetDevice_Click")
            && ui.Contains("RestoreHpetDevice_Click") && ui.Contains("动态节拍保持原样") && ui.Contains("备份并禁用 HPET 设备") && ui.Contains("不修改 BCD"), "device-only HPET and separate source actions are visible");
    }

    private static void DeviceOnlyChecks(string root)
    {
        var index = 0;
        (World W, ClockEnvironmentStore S, ClockEnvironmentEngine E) Make()
        {
            var world = new World { ForbidSourceWrites = true };
            var store = new ClockEnvironmentStore(Path.Combine(root, "device-" + (++index)), Machine, Sid);
            return (world, store, new(store, world));
        }
        OperationResult Apply((World W, ClockEnvironmentStore S, ClockEnvironmentEngine E) f, ClockEnvironmentAction action)
            => f.E.Change(new(action, f.E.Read().Fingerprint, true));
        foreach (var clock in new[] { "", "yes", "no" })
        foreach (var tick in new[] { "", "yes", "no" })
        foreach (var inherited in new[] { false, true })
        {
            var f = Make(); f.W.State = f.W.State with { Bcd = f.W.State.Bcd! with { Clock = clock, Tick = tick, InheritedOverride = inherited } };
            var original = f.W.State;
            f.W.BeforeWrite = _ =>
            {
                var saved = f.S.Read().Single();
                Check(saved is { Schema: 4, HasSource: false, EntryId: "", Inheritance: "" }
                    && saved.Steps.Count == 1 && saved.Steps[0] is { Original: "enabled", Applied: "disabled", Attempted: true },
                    "device originals persist before submission without any BCD ownership");
                return true;
            };
            Check(f.E.Read().CanDisableHpet && Apply(f, ClockEnvironmentAction.DisableHpet).Success,
                "device-only apply accepts every absent/yes/no and inherited BCD combination");
            Check(f.W.State.Bcd == original.Bcd && f.W.State.Devices[0].Disabled && f.W.Writes == 1,
                "apply changes only the HPET device");
            f.W.BeforeWrite = null;
            f.W.State = f.W.State with { BootId = "2", Bcd = new(Parent, "yes", "yes", new('D', 64), true) };
            var changed = f.W.State.Bcd;
            Check(!f.E.Read().Incomplete && !f.E.Read().PendingRestart && f.E.Read().Message.Contains("HPET 设备已禁用"),
                "device target is independent of current BCD values and identity");
            Check(Apply(f, ClockEnvironmentAction.RestoreHpet).Success && !f.W.State.Devices[0].Disabled && f.W.State.Bcd == changed,
                "device rollback preserves unrelated external BCD changes");
            Check(f.W.Calls.All(c => c.StartsWith("device:")) && f.W.DynamicTick == "yes", "apply and rollback never submit a BCD or dynamic-tick write");
        }
        foreach (var disabled in new[] { false, true })
        {
            var f = Make(); f.W.State = f.W.State with { Bcd = null, Error = "启动计时源读取失败：fixture denied",
                Devices = [f.W.State.Devices[0] with { Problem = disabled ? 22u : 0u }] };
            Check(f.E.Read().CanDisableHpet == !disabled, "unreadable BCD does not gate a device-only operation");
            if (!disabled)
            {
                Check(Apply(f, ClockEnvironmentAction.DisableHpet).Success && f.W.State.Bcd is null, "device disable succeeds without reading boot state");
                Check(Apply(f, ClockEnvironmentAction.RestoreHpet).Success && f.W.State.Bcd is null, "device restore also has no BCD dependency");
            }
            else Check(f.E.Read().Message.Contains("HPET 设备已禁用") && !Apply(f, ClockEnvironmentAction.DisableHpet).Success
                && f.W.Writes == 0 && f.S.Read().Count == 0, "already-disabled device with unreadable BCD creates no ownership or write");
        }
        foreach (var clock in new[] { "", "yes", "no" })
        foreach (var tick in new[] { "", "yes", "no" })
        {
            var f = Make(); f.W.State = f.W.State with { Bcd = f.W.State.Bcd! with { Clock = clock, Tick = tick },
                Devices = [f.W.State.Devices[0] with { Problem = 22 }] };
            Check(!f.E.Read().CanDisableHpet && f.E.Read().Message.Contains("HPET 设备已禁用"), "already-disabled target does not require a BCD target");
            Check(!Apply(f, ClockEnvironmentAction.DisableHpet).Success && f.W.Writes == 0 && f.S.Read().Count == 0,
                "screenshot scenario leaves both missing BCD values untouched without fabricating a backup");
        }
        foreach (var write in new[] { 1, 2 })
        foreach (var after in new[] { false, true })
        {
            var f = Make(); f.W.State = f.W.State with { Devices = [.. f.W.State.Devices, new(@"ACPI\PNP0103\1", Identity, "HPET", 0x2000, 0)] };
            var original = f.W.State.Bcd;
            f.W.FailWrite = write; f.W.FailAfterWrite = after;
            var result = Apply(f, ClockEnvironmentAction.DisableHpet);
            Check(!result.Success && result.OutcomeUnknown && result.RequiresReboot && f.S.Read().Single() is { Schema: 4, HasSource: false },
                "every device failure retains device-only originals and responsibility");
            Check(f.W.Calls.All(c => c.StartsWith("device:")) && f.W.State.Bcd == original, "device failure never triggers a BCD fallback");
            f.W.FailWrite = 0;
            Check(Apply(f, ClockEnvironmentAction.RestoreHpet).Success && f.W.State.Devices.All(d => !d.Disabled) && f.W.State.Bcd == original,
                "explicit restore recovers device failure before and after submission");
        }
        {
            var f = Make(); f.W.State = f.W.State with { Devices = Enumerable.Range(0, 16)
                .Select(i => new HpetDeviceState(@"ACPI\PNP0103\" + i, Identity, "HPET", 0x2000, i % 2 == 0 ? 0u : 22u)).ToArray() };
            Check(Apply(f, ClockEnvironmentAction.DisableHpet).Success && f.W.Writes == 8 && f.W.State.Devices.All(d => d.Disabled),
                "sixteen devices fit a complete device-only journal with only enabled devices submitted");
            var saved = f.S.Read().Single();
            Check(saved.Schema == 4 && saved.Steps.Count == 16 && saved.Steps.All(s => s.Verified)
                && saved.Steps.Count(s => s.Original == "disabled" && !s.Attempted) == 8, "pre-disabled originals remain explicit and unmodified");
            Throws(() => f.S.Save(saved with { EntryId = Entry, Inheritance = Inheritance }), "device-only journal cannot own a boot identity");
            Throws(() => f.S.Save(saved with { Steps = [.. saved.Steps, new("useplatformclock", Entry, "yes", "no")] }),
                "device-only schema cannot acquire BCD write authority");
            Throws(() => f.S.Save(saved with { Steps = [.. saved.Steps, new(@"ACPI\PNP0103\16", Identity, "enabled", "disabled")] }),
                "seventeen-device journal is rejected");
            Throws(() => f.S.Save(saved with { Schema = 1 }), "legacy enabled-only schema cannot reinterpret pre-disabled originals");
            Check(Apply(f, ClockEnvironmentAction.RestoreHpet).Success && f.W.State.Devices.Select((d, i) => d.Disabled == (i % 2 != 0)).All(x => x)
                && f.W.Calls.All(c => c.StartsWith("device:")), "mixed originals recover without enabling pre-disabled devices");
        }
        {
            var f = Make(); f.W.AfterRead = n => { if (n == 3) f.W.State = f.W.State with { Bcd = null }; };
            Check(Apply(f, ClockEnvironmentAction.DisableHpet).Success && f.W.State.Bcd is null,
                "BCD becoming unreadable after journaling does not create device-only dependency");
        }
        foreach (var failDevice in new[] { false, true })
        {
            var f = Make(); f.W.ForbidSourceWrites = false; var original = f.W.State.Bcd;
            Check(Apply(f, ClockEnvironmentAction.RestoreDefaultSource).Success, "independent source action remains explicit");
            f.W.State = f.W.State with { BootId = "2" };
            Check(Apply(f, ClockEnvironmentAction.DisableHpet).Success && f.S.Read().Count == 2, "device-only backup preserves separate source responsibility");
            Check(!f.E.Read().CanRestoreSource, "source restore waits for active device responsibility");
            if (failDevice)
            {
                f.W.FailWrite = f.W.Writes + 1; f.W.Calls.Clear();
                Check(!f.E.RestoreAll(default).Success && f.W.State.Bcd is { Clock: "", Tick: "" }
                    && f.W.Calls.All(c => c.StartsWith("device:")) && f.S.Read().All(r => !r.Restored),
                    "recover-all failure preserves separate BCD responsibility");
                f.W.FailWrite = 0;
            }
            Check(f.E.RestoreAll(default).Success && f.W.State.Bcd == original && !f.W.State.Devices[0].Disabled
                && f.S.Read().All(r => r.Restored), "recover-all restores device before separately requested source changes");
        }
        LegacyRestoreChecks(root);
    }
    private static void LegacyRestoreChecks(string root)
    {
        var index = 0;
        (World W, ClockEnvironmentStore S, ClockEnvironmentEngine E) Make(int schema, string clock, string tick, bool preDisabled = false)
        {
            var world = new World(); var store = new ClockEnvironmentStore(Path.Combine(root, "legacy-" + (++index)), Machine, Sid);
            world.State = world.State with { BootId = "2", Bcd = world.State.Bcd! with { Clock = schema == 3 ? "no" : "", Tick = "" },
                Devices = [world.State.Devices[0] with { Problem = 22 }] };
            var steps = new List<ClockEnvironmentStep>();
            if (schema != 1) steps.AddRange([
                new("useplatformclock", Entry, clock, schema == 3 ? "no" : "") { Attempted = true, Verified = true },
                new("useplatformtick", Entry, tick, "") { Attempted = true, Verified = true }]);
            steps.Add(new(@"ACPI\PNP0103\0", Identity, preDisabled ? "disabled" : "enabled", "disabled") { Attempted = !preDisabled, Verified = true });
            store.Save(new() { Schema = schema, Kind = "hpet", Machine = Machine, Sid = Sid, BootId = "1", Phase = "Applied",
                EntryId = schema == 1 ? "" : Entry, Inheritance = schema == 1 ? "" : Inheritance, Steps = steps });
            return (world, store, new(store, world));
        }
        OperationResult Restore((World W, ClockEnvironmentStore S, ClockEnvironmentEngine E) f)
            => f.E.Change(new(ClockEnvironmentAction.RestoreHpet, f.E.Read().Fingerprint, true));
        foreach (var schema in new[] { 1, 2, 3 })
        foreach (var clock in new[] { "", "yes", "no" })
        foreach (var tick in new[] { "", "yes", "no" })
        {
            var f = Make(schema, clock, tick); var before = f.W.State.Bcd;
            f.W.BeforeWrite = _ =>
            {
                if (f.W.Calls.Last().StartsWith("bcd:")) Check(!f.W.State.Devices[0].Disabled, "legacy rollback restores device before BCD originals");
                return true;
            };
            Check(Restore(f).Success && !f.W.State.Devices[0].Disabled, "all legacy schemas retain device recovery");
            Check(schema == 1 ? f.W.State.Bcd == before && f.W.Calls.Count == 1
                : f.W.State.Bcd is { } b && b.Clock == clock && b.Tick == tick, "legacy schema restores its original source contract");
            Check(f.S.Read().Single().Restored && f.W.DynamicTick == "yes", "legacy responsibility settles without dynamic-tick writes");
        }
        foreach (var clock in new[] { "", "yes", "no" })
        foreach (var tick in new[] { "", "yes", "no" })
        {
            var f = Make(3, clock, tick, preDisabled: true);
            Check(Restore(f).Success && f.W.State.Devices[0].Disabled && f.W.Calls.All(c => c.StartsWith("bcd:"))
                && f.W.State.Bcd is { } b && b.Clock == clock && b.Tick == tick, "schema-three pre-disabled original is never enabled");
        }
        foreach (var schema in new[] { 2, 3 })
        foreach (var after in new[] { false, true })
        {
            var f = Make(schema, "yes", "yes"); var bcd = f.W.State.Bcd;
            f.W.FailWrite = 1; f.W.FailAfterWrite = after;
            Check(!Restore(f).Success && f.W.State.Bcd == bcd && f.W.Calls.All(c => c.StartsWith("device:")),
                "legacy failed/unknown device recovery cannot advance to BCD recovery");
            f.W.FailWrite = 0;
            Check(Restore(f).Success && f.W.State.Bcd is { Clock: "yes", Tick: "yes" }, "legacy failure retains retryable original responsibility");
        }
        foreach (var change in new[] { "entry", "inheritance", "inherited", "source", "device" })
        {
            var f = Make(3, "yes", "no");
            var state = f.W.State;
            f.W.State = change switch
            {
                "entry" => state with { Bcd = state.Bcd! with { EntryId = Parent } },
                "inheritance" => state with { Bcd = state.Bcd! with { Inheritance = new('D', 64) } },
                "inherited" => state with { Bcd = state.Bcd! with { InheritedOverride = true } },
                "source" => state with { Bcd = state.Bcd! with { Tick = "yes" } },
                _ => state with { Devices = [state.Devices[0] with { Identity = new('D', 64) }] },
            };
            Check(!Restore(f).Success && f.W.Writes == 0 && !f.S.Read().Single().Restored, "legacy external conflict retains responsibility without writes");
        }
    }
}
