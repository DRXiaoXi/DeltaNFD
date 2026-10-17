using DeltaNFD.Backends;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

internal static class FrameBackgroundChecks
{
    internal static void Run()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-frame-background-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        var frameActive = false;
        var suppressionSelected = true;
        var memorySelected = true;
        var now = DateTime.UtcNow;
        var target = new ProcmanProcessIdentity(10101, now, @"C:\Apps\fixture.exe");
        var sample = new ProcmanCpuSample(20, [(target, 0.1)]);
        var writes = 0;
        var raceOff = false;
        var raceMemoryChoiceOff = false;
        using var engine = new ProcmanEngine(Path.Combine(root, "changes.json"))
        {
            BackgroundLoopDisabledForTests = true, FrameModeActiveForTests = () => frameActive,
            FrameSuppressionSelectedForTests = () => suppressionSelected,
            FrameMemorySelectedForTests = () => memorySelected,
            ClockForTests = () => now, SampleForTests = () => sample, ForegroundForTests = () => 20202,
            MemoryPressureForTests = () => 95, MemoryPressureWindowForTests = TimeSpan.Zero,
            MemorySampleDelayForTests = TimeSpan.Zero,
            ResponsivenessExcludedForTests = _ => false, MemoryReadForTests = _ => 600L * 1024 * 1024,
            MemoryTrimForTests = (identity, guard) =>
            {
                if (raceOff) frameActive = false;
                if (raceMemoryChoiceOff) memorySelected = false;
                if (!guard()) return new(identity.Pid, "fixture", "skipped", "frame stopped");
                writes++; return new(identity.Pid, "fixture", "completed", "simulated", 600L * 1024 * 1024, 200L * 1024 * 1024);
            },
        };
        var excluded = @"C:\Apps\excluded.exe";
        var config = new ProcmanBackgroundConfig
        {
            AutomaticMemoryEnabled = true, MemoryPressurePercent = 91, ExcludedExecutablePaths = [excluded],
            Suppression = new() { CpuBusyThreshold = 77, CpuIdleThreshold = 54, MaxAdjustPerRound = 3, CooldownSeconds = 23 },
        };
        Check(engine.SaveBackgroundConfig(config).Success, "帧格关闭时可以保存具体参数");
        Check(!engine.BackgroundStatus().AutomaticMemoryActive && !engine.Snapshot().ResponsivenessEnabled,
            "旧启用字段不能在帧格关闭时独立启动后台功能");
        engine.AutomaticMemoryOnceForTests();
        Check(writes == 0, "帧格关闭时内存高压仍不回收");
        Check(engine.ConfigureResponsiveness(new() { Enabled = true }).Success && !engine.Snapshot().ResponsivenessEnabled,
            "旧后台抑制启用命令也受帧格总开关约束");
        var rulePath = Path.Combine(root, DeltaProcessRules.ExecutableName);
        Check(engine.SaveRule(new() { ExecutablePath = rulePath, Capability = "priority", Value = "Normal" }).Success,
            "游戏离线规则与帧格后台功能共存");
        frameActive = true;
        Check(engine.SyncFrameBackground().Success && engine.BackgroundStatus().AutomaticMemoryActive && engine.Snapshot().ResponsivenessEnabled,
            "一键帧格开启同时启动后台抑制与自动回收");
        Check(engine.Snapshot().ResponsivenessConfig is { CpuBusyThreshold: 77, CpuIdleThreshold: 54, MaxAdjustPerRound: 3, CooldownSeconds: 23 },
            "启动使用进程管理保存的详细参数");
        Check(engine.SaveBackgroundConfig(engine.BackgroundStatus().Config with { AutomaticMemoryEnabled = false }).Success
            && engine.BackgroundStatus().AutomaticMemoryActive, "帧格开启时旧独立开关字段不能意外关闭自动回收");
        engine.AutomaticMemoryOnceForTests();
        Check(writes == 1, "帧格开启后持续高压才执行模拟回收");
        frameActive = false; now = now.AddMinutes(11);
        engine.AutomaticMemoryOnceForTests();
        Check(writes == 1, "即使停用同步尚未到达，写前总开关复核也阻止回收");
        Check(engine.SyncFrameBackground().Success && !engine.BackgroundStatus().AutomaticMemoryActive && !engine.Snapshot().ResponsivenessEnabled,
            "一键帧格关闭同时停用两项后台功能");
        Check(engine.ListRules().Rules is [{ Enabled: true }], "帧格关闭不删除或停用三角洲规则");
        Check(engine.BackgroundStatus().Config.Suppression == config.Suppression
            && engine.BackgroundStatus().Config.ExcludedExecutablePaths.SequenceEqual([excluded])
            && engine.BackgroundStatus().Config.MemoryPressurePercent == 91, "关闭帧格保留参数和排除名单");
        var changed = engine.BackgroundStatus().Config with { MemoryPressurePercent = 92,
            Suppression = config.Suppression with { CooldownSeconds = 31 } };
        Check(engine.SaveBackgroundConfig(changed).Success && !engine.Snapshot().ResponsivenessEnabled
            && !engine.BackgroundStatus().AutomaticMemoryActive, "帧格关闭时重新保存参数仍不会启动功能");
        frameActive = true;
        Check(engine.SyncFrameBackground().Success && engine.Snapshot().ResponsivenessConfig!.CooldownSeconds == 31,
            "重新开启使用新参数");
        raceOff = true; now = now.AddMinutes(11);
        engine.AutomaticMemoryOnceForTests();
        Check(writes == 1, "实际回收调用前帧格关闭，最后一道保护仍阻止写入");
        Check(engine.SyncFrameBackground().Success, "中途停用后仍可正常完成收尾");
        Check(BackendModules.RequiresWriteIntent(BackendModules.Procman, BackendCommands.ProcmanFrameBackgroundSync), "帧格联动命令纳入写前证据机制");
        var stored = new ProcmanBackgroundStore(Path.Combine(root, "changes.json.background.json")).Load();
        Check(stored.Suppression.CooldownSeconds == 31 && stored.MemoryPressurePercent == 92,
            "重新读取文件仍保留详细参数");
        raceOff = false;
        foreach (var master in new[] { false, true })
        foreach (var suppressionChoice in new[] { false, true })
        foreach (var memoryChoice in new[] { false, true })
        {
            frameActive = master; suppressionSelected = suppressionChoice; memorySelected = memoryChoice;
            Check(engine.SyncFrameBackground().Success, $"同步三开关组合：帧格={master}，抑制={suppressionChoice}，回收={memoryChoice}");
            Check(engine.Snapshot().ResponsivenessEnabled == (master && suppressionChoice)
                && engine.BackgroundStatus().AutomaticMemoryActive == (master && memoryChoice),
                "每项仅在自身功能开关和帧格总开关同时开启时运行");
        }
        frameActive = true; suppressionSelected = false; memorySelected = false;
        Check(engine.ConfigureResponsiveness(new() { Enabled = true }).Success && !engine.Snapshot().ResponsivenessEnabled,
            "旧抑制执行入口不能绕过未选择的功能开关");
        memorySelected = true;
        Check(engine.SyncFrameBackground().Success, "单独选择内存回收不隐式选择后台抑制");
        now = now.AddMinutes(11); raceMemoryChoiceOff = true;
        engine.AutomaticMemoryOnceForTests();
        Check(writes == 1, "内存回收功能选项在写入前关闭，最后复核仍阻止操作");
        frameActive = false; raceMemoryChoiceOff = false;
        Check(engine.SyncFrameBackground().Success, "收尾停用保留功能选项和参数");
        var settingsPath = Path.Combine(root, "feature-options.json");
        var initial = AppSettingsStore.Read(settingsPath);
        Check(!initial.FrameBackgroundSuppressionEnabled && !initial.FrameAutomaticMemoryEnabled, "两项功能选择默认关闭");
        AppSettingsStore.Update(settingsPath, s => s.FrameBackgroundSuppressionEnabled = true);
        AppSettingsStore.Update(settingsPath, s => s.FrameAutomaticMemoryEnabled = true);
        AppSettingsStore.Update(settingsPath, s => s.FrameBackgroundSuppressionEnabled = false);
        var saved = AppSettingsStore.Read(settingsPath);
        Check(!saved.FrameModeActive && !saved.FrameBackgroundSuppressionEnabled && saved.FrameAutomaticMemoryEnabled,
            "功能开关独立持久化，不开启帧格或改动另一项选择");
        Console.WriteLine($"Frame background integration checks: {count} passed. Frame states and memory writes simulated; no user processes modified.");
    }
}
