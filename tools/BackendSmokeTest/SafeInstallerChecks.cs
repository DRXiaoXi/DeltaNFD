using System.Security.Cryptography;
using System.Text.Json;
using DeltaNFD.Services;
using Microsoft.Win32;

internal static class SafeInstallerChecks
{
    private static readonly string EmptyHash = Convert.ToHexString(SHA256.HashData("[]"u8));
    private static SafeInstallerEntry Empty => new(false, false, null, 0, EmptyHash, 0, null);
    internal static async Task RunAsync()
    {
        int count = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); count++; }
        void Reject(Action action, string why) { try { action(); } catch { Check(true, why); return; } throw new Exception(why); }
        (Platform P, Store S, SafeInstallerEngine E) Fresh(int mode = 1)
        { var p = new Platform { Mode = mode }; var s = new Store(); p.Store = s; return (p, s, new(p, s)); }
        void Enable((Platform P, Store S, SafeInstallerEngine E) f) => f.E.Enable(f.E.Preview().Fingerprint, true, default);

        foreach (var mode in new[] { 1, 2 })
        {
            var f = Fresh(mode); var preview = f.E.Preview();
            Check(preview.Required && preview.CanEnable && f.P.Writes == 0 && f.S.Saves == 0, "preview strictly read-only");
            Enable(f);
            Check(f.S.Journal is { WriteIntent: true, StartIntent: true, ServicePid: > 0 } && f.P.Running == 4, "durable journal contains service identity");
            Check(f.P.Entries[mode].Owner == f.S.Journal!.Id && f.P.Entries[3 - mode] == Empty, "modify only current safe-mode branch");
            Check(f.P.BackupExistedBeforeWrite && f.P.StartIntentExistedBeforeStart, "persist before registry and service mutations");
            Check(f.E.Restore().Success && f.P.Running == 1 && f.P.Entries[mode] == Empty, "restore service and originally absent leaf");
            Check(f.S.Journal is null && f.S.Archives == 1 && f.P.Stops == 1, "archive only after verification");
        }
        var noConsent = Fresh(); Reject(() => noConsent.E.Enable(noConsent.E.Preview().Fingerprint, false, default), "explicit consent mandatory");
        Check(noConsent.P.Writes == 0 && noConsent.S.Saves == 0, "no consent, no side effects");
        var stale = Fresh(); var consent = stale.E.Preview().Fingerprint; stale.P.Entries[1] = Empty with { Exists = true };
        Reject(() => stale.E.Enable(consent, true, default), "snapshot drift must reject");
        Check(stale.P.Writes == 0, "drift before prepare performs no writes");
        foreach (var mode in new[] { 0, 3 }) { var f = Fresh(mode); Check(!f.E.Preview().Required && f.P.Writes == 0, "no activation outside supported safe modes"); }
        var admin = Fresh(); admin.P.Elevated = false; Check(!admin.E.Preview().CanEnable, "admin required");
        var disabled = Fresh(); disabled.P.Disabled = true; Check(!disabled.E.Preview().CanEnable, "never change service startup type");
        var missing = Fresh(); missing.P.Exists = false; Check(!missing.E.Preview().CanEnable, "missing service rejected");
        var starting = Fresh(); starting.P.Running = 2; Check(!starting.E.Preview().CanEnable, "transition state rejected");
        var running = Fresh(); running.P.Running = 4; Check(!running.E.Preview().Required && running.P.Writes == 0, "already running service remains unchanged");
        var busy = Fresh(); busy.P.IsBusy = true; Check(!busy.E.Preview().CanEnable, "busy MSI blocks enable");
        var owner = Fresh(); owner.P.Entries[1] = Empty with { Exists = true, Owner = "foreign" }; Check(!owner.E.Preview().CanEnable, "foreign marker refused");
        var foreignValue = Fresh(); foreignValue.P.Entries[1] = Empty with { Exists = true, HasDefault = true, Default = "Driver", Kind = 1 };
        Check(!foreignValue.E.Preview().CanEnable, "foreign SafeBoot default refused");
        var foreignKind = Fresh(); foreignKind.P.Entries[1] = Empty with { Exists = true, HasDefault = true, Default = "Service", Kind = (int)RegistryValueKind.ExpandString };
        Check(!foreignKind.E.Preview().CanEnable, "preserve unexpected registry type");

        foreach (var entry in new[] { Empty with { Exists = true }, Empty with { Exists = true, HasDefault = true, Default = "Service", Kind = 1 },
            Empty with { Exists = true, OtherHash = new string('A', 64), Subkeys = 2 } })
        {
            var f = Fresh(); f.P.Entries[1] = entry; Enable(f);
            Check(f.E.Restore().Success && f.P.Entries[1] == entry, "preserve pre-existing leaf, named values, subkeys and original default");
        }
        var changed = Fresh(); Enable(changed); changed.P.Entries[1] = changed.P.Entries[1] with { OtherHash = new string('B', 64) };
        Check(!changed.E.Restore().Success && changed.P.Stops == 0 && changed.S.Journal is not null, "external changes prevent service stop and registry overwrite");
        var marker = Fresh(); Enable(marker); marker.P.Entries[1] = marker.P.Entries[1] with { Owner = "different" };
        Check(!marker.E.Restore().Success && marker.P.Stops == 0, "ownership checked before stopping service");
        var newService = Fresh(); Enable(newService); newService.P.Pid++;
        Check(!newService.E.Restore().Success && newService.P.Stops == 0, "never stop a replacement Installer process");
        newService.P.Running = 1;
        Check(newService.E.Restore().Success, "recover after unowned service has naturally stopped");
        var inFlight = Fresh(); Enable(inFlight); inFlight.P.IsBusy = true;
        Check(!inFlight.E.Restore().Success && inFlight.P.Stops == 0 && inFlight.S.Journal is not null, "no cleanup during active MSI or unknown child");
        inFlight.P.IsBusy = false;
        Check(inFlight.E.Restore().Success, "manual retry after transaction finishes");
        var stopFail = Fresh(); Enable(stopFail); stopFail.P.FailStop = true;
        Check(!stopFail.E.Restore().Success && stopFail.S.Journal is not null, "stop failure retains registry and journal");
        stopFail.P.FailStop = false; Check(stopFail.E.Restore().Success, "stop failure retry");
        var regFail = Fresh(); Enable(regFail); regFail.P.FailRestore = true;
        Check(!regFail.E.Restore().Success && regFail.S.Journal is not null, "restore failure preserves evidence");
        regFail.P.FailRestore = false; Check(regFail.E.Restore().Success, "registry restore retry after service stopped");
        var reboot = Fresh(); Enable(reboot); reboot.P.Mode = 0; reboot.P.Running = 1;
        Check(reboot.E.Restore().Success, "normal-mode recovery uses journal branch without changing boot mode");
        var failBackup = Fresh(); failBackup.S.FailSave = 1;
        Reject(() => Enable(failBackup), "backup failure must abort");
        Check(failBackup.P.Writes == 0 && failBackup.P.Starts == 0, "no mutation before durable backup");
        var partialWrite = Fresh(); partialWrite.P.FailWrite = true;
        Reject(() => Enable(partialWrite), "partial registry write interrupted");
        Check(partialWrite.E.Restore().Success && partialWrite.P.Starts == 0, "restore write-intent after partial mutation");
        var failedStart = Fresh(); failedStart.P.FailStart = true;
        Reject(() => Enable(failedStart), "start-service failure");
        Check(failedStart.E.Restore().Success && failedStart.P.Entries[1] == Empty, "restore allowlist after start failure");
        var lostIdentity = Fresh(); lostIdentity.S.FailSave = 3;
        Reject(() => Enable(lostIdentity), "service identity commit failure");
        Check(!lostIdentity.E.Restore().Success && lostIdentity.P.Stops == 0, "unknown started instance cannot be stopped on guessed identity");
        lostIdentity.P.Running = 1; lostIdentity.S.FailSave = 0;
        Check(lostIdentity.E.Restore().Success, "recover unknown start after service exits");
        var pending = Fresh(); Enable(pending);
        Check(!pending.E.Preview().CanEnable && pending.S.Saves == 3, "pending journal prevents another enable");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var cancel = Fresh();
        Reject(() => cancel.E.Enable(cancel.E.Preview().Fingerprint, true, cancelled.Token), "cancel before enable");
        Check(cancel.P.Writes == 0 && cancel.S.Saves == 0, "cancel performs no writes");
        var corrupt = Fresh(); Enable(corrupt); corrupt.S.Journal = corrupt.S.Journal! with { Mode = 77 };
        Check(!corrupt.E.Restore().Success && corrupt.P.Stops == 0 && corrupt.P.Restores == 0, "invalid journal cannot select arbitrary registry path");

        var integrated = Fresh(); var repair = new RepairEnvironment(integrated.E, integrated.P);
        var previewRepair = Vc14Reinstall.Preview(repair, Vc14RepairMode.UninstallOnly);
        var result = await Vc14Reinstall.RunAsync(repair, previewRepair.Fingerprint, null, default, Vc14RepairMode.UninstallOnly);
        Check(result.Success && repair.Removals == 1 && integrated.S.Journal is null && integrated.P.Stops == 1, "enable, uninstall, restore sequence integrated");
        var failedRepair = Fresh(); var failingEnv = new RepairEnvironment(failedRepair.E, failedRepair.P) { FailRemoval = true };
        var fp = Vc14Reinstall.Preview(failingEnv, Vc14RepairMode.UninstallOnly).Fingerprint;
        Check(!(await Vc14Reinstall.RunAsync(failingEnv, fp, null, default, Vc14RepairMode.UninstallOnly)).Success
            && failedRepair.S.Journal is null && failedRepair.P.Running == 1, "uninstall failure also restores temporary settings");
        var uncertain = Fresh(); var uncertainEnv = new RepairEnvironment(uncertain.E, uncertain.P) { LeaveBusy = true };
        var unknown = await Vc14Reinstall.RunAsync(uncertainEnv, Vc14Reinstall.Preview(uncertainEnv, Vc14RepairMode.UninstallOnly).Fingerprint, null, default, Vc14RepairMode.UninstallOnly);
        Check(!unknown.Success && unknown.OutcomeUnknown && uncertain.S.Journal is not null && uncertain.P.Stops == 0, "busy cleanup cannot report full success");
        var fileRoot = Path.Combine(Path.GetTempPath(), "DeltaNFD-safe-installer-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fileRoot);
        try
        {
            var fs = new SafeInstallerFileStore(fileRoot); var data = Fresh(); Enable(data); var record = data.S.Journal!;
            fs.Save(record); Check(fs.Load() == record, "durable file-store round trip");
            Reject(() => fs.Save(record with { Mode = 99 }), "invalid path selector cannot be saved");
            Check(fs.Load() == record, "invalid save preserves existing evidence");
            fs.Archive(record); Check(fs.Load() is null && Directory.GetFiles(Path.Combine(fileRoot, "runtime-repair-history")).Length == 1, "archive record after verified restore");
            var file = Path.Combine(fileRoot, "safe-installer-active.json"); File.WriteAllText(file, "{broken");
            Reject(() => fs.Load(), "broken JSON rejected"); Check(File.ReadAllText(file) == "{broken", "broken evidence retained");
            File.WriteAllText(file, new string('X', 16385)); Reject(() => fs.Load(), "oversized record rejected");
        }
        finally
        {
            var resolved = Path.GetFullPath(fileRoot);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new Exception("test directory escaped temporary root");
            Directory.Delete(resolved, true);
        }
        Console.WriteLine($"Safe Installer checks: {count} passed. All registry/service/installer operations mocked; host services untouched.");
    }

    private sealed class Store : ISafeInstallerStore
    {
        public SafeInstallerJournal? Journal; public int Saves, Archives, FailSave;
        public SafeInstallerJournal? Load() => Journal;
        public void Save(SafeInstallerJournal journal) { Saves++; if (Saves == FailSave) throw new IOException("fixture disk failure"); Journal = journal; }
        public void Archive(SafeInstallerJournal journal) { if (Journal != journal) throw new IOException("changed journal"); Journal = null; Archives++; }
    }
    private sealed class Platform : ISafeInstallerPlatform
    {
        public Store Store = null!;
        public int Mode = 1, Pid = 700001, Writes, Starts, Stops, Restores;
        public uint Running = 1;
        public bool Elevated = true, Exists = true, Disabled, IsBusy, FailWrite, FailStart, FailStop, FailRestore;
        public bool BackupExistedBeforeWrite, StartIntentExistedBeforeStart;
        public Dictionary<int, SafeInstallerEntry> Entries = new() { [1] = Empty, [2] = Empty };
        public SafeInstallerState Capture(int? mode = null) => new(Mode, Elevated, Exists, Disabled, Running, Entries[mode ?? (Mode == 2 ? 2 : 1)], Running == 4 ? Pid : 0, Running == 4 ? 12345 : 0);
        public bool Busy() => IsBusy;
        public void Write(int mode, SafeInstallerEntry expected, string id)
        {
            if (Entries[mode] != expected) throw new IOException("changed leaf");
            BackupExistedBeforeWrite = Store.Journal is { WriteIntent: true };
            Writes++; Entries[mode] = expected with { Exists = true, Owner = id };
            if (FailWrite) throw new IOException("partial write");
            Entries[mode] = Entries[mode] with { HasDefault = true, Default = "Service", Kind = 1 };
        }
        public void Start()
        { Starts++; StartIntentExistedBeforeStart = Store.Journal?.StartIntent == true; if (FailStart) throw new IOException("1084"); Running = 4; }
        public void Stop(int mode, int expectedPid, long expectedStartTicks)
        { if (FailStop) throw new IOException("stop failed"); if (Pid != expectedPid || expectedStartTicks != 12345 || IsBusy) throw new IOException("identity changed"); Stops++; Running = 1; }
        public void Restore(int mode, SafeInstallerEntry before, string id)
        { if (FailRestore) throw new IOException("restore failed"); Restores++; Entries[mode] = before; }
    }
    private sealed class RepairEnvironment(SafeInstallerEngine engine, Platform platform) : IVc14ReinstallEnvironment
    {
        private bool prepared;
        private Vc14Continuation? pending;
        private readonly Vc14Entry product = new(Guid.NewGuid().ToString("B"), "Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.40.1", "14.40.1", "x64", "Minimum", "upgrade");
        public bool FailRemoval, LeaveBusy; public int Removals;
        public bool IsSafeMode => true;
        public string DiagnosticsDirectory => "fixture";
        public SafeInstallerPreparation? TemporaryInstaller() => prepared ? null : engine.Preview();
        public void PrepareTemporaryInstaller(string fingerprint, CancellationToken token) { prepared = true; engine.Enable(fingerprint, true, token); }
        public OperationResult RestoreTemporaryInstaller() => engine.Restore();
        public Vc14Scan Scan() => new(Removals == 0 || FailRemoval ? [product] : [], [], []);
        public IReadOnlyList<Vc14RemovalPlanItem> Plan(Vc14Scan scan) => [new(product.Code, "x64", product.Name, product.Version, "msi", product.Code, "fixture", "fixture")];
        public string? Blocker() => null;
        public void VerifyBaselines() { }
        public void VerifyInstalled() => throw new Exception("must not install in safe mode");
        public void Trace(string stage, object value) { }
        public Vc14Continuation? PendingInstall() => pending;
        public void SavePendingInstall(Vc14Continuation record) => pending = record;
        public OperationResult Cleanup(Vc14Scan scan, IProgress<string>? progress, CancellationToken token) => OperationResult.Ok("无可清理");
        public Task<Vc14StepResult> RemoveAsync(Vc14RemovalPlanItem item, IProgress<string>? progress, CancellationToken token)
        { if (platform.Running != 4) throw new Exception("Installer must start before uninstall"); Removals++; platform.IsBusy = LeaveBusy; return Task.FromResult(new Vc14StepResult(FailRemoval ? 1603 : 0)); }
        public Task<Vc14StepResult> InstallAsync(string arch, IProgress<string>? progress, CancellationToken token) => throw new Exception("must never install in safe mode");
    }
}
