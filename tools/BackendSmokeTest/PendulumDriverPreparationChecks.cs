using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

internal static class PendulumDriverPreparationChecks
{
    private static readonly PendulumPreparationArgs Prepare = new(false, true, PendulumPreparationPackage.Id);
    private static readonly PendulumPreparationArgs Resume = new(true, false, PendulumPreparationPackage.Id);
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        OperationResult Run(Fake env, PendulumPreparationArgs? args = null) => PendulumPreparationEngine.Run(env, args ?? Prepare);
        var fresh = new Fake();
        var first = Run(fresh);
        Check(first.Success && first.RequiresReboot && fresh.State?.Stage == "AwaitRestart", "Preparation queues reboot rather than admitting old boot");
        Check(fresh.BcdPending && !fresh.Mode && !fresh.Installed, "BCD setting is not current CI and never installs before reboot");
        Check(fresh.Root && fresh.Publisher && fresh.BootEdits == 1 && fresh.Backups == 1, "Known certificate scopes and one durable BCD backup");
        Check(fresh.State is { RootWasPresent: false, PublisherWasPresent: false, TestModeWasActive: false, RootAddedConfirmed: true, PublisherAddedConfirmed: true }, "Original state and completed trust tracked");
        Check(Run(fresh, Resume) is { Success: true, RequiresReboot: true } && fresh.BootEdits == 1, "Reopening before restart cannot fake readiness or redo BCD");
        fresh.Boot = Guid.NewGuid().ToString(); fresh.Mode = true;
        Check(Run(fresh, Resume).Success && fresh.State?.Stage == "Ready" && fresh.Installed, "Reboot resumes known driver installation");
        Check(fresh.BootEdits == 1 && fresh.TrustAdds == 2 && fresh.Installs == 1, "Resume never reimports trust or edits BCD");
        Check(Run(fresh, Resume).Success && fresh.Installs == 1, "Ready resume is idempotent");
        fresh.Installed = false;
        Check(!Run(fresh, Resume).Success && fresh.Installs == 1, "Respect explicit driver removal in backend as well as UI");
        Check(Run(fresh).Success && fresh.Installs == 2, "Fresh confirmation can reinstall removed driver");
        foreach (var args in new[] { Prepare with { ConfirmedSensitiveChanges = false }, Prepare with { PackageId = "other" } })
        { var env = new Fake(); Check(!Run(env, args).Success && env.Writes == 0, "No consent or wrong package does not write"); }
        var none = new Fake { Mode = true, Root = true, Publisher = true };
        Check(!Run(none, Resume).Success && none.Writes == 0, "App installation itself is not authorization");
        foreach (var fault in new[] { "verify", "read", "boot-id", "block" })
        { var env = new Fake { Fault = fault }; Check(!Run(env).Success && env.Writes == 0, "Unreadable identity/environment fails before writes: " + fault); }
        foreach (var fault in new[] { "save", "backup", "bcd", "root", "publisher", "trust-readback", "driver", "driver-readback" })
        {
            var env = new Fake { Fault = fault, Mode = fault.StartsWith("driver"), Root = fault.StartsWith("driver"), Publisher = fault.StartsWith("driver") };
            var result = Run(env);
            Check(!result.Success, "Fault never claims completion: " + fault);
            Check(env.State is not null || env.Writes == 0, "Fault preserves authorization/original record: " + fault);
            if (fault is "save" or "backup" or "bcd") Check(!env.Root && !env.Publisher && !env.Installed, "No later mutation after failed boot stage: " + fault);
        }
        foreach (var removeRoot in new[] { false, true })
        {
            var env = new Fake(); Run(env); env.Mode = true; env.Boot = Guid.NewGuid().ToString();
            if (removeRoot) env.Root = false; else env.Publisher = false;
            var count = env.Writes;
            Check(!Run(env, Resume).Success && env.Writes == count, "Revoked trust is not silently reimported");
        }
        var revoked = new Fake(); Run(revoked); revoked.Boot = Guid.NewGuid().ToString();
        var edits = revoked.BootEdits;
        Check(!Run(revoked, Resume).Success && revoked.BootEdits == edits, "Different boot without CI stops, never reenables mode automatically");
        var prior = new Fake { Mode = true, Root = true, Publisher = true };
        Check(Run(prior).Success && prior.TrustAdds == 0 && prior.BootEdits == 0 && prior.Backups == 0, "Existing prerequisites remain intact");
        Check(prior.State is { RootWasPresent: true, PublisherWasPresent: true, TestModeWasActive: true }, "Existing ownership preserved");
        foreach (var kind in new[] { "live", "unknown-launch" })
        {
            var env = new Fake { Mode = true, Root = true, Publisher = true }; Run(env);
            env.State!.ChildKind = "driver-Install"; env.State.ChildPid = kind == "live" ? 123 : null; env.Fault = "child";
            var count = env.Installs;
            Check(Run(env, Resume) is { Success: false, OutcomeUnknown: true } && env.Installs == count, "Unfinished child cannot be duplicated: " + kind);
        }
        foreach (var corrupt in new Action<PendulumPreparationRecord>[] { r => r.PackageId = "foreign", r => r.Id = "..", r => r.Stage = "execute", r => r.AuthorizedBootId = "unknown", r => r.Sid = "another-user" })
        { var env = new Fake { State = new() { Sid = "test-user", AuthorizedBootId = Guid.NewGuid().ToString() } }; corrupt(env.State); Check(!Run(env, Resume).Success && env.Installs == 0 && env.TrustAdds == 0 && env.BootEdits == 0, "Invalid record never authorizes system changes"); }
        Check(BackendModules.IsMutating(BackendModules.System, BackendCommands.SystemPendulumDriverPrepare)
            && BackendModules.RequiresWriteIntent(BackendModules.System, BackendCommands.SystemPendulumDriverPrepare), "Backend records write intent");
        Check(BackendModules.RetainsStaticBackup(BackendModules.System, BackendCommands.SystemPendulumDriverPrepare), "Successful static preparation retains original backup without unresolved intent");
        var roundtrip = JsonSerializer.Deserialize<PendulumPreparationRecord>(JsonSerializer.Serialize(fresh.State))!;
        Check(roundtrip.Id == fresh.State!.Id && roundtrip.Stage == "Ready" && roundtrip.Sid == fresh.Sid, "Journal identity survives serialization");
        Console.WriteLine($"Pendulum automatic preparation: {checks} checks passed; fake environment only, no host BCD/certificate/service/timer writes.");
    }
    private sealed class Fake : IPendulumPreparationEnvironment
    {
        public string Sid => "test-user";
        public string Boot = Guid.NewGuid().ToString();
        public string BootId => Fault == "boot-id" ? "unknown" : Boot;
        public string Fault = "";
        public bool Mode, Root, Publisher, Installed, BcdPending;
        public int Writes, BootEdits, TrustAdds, Installs, Backups;
        public PendulumPreparationRecord? State;
        public string? Blocker() => Fault == "block" ? "Blocked game/platform/elevation" : null;
        private void Fail(string point) { if (Fault == point) throw new IOException("Injected " + point); }
        public void VerifyPackage() => Fail("verify");
        public bool TestModeActive() => Mode;
        public bool TrustPresent(bool publisher) => publisher ? Publisher : Root;
        public PendulumWin10DriverStatus Driver() => PendulumWin10DriverInspection.Evaluate(Mode, Installed, Installed, Installed && Fault != "driver-readback", true);
        public PendulumPreparationRecord? Read() { Fail("read"); return State is null ? null : Clone(State); }
        private static PendulumPreparationRecord Clone(PendulumPreparationRecord record) => JsonSerializer.Deserialize<PendulumPreparationRecord>(JsonSerializer.Serialize(record))!;
        public void Save(PendulumPreparationRecord record) { Fail("save"); State = Clone(record); }
        public void CheckPreviousChild(PendulumPreparationRecord record) => Fail("child");
        private void BeforeWrite(PendulumPreparationRecord record)
        {
            if (State?.Id != record.Id || State.Sid != Sid || State.Stage != record.Stage) throw new Exception("No durable authorization before mutation");
            Writes++;
        }
        public void BackupBoot(PendulumPreparationRecord record) { BeforeWrite(record); Fail("backup"); record.BootBackupSha = new string('a', 64); Backups++; }
        public void EnableTestSigning(PendulumPreparationRecord record) { BeforeWrite(record); if (record.BootBackupSha.Length != 64) throw new Exception("Missing backup"); Fail("bcd"); BcdPending = true; BootEdits++; }
        public void AddTrust(bool publisher)
        {
            if (State is null || State.Stage != (publisher ? "PublisherTrust" : "RootTrust")) throw new Exception("No trust intent");
            Writes++; Fail(publisher ? "publisher" : "root"); TrustAdds++;
            if (Fault != "trust-readback") { if (publisher) Publisher = true; else Root = true; }
        }
        public void InstallOrStart(PendulumPreparationRecord record) { BeforeWrite(record); if (!Mode || !Root || !Publisher) throw new Exception("Missing runtime prerequisites"); Fail("driver"); Installs++; Installed = true; }
    }
}
