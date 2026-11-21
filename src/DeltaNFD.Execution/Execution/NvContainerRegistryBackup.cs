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
        private const string ValueName = "OriginalServiceState-v1";
        private static readonly SecurityIdentifier Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier SystemAccount = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        private static RegistrySecurity LockedAcl() {
            var security = new RegistrySecurity();
            security.SetOwner(Admins);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new RegistryAccessRule(Admins, RegistryRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new RegistryAccessRule(SystemAccount, RegistryRights.FullControl, AccessControlType.Allow));
            return security;
        }
        private static void VerifyAcl(RegistryKey key) {
            var security = key.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            if (!security.AreAccessRulesProtected || !Admins.Equals(security.GetOwner(typeof(SecurityIdentifier))) || rules.Count != 2)
                throw new InvalidOperationException("恢复备份的注册表权限不符合预期，未继续。");
            bool admin = false, system = false;
            foreach (RegistryAccessRule rule in rules) {
                if (rule.AccessControlType != AccessControlType.Allow || rule.RegistryRights != RegistryRights.FullControl ||
                    rule.IsInherited || rule.InheritanceFlags != InheritanceFlags.None || rule.PropagationFlags != PropagationFlags.None)
                    throw new InvalidOperationException("恢复备份的访问规则不符合预期，未继续。");
                if (Admins.Equals(rule.IdentityReference)) admin = true;
                if (SystemAccount.Equals(rule.IdentityReference)) system = true;
            }
            if (!admin || !system) throw new InvalidOperationException("恢复备份的访问对象不符合预期，未继续。");
        }

        public NvContainerBackup? Read() {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hklm.OpenSubKey(KeyName, false)) {
                if (key == null) return null;
                VerifyAcl(key);
                var value = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value == null) return null;
                var bytes = value as byte[];
                if (bytes == null) throw new InvalidOperationException("恢复备份的类型不正确，未继续。");
                return Decode(bytes);
            }
        }
        public void Save(NvContainerBackup backup) {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hklm.CreateSubKey(KeyName, RegistryKeyPermissionCheck.ReadWriteSubTree, LockedAcl())) {
                if (key == null) throw new InvalidOperationException("无法创建管理员恢复备份。");
                VerifyAcl(key);
                if (key.GetValue(ValueName) != null) throw new InvalidOperationException("发现既有恢复备份，不会覆盖。");
                key.SetValue(ValueName, Encode(backup), RegistryValueKind.Binary);
                Flush(key);
                NvContainerBackup stored = Decode(key.GetValue(ValueName) as byte[] ?? throw new IOException("无法回读恢复备份。"));
                if (stored.Image != backup.Image || stored.Mode != backup.Mode || stored.Delayed != backup.Delayed || stored.WasRunning != backup.WasRunning)
                    throw new InvalidOperationException("恢复备份回读不一致。");
            }
        }
        public void Clear() {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hklm.OpenSubKey(KeyName, true)) {
                if (key == null) throw new InvalidOperationException("未找到原恢复记录，请核对当前状态。");
                VerifyAcl(key);
                key.DeleteValue(ValueName, true);
                Flush(key);
                if (key.GetValue(ValueName) != null) throw new InvalidOperationException("恢复备份未确认清除。");
            }
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
    }
}


