using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using DeltaNFD.Services;

internal static class RegistryBackupChecks
{
    internal static void Run(Action<bool, string> check)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var admin = identity.User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var creator = new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);
        var parentPath = @"Software\DeltaNFD-AclFixture-" + Guid.NewGuid().ToString("N");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        var image = Path.Combine(NvDisplayContainerService.DriverStore, @"nv_test.inf_amd64_abcd\Display.NvContainer\NVDisplay.Container.exe");
        var original = new NvContainerBackup(image, NvContainerMode.Automatic, true, true);
        var bytes = NvContainerRegistryBackup.Encode(original);
        const string value = NvContainerRegistryBackup.ValueName;

        RegistrySecurity Policy(bool inheritable, bool allowUserWrites)
        {
            var policy = new RegistrySecurity();
            policy.SetOwner(admin);
            policy.SetAccessRuleProtection(true, false);
            var flags = inheritable ? InheritanceFlags.ContainerInherit : InheritanceFlags.None;
            foreach (var sid in new[] { admin, system })
                policy.AddAccessRule(new RegistryAccessRule(sid, RegistryRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));
            policy.AddAccessRule(new RegistryAccessRule(users, allowUserWrites ? RegistryRights.SetValue : RegistryRights.ReadKey,
                flags, PropagationFlags.None, AccessControlType.Allow));
            if (inheritable)
                policy.AddAccessRule(new RegistryAccessRule(creator, RegistryRights.FullControl, flags, PropagationFlags.InheritOnly, AccessControlType.Allow));
            return policy;
        }
        RegistrySecurity ReadAcl(RegistryKey key) => key.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        void Reject(Action action, string label)
        {
            var rejected = false;
            try { action(); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected, label);
        }

        try
        {
            root.CreateSubKey(parentPath).Dispose();
            using var parent = root.OpenSubKey(parentPath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.FullControl)!;
            parent.SetAccessControl(Policy(inheritable: true, allowUserWrites: false));
            NvContainerRegistryBackup Store(string leaf) => new(RegistryHive.CurrentUser, parentPath + @"\" + leaf, admin);
            var fresh = Store("fresh");
            check(fresh.Read() is null, "missing backup recognized without creating a key");
            fresh.Save(original);
            using (var key = parent.OpenSubKey("fresh")!)
            {
                var acl = ReadAcl(key);
                check(acl.AreAccessRulesProtected && acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Count == 2,
                    "new ACL is protected and has only the expected principals under an inheritable parent");
            }
            check(fresh.Read() == original, "new record round trips");
            Reject(() => fresh.Save(original), "new save never overwrites existing bytes");
            Reject(() => fresh.Clear(original with { WasRunning = false }), "clearing another backup is refused");
            fresh.Clear(original);

            var legacy = Store("inherited");
            using (var key = parent.CreateSubKey("inherited"))
            {
                key.SetValue(value, bytes, RegistryValueKind.Binary);
                var before = ReadAcl(key);
                check(!before.AreAccessRulesProtected && before.GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<RegistryAccessRule>().Any(r => r.IsInherited), "real Windows key reproduces the rejected inherited ACL");
                check(legacy.Read() == original && !ReadAcl(key).AreAccessRulesProtected, "legacy original is visible and inspection never rewrites permissions");
                legacy.PrepareRestore(original);
                var after = ReadAcl(key);
                check(after.AreAccessRulesProtected && after.GetAccessRules(true, true, typeof(SecurityIdentifier)).Count == 2,
                    "confirmed restore tightens inherited read permissions");
                check(((byte[])key.GetValue(value)!).SequenceEqual(bytes), "legacy backed-up bytes remain exactly equal after repair");
            }
            legacy.Clear(original);

            var empty = Store("empty");
            using (var key = parent.CreateSubKey("empty"))
            {
                check(!ReadAcl(key).AreAccessRulesProtected && empty.Read() is null, "empty legacy key does not block a new original backup");
                empty.Save(original);
                check(ReadAcl(key).AreAccessRulesProtected && empty.Read() == original, "empty legacy key is secured before recording the original");
            }

            var foreign = Store("writable");
            parent.CreateSubKey("writable").Dispose();
            using (var key = parent.OpenSubKey("writable", RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.FullControl)!)
            {
                key.SetAccessControl(Policy(inheritable: false, allowUserWrites: true));
                key.SetValue(value, bytes, RegistryValueKind.Binary);
                var before = ReadAcl(key).GetSecurityDescriptorBinaryForm();
                Reject(() => foreign.Read(), "potentially user-writable contents never acquire trust");
                Reject(() => foreign.PrepareRestore(original), "repair cannot bless an untrusted writer");
                check(ReadAcl(key).GetSecurityDescriptorBinaryForm().SequenceEqual(before) &&
                    ((byte[])key.GetValue(value)!).SequenceEqual(bytes), "untrusted key ACL and value are unchanged");
            }

            var unrelated = Store("unrelated");
            using (var key = parent.CreateSubKey("unrelated"))
            {
                key.SetValue("Unrelated", 7);
                Reject(() => unrelated.Save(original), "unknown key contents prevent permission changes");
                check(!ReadAcl(key).AreAccessRulesProtected && Equals(key.GetValue("Unrelated"), 7), "unrelated data and ACL stay unchanged");
            }

            var malformed = Store("malformed");
            using (var key = parent.CreateSubKey("malformed"))
            {
                key.SetValue(value, new byte[] { 0x42 }, RegistryValueKind.Binary);
                Reject(() => malformed.PrepareRestore(original), "malformed content prevents permission changes");
                check(!ReadAcl(key).AreAccessRulesProtected && ((byte[])key.GetValue(value)!).SequenceEqual(new byte[] { 0x42 }),
                    "bad backup bytes remain exactly equal");
            }
            Reject(() => fresh.IsCanonicalAcl(new RawSecurityDescriptor("O:BUD:(A;;KR;;;BU)")),
                "untrusted owner prevents permission changes even with read-only DACL");
            Reject(() => fresh.IsCanonicalAcl(new RawSecurityDescriptor($"O:{admin.Value}D:NO_ACCESS_CONTROL")),
                "a null DACL is never treated as compatible");
        }
        finally { root.DeleteSubKeyTree(parentPath, throwOnMissingSubKey: false); }
    }
}
