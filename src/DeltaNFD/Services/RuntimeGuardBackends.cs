using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DeltaNFD.Services;

internal sealed class GuardRegistryBackend : IGuardRegistry
{
    private static RegistryKey Base() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
    public GuardRegistryValue Read(string name)
    {
        using var root = Base();
        using var key = root.OpenSubKey(RuntimeGuardProtection.IfeoRoot + "\\" + name);
        if (key is null) return new(null, RegistryValueKind.None, null, false);
        var names = key.GetValueNames();
        var present = names.Contains("Debugger", StringComparer.OrdinalIgnoreCase);
        var raw = present ? key.GetValue("Debugger", null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
        var kind = present ? key.GetValueKind("Debugger") : RegistryValueKind.None;
        return new(raw as string ?? (present ? "<unsupported debugger type>" : null), kind,
            key.GetValue(RuntimeGuardProtection.OwnerName) as string ?? (names.Contains(RuntimeGuardProtection.OwnerName, StringComparer.OrdinalIgnoreCase) ? "<invalid owner>" : null),
            names.Contains("RuntimeGuardHotPatch_Owner", StringComparer.OrdinalIgnoreCase));
    }
    private static RegistryKey? Open(string name, bool create)
    {
        using var root = Base();
        var path = RuntimeGuardProtection.IfeoRoot + "\\" + name;
        using var existing = root.OpenSubKey(path);
        if (existing is not null) return root.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.SetValue)
            ?? throw new UnauthorizedAccessException("无法写入 IFEO：" + name);
        if (!create) return null;
        using var parent = root.OpenSubKey(RuntimeGuardProtection.IfeoRoot, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.CreateSubKey)
            ?? throw new UnauthorizedAccessException("无法创建 IFEO 子键。");
        return parent.CreateSubKey(name, true);
    }
    public void SetDebugger(string name, string? value, RegistryValueKind kind)
    {
        using var key = Open(name, kind != RegistryValueKind.None);
        if (kind == RegistryValueKind.None) key?.DeleteValue("Debugger", false);
        else key!.SetValue("Debugger", value ?? "", kind);
    }
    public void SetOwner(string name, string? value)
    {
        using var key = Open(name, value is not null);
        if (value is null) key?.DeleteValue(RuntimeGuardProtection.OwnerName, false);
        else key!.SetValue(RuntimeGuardProtection.OwnerName, value, RegistryValueKind.String);
    }
    public void RemoveEmptyKey(string name)
    {
        using var root = Base();
        var path = RuntimeGuardProtection.IfeoRoot + "\\" + name;
        using (var key = root.OpenSubKey(path))
            if (key is null || key.ValueCount != 0 || key.SubKeyCount != 0) return;
        root.DeleteSubKey(path, false); // Never recursively delete other IFEO configuration.
    }
}

internal sealed class GuardStateStore : IGuardStateStore
{
    private readonly string _path;
    private readonly bool _machine;
    internal static readonly string DefaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Delta NFD", "RuntimeGuard", "state.json");
    internal GuardStateStore() { _path = DefaultPath; _machine = true; }
    internal GuardStateStore(string path) { _path = Path.GetFullPath(path); }
    private static void NoReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("防护状态路径包含重解析点，拒绝访问。");
    }
    private static void CheckSecurity(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new UnauthorizedAccessException("无法确认防护状态属主。");
        if (!owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && !owner.IsWellKnown(WellKnownSidType.LocalSystemSid))
            throw new UnauthorizedAccessException("防护状态属主不是管理员组或 SYSTEM。");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            var write = rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership);
            if (rule.AccessControlType == AccessControlType.Allow && write != 0 &&
                !sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && !sid.IsWellKnown(WellKnownSidType.LocalSystemSid))
                throw new UnauthorizedAccessException("防护状态可被非管理员修改，拒绝使用。");
        }
    }
    private void ValidateExisting()
    {
        NoReparse(_path);
        if (!_machine) return;
        var directory = Path.GetDirectoryName(_path)!;
        var parent = Path.GetDirectoryName(directory)!;
        if (Directory.Exists(parent)) CheckSecurity(FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(parent)));
        if (Directory.Exists(directory)) CheckSecurity(FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(directory)));
        if (File.Exists(_path)) CheckSecurity(FileSystemAclExtensions.GetAccessControl(new FileInfo(_path)));
    }
    public GuardProtectionState Load()
    {
        ValidateExisting();
        if (!File.Exists(_path)) return new();
        if (new FileInfo(_path).Length > 1024 * 1024) throw new InvalidDataException("防护状态文件过大。");
        var result = JsonSerializer.Deserialize<GuardProtectionState>(File.ReadAllText(_path)) ?? throw new InvalidDataException("防护状态为空。");
        if (result.Version != 1 || result.Ifeo is null || result.Acls is null || result.LegacyAclRepairs is null ||
            result.Ifeo.Any(e => !RuntimeGuardProtection.Names.Contains(e.Name, StringComparer.OrdinalIgnoreCase) || !e.Marker.StartsWith(RuntimeGuardProtection.OwnerPrefix, StringComparison.Ordinal)) ||
            result.Ifeo.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Ifeo.Count)
            throw new InvalidDataException("防护状态结构无效。");
        foreach (var acl in result.Acls)
        {
            if (!Path.IsPathFullyQualified(acl.Path) || !Path.GetFileName(acl.Path).StartsWith("UE4PrereqSetup_", StringComparison.OrdinalIgnoreCase) || !acl.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("防护 ACL 路径无效。");
            _ = RuntimeGuardProtection.NormalizeDacl(acl.Original);
            _ = RuntimeGuardProtection.NormalizeDacl(acl.Applied);
        }
        foreach (var repair in result.LegacyAclRepairs)
            if (!Path.IsPathFullyQualified(repair.Path) || string.IsNullOrWhiteSpace(repair.Identity) ||
                !RuntimeGuardProtection.TryRemoveLegacyExecuteRule(repair.Path, repair.Before, out var after) ||
                RuntimeGuardProtection.NormalizeDacl(after) != RuntimeGuardProtection.NormalizeDacl(repair.After))
                throw new InvalidDataException("旧 UE4 修复记录无效。");
        return result;
    }
    public void Save(GuardProtectionState state)
    {
        ValidateExisting();
        var directory = Path.GetDirectoryName(_path)!;
        if (!Directory.Exists(directory))
        {
            var parent = Path.GetDirectoryName(directory)!;
            var parentExisted = Directory.Exists(parent);
            Directory.CreateDirectory(directory);
            if (_machine)
            {
                var security = new DirectorySecurity();
                security.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
                if (!parentExisted) FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(parent), security);
                FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(directory), security);
            }
        }
        ValidateExisting();
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state), new UTF8Encoding(false));
            if (_machine)
            {
                var security = new FileSecurity();
                security.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)");
                FileSystemAclExtensions.SetAccessControl(new FileInfo(temporary), security);
                CheckSecurity(FileSystemAclExtensions.GetAccessControl(new FileInfo(temporary)));
            }
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class GuardFileBackend : IGuardFiles
{
    private static SafeFileHandle Open(string path, bool write)
    {
        var handle = CreateFileW(path, 0x20080u | (write ? 0x40000u : 0), write ? 3u : 7u, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }
    private static string Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return $"{info.Volume:X8}:{info.High:X8}{info.Low:X8}";
    }
    private static string Dacl(SafeFileHandle handle)
    {
        var code = GetSecurityInfo(handle, 1, 4, out _, out _, out _, out _, out var descriptor);
        if (code != 0) throw new System.ComponentModel.Win32Exception((int)code);
        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, 1, 4, out var text, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { return Marshal.PtrToStringUni(text) ?? throw new IOException("DACL 读取为空。"); }
            finally { LocalFree(text); }
        }
        finally { LocalFree(descriptor); }
    }
    public GuardFileValue Read(string path)
    {
        using var handle = Open(path, false);
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("无法确认 UE4 原文件路径。");
        var canonical = buffer.ToString();
        canonical = canonical.StartsWith(@"\\?\UNC\") ? @"\\" + canonical[8..] : canonical.StartsWith(@"\\?\") ? canonical[4..] : canonical;
        return new(canonical, Identity(handle), Dacl(handle));
    }
    public void SetDacl(string path, string identity, string expected, string sddl)
    {
        using var handle = Open(path, true);
        if (Identity(handle) != identity || RuntimeGuardProtection.NormalizeDacl(Dacl(handle)) != RuntimeGuardProtection.NormalizeDacl(expected))
            throw new IOException("UE4 文件或权限已变化，未写入。");
        var descriptor = new RawSecurityDescriptor(sddl);
        var acl = descriptor.DiscretionaryAcl ?? throw new IOException("拒绝写入空 DACL。");
        var buffer = new byte[acl.BinaryLength]; acl.GetBinaryForm(buffer, 0);
        var flags = 4u | ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0 ? 0x80000000u : 0x20000000u);
        var pointer = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.Copy(buffer, 0, pointer, buffer.Length);
            var code = SetSecurityInfo(handle, 1, flags, IntPtr.Zero, IntPtr.Zero, pointer, IntPtr.Zero);
            if (code != 0) throw new System.ComponentModel.Win32Exception((int)code);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfoNative
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, High, Low;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoNative info);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(SafeFileHandle handle, int type, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr descriptor, uint revision, uint information, out IntPtr text, out uint length);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
