using DeltaNFD.Backends;
using DeltaNFD.Services;
using static DeltaNFD.Services.NvContainerMode;
using static DeltaNFD.Services.NvContainerRun;

if (args is ["--host-nvidia-services", var serviceLog])
{
    var display = NvDisplayContainerService.Status();
    var legacy = NvDisplayContainerService.TelemetryStatus();
    var local = NvDisplayContainerService.LocalSystemStatus();
    File.WriteAllText(serviceLog, System.Text.Json.JsonSerializer.Serialize(new
    {
        Elevated = ElevationHelper.IsElevated,
        Display = new { display.Detail, display.Restore, Ready = display.Fingerprint is not null },
        Legacy = new { legacy.Detail, legacy.Restore, Ready = legacy.Fingerprint is not null },
        LocalSystem = new { local.Detail, local.Restore, Ready = local.Fingerprint is not null },
    }));
    if (!display.Restore || display.Fingerprint is null || local.Fingerprint is null) Environment.ExitCode = 2;
    return;
}
if (args is ["--host-system-wire", var systemLayout, var systemWireLog])
{
    try { await DisplayContainerWireProbe.RunAsync(systemLayout, systemWireLog, system: true); }
    catch (Exception ex) { File.WriteAllText(systemWireLog, ex.ToString()); Environment.ExitCode = 2; }
    return;
}
if (args is ["--host-wire", var layout, var wireLog])
{
    try { await DisplayContainerWireProbe.RunAsync(layout, wireLog); }
    catch (Exception ex) { File.WriteAllText(wireLog, ex.ToString()); Environment.ExitCode = 2; }
    return;
}
if (args is ["--host-status"] or ["--host-status", _])
{
    var status = NvDisplayContainerService.Status();
    string[] lines = [$"Elevated: {ElevationHelper.IsElevated}", status.Detail,
        $"Fingerprint ready: {status.Fingerprint is not null}; restore: {status.Restore}"];
    foreach (var line in lines) Console.WriteLine(line);
    if (args.Length == 2) File.WriteAllLines(args[1], lines);
    if (status.Fingerprint is null) Environment.ExitCode = 2;
    return;
}
if (args is ["--host-read-only"])
{
    using var svc = new NvContainerScm(false);
    // The protected backup store may require full administrator elevation; avoid opening it here.
    var state = svc.Read();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(state));
    var image = NvDisplayContainerService.ParseImage(state.Image)!;
    Console.WriteLine("NVIDIA signature: " + DeltaNFD.Execution.Procman.NativeNvidiaGpuShareApi.TrustedNvidia(image));
    return;
}
if (args is ["--host-telemetry-status"])
{
    var snapshot = NvDisplayContainerService.TelemetryReconcile();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(snapshot));
    return;
}
var token = Guid.NewGuid();
var isolated = Path.Combine(Path.GetTempPath(), "DeltaNFD-NvDisplayContainerChecks-" + token.ToString("N"));
Directory.CreateDirectory(isolated);
File.WriteAllText(Path.Combine(isolated, "isolated-test.marker"), token.ToString("D"));
AppDataPaths.ConfigureIsolatedDataRoot(isolated, token);
var count = 0;
void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException(label);
    ++count;
}
if (args is ["--registry-acl-fixture"])
{
    RegistryBackupChecks.Run(Check);
    Console.WriteLine($"Registry ACL: {count} checks passed; disposable HKCU fixtures only.");
    return;
}
await DisplayContainerReadyChecks.RunAsync(Check);
var path = Path.Combine(NvDisplayContainerService.DriverStore, @"nv_test.inf_amd64_abcd\Display.NvContainer\NVDisplay.Container.exe");
const string parameter = "-arg <ExeDir>\\arguments.txt";
var imageName = path + " " + parameter;
FileBackupChecks.Run(isolated, imageName, Check);
TelemetryChecks.Run(isolated, imageName, Check);
LocalSystemChecks.Run(isolated, imageName, Check);
foreach (var raw in new[] { imageName, $"\"{path}\"  -arg \"<ExeDir>\\arguments.txt\"", path,
    imageName.Replace(Path.GetDirectoryName(Environment.SystemDirectory)!, @"\SystemRoot"),
    imageName.Replace(Path.GetDirectoryName(Environment.SystemDirectory)!, "%SystemRoot%"),
    imageName.Replace(Path.GetDirectoryName(Environment.SystemDirectory)!, "%windir%") })
    Check(NvDisplayContainerService.ParseImage(raw) == path, "equivalent SCM spelling rejected: " + raw);
foreach (var raw in new[] { imageName + " -extra", imageName.Replace("nv_test", "..\\nv_test"), imageName.Replace(NvDisplayContainerService.DriverStore, @"C:\Users\Public"),
    imageName.Replace("NVDisplay.Container.exe", "nvcontainer.exe"), $"\"{path} + {parameter}", "%TEMP%\\" + imageName,
    imageName.Replace(".exe", ".exe:alt"), "\\\\?\\" + imageName, imageName.Replace("arguments.txt", "other.txt"), new string('a', 1025) })
    Check(NvDisplayContainerService.ParseImage(raw) is null, "foreign path accepted: " + raw);
Check(NvDisplayContainerService.ParseImage(imageName + " -extra", out var argumentReason) is null && argumentReason.Contains("启动参数"),
    "unsupported arguments are identified separately");
Check(NvDisplayContainerService.ParseImage(@"C:\Users\Public\NVDisplay.Container.exe " + parameter, out var pathReason) is null && pathReason.Contains("映像路径"),
    "unsupported path is identified separately");
foreach (var account in new[] { "LocalSystem", @"NT AUTHORITY\SYSTEM" })
    Check(NvDisplayContainerService.IsLocalSystem(account), "system account alias");
Check(!NvDisplayContainerService.IsLocalSystem(@"DOMAIN\LocalSystem"), "foreign account rejected");
Check(BackendModules.IsReadOnly(BackendModules.Gpu, BackendCommands.GpuNvDisplayContainerStatus), "status must be read-only");
Check(BackendModules.RequiresWriteIntent(BackendModules.Gpu, BackendCommands.GpuNvDisplayContainerChange), "writes require intent");
Check(BackendModules.IsReadOnly(BackendModules.System, BackendCommands.SystemNvDisplayContainerStatus)
    && BackendModules.RequiresWriteIntent(BackendModules.System, BackendCommands.SystemNvDisplayContainerChange)
    && BackendModules.IsMutating(BackendModules.System, BackendCommands.SystemNvDisplayContainerRecover)
    && !BackendModules.RequiresWriteIntent(BackendModules.System, BackendCommands.SystemNvDisplayContainerRecover),
    "normal system writes own intent and independent recovery uses existing intent");

OperationResult Apply(Fake f, bool? restore = null, string? fp = null, bool gate = true, bool trusted = true)
{
    var preview = NvDisplayContainerService.Describe(f.State, f.Saved);
    return NvDisplayContainerService.Commit(f, f, new(restore ?? preview.Restore, fp ?? preview.Fingerprint ?? ""),
        _ => trusted ? new MemoryStream() : throw new InvalidOperationException("untrusted"), () => gate);
}
foreach (var mode in new[] { Automatic, Manual })
foreach (var delayed in new[] { true, false })
foreach (var run in new[] { Running, Stopped })
{
    var original = new NvContainerState(imageName, mode, delayed, run, run == Running ? 71u : 0u);
    var fake = new Fake(original);
    Check(Apply(fake).Success && fake.State.Mode == Disabled && fake.State.Run == Stopped && fake.Saved != null, "disable");
    var old = fake.Saved!;
    Check(fake.Ops.SequenceEqual(run == Running ? ["save", "Disabled", "stop"] : ["save", "Disabled"]), "backup before write / never stops already-stopped state");
    fake.Saved = NvContainerRegistryBackup.Decode(NvContainerRegistryBackup.Encode(old));
    Check(fake.Saved == old, "legacy binary backup schema round trips");
    fake.Ops.Clear();
    Check(Apply(fake).Success && fake.State == original && fake.Saved == null, "restore verified");
    Check(fake.Ops.SequenceEqual(run == Running ? [mode.ToString(), "start", "clear"] : [mode.ToString(), "clear"]), "restore running state only if originally running");
}
var fresh = new NvContainerState(imageName, Automatic, false, Running, 71);
var f = new Fake(fresh);
Check(!Apply(f, gate: false).Success && f.Ops.Count == 0, "running game denies operation");
Check(!Apply(f, trusted: false).Success && f.Ops.Count == 0, "untrusted image denies operation");
Check(!Apply(f, restore: true).Success && f.Ops.Count == 0, "stale polarity denied");
Check(!Apply(f, fp: new string('0', 64)).Success && f.Ops.Count == 0, "stale preview denied");
f.State = fresh with { Mode = Disabled, Run = Stopped, Pid = 0 };
Check(!Apply(f).Success && f.Ops.Count == 0, "already disabled without ownership");
var review = NvDisplayContainerService.InspectBackup(f.State, f);
Check(review.StateVerified && review.Status.Fingerprint is null && !review.Status.Restore,
    "confirmed disabled service without a backup is readable for acknowledgement only");
f.State = fresh with { Run = StopPending };
Check(!Apply(f).Success && f.Ops.Count == 0, "pending service state denied");
Check(!NvDisplayContainerService.InspectBackup(f.State, f).StateVerified, "transitional state does not authorize acknowledgement");
f.State = fresh;
f.Fail = "read-backup";
review = NvDisplayContainerService.InspectBackup(f.State, f);
Check(review.StateVerified && review.Status.Fingerprint is null && review.Status.Detail.Contains("read-backup"),
    "backup read failure retains readable service and blocks all service changes");
f.Fail = "save";
var failedBackup = Apply(f);
Check(!failedBackup.Success && !failedBackup.OutcomeUnknown && failedBackup.Message.Contains("保存并回读原设置备份")
    && f.State == fresh && f.Ops.SequenceEqual(["save"]), "backup failure is handled before the service-write boundary");
f.Ops.Clear(); f.Fail = "stop";
var partial = Apply(f);
Check(partial.OutcomeUnknown && partial.Message.Contains("请求正常停止服务")
    && f.State.Mode == Disabled && f.State.Run == Running && f.Saved is not null, "partial stop preserves backup and identifies its stage");
f.Fail = "prepare-restore";
f.Ops.Clear();
Check(!Apply(f).OutcomeUnknown && f.Ops.Count == 0 && f.State.Mode == Disabled && f.Saved is not null,
    "legacy backup verification failure prevents any restore write");
f.Fail = null;
Check(Apply(f).Success && f.State == fresh && f.Saved == null, "partial stop can restore");
Check(Apply(f).Success, "repeat disable");
f.State = f.State with { Image = imageName.Replace("abcd", "eeee") };
Check(!Apply(f).Success && f.Saved != null, "driver update invalidates the old backup");
review = NvDisplayContainerService.InspectBackup(f.State, f);
Check(review.StateVerified && review.Status.Fingerprint is null && review.Status.Detail.Contains("不匹配"),
    "driver change allows explicit acknowledgement and never authorizes stale backup writes");
f.State = f.State with { Image = imageName };
f.Fail = "start";
Check(Apply(f).OutcomeUnknown && f.State is { Mode: Automatic, Run: Stopped } && f.Saved != null, "partial restart preserves backup");
f.Fail = "clear";
var failedClear = Apply(f);
Check(!failedClear.Success && !failedClear.OutcomeUnknown && failedClear.Message.Contains("服务恢复已通过回读")
    && f.State == fresh && f.Saved != null, "backup cleanup failure does not lock a verified restored service");
f.Fail = null;
Check(Apply(f).Success && f.State == fresh && f.Saved == null, "partial restart can retry");
var changed = fresh with { Image = imageName.Replace("abcd", "eeee"), Mode = Manual };
var obsolete = new NvContainerBackup(imageName, Automatic, false, true);
var upperPath = imageName.Replace(NvDisplayContainerService.DriverStore, NvDisplayContainerService.DriverStore.ToUpperInvariant(), StringComparison.Ordinal);
var caseOnly = new Fake(fresh with { Image = upperPath, Mode = Disabled, Run = Stopped, Pid = 0 }) { Saved = obsolete };
Check(!string.Equals(upperPath, imageName, StringComparison.Ordinal), "case-only test uses distinct DriverStore casing");
Check(NvDisplayContainerService.Describe(caseOnly.State, obsolete) is { Fingerprint: not null, Restore: true },
    "Windows upgrade path-casing difference retains recovery authority");
Check(Apply(caseOnly).Success && caseOnly.State.Mode == Automatic && caseOnly.State.Run == Running
    && caseOnly.State.Image == upperPath && caseOnly.Saved is null, "restore leaves the current image spelling in place");
var differentArgs = caseOnly.State with { Image = upperPath.Replace(" -arg ", " -ARG ", StringComparison.Ordinal), Mode = Disabled, Run = Stopped, Pid = 0 };
Check(!string.Equals(differentArgs.Image, upperPath, StringComparison.Ordinal)
    && NvDisplayContainerService.Describe(differentArgs, obsolete) is { Fingerprint: null, Restore: false },
    "argument casing remains sensitive even if the executable path case is normalized for comparison");
Check(NvDisplayContainerService.Describe(caseOnly.State with { Mode = Disabled, Delayed = true }, obsolete).Detail.Contains("延迟启动标记"),
    "real delay-flag conflicts identify the affected field");
var conflict = new Fake(changed) { Saved = obsolete };
var conflictPreview = NvDisplayContainerService.Describe(changed, obsolete);
Check(conflictPreview.Fingerprint is not null && !conflictPreview.Restore, "enabled driver replacement offers a new disable");
Check(!Apply(conflict, restore: true).Success && conflict.Ops.Count == 0, "conflict never allows restoring stale settings");
conflict.Fail = "archive";
Check(!Apply(conflict).Success && conflict.State == changed && conflict.Saved == obsolete,
    "archive failure prevents service writes");
conflict.Fail = null;
conflict.Ops.Clear();
Check(Apply(conflict).Success && conflict.Archived == obsolete
    && conflict.Ops.SequenceEqual(["archive", "save", "Disabled", "stop"]), "archive and backup precede service writes");
Check(Apply(conflict).Success && conflict.State == changed && conflict.Archived == obsolete,
    "restore uses the new driver configuration and keeps the old archive");
var previous = new NvContainerBackup(imageName, Automatic, false, true);
var bytes = NvContainerRegistryBackup.Encode(previous);
var legacyShape = Convert.ToHexString(bytes.AsSpan(0, 6));
Check(legacyShape == "4E4453310202", "existing standalone backup magic, mode and flags");
bytes[5] = 7;
try { NvContainerRegistryBackup.Decode(bytes); throw new Exception("invalid flag accepted"); }
catch (InvalidOperationException) { ++count; }
Console.WriteLine($"PASS {count} assertions; simulated writes only.");

sealed class Fake(NvContainerState state) : INvContainerScm, INvContainerBackupStore
{
    public NvContainerState State = state;
    public NvContainerBackup? Saved;
    public string? Fail;
    public List<string> Ops = [];
    private void Mutate(string action)
    {
        Ops.Add(action);
        if (Fail == action) throw new IOException(action);
    }
    NvContainerState INvContainerScm.Read() => State;
    NvContainerBackup? INvContainerBackupStore.Read() => Fail == "read-backup" ? throw new IOException(Fail) : Saved;
    public void Save(NvContainerBackup backup) { Mutate("save"); Saved = backup; }
    public NvContainerBackup? Archived { get; private set; }
    public void ReplaceConflicting(NvContainerBackup expected, NvContainerBackup replacement)
    {
        Mutate("archive");
        if (Saved != expected) throw new IOException("original backup changed");
        Archived = expected;
        Save(replacement);
    }
    public void PrepareRestore(NvContainerBackup backup)
    {
        if (Fail == "prepare-restore" || Saved != backup) throw new IOException("original backup unverified");
    }
    public void Clear(NvContainerBackup backup)
    {
        if (Saved != backup) throw new IOException("original backup changed");
        Mutate("clear");
        Saved = null;
    }
    public void SetMode(NvContainerMode mode) { Mutate(mode.ToString()); State = State with { Mode = mode }; }
    public void RequestStop() { Mutate("stop"); State = State with { Run = Stopped, Pid = 0 }; }
    public void RequestStart() { Mutate("start"); State = State with { Run = Running, Pid = 71 }; }
    public void WaitFor(NvContainerRun expected, NvContainerRun pending) { if (State.Run != expected) throw new IOException("unexpected run state"); }
}

