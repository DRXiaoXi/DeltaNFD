using System.Security.AccessControl;
using System.Security.Principal;

namespace DeltaNFD.Execution.Procman;

internal static class NvidiaGpuShareLease
{
    // A kernel-owned file share lock survives thread switches and is released on process death.
    // The protected common directory prevents separate Windows users from controlling the global setting concurrently.
    internal static IDisposable Acquire()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Delta NFD GPU Lease");
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        for (var p = root; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("GPU 共享锁目录包含重解析点。");
        var directory = new DirectoryInfo(root);
        if (!directory.Exists)
        {
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(admins);
            foreach (var sid in new[] { admins, system }) security.AddAccessRule(new(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.Create(security);
        }
        var acl = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!admins.Equals(acl.GetOwner(typeof(SecurityIdentifier))) && !system.Equals(acl.GetOwner(typeof(SecurityIdentifier))))
            throw new IOException("GPU 共享锁目录所有者不可信，未接管全局设置。");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !admins.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference))
                throw new IOException("GPU 共享锁目录权限不符合要求，未接管全局设置。");
        var path = Path.Combine(root, "active.lock");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("GPU 共享锁文件类型异常。");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("另一实例可能正在调整 NVIDIA 全局比例，请先关闭其帧格或稍后重试。", ex); }
    }
}
