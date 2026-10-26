using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using DeltaNFD.Native;

namespace DeltaNFD.Services;

/// <summary>
/// 强制替换前单个 DLL 的属主、组与 DACL 快照（完整 SDDL 含 O:/G:/D: 与 protected 标志）。
/// UsersCanRead 表示普通用户能读到文件（否则应用会加载失败），AdminsCanWrite 表示管理员组能
/// 覆盖文件（否则即使提权也会拒绝访问）。
/// </summary>
internal sealed record Vc14FileAcl(string OwnerSid, string GroupSid, string Sddl, bool DaclProtected,
    bool UsersCanRead, bool AdminsCanWrite)
{
    internal static readonly Vc14FileAcl Unknown = new("", "", "", false, false, false);
}

/// <summary>
/// 强制替换 14.x 的文件权限处理（对照外部 Vc14RuntimeFix 1.2 的 ACL/locked 能力）：
/// 检测普通用户可读、管理员可写；替换前按需临时接管所有权并授予写权限；替换后恢复原属主、
/// 组与 DACL；权限被改坏的文件重置为目录继承权限；重启后替换的暂存文件使用安全默认 ACL。
/// 只操作明确的单个文件，不递归、不改目录 ACL。
/// </summary>
internal static class Vc14ForceReplaceAcl
{
    internal static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    internal static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    internal static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    internal static readonly SecurityIdentifier EveryoneSid = new("S-1-1-0");
    internal static readonly SecurityIdentifier AuthenticatedUsersSid = new("S-1-5-11");
    internal static readonly SecurityIdentifier TrustedInstallerSid =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    internal static readonly SecurityIdentifier AllApplicationPackagesSid = new("S-1-15-2-1");
    internal static readonly SecurityIdentifier AllRestrictedApplicationPackagesSid = new("S-1-15-2-2");

    /// <summary>读取属主/组/DACL，并判断普通用户可读、管理员可写（Allow 减 DENY）。</summary>
    internal static Vc14FileAcl Inspect(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group
            | AccessControlSections.Access);
        var owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value ?? "";
        var group = (security.GetGroup(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value ?? "";
        var usersAllowRead = false;
        var usersDenyRead = false;
        var adminsAllowWrite = false;
        var adminsDenyWrite = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference is not SecurityIdentifier sid) continue;
            var allow = rule.AccessControlType == AccessControlType.Allow;
            if (IsBroadReadPrincipal(sid))
            {
                // 普通用户可读：Users/Everyone/Authenticated Users 任一 Allow Read，且无 DENY ReadData。
                if (allow && (rule.FileSystemRights & FileSystemRights.Read) == FileSystemRights.Read)
                    usersAllowRead = true;
                else if (!allow && (rule.FileSystemRights & FileSystemRights.ReadData) != 0)
                    usersDenyRead = true;
            }
            if (sid.Equals(AdministratorsSid))
            {
                if (allow && (rule.FileSystemRights & FileSystemRights.Write) == FileSystemRights.Write)
                    adminsAllowWrite = true;
                else if (!allow && (rule.FileSystemRights & FileSystemRights.WriteData) != 0)
                    adminsDenyWrite = true;
            }
        }
        var sddl = security.GetSecurityDescriptorSddlForm(
            AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        return new Vc14FileAcl(owner, group, sddl, security.AreAccessRulesProtected,
            usersAllowRead && !usersDenyRead, adminsAllowWrite && !adminsDenyWrite);
    }

    private static bool IsBroadReadPrincipal(SecurityIdentifier sid) =>
        sid.Equals(UsersSid) || sid.Equals(EveryoneSid) || sid.Equals(AuthenticatedUsersSid);

    internal static Vc14FileAcl InspectOrUnknown(string path)
    {
        try { return Inspect(path); }
        catch (Exception) { return Vc14FileAcl.Unknown; }
    }

    /// <summary>尝试实际写入目标文件，判断当前进程（提权后的管理员）能否直接覆盖它。</summary>
    internal static bool CanWrite(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// 确保当前进程可以覆盖目标。先尝试直接添加临时 ACE（文件属主或已有 WRITE_DAC 时可行）；
    /// 仍失败则启用 SeTakeOwnershipPrivilege 接管所有权后再授权。返回是否成功及接管方式说明。
    /// </summary>
    internal static bool EnsureWritable(string path, out bool tookOwnership, out string detail)
    {
        tookOwnership = false;
        detail = "";
        if (CanWrite(path)) return true;

        Exception? grantError = null;
        try
        {
            GrantTemporaryWrite(path);
            if (CanWrite(path)) { detail = "已临时添加写权限"; return true; }
        }
        catch (Exception ex) { grantError = ex; }

        try
        {
            Privilege.Enable("SeTakeOwnershipPrivilege");
            TakeOwnership(path);
            tookOwnership = true;
            GrantTemporaryWrite(path);
            if (CanWrite(path)) { detail = "已临时接管所有权并添加写权限"; return true; }
            detail = "接管所有权后文件仍不可写";
            return false;
        }
        catch (Exception ex)
        {
            detail = "无法取得文件写权限：" + ex.Message + (grantError is null ? "" : "（直接授权也失败：" + grantError.Message + "）");
            return false;
        }
    }

    private static void TakeOwnership(string path)
    {
        var info = new FileInfo(path);
        var owner = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法读取当前用户 SID。");
        var security = info.GetAccessControl(AccessControlSections.Owner);
        security.SetOwner(owner);
        FileSystemAclExtensions.SetAccessControl(info, security);
    }

    private static void GrantTemporaryWrite(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl(AccessControlSections.Access);
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法读取当前用户 SID。");
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(info, security);
    }

    /// <summary>只恢复属主/组、不动 DACL。SYSTEM/TrustedInstaller 属主需要 SeRestorePrivilege。</summary>
    internal static bool RestoreIdentity(string path, string ownerSid, string groupSid, out string detail)
    {
        detail = "";
        try
        {
            var info = new FileInfo(path);
            var current = info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group);
            var currentOwner = (current.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value ?? "";
            var currentGroup = (current.GetGroup(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value ?? "";
            var needOwner = ownerSid.Length > 0 && !currentOwner.Equals(ownerSid, StringComparison.OrdinalIgnoreCase);
            var needGroup = groupSid.Length > 0 && !currentGroup.Equals(groupSid, StringComparison.OrdinalIgnoreCase);
            if (!needOwner && !needGroup) return true;
            try { Privilege.Enable("SeRestorePrivilege"); }
            catch (Exception ex) { detail = "恢复属主/组需要管理员还原特权：" + ex.Message + "。"; }
            if (needOwner) current.SetOwner(new SecurityIdentifier(ownerSid));
            if (needGroup) current.SetGroup(new SecurityIdentifier(groupSid));
            FileSystemAclExtensions.SetAccessControl(info, current);
            var after = Inspect(path);
            var problems = new List<string>();
            if (needOwner && !after.OwnerSid.Equals(ownerSid, StringComparison.OrdinalIgnoreCase))
                problems.Add("属主仍为 " + (after.OwnerSid.Length == 0 ? "未知" : after.OwnerSid));
            if (needGroup && !after.GroupSid.Equals(groupSid, StringComparison.OrdinalIgnoreCase))
                problems.Add("组仍为 " + (after.GroupSid.Length == 0 ? "未知" : after.GroupSid));
            if (problems.Count == 0) return true;
            detail = (detail.Length == 0 ? "" : detail + " ") + "恢复后与原值不一致：" + string.Join("；", problems);
            return false;
        }
        catch (Exception ex)
        {
            detail = (detail.Length == 0 ? "" : detail + " ") + "恢复原属主/组失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 恢复替换前的原属主、组与 DACL：先写 DACL（不依赖还原特权），再单独恢复属主/组
    /// （SYSTEM/TrustedInstaller 属主需要 SeRestorePrivilege）。最后按语义复核。
    /// </summary>
    internal static bool Restore(string path, Vc14FileAcl snapshot, out string detail)
    {
        detail = "";
        if (snapshot.Sddl.Length == 0) { detail = "缺少原 ACL 快照。"; return false; }
        try
        {
            var info = new FileInfo(path);
            var current = info.GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
            var currentOwner = (current.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value ?? "";
            var currentGroup = (current.GetGroup(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value ?? "";
            var needOwner = snapshot.OwnerSid.Length > 0
                && !currentOwner.Equals(snapshot.OwnerSid, StringComparison.OrdinalIgnoreCase);
            var needGroup = snapshot.GroupSid.Length > 0
                && !currentGroup.Equals(snapshot.GroupSid, StringComparison.OrdinalIgnoreCase);
            if (needOwner || needGroup)
            {
                try { Privilege.Enable("SeRestorePrivilege"); }
                catch (Exception ex) { detail = "恢复属主/组需要管理员还原特权：" + ex.Message + "。"; }
            }

            var dacl = new FileSecurity();
            dacl.SetSecurityDescriptorSddlForm(snapshot.Sddl, AccessControlSections.Access);
            FileSystemAclExtensions.SetAccessControl(info, dacl);

            if (needOwner || needGroup)
            {
                var ownership = info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group);
                if (needOwner) ownership.SetOwner(new SecurityIdentifier(snapshot.OwnerSid));
                if (needGroup) ownership.SetGroup(new SecurityIdentifier(snapshot.GroupSid));
                FileSystemAclExtensions.SetAccessControl(info, ownership);
            }

            var after = Inspect(path);
            var problems = new List<string>();
            if (snapshot.OwnerSid.Length > 0 && !after.OwnerSid.Equals(snapshot.OwnerSid, StringComparison.OrdinalIgnoreCase))
                problems.Add("属主为 " + (after.OwnerSid.Length == 0 ? "未知" : after.OwnerSid));
            if (snapshot.GroupSid.Length > 0 && !after.GroupSid.Equals(snapshot.GroupSid, StringComparison.OrdinalIgnoreCase))
                problems.Add("组为 " + (after.GroupSid.Length == 0 ? "未知" : after.GroupSid));
            if (after.UsersCanRead != snapshot.UsersCanRead)
                problems.Add("用户可读=" + after.UsersCanRead);
            if (after.AdminsCanWrite != snapshot.AdminsCanWrite)
                problems.Add("管理员可写=" + after.AdminsCanWrite);
            if (after.DaclProtected != snapshot.DaclProtected)
                problems.Add("DACL protected=" + after.DaclProtected);
            if (problems.Count == 0) return true;
            detail = (detail.Length == 0 ? "" : detail + " ") + "恢复后与原值不一致：" + string.Join("；", problems);
            return false;
        }
        catch (Exception ex)
        {
            detail = (detail.Length == 0 ? "" : detail + " ") + "恢复原 ACL/属主失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 把文件权限重置为父目录继承权限。外部脚本用 icacls /reset；本实现优先使用等价的 .NET
    /// SetAccessRuleProtection(false,false)（探针验证与 icacls /reset 逐字节等价），失败再回退 icacls，
    /// 最后回退安全默认 ACL。随后复核普通用户可读与管理员可写。
    /// </summary>
    internal static bool ResetInherited(string path, out string detail)
    {
        detail = "";
        try
        {
            var info = new FileInfo(path);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(false, false);
            FileSystemAclExtensions.SetAccessControl(info, security);
            if (Inspect(path).UsersCanRead)
            {
                EnsureAdminsCanWrite(path, ref detail);
                return true;
            }
            detail = "继承权限重置后普通用户仍不可读。";
        }
        catch (Exception ex) { detail = "继承权限重置失败：" + ex.Message; }

        try
        {
            if (RunIcacls(path, "/reset") && Inspect(path).UsersCanRead)
            {
                EnsureAdminsCanWrite(path, ref detail);
                return true;
            }
        }
        catch (Exception ex) { detail = (detail.Length == 0 ? "" : detail + "；") + "icacls /reset 失败：" + ex.Message; }

        // 最后的兜底：写一份与系统运行库相符的安全 ACL（SYSTEM/Administrators 完全控制，
        // 普通用户与常见应用容器只读），至少保证 DLL 能被普通进程加载。
        if (ApplySafeAcl(path, out var safeDetail))
        {
            detail = safeDetail;
            return Inspect(path).UsersCanRead;
        }
        detail = (detail.Length == 0 ? "" : detail + "；") + safeDetail;
        return false;
    }

    private static void EnsureAdminsCanWrite(string path, ref string detail)
    {
        try
        {
            if (Inspect(path).AdminsCanWrite) return;
            GrantAdministratorsWrite(path);
            if (!Inspect(path).AdminsCanWrite) detail = "重置继承权限后管理员仍不可写，已尝试安全 ACL。";
        }
        catch (Exception ex) { detail = "补充管理员写权限失败：" + ex.Message; }
    }

    private static void GrantAdministratorsWrite(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(info, security);
    }

    /// <summary>重启后替换的暂存文件使用安全默认 ACL，避免 Windows\Temp 的 ACL 被搬进 System32。</summary>
    internal static bool ApplySafeAcl(string path, out string detail)
    {
        detail = "";
        try
        {
            var info = new FileInfo(path);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(AllApplicationPackagesSid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(AllRestrictedApplicationPackagesSid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(TrustedInstallerSid, FileSystemRights.FullControl, AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(info, security);
            return true;
        }
        catch (Exception ex)
        {
            detail = "写入安全 ACL 失败：" + ex.Message;
            return false;
        }
    }

    private static bool RunIcacls(string path, string arguments)
    {
        var icacls = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "icacls.exe");
        var info = new ProcessStartInfo(File.Exists(icacls) ? icacls : "icacls.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(path);
        foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("/C");
        info.ArgumentList.Add("/Q");
        using var process = Process.Start(info);
        if (process is null) return false;
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return false;
        }
        return process.ExitCode == 0;
    }

    /// <summary>用于结果/清单的属主描述（SID 无法翻译时保留 SID）。</summary>
    internal static string DescribeOwnerSid(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return "";
        try { return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
        catch (Exception) { return sid; }
    }

    /// <summary>测试/诊断用：把任意 Win32 错误码转为可读原因。</summary>
    internal static string Explain(Exception ex) => ex is Win32Exception win32
        ? ex.Message + "（Win32 " + win32.NativeErrorCode + "）"
        : ex.Message;
}
