using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

public static class PendulumPreparationInspection
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DeltaNFD Pendulum Preparation");
    public static string RecordPath => Path.Combine(DirectoryPath, "state.json");
    public static void VerifyProtectedDirectory()
    {
        for (var dir = new DirectoryInfo(DirectoryPath); dir is not null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("自动准备目录不能经过重解析点。");
        VerifyAccess(new DirectoryInfo(DirectoryPath).GetAccessControl());
    }
    public static void VerifyProtectedFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("自动准备文件不能是重解析点。");
        VerifyAccess(new FileInfo(path).GetAccessControl());
    }
    private static void VerifyAccess(FileSystemSecurity acl)
    {
        var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ?? throw new IOException("无法确认自动准备文件所有者。");
        if (owner.Value is not ("S-1-5-18" or "S-1-5-32-544")) throw new IOException("自动准备目录所有者不可信。");
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0
                && ((SecurityIdentifier)rule.IdentityReference).Value is not ("S-1-5-18" or "S-1-5-32-544"))
                throw new IOException("自动准备目录允许非管理员写入，拒绝自动续办。");
    }
    public static PendulumPreparationRecord? Read()
    {
        if (!File.Exists(RecordPath)) return null;
        VerifyProtectedDirectory();
        VerifyProtectedFile(RecordPath);
        using var file = new FileStream(RecordPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 65536) throw new IOException("自动准备记录大小异常。");
        var value = JsonSerializer.Deserialize<PendulumPreparationRecord>(file) ?? throw new IOException("自动准备记录为空。");
        Validate(value);
        if (value.Sid != WindowsIdentity.GetCurrent().User?.Value) throw new IOException("自动准备记录属于其他用户。");
        return value;
    }
    public static void Validate(PendulumPreparationRecord value)
    {
        if (!Guid.TryParseExact(value.Id, "N", out _) || !Guid.TryParse(value.AuthorizedBootId, out _)
            || value.PackageId != PendulumPreparationPackage.Id || string.IsNullOrWhiteSpace(value.Sid)
            || value.Stage is not ("Authorized" or "BootBackup" or "BootSetting" or "RootTrust" or "PublisherTrust" or "AwaitRestart" or "Driver" or "Ready"))
            throw new IOException("自动准备记录身份或阶段无效，原件已保留。");
    }
}
