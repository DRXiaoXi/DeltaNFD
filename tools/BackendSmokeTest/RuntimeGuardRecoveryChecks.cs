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
                var guard = new RuntimeGuardProtection(registry, files, state, backup) { Wait = _ => registry.ApplyRewrites() };
                return new(guard, registry, files, state, backup);
            }
            var names = RuntimeGuardProtection.Names;
            GuardRegistryValue Taskkill(string? owner = null, bool hotPatch = false, string debugger = RuntimeGuardProtection.Debugger,
                RegistryValueKind kind = RegistryValueKind.String) =>
                new(debugger, kind, owner, hotPatch) { ForeignMarker = hotPatch ? "RuntimeGuardHotPatch" : null };
            bool Clean(Fixture fx) => fx.Registry.Values.Count == 0;

            var f = New();
            Check(f.Guard.Disable(default(string?)).Success && f.Registry.Writes == 0, "未开启防护也能直接关闭，且不写入");
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "src/DeltaNFD/ViewModels/RuntimeViewModel.cs"))) repo = repo.Parent;
            var vmSource = File.ReadAllText(Path.Combine(repo!.FullName, "src/DeltaNFD/ViewModels/RuntimeViewModel.cs"));
            Check(vmSource.Contains("GuardCleanupEnabled => !GuardIsBusy && !IsRepairing && !UninstallV14Busy"),
                "强制清理入口不依赖已开启、残留判断或读取状态成功");
            Check(vmSource.Contains("(enable && !_guardStateKnown)"), "状态未知仍允许关闭重试，但禁止未知状态启用");

            Check(f.Guard.Enable(null).Success && f.Registry.Values.Count == 11 &&
                  f.Registry.Values.Values.All(v => v.Debugger == RuntimeGuardProtection.Debugger && v.Owner!.StartsWith(RuntimeGuardProtection.OwnerPrefix)),
                "完全防护写入十一项并标记归属");
            Check(f.State.Value.Acls.Count == 0 && f.Files.Writes == 0, "开启不再写 UE4 文件权限");
            Check(f.Guard.RepairBlocker(null) is not null, "防护开启时拒绝修复");
            var progress = 0;
            var repair = await new RuntimeGuardService(f.Guard).RepairVcRedistAsync(new InlineProgress(_ => progress++));
            var compatibility = await new RuntimeGuardService(f.Guard).RepairCompatibilityAsync(new InlineProgress(_ => progress++));
            Check(!compatibility.Success && progress == 0, "新版防护也在资源校验和修改前阻止兼容性修复");
            var strongRepair = await new RuntimeGuardService(f.Guard).RepairVcRedistAsync(strongRepairMode: true);
            var strongCompatibility = await new RuntimeGuardService(f.Guard).RepairCompatibilityAsync(strongRepairMode: true);
            Check(!strongRepair.Success && !strongCompatibility.Success, "强力模式的两个真实入口仍服从新版防护门禁，不启动 DISM/SFC");
            if (ElevationHelper.IsElevated)
                Check(!repair.Success && repair.Message.Contains("尚未卸载") && progress == 0, "真实修复入口在卸载及包校验前短路");
            else
            {
                Check(!repair.Success && repair.Message == ElevationHelper.NotElevatedMessage && progress == 0,
                    "未提权修复在诊断、校验和卸载前拒绝");
                Console.WriteLine("[SKIP] 真实修复入口的防护短路需管理员；注入防护策略检查已通过，不能替代该实测。");
            }
            Check(f.Guard.Enable(null).Success && f.Registry.Values.Count == 11, "重复开启幂等");
            Check(f.Guard.Disable(default(string?)).Success && Clean(f), "关闭完整移除并删除空键");
            Check(f.Guard.RepairBlocker(null) is null, "关闭后修复不再被拦");

            f = New(); f.Registry.Values[names[0]] = new("third-party.exe", RegistryValueKind.String, null, false);
            var partial = f.Guard.Enable(null);
            Check(!partial.Success && partial.Message.Contains("10/11") && f.Registry.Values.Count(v => v.Value.Debugger == RuntimeGuardProtection.Debugger) == 10,
                "单项第三方占用只跳过该项，其余十项生效并如实报告");
            Check(f.Registry.Values[names[0]].Debugger == "third-party.exe" && f.Registry.Values[names[0]].Owner is null, "开启不覆盖、不标记第三方项");
            Check(f.Guard.Disable(default(string?)).Success && f.Registry.Values.Count == 1 && f.Registry.Values[names[0]].Debugger == "third-party.exe",
                "关闭移除本工具拦截，保留第三方值且不算失败");
            Check(f.Guard.RepairBlocker(null) is not null, "第三方安装器拦截仍阻止修复");
            Check(f.Guard.ForceCleanup(null, default(string?)).Success && Clean(f), "强制清理移除第三方 Debugger");
            Check(f.State.Value.LegacyIfeoRepairs.Single().Before.Debugger == "third-party.exe", "强制清理留存第三方原值快照");

            f = New();
            foreach (var name in names) f.Registry.Values[name] = Taskkill();
            Check(f.Guard.Probe(null).Managed == 11 && f.Guard.Disable(default(string?)).Success && Clean(f), "0.83 及更早无属主旧拦截：普通关闭直接移除");

            f = New();
            foreach (var name in names) f.Registry.Values[name] = Taskkill(RuntimeGuardProtection.OwnerPrefix + "lost/backed", kind: RegistryValueKind.ExpandString);
            Check(f.Guard.Disable(default(string?)).Success && Clean(f), "上一版本（Beta0.9）属主在、机器记录丢失：普通关闭直接移除");

            f = New(); f.Guard.Enable(null);
            f.State.Value = new GuardProtectionState { Mode = RuntimeGuardMode.Full };
            Check(f.Guard.Disable(default(string?)).Success && Clean(f), "机器状态丢失后仍可关闭");

            f = New();
            f.Registry.Values[names[0]] = Taskkill(debugger: @"C:\Windows\System32\taskkill.exe");
            f.Registry.Values[names[1]] = Taskkill(debugger: "\"%SystemRoot%\\system32\\TASKKILL.EXE\" /f /im");
            f.Registry.Values[names[2]] = Taskkill(debugger: "taskkill /F");
            Check(f.Guard.Probe(null).Managed == 3 && f.Guard.Disable(default(string?)).Success && Clean(f), "展开路径、带引号、带参数的 taskkill 变体均识别并移除");

            f = New(); f.Registry.Values[names[3]] = new(null, RegistryValueKind.None, RuntimeGuardProtection.OwnerPrefix + "x/absent", false);
            Check(f.Guard.Probe(null).PendingIfeo && f.Guard.Disable(default(string?)).Success && Clean(f), "只剩属主标记的半写入残留可清理");

            f = New(); f.Registry.Values[names[0]] = Taskkill(hotPatch: true);
            Check(f.Guard.Probe(null).LegacyIfeo.Count == 1 && f.Guard.Probe(null).Managed == 0, "独立工具拦截单独列出");
            var kept = f.Guard.Disable(default(string?));
            Check(kept.Success && kept.Message.Contains("强制清理") && f.Registry.Values[names[0]].Debugger is not null, "普通关闭不删独立工具拦截，但给出强制清理入口");
            Check(f.Guard.Enable(null, RuntimeGuardMode.BasicV14).Success, "独立工具已拦截的项开启时视为已生效");
            Check(f.Guard.ForceCleanup(null, default(string?)).Success && Clean(f), "强制清理移除独立工具拦截及其属主标记");
            f = New(); f.Registry.Values[names[0]] = Taskkill(hotPatch: true) with { ForeignMarker = "SomeOtherVersion" };
            Check(f.Guard.ForceCleanup(null, default(string?)).Success && Clean(f), "独立工具标记内容不同也能强制清理");

            f = New(); f.Registry.Values[names[0]] = Taskkill(hotPatch: true); f.Registry.Sticky.Add(names[0]);
            var rewritten = f.Guard.ForceCleanup(null, default(string?));
            Check(!rewritten.Success && rewritten.Message.Contains("重新写入") && rewritten.Message.Contains(names[0]), "清理后被后台程序重新写入时明确提示");

            f = New(); f.Guard.Enable(null); f.Registry.FailAfterWrite = true;
            Check(!f.Guard.Disable(default(string?)).Success && f.Registry.Values.Count < 11, "单项写入异常不阻断其他项");
            Check(f.Guard.Disable(default(string?)).Success && Clean(f), "失败后直接重试即可完成");

            f = New(); f.State.FailLoad = true;
            Check(f.Guard.Probe(null).Issue.Length > 0, "状态文件损坏时状态读取不抛异常并给出说明");
            var corruptedEnable = f.Guard.Enable(null, RuntimeGuardMode.BasicV14);
            f.State.FailLoad = true; // Enable 保存范围记录后复位了标志，恢复损坏状态再验证关闭路径
            var corruptedDisable = f.Guard.Disable(default(string?));
            Check(corruptedEnable.Success && corruptedDisable.Success && Clean(f) && f.State.Saves == 1,
                "状态文件损坏时开关仍可用，关闭不覆盖损坏文件（开启=" + corruptedEnable.Message + "；关闭=" + corruptedDisable.Message + "）");
            f.Registry.Values[names[5]] = Taskkill();
            Check(f.Guard.ForceCleanup(null, default(string?)).Success && Clean(f) && f.State.Saves == 2, "强制清理重置损坏的状态文件");

            f = New(); f.State.FailSave = true;
            Check(f.Guard.Enable(null, RuntimeGuardMode.BasicV14).Success && f.Registry.Values.Count == 3, "状态保存失败不阻止开启");
            Check(f.Guard.Disable(default(string?)).Success && Clean(f), "状态保存失败不阻止关闭");
            f.Registry.Values[names[0]] = Taskkill();
            Check(f.Guard.ForceCleanup(null, default(string?)).Success && Clean(f), "状态保存失败不阻止强制清理");

            f = New();
            Check(f.Guard.Probe(null).Mode == RuntimeGuardMode.BasicV14, "新机器默认基础 V14");
            Check(f.Guard.Enable(null, RuntimeGuardMode.BasicV14).Success && f.Registry.Values.Count == 3, "基础仅三项");
            Check(f.Guard.Probe(null).ScopeManaged == 3 && !f.Guard.SelectMode(RuntimeGuardMode.Full, null).Success, "开启时禁止切换范围");
            Check(f.Guard.Disable(default(string?)).Success && f.Guard.SelectMode(RuntimeGuardMode.Full, null).Success && f.Guard.Enable(null).Success &&
                  f.Registry.Values.Count == 11, "关闭后可切到完全防护");
            Check(f.Guard.Enable(null, RuntimeGuardMode.BasicV14).Success && f.Registry.Values.Count == 3, "从完全防护收窄时移除范围外的本工具拦截");

            f = New(); f.Guard.Enable(null); f.Registry.FailRead = true;
            var blocker = f.Guard.RepairBlocker(null);
            Check(blocker is not null && blocker.Contains("无法读取"), "查询失败时拒绝破坏性修复（" + blocker + "）");

            f = New(); f.Guard.Enable(null); f.Registry.Values[names[0]] = f.Registry.Values[names[0]] with { Debugger = "new-owner.exe" };
            Check(f.Guard.Disable(default(string?)).Success && f.Registry.Values[names[0]] is { Debugger: "new-owner.exe", Owner: null }, "防护期间被第三方改写的项保留其值，只去掉本工具标记");

            var path = Path.Combine(root, "UE4PrereqSetup_x64.exe");
            var legacyAcl = "D:P(D;;0x20;;;WD)" + Original[3..];
            f = New(); f.Files.Values[path] = new(path, "legacy", legacyAcl);
            Check(f.Guard.Probe(path).FileDenied && f.Guard.CanRepairLegacyAcl(path), "检测到上一版本的 UE4 拒绝执行残留");
            Check(f.Guard.Disable(path as string).Success && Norm(f.Files.Values[path].Dacl) == Norm(Original), "关闭自动剥离无记录的旧版拒绝执行，保留原有拒绝读取");
            Check(f.Guard.Enable(path).Success && f.Files.Writes == 1 && f.Guard.Disable(path as string).Success, "剥离后开关可往返且不再写权限");
            foreach (var acl in new[] { "D:P(D;;0x20;;;WD)(D;;0x20;;;WD)" + Original[3..], "D:P(D;;FX;;;WD)" + Original[3..], "D:P(D;;0x120020;;;WD)" + Original[3..] })
            {
                f = New(); f.Files.Values[path] = new(path, "legacy", acl);
                Check(f.Guard.ForceCleanup(null, path as string).Success && Norm(f.Files.Values[path].Dacl) == Norm(Original), "强制清理剥离重复/宽泛执行拒绝：" + acl[..22]);
            }
            f = New(); f.Files.Values[path] = new(path, "legacy", "D:(D;;0x21;;;WD)(A;;FA;;;BA)");
            Check(f.Guard.Disable(path as string).Success && Norm(f.Files.Values[path].Dacl) == Norm("D:(D;;0x1;;;WD)(A;;FA;;;BA)"), "组合拒绝只去掉执行位，保留其余拒绝");
            f = New(); f.Files.Values[path] = new(path, "legacy", "D:AI(D;ID;0x20;;;WD)(A;ID;FA;;;BA)");
            var inherited = f.Guard.Disable(path as string);
            Check(inherited.Success && inherited.Message.Contains("继承") && f.Files.Writes == 0, "继承的拒绝执行不改动、不算失败");

            f = New(); f.Files.Values[path] = new(path, "replaced", Original);
            f.State.Value.Acls.Add(new GuardAclRecord { Path = path, Identity = "old", Original = Original, Applied = legacyAcl });
            f.State.Value.Mode = RuntimeGuardMode.Full;
            Check(f.Guard.Disable(default(string?)).Success && f.State.Value.Acls.Count == 0, "上一版本记录的文件被游戏替换：直接作废记录");
            f = New(); f.State.Value.Acls.Add(new GuardAclRecord { Path = Path.Combine(root, "gone", "UE4PrereqSetup_x64.exe"), Identity = "x", Original = Original, Applied = legacyAcl });
            Check(f.Guard.RepairBlocker(null) is null && f.Guard.Probe(null).Issue.Length == 0, "记录的文件已不存在不再阻塞修复和状态读取");
            Check(f.Guard.Disable(default(string?)).Success && f.State.Value.Acls.Count == 0, "不存在的文件记录关闭时作废");
            f = New(); f.Files.Values[path] = new(path, "legacy", legacyAcl);
            f.State.Value.LegacyAclRepairs.Add(new GuardLegacyAclRepair { Path = path, Identity = "legacy", Before = legacyAcl, After = Original });
            f.Files.FailAfterWrite = true;
            Check(!f.Guard.Disable(default(string?)).Success, "权限写入异常如实报告");
            Check(f.Guard.Disable(default(string?)).Success && f.State.Value.LegacyAclRepairs.All(r => r.Completed) && Norm(f.Files.Values[path].Dacl) == Norm(Original),
                "上一版本未完成的旧权限修复可直接续作");
            f = New(); f.Files.Values[path] = new(path, "legacy", legacyAcl);
            f.State.Value.Acls.Add(new GuardAclRecord { Path = path, Identity = "legacy", Original = Original, Applied = legacyAcl });
            f.Files.FailAfterWrite = true;
            Check(!f.Guard.ForceCleanup(null, default(string?)).Success && f.State.Value.Acls.Count == 1,
                "没有当前游戏路径时强制清理仍处理备份路径，失败不删除证据");
            Check(f.Guard.ForceCleanup(null, default(string?)).Success && f.State.Value.Acls.Count == 0 && Norm(f.Files.Values[path].Dacl) == Norm(Original),
                "无当前路径的强制清理失败可续作并复核");
            Check(!RuntimeGuardProtection.StripExecuteDeny("D:(A;;FA;;;BA)", out _), "无拒绝执行时不改动");

            var storePath = Path.Combine(root, "machine.json");
            var disk = new GuardStateStore(storePath);
            disk.Save(new GuardProtectionState());
            Check(disk.Load().Version == 1, "机器记录 JSON 往返");
            File.WriteAllText(storePath, "{bad-json");
            try { disk.Load(); throw new Exception("corrupt state accepted"); } catch (JsonException) { }
            Check(File.ReadAllText(storePath) == "{bad-json", "损坏状态文件读取时不覆盖");
            File.Delete(storePath);
            var beta09 = new GuardProtectionState
            {
                Mode = RuntimeGuardMode.Full,
                Ifeo = [new GuardIfeoRecord { Name = names[0], Marker = RuntimeGuardProtection.OwnerPrefix + "a/absent" }],
                Acls = [new GuardAclRecord { Path = path, Identity = "i", Original = Original, Applied = legacyAcl }],
                LegacyAclRepairs = [new GuardLegacyAclRepair { Path = path, Identity = "i", Before = legacyAcl, After = Original }],
                LegacyIfeoRepairs = [new GuardLegacyIfeoRepair { Name = names[1], Before = Taskkill(hotPatch: true) },
                    new GuardLegacyIfeoRepair { Name = names[2], Before = new("other.exe", RegistryValueKind.String, null, false), Completed = true }],
            };
            disk.Save(beta09);
            Check(disk.Load() is { Ifeo.Count: 1, Acls.Count: 1, LegacyAclRepairs.Count: 1, LegacyIfeoRepairs.Count: 2 }, "上一版本状态及强制清理快照 JSON 往返");

            // Only a disposable fixture file is touched; no installed prerequisite or IFEO key is changed.
            File.WriteAllText(path, "fixture");
            var native = new GuardFileBackend();
            var before = native.Read(path);
            var security = new FileSecurity();
            security.SetSecurityDescriptorSddlForm(before.Dacl, AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadData, AccessControlType.Deny));
            native.SetDacl(path, before.Identity, before.Dacl, security.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            var denyRead = native.Read(path);
            security.SetSecurityDescriptorSddlForm(denyRead.Dacl, AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ExecuteFile, AccessControlType.Deny));
            native.SetDacl(path, before.Identity, denyRead.Dacl, security.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            Check(RuntimeGuardProtection.HasExplicitExecuteDeny(native.Read(path).Dacl), "临时文件按上一版本方式写入拒绝执行");
            var nativeGuard = new RuntimeGuardProtection(new RegistryFake(), native, new StateFake(), new TweakBackupStore(Path.Combine(root, "native-legacy.json")));
            Check(nativeGuard.Disable(path as string).Success && Norm(native.Read(path).Dacl) == Norm(denyRead.Dacl), "真实剥离拒绝执行，保留预先存在的拒绝读取及继承状态");
            native.SetDacl(path, before.Identity, denyRead.Dacl, before.Dacl);
            Console.WriteLine("Runtime protection recovery checks passed; real HKLM and prerequisite files were not modified.");
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
    private static string Norm(string sddl) => RuntimeGuardProtection.NormalizeDacl(sddl);
    private sealed record Fixture(RuntimeGuardProtection Guard, RegistryFake Registry, FilesFake Files, StateFake State, TweakBackupStore Legacy);
    private sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
    private sealed class StateFake : IGuardStateStore
    {
        internal GuardProtectionState Value = new(); internal bool FailSave, FailLoad; internal int Saves;
        public GuardProtectionState Load() => FailLoad ? throw new InvalidDataException("防护状态结构无效。")
            : JsonSerializer.Deserialize<GuardProtectionState>(JsonSerializer.Serialize(Value))!;
        public void Save(GuardProtectionState state)
        {
            if (FailSave) throw new IOException("save failed");
            Value = JsonSerializer.Deserialize<GuardProtectionState>(JsonSerializer.Serialize(state))!; Saves++; FailLoad = false;
        }
    }
    private sealed class RegistryFake : IGuardRegistry
    {
        internal readonly Dictionary<string, GuardRegistryValue> Values = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Sticky = new(StringComparer.OrdinalIgnoreCase);
        internal int Writes; internal bool FailAfterWrite, FailRead;
        public GuardRegistryValue Read(string name) => FailRead ? throw new IOException("read failed") : Values.GetValueOrDefault(name, new(null, RegistryValueKind.None, null, false));
        internal void ApplyRewrites()
        {
            foreach (var name in Sticky) Values[name] = new(RuntimeGuardProtection.Debugger, RegistryValueKind.String, null, true) { ForeignMarker = "RuntimeGuardHotPatch" };
        }
        public void SetDebugger(string name, string? value, RegistryValueKind kind)
        {
            Values[name] = Read(name) with { Debugger = value, Kind = kind }; Writes++;
            if (FailAfterWrite) { FailAfterWrite = false; throw new IOException("write changed value then failed"); }
        }
        public void SetOwner(string name, string? value) { Values[name] = Read(name) with { Owner = value }; Writes++; }
        public void ClearForeignOwner(string name) { Values[name] = Read(name) with { ForeignOwner = false, ForeignMarker = null }; Writes++; }
        public void RemoveEmptyKey(string name)
        {
            if (Values.TryGetValue(name, out var v) && v.Debugger is null && v.Owner is null && !v.ForeignOwner) Values.Remove(name);
        }
    }
    private sealed class FilesFake : IGuardFiles
    {
        internal readonly Dictionary<string, GuardFileValue> Values = new(StringComparer.OrdinalIgnoreCase);
        internal bool FailAfterWrite; internal int Writes;
        public GuardFileValue Read(string path) => Values.TryGetValue(path, out var result) ? result : throw new FileNotFoundException(path);
        public void SetDacl(string path, string identity, string expected, string sddl)
        {
            var current = Read(path);
            if (current.Identity != identity || RuntimeGuardProtection.NormalizeDacl(current.Dacl) != RuntimeGuardProtection.NormalizeDacl(expected)) throw new IOException("conflict");
            Values[path] = current with { Dacl = sddl }; Writes++;
            if (FailAfterWrite) { FailAfterWrite = false; throw new InvalidOperationException("acl changed then failed"); }
        }
    }
}
