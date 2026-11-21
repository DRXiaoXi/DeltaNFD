using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Services {
    internal sealed class NvContainerRegistryBackup : INvContainerBackupStore {
        internal const string KeyName = @"SOFTWARE\DeltaNFD.NVIDIA-Display-Container-Switch";
        internal const string ValueName = "OriginalServiceState-v1";
        private static readonly SecurityIdentifier Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier SystemAccount = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        private const RegistryRights WriteRights = RegistryRights.ReadKey | RegistryRights.SetValue | RegistryRights.ChangePermissions;
        private const int OpenLink = 0x00000008, Wow64Key64 = 0x0100;
        private const int FileNotFound = 2, InsufficientBuffer = 122;
        private const int OwnerAndDaclInformation = 0x00000001 | 0x00000004;
        private readonly RegistryHive hive;
        private readonly string keyName;
        private readonly SecurityIdentifier admin;

        internal NvContainerRegistryBackup() : this(RegistryHive.LocalMachine, KeyName, Admins) { }

        // Isolated tests use the current user's SID as the administrator in a disposable HKCU key.
        internal NvContainerRegistryBackup(RegistryHive hive, string keyName, SecurityIdentifier admin) {
            this.hive = hive;
            this.keyName = keyName;
            this.admin = admin;
        }

        private RegistrySecurity LockedAcl(bool setOwner) {
            var security = new RegistrySecurity();
            if (setOwner) security.SetOwner(admin);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new RegistryAccessRule(admin, RegistryRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new RegistryAccessRule(SystemAccount, RegistryRights.FullControl, AccessControlType.Allow));
            return security;
        }

        private bool IsTrusted(SecurityIdentifier? sid) => sid is not null && (admin.Equals(sid) || SystemAccount.Equals(sid));

        // Inspect the raw DACL so unknown or conditional ACEs cannot disappear in a managed rule projection.
        // Legacy inherited read permissions never authorize writes to the saved value.
        internal bool IsCanonicalAcl(RawSecurityDescriptor descriptor) {
            if (!IsTrusted(descriptor.Owner))
                throw new InvalidOperationException("恢复备份的所有者不是受信任的管理员组或 SYSTEM。");
            if (descriptor.DiscretionaryAcl is not { } acl
                || !descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclPresent))
                throw new InvalidOperationException("恢复备份缺少有效的访问控制列表。");
            bool canonical = descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected) && acl.Count == 2;
            bool hasAdmin = false, hasSystem = false;
            foreach (GenericAce entry in acl) {
                if (entry is not CommonAce { AceType: AceType.AccessAllowed } rule)
                    throw new InvalidOperationException("恢复备份含有无法安全整理的访问规则。");
                if (!rule.AceFlags.HasFlag(AceFlags.InheritOnly) && !IsTrusted(rule.SecurityIdentifier)
                    && (rule.AccessMask & ~(int)RegistryRights.ReadKey) != 0)
                    throw new InvalidOperationException("恢复备份允许其他账号修改，已保留原记录。");
                canonical &= rule.AceFlags == AceFlags.None && rule.AccessMask == (int)RegistryRights.FullControl;
                hasAdmin |= admin.Equals(rule.SecurityIdentifier);
                hasSystem |= SystemAccount.Equals(rule.SecurityIdentifier);
            }
            return canonical && hasAdmin && hasSystem;
        }

        private bool InspectAcl(RegistryKey key) {
            var length = 0;
            var result = RegGetKeySecurity(key.Handle, OwnerAndDaclInformation, null, ref length);
            if (result != InsufficientBuffer)
                throw NvContainerScm.Error("查询恢复备份权限大小", result);
            if (length is < 20 or > 131072)
                throw new InvalidOperationException("恢复备份权限描述符长度异常。");
            var bytes = new byte[length];
            result = RegGetKeySecurity(key.Handle, OwnerAndDaclInformation, bytes, ref length);
            if (result != 0) throw NvContainerScm.Error("读取恢复备份权限", result);
            return IsCanonicalAcl(new RawSecurityDescriptor(bytes, 0));
        }

        private static NvContainerBackup? ReadValue(RegistryKey key) {
            if (key.SubKeyCount != 0 || key.ValueCount > 1)
                throw new InvalidOperationException("恢复备份键含有其他数据，保留原状。");
            var names = key.GetValueNames();
            if (names.Length == 0) return null;
            if (names.Length != 1 || !ValueName.Equals(names[0], StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("恢复备份键含有未知数据或注册表链接，保留原状。");
            if (key.GetValueKind(ValueName) != RegistryValueKind.Binary ||
                key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not byte[] bytes)
                throw new InvalidOperationException("恢复备份的类型不正确，已保留原记录。");
            return Decode(bytes);
        }

        private RegistryKey? OpenKey(RegistryKey root, RegistryRights rights) {
            // Open the fixed key itself, never the destination of a registry link at that name.
            var result = RegOpenKeyExW(root.Handle, keyName, OpenLink, (int)rights | Wow64Key64, out var handle);
            if (result != 0) {
                handle?.Dispose();
                if (result == FileNotFound) return null;
                throw NvContainerScm.Error("打开显示容器专用恢复备份键", result);
            }
            return RegistryKey.FromHandle(handle, RegistryView.Registry64);
        }

        private void ProtectVerifiedKey(RegistryKey key, NvContainerBackup? expected) {
            // Locking a key cannot revoke handles that already have write access. Only tighten a
            // descriptor whose effective writers were already limited to administrators and SYSTEM.
            var canonical = InspectAcl(key);
            if (ReadValue(key) != expected) throw new InvalidOperationException("恢复备份已变化，未操作服务。");
            if (canonical) return;
            key.SetAccessControl(LockedAcl(setOwner: false));
            Flush(key);
            if (!InspectAcl(key) || ReadValue(key) != expected)
                throw new InvalidOperationException("恢复备份权限整理后未通过回读，已保留原记录。");
            Log.Info("NVIDIA 显示容器备份键：旧版继承或额外的只读权限已收紧；原备份内容保持。");
        }

        public NvContainerBackup? Read() {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = OpenKey(root, RegistryRights.ReadKey);
            if (key is null) return null;
            InspectAcl(key);
            return ReadValue(key);
        }
        public void Save(NvContainerBackup backup) {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = OpenKey(root, WriteRights) ?? CreateKey(root);
            ProtectVerifiedKey(key, expected: null);
            key.SetValue(ValueName, Encode(backup), RegistryValueKind.Binary);
            Flush(key);
            if (!InspectAcl(key) || ReadValue(key) != backup)
                throw new InvalidOperationException("恢复备份内容或权限回读不一致。");
        }

        private RegistryKey CreateKey(RegistryKey root) {
            // The managed CreateSubKey overload can create the key with inherited permissions on .NET 8.
            // Pass the protected DACL to Windows at creation so an untrusted writer never gets a handle.
            var descriptor = LockedAcl(setOwner: true).GetSecurityDescriptorBinaryForm();
            var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
            try {
                var attributes = new SecurityAttributes {
                    Length = Marshal.SizeOf<SecurityAttributes>(),
                    Descriptor = pinned.AddrOfPinnedObject(),
                    InheritHandle = 0,
                };
                var result = RegCreateKeyExW(root.Handle, keyName, 0, IntPtr.Zero, 0,
                    (int)WriteRights | Wow64Key64, ref attributes, out var handle, out _);
                using (handle) {
                    if (result != 0) throw NvContainerScm.Error("创建受保护的恢复备份键", result);
                    return OpenKey(root, WriteRights) ?? throw new InvalidOperationException("无法回读管理员恢复备份键。");
                }
            }
            finally { pinned.Free(); }
        }

        public void PrepareRestore(NvContainerBackup backup) {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = OpenKey(root, WriteRights) ?? throw new InvalidOperationException("未找到原恢复记录。");
            ProtectVerifiedKey(key, backup);
        }

        public void Clear(NvContainerBackup backup) {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = OpenKey(root, RegistryRights.ReadKey | RegistryRights.SetValue)
                ?? throw new InvalidOperationException("未找到原恢复记录，请核对当前状态。");
            if (!InspectAcl(key) || ReadValue(key) != backup)
                throw new InvalidOperationException("恢复备份或权限已变化，保留原记录。");
            key.DeleteValue(ValueName, true);
            Flush(key);
            if (ReadValue(key) is not null) throw new InvalidOperationException("恢复备份未确认清除。");
        }

        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        internal static byte[] Encode(NvContainerBackup backup) {
            using (var mem = new MemoryStream())
            using (var writer = new BinaryWriter(mem, Utf8)) {
                writer.Write(0x3153444e); // NDS1
                writer.Write((byte)backup.Mode);
                writer.Write((byte)((backup.Delayed ? 1 : 0) | (backup.WasRunning ? 2 : 0)));
                writer.Write(backup.Image);
                writer.Flush(); return mem.ToArray();
            }
        }
        internal static NvContainerBackup Decode(byte[] bytes) {
            if (bytes == null || bytes.Length < 7 || bytes.Length > 4096) throw new InvalidOperationException("恢复备份长度异常，未继续。");
            using (var mem = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(mem, Utf8)) {
                if (reader.ReadUInt32() != 0x3153444e) throw new InvalidOperationException("无法识别恢复备份，未继续。");
                NvContainerMode mode = (NvContainerMode)reader.ReadByte();
                byte flags = reader.ReadByte();
                if (flags > 3) throw new InvalidOperationException("恢复备份标记无效，未继续。");
                string image = reader.ReadString();
                if (mem.Position != mem.Length) throw new InvalidOperationException("恢复备份含多余数据，未继续。");
                if (NvDisplayContainerService.ParseImage(image) is null || mode is not (NvContainerMode.Automatic or NvContainerMode.Manual))
                    throw new InvalidOperationException("备份中的服务身份或启动类型不受支持。");
                return new NvContainerBackup(image, mode, (flags & 1) != 0, (flags & 2) != 0);
            }
        }
        private static void Flush(RegistryKey key) {
            int result = RegFlushKey(key.Handle);
            if (result != 0) throw NvContainerScm.Error("确认恢复备份已写入磁盘", result);
        }
        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern int RegFlushKey(SafeRegistryHandle handle);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegOpenKeyExW(SafeRegistryHandle key, string subKey, int options, int rights, out SafeRegistryHandle result);
        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern int RegGetKeySecurity(SafeRegistryHandle key, int information, [Out] byte[]? descriptor, ref int length);
        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes {
            internal int Length;
            internal IntPtr Descriptor;
            internal int InheritHandle;
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegCreateKeyExW(SafeRegistryHandle key, string subKey, int reserved, IntPtr className,
            int options, int rights, ref SecurityAttributes security, out SafeRegistryHandle result, out int disposition);
    }
}


