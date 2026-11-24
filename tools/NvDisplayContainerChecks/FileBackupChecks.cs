using System.Text.Json.Nodes;
using System.Security.AccessControl;
using System.Security.Principal;
using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class FileBackupChecks
{
    internal static void Run(string isolated, string image, Action<bool, string> check)
    {
        var backup = new NvContainerBackup(image, NvContainerMode.Automatic, Delayed: true, WasRunning: true);
        var directory = Path.Combine(isolated, "files");
        var path = Path.Combine(directory, "display-container.json");
        using var identity = WindowsIdentity.GetCurrent();
        var testAdministrator = identity.User!.Value;
        var legacy = (NvContainerBackup?)null;
        var legacyReads = 0;
        NvContainerFileBackup Store(string machine = "machine") => new(directory, machine, () =>
        {
            legacyReads++;
            return legacy;
        }, administratorSid: testAdministrator);
        var store = Store();

        void Reject(Action action, string label)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or BackendContractException)
            { check(true, label); return; }
            check(false, label);
        }

        check(store.Read() is null && !Directory.Exists(directory), "new read does not create a directory");
        legacy = backup;
        check(store.Read() == backup && !File.Exists(path), "legacy preview is read-only");
        Reject(() => store.Save(backup), "active legacy original cannot be shadowed");
        Reject(() => store.PrepareRestore(backup with { WasRunning = false }), "changed legacy original is not migrated");
        store.PrepareRestore(backup);
        var readsAtMigration = legacyReads;
        check(File.Exists(path) && Store().Read() == backup && legacyReads == readsAtMigration,
            "file migration is durable and takes precedence over legacy");
        check(new DirectoryInfo(directory).GetAccessControl().AreAccessRulesProtected
            && new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value == testAdministrator,
            "migration creates a protected directory and the expected Windows file owner");
        Reject(() => store.Save(backup), "active file original is never overwritten");
        Reject(() => store.Clear(backup with { Delayed = false }), "another original cannot be marked restored");
        Reject(() => Store("other-machine").Read(), "another machine cannot use the file");

        var state = new NvContainerState(image, NvContainerMode.Disabled, true, NvContainerRun.Stopped, 0);
        var available = NvDisplayContainerService.Snapshot(BackendModules.Gpu, NvDisplayContainerService.Describe(state, store.Read()),
            stateVerified: true, offerRecovery: true);
        check(available.Items is [{ HasBackup: true }] && available.RecoveryScopes is [{ Fingerprint: { Length: 64 } }],
            "only a matching backup advertises a confirmation fingerprint");
        var mismatch = NvDisplayContainerService.Describe(state with { Delayed = false }, store.Read());
        check(NvDisplayContainerService.Snapshot(BackendModules.System, mismatch, stateVerified: true, offerRecovery: true)
            .RecoveryScopes.Count == 0, "changed service configuration offers no restore");

        store.Clear(backup);
        check(Store().Read() is null && JsonNode.Parse(File.ReadAllText(path))!["Restored"]!.GetValue<bool>()
            && legacyReads == readsAtMigration, "restart preserves restored original and does not replay legacy");
        var next = backup with { Mode = NvContainerMode.Manual, Delayed = false, WasRunning = false };
        Store().Save(next);
        check(Store().Read() == next, "a subsequent disable records the new original");
        var valid = File.ReadAllText(path);
        foreach (var name in new[] { "Delayed", "WasRunning" })
        {
            var invalid = JsonNode.Parse(valid)!.AsObject();
            invalid["Backup"]!.AsObject().Remove(name);
            File.WriteAllText(path, invalid.ToJsonString());
            Reject(() => Store().Read(), "missing original field " + name + " refuses restore");
            Reject(() => Store().Save(backup), "malformed file is never replaced");
            check(legacyReads == readsAtMigration, "malformed file cannot silently fall back to legacy");
        }
        File.WriteAllText(path, valid);
        var file = new FileInfo(path);
        var acl = file.GetAccessControl();
        var otherWriter = new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Write, AccessControlType.Allow);
        acl.AddAccessRule(otherWriter);
        file.SetAccessControl(acl);
        Reject(() => Store().Read(), "Windows file allowing other writers cannot be used");
        acl.RemoveAccessRuleSpecific(otherWriter);
        file.SetAccessControl(acl);
        check(Store().Read() == next, "restoring trusted fixture permissions makes the same file readable");
        var replacement = next with { Image = image.Replace("abcd", "eeee") };
        Store().ReplaceConflicting(next, replacement);
        var archived = Directory.GetFiles(directory, "display-container.conflict-*.json").Single();
        check(Store().Read() == replacement && File.ReadAllText(archived) == valid,
            "file conflict archives the exact original record and activates the replacement");

        var legacyDirectory = Path.Combine(isolated, "legacy-conflict");
        var legacyStore = new NvContainerFileBackup(legacyDirectory, "machine", () => backup, testAdministrator);
        legacyStore.ReplaceConflicting(backup, replacement);
        check(legacyStore.Read() == replacement && Directory.GetFiles(legacyDirectory, "display-container.conflict-*.json").Length == 1,
            "registry conflict is archived before the active file supersedes the legacy value");
        check(Directory.GetFiles(directory, "*.tmp").Length == 0, "no temporary files are left behind");
    }
}
