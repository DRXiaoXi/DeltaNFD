using DeltaNFD.Services;
using Microsoft.Win32;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

internal static class RuntimeGuardRecoveryChecks
{
    private const string Original = "D:P(D;;0x1;;;WD)(A;;FA;;;BA)(A;;FA;;;SY)";
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-guard-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
        try
        {
            var count = 0;
            Fixture New()
            {
                var registry = new RegistryFake(); var files = new FilesFake(); var state = new StateFake();
                var backup = new TweakBackupStore(Path.Combine(root, "legacy" + ++count + ".json"));
                return new(new RuntimeGuardProtection(registry, files, state, backup), registry, files, state, backup);
            }
            var f = New();
            Check(f.Guard.Enable(null).Success && f.Registry.Values.Count == 11, "新防护写入十一项并标记归属");
            Check(f.Guard.RepairBlocker(null) is not null, "防护开启时拒绝修复");
            var progress = 0;
            var repair = await new RuntimeGuardService(f.Guard).RepairVcRedistAsync(new InlineProgress(_ => progress++));
            Check(!repair.Success && repair.Message.Contains("尚未卸载") && progress == 0, "真实修复入口在卸载及包校验前短路");
            Check(f.Guard.Disable(null).Success && f.Registry.Values.Values.All(v => v.Debugger is null && v.Owner is null), "新防护完整恢复");
            Check(f.State.Value.Ifeo.Count == 0, "恢复后清理机器级记录");

            f = New(); f.Registry.Values[RuntimeGuardProtection.Names[0]] = new("third-party.exe", RegistryValueKind.String, null, false);
            Check(!f.Guard.Enable(null).Success && f.Registry.Writes == 0, "第三方 IFEO 存在时启用不覆盖");
            Check(f.Guard.Disable(null).Success && f.Registry.Values[RuntimeGuardProtection.Names[0]].Debugger == "third-party.exe", "关闭保留第三方值");
            f = New(); f.Registry.Values[RuntimeGuardProtection.Names[0]] = new(RuntimeGuardProtection.Debugger, RegistryValueKind.String, null, true);
            Check(!f.Guard.Enable(null).Success && f.Guard.Disable(null).Success && f.Registry.Values[RuntimeGuardProtection.Names[0]].Debugger is not null,
                "独立热补丁同签名不认领、不删除");
            f = New(); f.Registry.Values[RuntimeGuardProtection.Names[0]] = new(RuntimeGuardProtection.Debugger, RegistryValueKind.String, null, false);
            Check(!f.Guard.Disable(null).Success && f.Registry.Writes == 0, "无归属、无备份的旧签名不盲删");
            f = New();
            foreach (var name in RuntimeGuardProtection.Names)
            {
                f.Registry.Values[name] = new(RuntimeGuardProtection.Debugger, RegistryValueKind.String, null, false);
                f.Legacy.SaveStrict(new RegistryValueBackup { Hive = "HKLM", KeyPath = RuntimeGuardProtection.IfeoRoot + "\\" + name,
                    ValueName = "Debugger", ValueKind = RegistryValueKind.None });
            }
            Check(f.Guard.Disable(null).Success && f.Registry.Values.Values.All(v => v.Debugger is null), "旧版十一条原值不存在备份可恢复");
            f = New(); f.Guard.Enable(null); f.State.Value = new();
            Check(f.Guard.Disable(null).Success && f.Registry.Values.Values.All(v => v.Debugger is null), "机器状态丢失后仅有明确 absent 属主才自愈");
            f = New(); f.Registry.FailAfterWrite = true;
            Check(!f.Guard.Enable(null).Success && f.Registry.Values.Values.All(v => v.Debugger is null), "写入后抛异常也回滚当前项");
            f = New(); f.State.FailSave = true;
            Check(!RuntimeGuardProtection.Exclusive(() => f.Guard.Enable(null)).Success && f.Registry.Writes == 0, "备份保存失败时不写注册表");
            f = New(); f.Guard.Enable(null); f.Registry.Values[RuntimeGuardProtection.Names[0]] = f.Registry.Values[RuntimeGuardProtection.Names[0]] with { Debugger = "new-owner.exe" };
            Check(!f.Guard.Disable(null).Success && f.Registry.Values[RuntimeGuardProtection.Names[0]].Debugger == "new-owner.exe", "关闭不覆盖防护期间外部更新");
            f = New(); f.Registry.FailRead = true;
            Check(f.Guard.RepairBlocker(null) is not null && f.Registry.Writes == 0, "查询失败时拒绝破坏性修复");

            var path = Path.Combine(root, "UE4PrereqSetup_x64.exe");
            f = New(); f.Files.Values[path] = new(path, "original-file", Original);
            Check(f.Guard.Enable(path).Success && RuntimeGuardProtection.HasExecuteDeny(f.Files.Values[path].Dacl), "UE4 写入执行拒绝并复核");
            Check(f.Guard.Disable(null).Success && RuntimeGuardProtection.NormalizeDacl(f.Files.Values[path].Dacl) == RuntimeGuardProtection.NormalizeDacl(Original),
                "目录选择变化后仍恢复记录的原文件，原有拒绝读取保留");
            f = New(); f.Files.Values[path] = new(path, "old", Original); f.Guard.Enable(path);
            f.Files.Values[path] = f.Files.Values[path] with { Identity = "replacement" };
            Check(!f.Guard.Disable(null).Success && f.State.Value.Acls.Count == 1, "文件替换后不向新文件写入旧权限，保留记录");
            f = New(); f.Files.Values[path] = new(path, "old", Original); f.Guard.Enable(path);
            var changed = f.Files.Values[path].Dacl + "(A;;FR;;;BU)";
            f.Files.Values[path] = f.Files.Values[path] with { Dacl = changed };
            Check(!f.Guard.Disable(null).Success && f.Files.Values[path].Dacl == changed, "外部权限修改不被快照覆盖");
            f = New(); f.Files.Values[path] = new(path, "old", "D:P(D;;FX;;;WD)(A;;FA;;;BA)");
            f.Guard.Enable(path);
            Check(f.State.Value.Acls.Count == 0 && !f.Guard.Disable(path).Success, "不接管或移除已有第三方执行拒绝");
            var legacyAcl = "D:P(D;;0x20;;;WD)" + Original[3..];
            f = New(); f.Files.Values[path] = new(path, "legacy", legacyAcl);
            Check(!f.Guard.Enable(path).Success && f.Registry.Writes == 0, "旧 ACL 预检失败不先开启 IFEO");
            Check(f.Guard.CanRepairLegacyAcl(path), "仅显式单条 Everyone ExecuteFile 可确认修复");
            Check(!f.Guard.Disable(path).Success && f.Files.Values[path].Dacl == legacyAcl, "关闭本身不擅自接管未知 ACL");
            Check(f.Guard.RepairLegacyAcl(path).Success && f.State.Value.LegacyAclRepairs.Single().Completed,
                "明确修复保存前后快照与完成记录");
            var legacyJournal = f.State.Value;
            Check(RuntimeGuardProtection.NormalizeDacl(f.Files.Values[path].Dacl) == RuntimeGuardProtection.NormalizeDacl(Original),
                "旧规则修复保留原有拒绝读取与其他权限");
            for (var cycle = 0; cycle < 2; cycle++)
                Check(f.Guard.Enable(path).Success && f.Guard.Disable(path).Success &&
                    RuntimeGuardProtection.NormalizeDacl(f.Files.Values[path].Dacl) == RuntimeGuardProtection.NormalizeDacl(Original),
                    "旧规则修复后开关往返 " + cycle);
            f = New(); f.Files.Values[path] = new(path, "legacy", legacyAcl); f.State.FailSave = true;
            Check(!f.Guard.RepairLegacyAcl(path).Success && f.Files.Values[path].Dacl == legacyAcl, "旧修复备份失败零权限写入");
            f = New(); f.Files.Values[path] = new(path, "legacy", legacyAcl); f.Files.FailAfterWrite = true;
            Check(!f.Guard.RepairLegacyAcl(path).Success && !f.State.Value.LegacyAclRepairs.Single().Completed,
                "旧修复写后异常保留未完成记录");
            Check(f.Guard.RepairLegacyAcl(path).Success && f.State.Value.LegacyAclRepairs.Single().Completed, "旧修复写后异常可按已完成原值重试");
            f = New(); f.Files.Values[path] = new(path, "legacy", legacyAcl); f.Files.FailAfterWrite = true; f.Guard.RepairLegacyAcl(path);
            f.Files.Values[path] = f.Files.Values[path] with { Identity = "replaced" };
            Check(!f.Guard.RepairLegacyAcl(path).Success, "旧修复拒绝文件身份变化");
            f = New(); f.Files.Values[path] = new(path, "legacy", legacyAcl); f.Files.FailAfterWrite = true; f.Guard.RepairLegacyAcl(path);
            f.Files.Values[path] = f.Files.Values[path] with { Dacl = f.Files.Values[path].Dacl + "(A;;FR;;;BU)" };
            Check(!f.Guard.RepairLegacyAcl(path).Success && !f.State.Value.LegacyAclRepairs.Single().Completed, "旧修复不覆盖外部权限变化");
            foreach (var acl in new[] { "D:P(D;;FX;;;WD)(A;;FA;;;BA)", "D:P(D;ID;0x20;;;WD)(A;;FA;;;BA)", "D:P(D;;0x20;;;WD)(D;;0x20;;;WD)(A;;FA;;;BA)" })
                Check(!RuntimeGuardProtection.TryRemoveLegacyExecuteRule(path, acl, out _), "拒绝宽泛/继承/重复拒绝规则");
            Check(!RuntimeGuardProtection.TryRemoveLegacyExecuteRule(Path.Combine(root, "other.exe"), legacyAcl, out _), "拒绝修复非 UE4 文件");
            f = New(); f.Files.Values[path] = new(path, "old", Original); f.Files.FailAfterWrite = true;
            f.Guard.Enable(path);
            Check(f.State.Value.Acls.Count == 1 && f.Guard.Disable(null).Success, "ACL 写入后异常保留记录并可恢复");

            var storePath = Path.Combine(root, "machine.json");
            var disk = new GuardStateStore(storePath);
            disk.Save(new GuardProtectionState());
            Check(disk.Load().Version == 1, "机器记录 JSON 往返");
            File.WriteAllText(storePath, "{bad-json");
            try { disk.Load(); throw new Exception("corrupt state accepted"); } catch (JsonException) { }
            Check(File.ReadAllText(storePath) == "{bad-json", "损坏状态文件不覆盖");
            File.Delete(storePath);
            disk.Save(legacyJournal);
            Check(disk.Load().LegacyAclRepairs.Count == 1, "旧权限修复日志 JSON 往返");

            // Only a disposable fixture file is touched; no installed prerequisite or IFEO key is changed.
            File.WriteAllText(path, "fixture");
            var native = new GuardFileBackend();
            var before = native.Read(path);
            native.SetDacl(path, before.Identity, before.Dacl, before.Dacl);
            Check(RuntimeGuardProtection.NormalizeDacl(native.Read(path).Dacl) == RuntimeGuardProtection.NormalizeDacl(before.Dacl), "临时文件原生 DACL 读写保真");
            var security = new FileSecurity();
            security.SetSecurityDescriptorSddlForm(before.Dacl, AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadData, AccessControlType.Deny));
            native.SetDacl(path, before.Identity, before.Dacl, security.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            var denyRead = native.Read(path);
            var nativeGuard = new RuntimeGuardProtection(new RegistryFake(), native, new StateFake(), new TweakBackupStore(Path.Combine(root, "native-legacy.json")));
            Check(nativeGuard.Enable(path).Success && RuntimeGuardProtection.HasExecuteDeny(native.Read(path).Dacl), "临时文件真实叠加执行拒绝");
            Check(nativeGuard.Disable(path).Success && RuntimeGuardProtection.NormalizeDacl(native.Read(path).Dacl) == RuntimeGuardProtection.NormalizeDacl(denyRead.Dacl),
                "真实恢复保留预先存在的拒绝读取及继承状态");
            native.SetDacl(path, before.Identity, denyRead.Dacl, before.Dacl);
            Console.WriteLine("Runtime protection recovery checks passed; real HKLM and prerequisite files were not modified.");
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
    private sealed record Fixture(RuntimeGuardProtection Guard, RegistryFake Registry, FilesFake Files, StateFake State, TweakBackupStore Legacy);
    private sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
    private sealed class StateFake : IGuardStateStore
    {
        internal GuardProtectionState Value = new(); internal bool FailSave;
        public GuardProtectionState Load() => JsonSerializer.Deserialize<GuardProtectionState>(JsonSerializer.Serialize(Value))!;
        public void Save(GuardProtectionState state) { if (FailSave) throw new IOException("save failed"); Value = JsonSerializer.Deserialize<GuardProtectionState>(JsonSerializer.Serialize(state))!; }
    }
    private sealed class RegistryFake : IGuardRegistry
    {
        internal readonly Dictionary<string, GuardRegistryValue> Values = new(StringComparer.OrdinalIgnoreCase);
        internal int Writes; internal bool FailAfterWrite, FailRead;
        public GuardRegistryValue Read(string name) => FailRead ? throw new IOException("read failed") : Values.GetValueOrDefault(name, new(null, RegistryValueKind.None, null, false));
        public void SetDebugger(string name, string? value, RegistryValueKind kind)
        {
            Values[name] = Read(name) with { Debugger = value, Kind = kind }; Writes++;
            if (FailAfterWrite) { FailAfterWrite = false; throw new IOException("write changed value then failed"); }
        }
        public void SetOwner(string name, string? value) { Values[name] = Read(name) with { Owner = value }; Writes++; }
        public void RemoveEmptyKey(string name) { }
    }
    private sealed class FilesFake : IGuardFiles
    {
        internal readonly Dictionary<string, GuardFileValue> Values = new(StringComparer.OrdinalIgnoreCase);
        internal bool FailAfterWrite;
        public GuardFileValue Read(string path) => Values.TryGetValue(path, out var result) ? result : throw new FileNotFoundException(path);
        public void SetDacl(string path, string identity, string expected, string sddl)
        {
            var current = Read(path);
            if (current.Identity != identity || RuntimeGuardProtection.NormalizeDacl(current.Dacl) != RuntimeGuardProtection.NormalizeDacl(expected)) throw new IOException("conflict");
            Values[path] = current with { Dacl = sddl };
            if (FailAfterWrite) { FailAfterWrite = false; throw new IOException("acl changed then failed"); }
        }
    }
}
