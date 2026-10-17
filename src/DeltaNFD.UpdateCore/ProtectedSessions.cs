using System.Security.AccessControl;
using System.Security.Principal;

namespace DeltaNFD.Updates;

public static class ProtectedSessions
{
    public static string BaseDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeltaNFD-FileUpdates");
    public static string Create()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("增量替换需要管理员身份。");
        var acl = new DirectorySecurity();
        acl.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
        if (!Directory.Exists(BaseDirectory)) new DirectoryInfo(BaseDirectory).Create(acl);
        ValidateDirectory(BaseDirectory);
        var directory = Path.Combine(BaseDirectory, Guid.NewGuid().ToString("N"));
        new DirectoryInfo(directory).Create(acl);
        Validate(directory);
        return directory;
    }
    public static void Validate(string session)
    {
        var full = Path.GetFullPath(session).TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.GetDirectoryName(full)!.Equals(Path.GetFullPath(BaseDirectory), StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) throw new InvalidDataException("更新会话目录非法。");
        ValidateDirectory(BaseDirectory);
        ValidateDirectory(full);
    }
    private static void ValidateDirectory(string directory)
    {
        FileUpdateRules.Under(directory, "probe");
        var acl = new DirectoryInfo(directory).GetAccessControl();
        var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !owner.Equals(admin) || !acl.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("更新暂存目录所有者或权限不可信。");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(admin) && !rule.IdentityReference.Equals(system))
                throw new UnauthorizedAccessException("更新暂存目录允许非管理员写入，已拒绝使用。");
    }
}
