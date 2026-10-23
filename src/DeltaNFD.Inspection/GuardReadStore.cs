using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;
internal sealed class GuardReadStore : IGuardStateStore
{
    private readonly string _path;
    private readonly bool _machine;
    internal static readonly string DefaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Delta NFD", "RuntimeGuard", "state.json");
    private static void NoReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("防护状态路径包含重解析点，拒绝访问。");
    }

    private static void CheckSecurity(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ?? throw new UnauthorizedAccessException("无法确认防护状态属主。");
        if (!owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && !owner.IsWellKnown(WellKnownSidType.LocalSystemSid))
            throw new UnauthorizedAccessException("防护状态属主不是管理员组或 SYSTEM。");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            var write = rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership);
            if (rule.AccessControlType == AccessControlType.Allow && write != 0 && !sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && !sid.IsWellKnown(WellKnownSidType.LocalSystemSid))
                throw new UnauthorizedAccessException("防护状态可被非管理员修改，拒绝使用。");
        }
    }

    private void ValidateExisting()
    {
        NoReparse(_path);
        if (!_machine)
            return;
        var directory = Path.GetDirectoryName(_path)!;
        var parent = Path.GetDirectoryName(directory)!;
        if (Directory.Exists(parent))
            CheckSecurity(FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(parent)));
        if (Directory.Exists(directory))
            CheckSecurity(FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(directory)));
        if (File.Exists(_path))
            CheckSecurity(FileSystemAclExtensions.GetAccessControl(new FileInfo(_path)));
    }

    public GuardProtectionState Load()
    {
        ValidateExisting();
        if (!File.Exists(_path))
            return new();
        if (new FileInfo(_path).Length > 1024 * 1024)
            throw new InvalidDataException("防护状态文件过大。");
        var result = JsonSerializer.Deserialize<GuardProtectionState>(File.ReadAllText(_path)) ?? throw new InvalidDataException("防护状态为空。");
        if (result.Version != 1 || result.Ifeo is null || result.Acls is null || result.LegacyAclRepairs is null || result.LegacyIfeoRepairs is null || (result.Mode is not null && !Enum.IsDefined(result.Mode.Value)) || result.Ifeo.Any(e => !GuardProtectionInspection.Names.Contains(e.Name, StringComparer.OrdinalIgnoreCase) || !e.Marker.StartsWith(GuardProtectionInspection.OwnerPrefix, StringComparison.Ordinal)) || result.Ifeo.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Ifeo.Count)
            throw new InvalidDataException("防护状态结构无效。");
        // 清理快照只用于留档，不参与写入决策，不再因内容不合预期拒绝整个状态文件。
        if (result.LegacyIfeoRepairs.Any(r => r is null || r.Before is null || !GuardProtectionInspection.Names.Contains(r.Name, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("旧 IFEO 清理记录无效。");
        // ACL 路径只会被剥离 Everyone 拒绝执行，且仅限 UE4 前置包文件名。
        if (result.Acls.Any(a => a is null || !Path.IsPathFullyQualified(a.Path)) || result.LegacyAclRepairs.Any(r => r is null || !Path.IsPathFullyQualified(r.Path)))
            throw new InvalidDataException("防护 ACL 路径无效。");
        return result;
    }

    internal GuardReadStore()
    {
        _path = DefaultPath;
        _machine = true;
    }
}
