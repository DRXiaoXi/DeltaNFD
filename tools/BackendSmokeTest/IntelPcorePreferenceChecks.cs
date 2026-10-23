using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class IntelPcorePreferenceChecks
{
    private sealed class Fixture
    {
        internal readonly string Scheme = Guid.NewGuid().ToString("D");
        internal string Active;
        internal readonly Dictionary<string, (int Ac, int Dc)> Values = PowerSchemes.HeteroPolicySettings
            .Select((v, i) => (v.Guid, Value: i == 0 ? (4, 3) : (5, 4))).ToDictionary(x => x.Guid, x => x.Value);
        internal readonly List<string> Calls = [];
        internal int WriteStep, FailOnceAt, IgnoreAt, SwitchAt;
        internal bool ReadFailure, ReadFailureAfterWrite;
        internal Fixture() { Active = Scheme; }
        internal PowerService Service(bool elevated = true, bool intel = true) => new(Run, () => elevated) { IsIntelForTests = () => intel };
        internal Task<(int, string, string)> Run(string command, TimeSpan timeout)
        {
            Calls.Add(command); var p = command.Split(' ');
            if (p[0] == "/getactivescheme") return Task.FromResult((0, $"Power Scheme GUID: {Active} (fixture)", ""));
            if (p[0] == "/qh")
            {
                if (ReadFailure || ReadFailureAfterWrite && WriteStep > 0) return Task.FromResult((5, "", "fixture read denied"));
                var v = Values[p[3]]; return Task.FromResult((0, $"AC 0x{v.Ac:X8}\nDC 0x{v.Dc:X8}", ""));
            }
            if (p[0] == "/setactive") { Active = p[1]; return Task.FromResult((0, "", "")); }
            if (p[0] is not ("/setacvalueindex" or "/setdcvalueindex") || p[1] != Scheme) throw new Exception("Unexpected or unpinned command: " + command);
            WriteStep++;
            if (WriteStep == FailOnceAt) return Task.FromResult((5, "", "fixture one-shot write failure"));
            var old = Values[p[3]];
            if (WriteStep != IgnoreAt) Values[p[3]] = p[0] == "/setacvalueindex" ? (int.Parse(p[4]), old.Dc) : (old.Ac, int.Parse(p[4]));
            if (WriteStep == SwitchAt) Active = Guid.NewGuid().ToString("D");
            return Task.FromResult((0, "", ""));
        }
    }

    internal static async Task RunAsync()
    {
        var id = Guid.NewGuid(); var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-intel-pcore-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        var initial = new Fixture();
        var readOnly = await initial.Service().GetHeteroPoliciesReadOnlyAsync();
        Check(readOnly.Count == 3 && readOnly.All(p => p.Supported) && initial.WriteStep == 0
            && initial.Calls.All(c => c.StartsWith("/qh") || c == "/getactivescheme"), "查询隐藏策略不修改显隐或设置");
        initial.Calls.Clear();
        Check(!(await initial.Service(intel: false).PreferIntelPcoresAsync()).Success && initial.Calls.Count == 0, "非 Intel 拒绝且无 powercfg 调用");
        Check(!(await initial.Service(elevated: false).PreferIntelPcoresAsync()).Success && initial.Calls.Count == 0, "非管理员拒绝且无 powercfg 调用");
        initial.ReadFailure = true;
        Check(!(await initial.Service().PreferIntelPcoresAsync()).Success && initial.WriteStep == 0, "无法读取全部原值时不写入");
        initial.ReadFailure = false;
        var before = initial.Values.ToDictionary(x => x.Key, x => x.Value);
        var service = initial.Service();
        Check((await service.PreferIntelPcoresAsync()).Success, "成功应用 Intel 快捷预设");
        Check(initial.Values.All(v => v.Value == (v.Key == PowerSchemes.HeteroPolicySettings[0].Guid ? (0, 0) : (2, 2))), "交流/电池精确写入 0/2/2");
        Check(initial.Calls.All(c => !c.Contains("SCHEME_CURRENT") && !c.Contains("/attributes")), "全部读写绑定计划 GUID 且不改变隐藏属性");
        var writes = initial.WriteStep;
        Check((await service.PreferIntelPcoresAsync()).Success && initial.WriteStep == writes, "重复点击不覆盖备份或再次写入");
        Check(service.GetIntelPcoreRecoveryItems() is [{ State: "原值备份（操作已验证）", HasBackup: true }], "成功后仍可核对原值备份");
        initial.Active = Guid.NewGuid().ToString("D"); initial.Calls.Clear();
        Check((await service.RestoreIntelPcoresAsync()).Success && before.All(v => initial.Values[v.Key] == v.Value), "精确恢复原交流和电池差异");
        Check(!initial.Calls.Any(c => c.StartsWith("/setactive")) && service.GetIntelPcoreRecoveryItems().Count == 0, "恢复其他计划不切换活动计划且记录归档");
        for (var step = 1; step <= 6; step++)
        {
            var fake = new Fixture { FailOnceAt = step }; var originals = fake.Values.ToDictionary(x => x.Key, x => x.Value);
            var result = await fake.Service().PreferIntelPcoresAsync();
            Check(!result.Success && !result.OutcomeUnknown && originals.All(v => fake.Values[v.Key] == v.Value), "第 " + step + " 次写入失败时回滚准确");
        }
        var ignored = new Fixture { IgnoreAt = 3 };
        Check(!(await ignored.Service().PreferIntelPcoresAsync()).Success, "命令成功但回读不一致时不报成功");
        var switched = new Fixture { SwitchAt = 2 }; var switchedBefore = switched.Values.ToDictionary(x => x.Key, x => x.Value);
        Check(!(await switched.Service().PreferIntelPcoresAsync()).Success && switchedBefore.All(v => switched.Values[v.Key] == v.Value)
            && switched.Calls.All(c => !c.StartsWith("/setactive")), "途中切换计划只回滚原目标，不抢占新计划");
        var unknown = new Fixture { ReadFailureAfterWrite = true }; var uncertain = unknown.Service();
        var outcome = await uncertain.PreferIntelPcoresAsync();
        Check(!outcome.Success && outcome.OutcomeUnknown && uncertain.GetIntelPcoreRecoveryItems() is [{ State: "待核对" }], "失败后无法读取时保留待核对证据");
        var oldWrites = unknown.WriteStep;
        Check(!(await uncertain.PreferIntelPcoresAsync()).Success && unknown.WriteStep == oldWrites, "未知结果不重复执行");
        unknown.ReadFailureAfterWrite = false;
        Check((await uncertain.RestoreIntelPcoresAsync()).Success, "条件恢复后可按原记录恢复");
        var conflict = new Fixture(); var conflictService = conflict.Service();
        Check((await conflictService.PreferIntelPcoresAsync()).Success, "建立冲突恢复夹具");
        var first = PowerSchemes.HeteroPolicySettings[0].Guid; conflict.Values[first] = (1, 1); oldWrites = conflict.WriteStep;
        Check(!(await conflictService.RestoreIntelPcoresAsync()).Success && conflict.WriteStep == oldWrites, "外部值冲突不覆盖且保留证据");
        conflict.Values[first] = (0, 0); Check((await conflictService.RestoreIntelPcoresAsync()).Success, "冲突排除后可恢复");
        AppSettingsStore.Update(s => { s.FrameModeActive = true; s.FramePowerLockEnabled = true; });
        var locked = new Fixture();
        Check(!(await locked.Service().PreferIntelPcoresAsync()).Success && locked.Calls.Count == 0, "帧格锁定中拒绝互相覆盖");
        AppSettingsStore.Update(s => { s.FrameModeActive = false; s.FramePowerLockEnabled = false; });
        Check(BackendModules.IsAllowed(BackendModules.System, BackendCommands.SystemPowerIntelPcores)
            && BackendModules.RequiresWriteIntent(BackendModules.System, BackendCommands.SystemPowerIntelPcores)
            && BackendModules.RetainsStaticBackup(BackendModules.System, BackendCommands.SystemPowerIntelPcores), "合同纳入白名单、写前意图和静态备份");
        var corrupt = new Fixture(); var corruptService = corrupt.Service();
        Check((await corruptService.PreferIntelPcoresAsync()).Success, "建立损坏备份夹具");
        var backupPath = Path.Combine(root, "intel-pcore-policies", corrupt.Scheme + ".json");
        File.WriteAllText(backupPath, "{broken"); oldWrites = corrupt.WriteStep;
        Check(!(await corruptService.PreferIntelPcoresAsync()).Success && !(await corruptService.RestoreIntelPcoresAsync()).Success
            && corrupt.WriteStep == oldWrites && File.ReadAllText(backupPath) == "{broken", "备份损坏拒绝应用和恢复，文件保持原样");
        Console.WriteLine($"Intel P-core preference: {count} assertions passed; every powercfg operation simulated; no real power plans modified.");
    }
}
