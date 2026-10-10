using System.Reflection;
using DeltaNFD.Services;

internal static class ApplicationPreLaunchChecks
{
    internal static async Task RunAsync()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_PreLaunch_" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        (TweakBackupStore Store, Fake Agent, ApplicationPreLaunchControl Control) New(bool enabled = true)
        {
            var store = new TweakBackupStore(Path.Combine(root, Guid.NewGuid().ToString("N") + ".json"));
            var agent = new Fake { Enabled = enabled };
            return (store, agent, new ApplicationPreLaunchControl(store, agent.Execute));
        }
        var on = New();
        Check((await on.Control.DisableAsync()).Success && !on.Agent.Enabled, "实际 MMAgent 开关被定向关闭并复检");
        Check(on.Store.GetStrict("State", "MMAgent", "ApplicationPreLaunch")?.Data == "1", "关闭前备份原始开启状态");
        Check((await on.Control.DisableAsync()).Success && on.Store.GetStrict("State", "MMAgent", "ApplicationPreLaunch")?.Data == "1", "重复关闭不覆盖最初备份");
        Check((await on.Control.RestoreAsync()).Success && on.Agent.Enabled, "按原值恢复开启且复检");
        Check(on.Store.GetStrict("State", "MMAgent", "ApplicationPreLaunch") is null, "只有恢复复检成功才移除记录");
        var off = New(false);
        Check((await off.Control.DisableAsync()).Success && off.Store.GetStrict("State", "MMAgent", "ApplicationPreLaunch")?.Data == "0", "原来已关闭也不会备份成开启");
        Check((await off.Control.RestoreAsync()).Success && !off.Agent.Enabled && off.Agent.Commands.All(c => c != ApplicationPreLaunchControl.Command(true)), "原来关闭时不误启用");
        var old = New();
        Check((await old.Control.RestoreAsync()).IsSkipped && old.Agent.Commands.Count == 0, "旧版本只有注册表备份时不猜测 MMAgent 原状态");
        var unread = New(); unread.Agent.FailRead = true;
        Check(!(await unread.Control.DisableAsync()).Success && unread.Agent.Enabled && unread.Agent.Writes == 0, "读不到原状态时不执行写入");
        var fail = New(); fail.Agent.FailWrite = true;
        Check(!(await fail.Control.DisableAsync()).Success && fail.Store.GetStrict("State", "MMAgent", "ApplicationPreLaunch")?.Data == "1", "关闭报错保留原状态");
        var noEffect = New(); noEffect.Agent.IgnoreWrites = true;
        Check(!(await noEffect.Control.DisableAsync()).Success && noEffect.Agent.Enabled, "命令退出零但状态仍开启不能报成功");
        var restoreFailed = New(); await restoreFailed.Control.DisableAsync(); restoreFailed.Agent.FailWrite = true;
        Check(!(await restoreFailed.Control.RestoreAsync()).Success && restoreFailed.Store.GetStrict("State", "MMAgent", "ApplicationPreLaunch") is not null, "恢复失败保留记录");
        restoreFailed.Agent.FailWrite = false;
        Check((await restoreFailed.Control.RestoreAsync()).Success && restoreFailed.Agent.Enabled, "恢复失败可重试");
        var restoreNoEffect = New(); await restoreNoEffect.Control.DisableAsync(); restoreNoEffect.Agent.IgnoreWrites = true;
        Check(!(await restoreNoEffect.Control.RestoreAsync()).Success && restoreNoEffect.Store.GetStrict("State", "MMAgent", "ApplicationPreLaunch") is not null,
            "恢复命令退出零但状态未还原仍保留备份");
        var corrupt = New();
        corrupt.Store.SaveStrict(new RegistryValueBackup { Hive="State",KeyPath="MMAgent",ValueName="ApplicationPreLaunch",Data="invalid" });
        Check(!(await corrupt.Control.DisableAsync()).Success && corrupt.Agent.Writes == 0, "损坏原状态不被覆盖或继续写入");
        var blocked = New();
        blocked.Store.SaveStrict(new RegistryValueBackup { Hive="State",KeyPath="fixture",ValueName="original",Data="keep" });
        var path = (string)typeof(TweakBackupStore).GetField("_filePath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(blocked.Store)!;
        using (var locked = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            Check(!(await blocked.Control.DisableAsync()).Success && blocked.Agent.Writes == 0, "备份写入失败不得关闭真实功能");
        foreach (var output in new string?[] { null, "", "unknown", "False", "2", "0\n1" })
            Check(ApplicationPreLaunchControl.Parse(0,output) is null, "坏输出不当成关闭");
        Check(ApplicationPreLaunchControl.Parse(1,"0") is null, "失败退出码不采信状态输出");
        Check(ApplicationPreLaunchControl.Parse(0,"0") == false && ApplicationPreLaunchControl.Parse(0,"1") == true, "合法布尔状态解析");
        var statusMethod = typeof(SystemTweakService).GetMethod("PrefetchTrioToState", BindingFlags.Static | BindingFlags.NonPublic)!;
        var trueStatus = ((bool Optimized,string Detail))statusMethod.Invoke(null,[0,0,0,true])!;
        var unknownStatus = ((bool Optimized,string Detail))statusMethod.Invoke(null,[0,0,0,null])!;
        var closedStatus = ((bool Optimized,string Detail))statusMethod.Invoke(null,[0,0,0,false])!;
        Check(!trueStatus.Optimized && trueStatus.Detail.Contains("ApplicationPreLaunch 开启"), "注册表关闭但 MMAgent 开启时不能显示全部优化");
        Check(!unknownStatus.Optimized && unknownStatus.Detail.Contains("无法确认"), "查询失败不能显示全部关闭");
        Check(closedStatus.Optimized, "实际关闭与注册表状态一致才显示已优化");
        var onlyState = New();
        onlyState.Store.SaveStrict(new RegistryValueBackup { Hive="State",KeyPath="MMAgent",ValueName="ApplicationPreLaunch",Data="1" });
        Check(new SystemTweakService(onlyState.Store).GetBackedUpTweaks().Contains(SystemTweak.Prefetch), "一键恢复能发现仅 MMAgent 的新备份");
        Check(on.Agent.Commands.All(c => c == ApplicationPreLaunchControl.Query || c == ApplicationPreLaunchControl.Command(false) || c == ApplicationPreLaunchControl.Command(true)), "命令限于查询及单参数启停，不存在 SysMain 或其他 MMAgent 写入");
        var source = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),"src/DeltaNFD.Execution/Execution/SystemTweakService.cs"));
        var apply = source[source.IndexOf("private async Task<OperationResult> ApplyPrefetchTrioOffAsync()", StringComparison.Ordinal)..source.IndexOf("private void BackupDword(", StringComparison.Ordinal)];
        Check(!apply.Contains("sc.exe") && !apply.Contains("Set-Service") && !apply.Contains("Stop-Service") && !apply.Contains("Disable-MMAgent -MemoryCompression"), "预读取入口没有关闭 SysMain/内存压缩的旁路");
        Console.WriteLine($"ApplicationPreLaunch 检查通过：{count} 项；所有写入为模拟，备份为临时 JSON，没有修改 SysMain 或 MMAgent。");
    }

    private sealed class Fake
    {
        internal bool Enabled, FailRead, FailWrite, IgnoreWrites;
        internal int Writes;
        internal List<string> Commands = [];
        internal Task<(int Code,string? Output,string? Error)> Execute(string command)
        {
            Commands.Add(command);
            if (command == ApplicationPreLaunchControl.Query)
                return Task.FromResult<(int,string?,string?)>(FailRead ? (1,null,"unsupported") : (0,Enabled ? "1" : "0",null));
            Writes++;
            if (FailWrite) return Task.FromResult<(int,string?,string?)>((1,null,"write failed"));
            if (!IgnoreWrites) Enabled = command == ApplicationPreLaunchControl.Command(true);
            return Task.FromResult<(int,string?,string?)>((0,null,null));
        }
    }
}
