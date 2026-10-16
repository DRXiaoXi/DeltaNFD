using System.Xml.Linq;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

internal static class DeltaRulesOfflineChecks
{
    internal static void Run()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-delta-offline-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        var target = Path.Combine(root, "game-not-started", DeltaProcessRules.ExecutableName);
        Check(!File.Exists(target) && DeltaProcessRules.NormalizeTarget(target) == target, "主进程未启动且文件尚未出现也可指定规则目标");
        foreach (var invalid in new[] { DeltaProcessRules.ExecutableName, Path.Combine(root, "Launcher.exe"), Path.Combine(root, "ACE-CORE.sys"), "" })
        {
            var rejected = false;
            try { DeltaProcessRules.NormalizeTarget(invalid); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "三角洲页拒绝其他文件和相对路径：" + invalid);
        }
        var install = Path.Combine(root, "installed");
        var exe = Path.Combine(install, "DeltaForce", "Binaries", "Win64", DeltaProcessRules.ExecutableName);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!); File.WriteAllBytes(exe, []);
        Check(DeltaProcessRules.FindTarget([install], []) == exe, "只读安装目录即可识别主进程，无需查询运行进程");
        var saved = new[] { new ProcmanRuleEntry { ExecutablePath = target, Capability = "priority", Value = "Normal" } };
        Check(DeltaProcessRules.FindTarget([], saved) == target, "未安装/已移动时仍可从原保存规则选中目标");
        Check(DeltaProcessRules.FindTarget([install], saved) == exe, "存在安装目录时优先使用安装位置");
        Check(DeltaProcessRules.FindTarget([], []) == "", "无法识别时不猜测游戏路径");

        var changes = Path.Combine(root, "offline-changes.json");
        using var engine = new ProcmanEngine(changes) { BackgroundLoopDisabledForTests = true };
        foreach (var (capability, value) in new[] { ("priority", "AboveNormal"), ("ecoQos", "false"), ("priorityBoost", "True"),
            ("affinity", "g0:0x1"), ("cpuSet", "g0:0x1"), ("ioPriority", "Normal"), ("memoryPriority", "5"), ("gpuPriority", "2") })
            Check(engine.SaveRule(new() { ExecutablePath = target, Capability = capability, Value = value }).Success,
                "未运行主进程时可持久保存属性：" + capability);
        var rules = new ProcmanRuleStore(changes + ".rules.json").Load();
        Check(rules.Count == 8 && rules.All(r => r.ExecutablePath == target && !r.AutoTakeover), "八项设置仅属于同一个完整路径，未隐式授权接管");
        Check(engine.Snapshot().HeldChangeCount == 0, "离线保存不会伪造运行时修改记录");
        Check(engine.SaveRule(new() { ExecutablePath = target, Capability = "priority", Value = "High", Enabled = false }).Success,
            "未启动时也可以编辑并停用已有规则");
        rules = engine.ListRules().Rules;
        Check(rules.Count == 8 && rules.Single(r => r.Capability == "priority") is { Value: "High", Enabled: false },
            "编辑替换单一属性，不追加重复项或改动其他属性");
        Check(engine.Snapshot().ResponsivenessConfig is null, "未配置抑制时不会伪造已保存参数");
        Check(engine.ConfigureResponsiveness(new()
        {
            Enabled = false, CpuBusyThreshold = 74, CpuIdleThreshold = 53, MaxAdjustPerRound = 3,
            CooldownSeconds = 22, CpuBusySeconds = 7, CpuIdleSeconds = 11, CandidateCpuFloorPercent = 13,
        }).Success, "设置帧格页需要回显的抑制参数");
        var config = engine.Snapshot().ResponsivenessConfig;
        Check(config is { Enabled: false, CpuBusyThreshold: 74, CpuIdleThreshold: 53, MaxAdjustPerRound: 3,
            CooldownSeconds: 22, CpuBusySeconds: 7, CpuIdleSeconds: 11, CandidateCpuFloorPercent: 13 }, "快照完整回显可见和隐藏抑制参数");
        config!.CpuBusyThreshold = 99;
        Check(engine.Snapshot().ResponsivenessConfig!.CpuBusyThreshold == 74, "UI 修改快照不会直接改写后台执行配置");
        var excluded = Path.Combine(root, "excluded.exe");
        Check(engine.SaveBackgroundConfig(new() { ExcludedExecutablePaths = [excluded], MemoryPressurePercent = 87 }).Success,
            "保存共用排除名单与内存阈值");
        var memory = engine.BackgroundStatus();
        Check(engine.SaveBackgroundConfig(memory.Config with { AutomaticMemoryEnabled = true, MemoryPressurePercent = 90 }).Success,
            "进程管理保存后台参数，沿用最新共享配置");
        Check(engine.BackgroundStatus().Config.ExcludedExecutablePaths.SequenceEqual([excluded])
            && engine.BackgroundStatus().Config.MemoryPressurePercent == 90, "迁移开关不丢失进程管理中的排除项");

        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var views = Path.Combine(Directory.GetCurrentDirectory(), "src", "DeltaNFD", "Views");
        var process = XDocument.Load(Path.Combine(views, "ProcessPage.xaml"));
        var frame = XDocument.Load(Path.Combine(views, "FramePage.xaml"));
        XElement Named(XDocument doc, string name) => doc.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
        Check((string?)Named(process, "GameTab").Attribute("Header") == "三角洲规则", "三角洲页签呈现离线规则用途");
        Check(Named(process, "GameTab").Descendants().Any(e => (string?)e.Attribute(x + "Name") == "GameRulePathBox"), "规则页有独立目标路径入口");
        Check(Named(process, "RuleCpuGroupPanel").Name.LocalName == "StackPanel", "CPU 规则有独立拓扑选择器");
        foreach (var name in new[] { "ResponsivenessToggle", "AutomaticMemoryToggle" })
        {
            Check(!process.Descendants().Any(e => (string?)e.Attribute(x + "Name") == name), "进程管理移除自动开关/参数：" + name);
            Check(!frame.Descendants().Any(e => (string?)e.Attribute(x + "Name") == name), "帧格不保留旧的独立执行开关：" + name);
        }
        foreach (var name in new[] { "FrameSuppressionToggle", "FrameAutomaticMemoryToggle" })
        {
            var option = Named(frame, name);
            Check(option.Name.LocalName == "ToggleSwitch" && (string?)option.Attribute("Toggled") == "FrameBackgroundChoice_Toggled", "帧格提供持久化功能选择开关：" + name);
            Check(((string?)option.Attribute("IsEnabled"))?.Contains("FrameSrv.FeaturesEditable") == true, "功能选项与其余帧格功能一致，先退出帧格再编辑");
        }
        foreach (var name in new[] { "BusyThresholdBox", "IdleThresholdBox", "MaxAdjustBox", "CooldownBox", "MemoryPressureBox" })
        {
            Check(Named(process, name) is not null, "具体后台参数保留在进程管理：" + name);
            Check(!frame.Descendants().Any(e => (string?)e.Attribute(x + "Name") == name), "帧格页不重复配置参数：" + name);
        }
        Check(process.Descendants().Any(e => (string?)e.Attribute("Click") == "OpenFrameBackground_Click"), "进程管理引导前往帧格");
        Check(Named(process, "MemoryPrelaunchPanel") is not null && Named(process, "MemoryPreviewButton") is not null,
            "保留预启动设置与手动内存回收");
        Console.WriteLine($"Delta offline rules and Frame background checks: {count} passed. Isolated rule/config files only; no game process started or modified.");
    }
}
