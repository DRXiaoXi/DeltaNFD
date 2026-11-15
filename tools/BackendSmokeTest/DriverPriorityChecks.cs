using System.Reflection;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Inspection;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class DriverPriorityChecks
{
    private static int _checks;
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); _checks++; }
    private static readonly DriverInterruptSettings Original = new(default, new(true, 4), new(true, 0));
    private static DriverPriorityDevice Device(string suffix = "1") => new(@"PCI\VEN_10DE&DEV_1234\" + suffix, "Owned fake PCI " + suffix,
        "Display", "1.0.0.0", "fixture-driver", DriverPriorityPolicy.Hash("device-" + suffix), 7, 16, true, Original, [32]);
    private sealed class Api : IDriverPriorityEnvironment
    {
        public readonly Dictionary<string, DriverPriorityDevice> Devices = new(StringComparer.OrdinalIgnoreCase);
        public string? Block;
        public readonly List<string> Errors = [];
        public int Writes, Discovers;
        public Action<DriverPriorityDevice, int>? BeforeWrite;
        public Action? AfterWrite;
        public Func<string, DriverPriorityDevice?>? Reader;
        public bool WrongReadback;
        public string? Blocker(bool restoring) => Block;
        public DriverPriorityDiscovery Discover() { Discovers++; return new(Devices.Values.ToArray(), Errors); }
        public DriverPriorityDevice? Read(string id) => Reader is null ? Devices.GetValueOrDefault(id) : Reader(id);
        public void Write(DriverPriorityDevice expected, int slot, InterruptDword value, bool restoring)
        {
            BeforeWrite?.Invoke(expected, slot);
            var current = Devices[expected.InstanceId];
            if (current.Identity != expected.Identity || current.Settings != expected.Settings || Block is not null) throw new IOException("fixture native precondition changed");
            Writes++; Devices[current.InstanceId] = current with { Settings = current.Settings!.Set(slot, WrongReadback ? new(true, 2) : value) }; AfterWrite?.Invoke();
        }
    }
    private sealed class Lab
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "DeltaNFD-driver-priority-" + Guid.NewGuid().ToString("N"));
        public readonly DriverPriorityStore Store;
        public readonly Api Api = new();
        public readonly DriverPriorityEngine Engine;
        public Lab() { Store = new(Root, "owned-fixture-machine"); var d = Device(); Api.Devices[d.InstanceId] = d; Engine = new(Store, Api); }
        public DriverPriorityItem Row(string? id = null) => Engine.List().Items.Single(x => x.Device.InstanceId == (id ?? Device().InstanceId));
        public OperationResult Apply(bool? msi = null, bool limitSet = false, uint? limit = null, int? priority = null)
        { var r = Row(); return Engine.Change(new(r.Device.InstanceId, r.Fingerprint, DriverPriorityAction.Apply, true, msi, limitSet, limit, priority)); }
        public OperationResult Restore() { var r = Row(); return Engine.Change(new(r.Device.InstanceId, r.Fingerprint, DriverPriorityAction.Restore, true)); }
        public DriverPriorityDevice Current => Api.Devices[Device().InstanceId];
    }
    internal static void Run()
    {
        _checks = 0;
        var testId = Guid.NewGuid(); var isolated = Path.Combine(Path.GetTempPath(), "DeltaNFD-driver-review-" + testId.ToString("N"));
        Directory.CreateDirectory(isolated); File.WriteAllText(Path.Combine(isolated, "isolated-test.marker"), testId.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(isolated, testId);
        var advice = typeof(ShaderInspection).GetMethod("BuildDriverAdvice", BindingFlags.NonPublic | BindingFlags.Static)!;
        (ShaderDriverState State, string Advice) Driver(string v, ShaderGpuDetectionState gpu = ShaderGpuDetectionState.Nvidia)
            => ((ShaderDriverState, string))advice.Invoke(null, [gpu, v])!;
        for (var i = 0; i < 100; i++) Check(Driver($"591.{i:00}").State == ShaderDriverState.ProblemSeries, "all 591.xx versions are problem-series");
        foreach (var v in new[] { "576.88", "590.99", "592.00", "592.71", "596.49", "610.03", "616.24", "617.14", "621.14", "622.01" })
            Check(Driver(v).State == ShaderDriverState.Ok, "other supported driver families unchanged: " + v);
        foreach (var v in new[] { "", "591", "591.100", "591.-1", "-591.10", "2147483647.99", "not-a-version" })
            Check(Driver(v).State == ShaderDriverState.NotApplicable, "bad driver version cannot be presented as problem-series: " + v);
        Check(Driver("572.83").State == ShaderDriverState.TooOld && Driver("591.32", ShaderGpuDetectionState.NotPresent).State == ShaderDriverState.NotApplicable,
            "old-driver and non-NVIDIA behavior retained");
        foreach (var id in new[] { @"PCI\VEN_10DE&DEV_1234\4&123&0&0008", @"pci\VEN_8086&DEV_ABCD\1" }) Check(DriverPriorityPolicy.ValidId(id), "real PCI instance shape admitted");
        foreach (var id in new[] { "", @"USB\VID_1234\1", @"PCI\..\..", @"PCI\VEN_1\1\Device Parameters", @"PCI\VEN_1\..", @"HKEY_LOCAL_MACHINE\SYSTEM\Enum", @"PCI\VEN_1\1/escape" })
            Check(!DriverPriorityPolicy.ValidId(id), "non-PCI or escaped ID rejected: " + id);
        var d = Device();
        for (var priority = 0; priority <= 3; priority++)
        {
            var s = DriverPriorityPolicy.Desired(d, new(d.InstanceId, "", DriverPriorityAction.Apply, true, Priority: priority));
            Check(priority == 0 ? !s.Priority.Exists : s.Priority == new InterruptDword(true, (uint)priority), "priority ordinal follows IRQ_PRIORITY");
        }
        foreach (var n in new uint[] { 1, 2, 4, 8, 16 })
            Check(DriverPriorityPolicy.Desired(d with { SupportedModes = 3 }, new(d.InstanceId, "", DriverPriorityAction.Apply, true, SetMessageLimit: true, MessageLimit: n)).MessageLimit.Value == n,
                "plain MSI valid power-of-two message count");
        foreach (var n in new uint[] { 3, 5, 17, 2049 })
        {
            try { DriverPriorityPolicy.Desired(d with { SupportedModes = 3 }, new(d.InstanceId, "", DriverPriorityAction.Apply, true, SetMessageLimit: true, MessageLimit: n)); throw new Exception("bad MSI limit accepted"); }
            catch (InvalidOperationException) { Check(true, "plain MSI rejects unsupported count"); }
        }
        Check(DriverPriorityPolicy.Desired(d, new(d.InstanceId, "", DriverPriorityAction.Apply, true, SetMessageLimit: true, MessageLimit: 3)).MessageLimit.Value == 3, "MSI-X permits bounded non-power-of-two count");
        foreach (var command in new[] { BackendCommands.SystemDriverPriorityChange, BackendCommands.SystemDriverPriorityRestoreAll })
            Check(BackendModules.RequiresWriteIntent(BackendModules.System, command) && BackendModules.RetainsStaticBackup(BackendModules.System, command), "writes have intent and retain persistent backup");
        Check(BackendModules.IsReadOnly(BackendModules.System, BackendCommands.SystemDriverPriorityList), "device list is read-only");
        foreach (var command in new[] { BackendCommands.SystemDriverPriorityChange, BackendCommands.SystemDriverPriorityRestoreAll })
        {
            var review = new BackendUnconfirmedStore.Review(BackendModules.System, "owned", "owned interruption", true) { Command = command, CanRestore = true };
            var snapshot = new ReconcileSnapshot { Module = BackendModules.System, Complete = true, Summary = "owned fixture",
                RecoveryScopes = [new() { Id = "driver-priority", Description = "owned interrupt originals", Available = true, AffectedCount = 1 }, new() { Id = "startup", Description = "unrelated startup", Available = true }] };
            Check(BackendRecovery.RecoveryChoices(review, snapshot).Select(x => x.Id).SequenceEqual(["driver-priority"]), "driver interruption only offers its own recovery scope");
            foreach (var scope in new[] { "startup", "driver-priority" })
            {
                try { BackendUnconfirmedStore.ArchiveAfterRecovery(review, scope, true, "owned", snapshot); throw new Exception("unverified scope cleared driver interruption"); }
                catch (IOException) { Check(true, "archive independently rejects unrelated or still-pending recovery scope"); }
            }
        }
        var nativeInfo = typeof(DriverPriorityInspection).GetNestedType("DevInfo", BindingFlags.NonPublic)!;
        var nativeKey = typeof(DriverPriorityInspection).GetNestedType("PropertyKey", BindingFlags.NonPublic)!;
        Check(System.Runtime.InteropServices.Marshal.SizeOf(nativeInfo) == (IntPtr.Size == 8 ? 32 : 28), "SP_DEVINFO_DATA ABI matches process pointer width");
        Check(System.Runtime.InteropServices.Marshal.SizeOf(nativeKey) == 20, "DEVPROPKEY ABI is GUID plus DWORD");
        Check((Guid)typeof(DriverPriorityInspection).GetField("PciProperties", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!
            == new Guid("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62"), "PCI capability GUID matches installed Microsoft pciprop.h");
        var empty = new Lab(); Check(empty.Engine.List(true).BackupCount == 0 && empty.Api.Discovers == 0, "unrelated reconciliation does not enumerate PCI when no backups");
        Check(empty.Apply().Success && empty.Api.Writes == 0 && empty.Store.ReadAll().Count == 0, "unchanged settings never write or create original guesses");
        var basic = new Lab();
        basic.Api.BeforeWrite = (device, _) => Check(basic.Store.ReadAll().Single().Original == Original, "exact original is durable before native write");
        var applied = basic.Apply(true, true, 8, 3);
        Check(applied.Success && applied.RequiresReboot && basic.Api.Writes == 3 && basic.Store.ReadAll().Count == 1, "three requested settings applied and reboot reported");
        Check(basic.Apply(priority: 1).Success && basic.Store.ReadAll().Single().Original == Original, "later edits retain first-seen originals");
        basic.Api.BeforeWrite = null;
        Check(basic.Restore().Success && basic.Current.Settings == Original && basic.Store.ReadAll().Count == 0, "restore distinguishes original absent MSI and existing zero priority");
        Check(Directory.GetFiles(Path.Combine(basic.Root, "archive"), "*.json").Length == 1, "successful recovery archives original evidence");
        var stale = new Lab(); var old = stale.Row(); stale.Api.Devices[d.InstanceId] = stale.Current with { Settings = Original with { Priority = new(true, 2) } };
        Check(!stale.Engine.Change(new(d.InstanceId, old.Fingerprint, DriverPriorityAction.Apply, true, Priority: 3)).Success && stale.Api.Writes == 0, "stale UI settings cannot overwrite a changed value");
        var readSwap = new Lab(); readSwap.Api.Reader = _ => readSwap.Current with { InstanceId = Device("other").InstanceId };
        Check(!readSwap.Apply(priority: 3).Success && readSwap.Api.Writes == 0 && readSwap.Store.ReadAll().Count == 0, "changed instance after enumeration is rejected before persistence or write");
        var gate = new Lab(); gate.Api.Block = "game is running";
        Check(!gate.Apply(priority: 3).Success && gate.Api.Writes == 0 && gate.Store.ReadAll().Count == 0, "game/permission blocker runs before backup or native write");
        var disk = new Lab(); disk.Store.BeforeSaveForTests = () => throw new IOException("owned simulated disk failure");
        Check(!disk.Apply(priority: 3).Success && disk.Api.Writes == 0, "failed durable backup prevents native mutation");
        var interrupted = new Lab(); interrupted.Api.BeforeWrite = (_, slot) => { if (slot == 1) throw new IOException("owned simulated interruption"); };
        var partial = interrupted.Apply(true, true, 8, 3);
        Check(!partial.Success && partial.OutcomeUnknown && interrupted.Api.Writes == 1 && interrupted.Store.ReadAll().Count == 1, "interrupted multi-value write retains intent and first original");
        interrupted.Api.BeforeWrite = null;
        var restarted = new DriverPriorityEngine(new(interrupted.Root, "owned-fixture-machine"), interrupted.Api);
        Check(restarted.RestoreAll().Success && interrupted.Current.Settings == Original && interrupted.Store.ReadAll().Count == 0, "new backend recovers an interrupted prefix without guessing unwritten fields");
        var wrong = new Lab(); wrong.Api.WrongReadback = true;
        Check(!wrong.Apply(priority: 3).Success && wrong.Store.ReadAll().Count == 1, "wrong readback never reports success or drops responsibility");
        var conflict = new Lab(); Check(conflict.Apply(true, priority: 3).Success, "conflict fixture applies");
        conflict.Api.Devices[d.InstanceId] = conflict.Current with { Settings = conflict.Current.Settings! with { Msi = new(true, 0) } };
        Check(!conflict.Restore().Success && conflict.Current.Settings!.Msi == new InterruptDword(true, 0)
            && conflict.Current.Settings.Priority == Original.Priority && conflict.Store.ReadAll().Count == 1, "external change is preserved while independent owned values are restored");
        var archive = conflict.Row(); var writes = conflict.Api.Writes;
        Check(conflict.Engine.Change(new(d.InstanceId, archive.Fingerprint, DriverPriorityAction.KeepCurrent, true)).Success
            && conflict.Api.Writes == writes && conflict.Store.ReadAll().Count == 0, "explicit keep-current archives conflict without native mutation");
        var identity = new Lab(); Check(identity.Apply(priority: 3).Success, "identity fixture applies");
        identity.Api.Devices[d.InstanceId] = identity.Current with { Identity = DriverPriorityPolicy.Hash("new driver") };
        writes = identity.Api.Writes; Check(!identity.Restore().Success && identity.Api.Writes == writes && identity.Store.ReadAll().Count == 1, "updated driver cannot receive an old driver's backup");
        var missing = new Lab(); Check(missing.Apply(priority: 3).Success, "missing-device fixture applies"); missing.Api.Devices.Clear();
        Check(missing.Engine.List().Items.Single().HasBackup && !missing.Engine.RestoreAll().Success && missing.Store.ReadAll().Count == 1, "disconnected device remains visible with its original responsibility");
        var denied = new Lab(); Check(denied.Apply(priority: 3).Success, "journal lease fixture applies");
        var journal = Directory.GetFiles(denied.Root, "*.json").Single(); var backup = denied.Store.ReadAll().Single();
        using (denied.Store.Hold(backup))
        {
            try { File.Delete(journal); throw new Exception("journal deleted during a native write lease"); }
            catch (IOException) { Check(true, "same journal handle refuses deletion while a write can rely on it"); }
        }
        Check(denied.Restore().Success, "journal can be archived normally after the protecting handle is released");
        var corrupt = new Lab(); Check(corrupt.Apply(priority: 3).Success, "corruption fixture applies");
        var file = Directory.GetFiles(corrupt.Root, "*.json").Single(); File.WriteAllText(file, "{broken}");
        Check(!corrupt.Engine.List().Complete && corrupt.Engine.List().BackupCount > 0 && !corrupt.Engine.RestoreAll().Success, "corrupt journal is not empty or successfully recovered");
        var machine = new Lab(); Check(machine.Apply(priority: 3).Success, "machine fixture applies");
        Check(!new DriverPriorityEngine(new(machine.Root, "different-machine"), machine.Api).List().Complete, "another machine/user cannot silently adopt a journal");
        var duplicate = new Lab(); Check(duplicate.Apply(priority: 3).Success, "duplicate-property fixture applies");
        var duplicatePath = Directory.GetFiles(duplicate.Root, "*.json").Single();
        var duplicateJson = File.ReadAllText(duplicatePath).Replace("\"Schema\":1", "\"Schema\":1,\"Schema\":1", StringComparison.Ordinal);
        File.WriteAllText(duplicatePath, duplicateJson);
        Check(!duplicate.Engine.List().Complete && duplicate.Engine.List().BackupCount > 0, "duplicate JSON properties cannot replace trusted journal fields");
        var several = new Lab(); several.Api.Devices[Device("2").InstanceId] = Device("2");
        Check(several.Apply(priority: 3).Success, "multi-device first original captured");
        var second = several.Row(Device("2").InstanceId);
        Check(several.Engine.Change(new(second.Device.InstanceId, second.Fingerprint, DriverPriorityAction.Apply, true, Priority: 3)).Success, "multi-device second original captured");
        several.Api.BeforeWrite = (dev, _) => { if (dev.InstanceId == d.InstanceId) throw new IOException("owned first-device denial"); };
        Check(!several.Engine.RestoreAll().Success && several.Api.Devices[second.Device.InstanceId].Settings == Original
            && several.Store.ReadAll().Count == 1, "all-device recovery attempts later devices after one fails, retaining only pending original");
        var unknown = new Lab(); unknown.Api.Devices[d.InstanceId] = unknown.Current with { SupportedModes = null, MaximumMessages = null };
        Check(!unknown.Apply(true).Success && unknown.Api.Writes == 0, "unknown capability cannot enable MSI");
        Check(!unknown.Apply(limitSet: true, limit: 8).Success && unknown.Api.Writes == 0, "unknown message limit cannot be guessed");
        Check(unknown.Apply(priority: 3).Success && unknown.Restore().Success, "priority independent of unknown MSI capability remains restorable");
        var msix = new Lab(); msix.Api.Devices[d.InstanceId] = msix.Current with { SupportedModes = 6 };
        Check(!msix.Apply(false).Success && msix.Api.Writes == 0, "device without line-based support cannot be forced off MSI");
        var changed = new Lab(); changed.Api.AfterWrite = () => changed.Api.Block = "game started";
        Check(!changed.Apply(true, true, 8, 3).Success && changed.Api.Writes == 1, "new game blocker stops remaining writes in same batch");
        changed.Api.Block = null; changed.Api.AfterWrite = null; Check(changed.Restore().Success && changed.Current.Settings == Original, "stopped batch retains enough evidence to restore only actual changes");
        var canceled = new Lab(); using var cts = new CancellationTokenSource(); var row = canceled.Row(); cts.Cancel();
        Check(!canceled.Engine.Change(new(d.InstanceId, row.Fingerprint, DriverPriorityAction.Apply, true, Priority: 3), cts.Token).Success && canceled.Api.Writes == 0, "pre-canceled operation never writes");
        var noConfirm = new Lab(); row = noConfirm.Row();
        Check(!noConfirm.Engine.Change(new(d.InstanceId, row.Fingerprint, DriverPriorityAction.Apply, Priority: 3)).Success && noConfirm.Api.Writes == 0, "backend requires concrete confirmation");
        var src = new DirectoryInfo(AppContext.BaseDirectory); while (src is not null && !File.Exists(Path.Combine(src.FullName, "DeltaNFD.sln"))) src = src.Parent;
        Check(src is not null, "current source located for UI checks");
        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(src!.FullName, "src/DeltaNFD/Views/DriverPriorityPage.xaml"));
        Check(xaml.Descendants().Count(x => x.Name.LocalName == "ComboBox" && x.Attribute("Header") is not null) == 2, "MSI and IRQ priority have labeled separate selectors");
        Check(xaml.Descendants().Any(x => x.Name.LocalName == "TextBox" && ((string?)x.Attribute("Header"))?.Contains("消息数量") == true), "message-limit input exposes its meaning and default behavior");
        Console.WriteLine($"Driver priority and 591 series: {_checks} checks passed. Real policy/store/engine with fake PCI, registry and game state; no native device discovery, registry writes, tool execution or reboot.");
        Console.WriteLine("Owned evidence fixtures: " + Path.GetTempPath());
    }
}
