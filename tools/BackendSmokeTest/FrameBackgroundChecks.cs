using System.Text.Json;
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
        var frameActive = false; var memorySelected = true;
        var now = DateTime.UtcNow;
        var target = new ProcmanProcessIdentity(10101, now, @"C:\Apps\fixture.exe");
        var writes = 0; var raceOff = false; var raceMemoryChoiceOff = false;
        using var engine = new ProcmanEngine(Path.Combine(root, "changes.json"))
        {
            BackgroundLoopDisabledForTests = true, FrameModeActiveForTests = () => frameActive,
            FrameMemorySelectedForTests = () => memorySelected, ClockForTests = () => now,
            MemoryIdentitiesForTests = () => [target],
            SampleForTests = () => throw new Exception("Memory reclaim must not sample CPU after suppression retirement."),
            ForegroundForTests = () => 20202, MemoryPressureForTests = () => 95,
            MemoryPressureWindowForTests = TimeSpan.Zero, MemorySampleDelayForTests = TimeSpan.Zero,
            BackgroundExcludedForTests = _ => false, MemoryReadForTests = _ => 600L * 1024 * 1024,
            MemoryTrimForTests = (identity, guard) =>
            {
                if (raceOff) frameActive = false;
                if (raceMemoryChoiceOff) memorySelected = false;
                if (!guard()) return new(identity.Pid, "fixture", "skipped", "frame stopped");
                writes++; return new(identity.Pid, "fixture", "completed", "simulated", 600L * 1024 * 1024, 200L * 1024 * 1024);
            },
        };
        var backgroundPath = Path.Combine(root, "changes.json.background.json");
        // Unknown/invalid retired values must not block memory settings or revive CPU suppression.
        File.WriteAllText(backgroundPath, """{"SchemaVersion":1,"AutomaticMemoryEnabled":true,"MemoryPressurePercent":91,"ExcludedExecutablePaths":["C:\\Apps\\excluded.exe"],"Suppression":{"Enabled":true,"CpuBusyThreshold":-100,"CpuIdleThreshold":999}}""");
        Check(engine.ActivateBackground().Success && !engine.BackgroundStatus().AutomaticMemoryActive,
            "旧抑制配置被忽略，帧格关闭时也不独立启用自动回收");
        Check(!engine.Snapshot().ResponsivenessEnabled && engine.Snapshot().ResponsivenessConfig is null,
            "旧状态字段保持停用，不返回可执行抑制参数");
        Check(!File.ReadAllText(backgroundPath).Contains("Suppression"), "保存后移除退休的抑制参数");
        var excluded = @"C:\Apps\excluded.exe";
        Check(engine.BackgroundStatus().Config.MemoryPressurePercent == 91
            && engine.BackgroundStatus().Config.ExcludedExecutablePaths.SequenceEqual([excluded]), "保留已有内存阈值与排除名单");
        engine.AutomaticMemoryOnceForTests(); Check(writes == 0, "帧格关闭时不回收");
        var rulePath = Path.Combine(root, DeltaProcessRules.ExecutableName);
        Check(engine.SaveRule(new() { ExecutablePath = rulePath, Capability = "priority", Value = "Normal" }).Success,
            "保留三角洲离线规则");
        foreach (var master in new[] { false, true })
        foreach (var choice in new[] { false, true })
        {
            frameActive = master; memorySelected = choice;
            Check(engine.SyncFrameBackground().Success, $"同步帧格={master}，回收={choice}");
            Check(engine.BackgroundStatus().AutomaticMemoryActive == (master && choice) && !engine.Snapshot().ResponsivenessEnabled,
                "只有自动回收的两个开关共同生效，不启用抑制");
        }
        frameActive = memorySelected = true;
        Check(engine.SaveBackgroundConfig(engine.BackgroundStatus().Config with { AutomaticMemoryEnabled = false }).Success
            && engine.BackgroundStatus().AutomaticMemoryActive, "兼容字段不能覆盖帧格功能选择");
        engine.AutomaticMemoryOnceForTests(); Check(writes == 1, "保留持续高压自动回收，无 CPU 采样");
        raceOff = true; now = now.AddMinutes(11); engine.AutomaticMemoryOnceForTests();
        Check(writes == 1, "实际回收前复核帧格开关");
        Check(engine.SyncFrameBackground().Success && !engine.BackgroundStatus().AutomaticMemoryActive, "帧格停用后完成收尾");
        raceOff = false; frameActive = true; memorySelected = true;
        Check(engine.SyncFrameBackground().Success, "再次启用自动内存回收");
        raceMemoryChoiceOff = true; now = now.AddMinutes(11); engine.AutomaticMemoryOnceForTests();
        Check(writes == 1, "实际回收前复核功能选择");
        frameActive = false; Check(engine.SyncFrameBackground().Success, "关闭帧格保留配置");
        Check(engine.ListRules().Rules is [{ Enabled: true }], "关闭帧格不删除或停用三角洲规则");
        var settingsPath = Path.Combine(root, "feature-options.json");
        File.WriteAllText(settingsPath, """{"FrameModeActive":true,"FrameBackgroundSuppressionEnabled":true,"FrameAutomaticMemoryEnabled":true}""");
        var settings = AppSettingsStore.Read(settingsPath);
        Check(settings.FrameAutomaticMemoryEnabled && settings.FrameModeActive, "读取旧设置保留帧格与内存选择");
        Check(!JsonSerializer.Serialize(settings).Contains("FrameBackgroundSuppressionEnabled"), "旧启用字段不进入新设置对象");
        AppSettingsStore.Update(settingsPath, s => s.FrameAutomaticMemoryEnabled = false);
        Check(!File.ReadAllText(settingsPath).Contains("FrameBackgroundSuppressionEnabled"), "新设置不写回旧抑制开关");
        foreach (var command in new[] { BackendCommands.ProcmanResponsivenessConfig, BackendCommands.ProcmanResponsivenessEvents })
            Check(!BackendModules.IsAllowed(BackendModules.Procman, command), "后端不再准入退休命令：" + command);
        // Use the actual persisted frame switches and actual 30-second pressure window, including
        // a corrupt CPU/frame record. That module must not silently disable the separate memory engine.
        var cpuRecord = Path.Combine(root, "official-backend-unconfirmed", "cpuframe.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cpuRecord)!);
        File.WriteAllBytes(cpuRecord, new byte[128]);
        AppSettingsStore.Update(s => { s.FrameModeActive = true; s.FrameAutomaticMemoryEnabled = true; });
        foreach (var mode in new[] { ProcmanMemoryModes.Normal, ProcmanMemoryModes.DeltaStable, ProcmanMemoryModes.Aggressive })
        {
            var trims = 0; uint pressure = 90; now = DateTime.UtcNow;
            using var available = new ProcmanEngine(Path.Combine(root, mode, "changes.json"))
            {
                BackgroundLoopDisabledForTests = true, ClockForTests = () => now,
                ForegroundForTests = () => 20202, MemoryPressureForTests = () => pressure,
                MemoryIdentitiesForTests = () => [target], MemoryReadForTests = _ => 1,
                BackgroundExcludedForTests = _ => false, MemorySystemProcessForTests = _ => false,
                MemoryPolicyForTests = () => new([]),
                SampleForTests = () => throw new Exception("Memory operation must not sample CPU."),
                MemoryTrimForTests = (_, guard) => { if (guard()) trims++; return new(target.Pid, "fixture", "completed", "simulated"); },
                MemoryGlobalTrimForTests = guard => { if (guard()) trims++; return new([], "simulated global request"); }
            };
            Check(available.SaveBackgroundConfig(new() { MemoryMode = mode, MemoryCooldownSeconds = 60 }).Success
                && available.BackgroundStatus().AutomaticMemoryActive, mode + ": both real frame switches activate memory independently.");
            available.AutomaticMemoryOnceForTests();
            now = now.AddSeconds(29); available.AutomaticMemoryOnceForTests();
            Check(trims == 0, mode + ": brief pressure does not trim.");
            now = now.AddSeconds(1); available.AutomaticMemoryOnceForTests();
            Check(trims == 1, mode + ": sustained 30-second pressure reaches the trim dispatcher.");
            now = now.AddSeconds(30); available.AutomaticMemoryOnceForTests();
            Check(trims == 1, mode + ": configured interval is respected.");
            pressure = 50; now = now.AddSeconds(30); available.AutomaticMemoryOnceForTests();
            Check(trims == 1, mode + ": low pressure does not trigger more cleaning.");
            AppSettingsStore.Update(s => s.FrameAutomaticMemoryEnabled = false);
            pressure = 95; now = now.AddMinutes(2); available.AutomaticMemoryOnceForTests();
            Check(trims == 1 && !available.BackgroundStatus().AutomaticMemoryActive, mode + ": disabling the persisted option prevents writes.");
            AppSettingsStore.Update(s => s.FrameAutomaticMemoryEnabled = true);
        }
        Check(File.ReadAllBytes(cpuRecord).All(b => b == 0), "Memory checks never discard corrupt CPU/frame recovery evidence.");
        Check(BackendModules.RequiresWriteIntent(BackendModules.Procman, BackendCommands.ProcmanFrameBackgroundSync), "帧格同步仍保留写前证据");
        Console.WriteLine($"Frame background retirement checks: {count} passed. Frame states and memory writes simulated; no user processes modified.");
    }
}
