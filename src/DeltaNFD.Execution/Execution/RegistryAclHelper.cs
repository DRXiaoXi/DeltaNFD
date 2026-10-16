using System.Security.AccessControl;
using System.Security.Principal;
using DeltaNFD.Native;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>
/// 注册表写入权限处理。
/// HKLM\SYSTEM\CurrentControlSet\Enum 下的设备键默认由 TrustedInstaller 所有、管理员只有读权限，
/// 直接写会 Access Denied；这里在必要时启用 SeTakeOwnershipPrivilege 接管所有权并授予
/// Administrators 完全控制（等效于在 regedit 里手动「取得所有权」）。
/// </summary>
internal static class RegistryAclHelper
{
    /// <summary>
    /// 确保当前进程能写入指定键（HKLM 相对路径，如 SYSTEM\CurrentControlSet\Enum\PCI\...）。
    /// 返回 false 表示键不存在（调用方可用 CreateSubKey 自行创建）。
    /// </summary>
    public static bool EnsureWritable(string keyPath)
    {
        using (var readKey = Registry.LocalMachine.OpenSubKey(keyPath))
        {
            if (readKey is null)
            {
                return false;
            }
        }

        try
        {
            using var probe = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (System.Security.SecurityException)
        {
        }

        TakeOwnershipAndGrantAdministrators(keyPath);
        return true;
    }

    private static void TakeOwnershipAndGrantAdministrators(string keyPath)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        // 1. 启用 SeTakeOwnershipPrivilege 后把键所有者改为 Administrators
        Privilege.Enable("SeTakeOwnershipPrivilege");
        using (var key = Registry.LocalMachine.OpenSubKey(
                   keyPath, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.TakeOwnership))
        {
            if (key is null)
            {
                throw new UnauthorizedAccessException($"无法打开注册表键：HKLM\\{keyPath}");
            }

            var ownerAcl = key.GetAccessControl(AccessControlSections.Owner);
            ownerAcl.SetOwner(admins);
            key.SetAccessControl(ownerAcl);
        }

        // 2. 成为所有者后修改 DACL，授予 Administrators 完全控制
        using (var key = Registry.LocalMachine.OpenSubKey(
                   keyPath, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ChangePermissions))
        {
            if (key is null)
            {
                throw new UnauthorizedAccessException($"无法打开注册表键：HKLM\\{keyPath}");
            }

            var dacl = key.GetAccessControl(AccessControlSections.Access);
            dacl.ResetAccessRule(new RegistryAccessRule(
                admins,
                RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            key.SetAccessControl(dacl);
        }
    }
}
