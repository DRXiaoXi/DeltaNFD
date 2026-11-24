using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Services {
    // Retired NDS1 registry format. Only read it for first-time file migration.
    internal sealed class NvContainerRegistryBackup {
        internal const string KeyName = @"SOFTWARE\DeltaNFD.NVIDIA-Display-Container-Switch";
        internal const string ValueName = "OriginalServiceState-v1";
        private static readonly SecurityIdentifier Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier SystemAccount = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
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

        public NvContainerBackup? Read() {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = OpenKey(root, RegistryRights.ReadKey);
            if (key is null) return null;
            // RC5 could leave an empty key with inherited permissions. No values can be imported from it.
            if (key.SubKeyCount == 0 && key.ValueCount == 0) return null;
            InspectAcl(key);
            return ReadValue(key);
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
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegOpenKeyExW(SafeRegistryHandle key, string subKey, int options, int rights, out SafeRegistryHandle result);
        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern int RegGetKeySecurity(SafeRegistryHandle key, int information, [Out] byte[]? descriptor, ref int length);
    }
}


