using System.Text.Json;
using DeltaNFD.Services;

internal static class FactoryResetChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        bool Refused(Action action) { try { action(); return false; } catch { return true; } }
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-factory-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string Make(string name)
        {
            var directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new AppSettings
            {
                FrameModeActive = true, GamePriorityEnabled = true, GameAffinityRuleEnabled = true,
                SingleCcdExcludeCpu0Enabled = true, DualCcdImmediateEnabled = true, DwmRestartOnGameStart = true,
                CloseToTrayEnabled = true, FrameAutomaticMemoryEnabled = true,
                BackgroundPath = @"D:\custom-background.png", GameRootOverride = @"D:\game", AccentColor = "#123456",
                TempSpoofRestorePending = true, TempSpoofRestorePath = "original-gpu-path", GameAffinityRuleMask = ulong.MaxValue,
            }));
            File.WriteAllText(Path.Combine(directory, "procman-rules.json"), "{\"SchemaVersion\":1,\"Rules\":[{\"ExecutablePath\":\"C:\\\\App.exe\",\"Capability\":\"priority\",\"Value\":\"High\"}]}");
            File.WriteAllText(Path.Combine(directory, "procman-background.json"), "{\"SchemaVersion\":1,\"MemoryPressurePercent\":92}");
            foreach (var file in new[] { "offline-mode.json", "procman-changes.json", "backups.json", "optimize_backup.json", "unknown-future-state.json" })
                File.WriteAllText(Path.Combine(directory, file), "ORIGINAL EVIDENCE: " + file);
            Directory.CreateDirectory(Path.Combine(directory, "logs"));
            File.WriteAllText(Path.Combine(directory, "logs", "original.log"), "retain original diagnostics");
            return directory;
        }
        byte[] Read(string directory, string name) => File.ReadAllBytes(Path.Combine(directory, name));
        AppSettings Settings(string directory) => JsonSerializer.Deserialize<AppSettings>(Read(directory, "settings.json"))!;
        var normalRoot = Make("normal");
        var originals = Directory.GetFiles(normalRoot).ToDictionary(file => Path.GetFileName(file)!, File.ReadAllBytes);
        var normal = new FactoryResetConfiguration(normalRoot);
        Check(Refused(normal.CommitDefaults), "没有备份不能直接恢复默认值");
        normal.Backup();
        Check(File.Exists(Path.Combine(normal.BackupDirectory, "manifest.json")), "配置重置前创建备份清单");
        foreach (var name in new[] { "settings.json", "procman-rules.json", "procman-background.json", "offline-mode.json", "procman-changes.json" })
            Check(Read(normal.BackupDirectory, name).SequenceEqual(originals[name]!), "原始配置与证据备份字节保持：" + name);
        normal.PauseAutomation();
        var paused = Settings(normalRoot);
        Check(!paused.FrameModeActive && !paused.GamePriorityEnabled && !paused.GameAffinityRuleEnabled && !paused.SingleCcdExcludeCpu0Enabled
            && !paused.DualCcdImmediateEnabled && !paused.DwmRestartOnGameStart && !paused.CloseToTrayEnabled
            && !paused.FrameAutomaticMemoryEnabled, "恢复前停用所有 CPU/帧格自动启动标记");
        Check(paused.GameAffinityRuleMask == ulong.MaxValue && paused.TempSpoofRestorePending && paused.TempSpoofRestorePath == "original-gpu-path",
            "暂停不丢 CPU 掩码、待恢复显卡状态和路径");
        Check(paused.BackgroundPath == @"D:\custom-background.png" && paused.AccentColor == "#123456", "恢复执行前不清空其他偏好");
        normal.CommitDefaults();
        var defaults = Settings(normalRoot);
        Check(!defaults.FrameModeActive && !defaults.GamePriorityEnabled && !defaults.FrameAutomaticMemoryEnabled,
            "默认配置没有启用任何自动功能");
        Check(defaults.BackgroundPath == "" && defaults.GameRootOverride == "" && defaults.AccentColor == new AppSettings().AccentColor,
            "外观与目录偏好恢复产品默认值");
        using (var rules = JsonDocument.Parse(Read(normalRoot, "procman-rules.json")))
            Check(rules.RootElement.GetProperty("Rules").GetArrayLength() == 0 && rules.RootElement.GetProperty("SchemaVersion").GetInt32() == 1,
                "规则清空为有效文件，不删除文件让旧版迁移重新导入");
        var background = JsonSerializer.Deserialize<ProcmanBackgroundConfig>(Read(normalRoot, "procman-background.json"))!;
        Check(background.MemoryPressurePercent == 85 && background.ExcludedExecutablePaths.Count == 0
            && background.MemoryMode == ProcmanMemoryModes.Normal && background.MemoryBatchLimit == 5 && background.MemoryCooldownSeconds == 600,
            "后台详细参数与排除名单恢复默认值");
        foreach (var name in new[] { "offline-mode.json", "procman-changes.json", "backups.json", "optimize_backup.json", "unknown-future-state.json" })
            Check(Read(normalRoot, name).SequenceEqual(originals[name]!), "配置重置从不删改恢复或未知证据：" + name);
        Check(File.ReadAllText(Path.Combine(normalRoot, "logs", "original.log")) == "retain original diagnostics", "日志保持原样");
        Check(Read(Path.Combine(normal.BackupDirectory, "after-recovery"), "settings.json").SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(paused)),
            "原设置与恢复后安全配置分别备份");

        var failedRoot = Make("failed"); var failed = new FactoryResetConfiguration(failedRoot);
        failed.Backup(); failed.PauseAutomation();
        var beforeCommit = new[] { "settings.json", "procman-rules.json", "procman-background.json" }.ToDictionary(n => n, n => Read(failedRoot, n));
        failed.BeforeReplaceForTests = name => { if (name == "procman-background.json") throw new IOException("fixture disk failure"); };
        Check(Refused(failed.CommitDefaults), "第三项配置写入失败会停止重置");
        foreach (var name in beforeCommit.Keys)
            Check(Read(failedRoot, name).SequenceEqual(beforeCommit[name]), "部分配置重置失败时回退恢复后的安全值：" + name);
        Check(!Settings(failedRoot).FrameModeActive && Settings(failedRoot).TempSpoofRestorePending, "写入失败不会重新启用旧功能或丢失待恢复标记");
        failed.BeforeReplaceForTests = null; failed.CommitDefaults();
        Check(Settings(failedRoot).AccentColor == new AppSettings().AccentColor, "原备份保留且配置事务可重试");

        var corruptRoot = Make("corrupt"); File.WriteAllText(Path.Combine(corruptRoot, "settings.json"), "{corrupt");
        var corrupt = new FactoryResetConfiguration(corruptRoot); corrupt.Backup();
        Check(Refused(corrupt.PauseAutomation) && File.ReadAllText(Path.Combine(corruptRoot, "settings.json")) == "{corrupt",
            "损坏设置保留原始文件，不能猜测待恢复选项");
        Check(File.ReadAllText(Path.Combine(corrupt.BackupDirectory, "settings.json")) == "{corrupt", "损坏原始配置也备份为原始字节");
        var missingRoot = Path.Combine(root, "missing"); Directory.CreateDirectory(missingRoot);
        var missing = new FactoryResetConfiguration(missingRoot); missing.Backup(); missing.PauseAutomation(); missing.CommitDefaults();
        Check(new[] { "settings.json", "procman-rules.json", "procman-background.json" }.All(n => File.Exists(Path.Combine(missingRoot, n))),
            "首次运行或缺失配置可写入有效默认文件");
        var collisionRoot = Path.Combine(root, "collision"); Directory.CreateDirectory(collisionRoot);
        Directory.CreateDirectory(Path.Combine(collisionRoot, "settings.json"));
        Check(Refused(new FactoryResetConfiguration(collisionRoot).Backup), "同名目录不能作为设置文件覆盖");
        var raceRoot = Make("race"); var raced = new FactoryResetConfiguration(raceRoot); raced.Backup(); raced.PauseAutomation();
        raced.BeforeReplaceForTests = name => { if (name == "procman-background.json") File.WriteAllText(Path.Combine(raceRoot, name), "external changed"); };
        Check(Refused(raced.CommitDefaults) && File.ReadAllText(Path.Combine(raceRoot, "procman-background.json")) == "external changed",
            "重置中出现外部修改保留外部文件，不盲目覆盖");
        Check(!Settings(raceRoot).FrameModeActive && Settings(raceRoot).AccentColor == "#123456", "外部修改拒绝后其他已写配置回退");
        Check(!Directory.GetFiles(root, ".factory-reset-*.tmp", SearchOption.AllDirectories).Any(), "所有事务临时文件清理完成");
        foreach (var result in new[] { OperationResult.Fail("fixture restore failure"),
            new OperationResult { Success = true, IsPartial = true }, new OperationResult { Success = true, OutcomeUnknown = true } })
        {
            var blockedRoot = Make("restore-blocked-" + Guid.NewGuid().ToString("N"));
            var blockedReset = new FactoryResetConfiguration(blockedRoot); blockedReset.Backup(); blockedReset.PauseAutomation();
            var before = Read(blockedRoot, "procman-rules.json");
            var caught = false;
            try { await blockedReset.CommitAfterRecoveryAsync(() => Task.FromResult(result)); } catch (InvalidOperationException) { caught = true; }
            Check(caught && Read(blockedRoot, "procman-rules.json").SequenceEqual(before)
                && Settings(blockedRoot).AccentColor == "#123456", "恢复失败、部分或未知结果都不能进入默认配置提交");
        }
        var successfulRoot = Make("restore-complete");
        var successfulReset = new FactoryResetConfiguration(successfulRoot); successfulReset.Backup(); successfulReset.PauseAutomation();
        await successfulReset.CommitAfterRecoveryAsync(() => Task.FromResult(OperationResult.Ok("fixture verified")));
        Check(Settings(successfulRoot).AccentColor == new AppSettings().AccentColor, "恢复阶段确认完成后才允许提交默认配置");
        var guarded = Path.Combine(root, "write-guard.json");
        var unrelated = Path.Combine(root, "unrelated-settings.json");
        AppSettingsStore.Update(guarded, s => s.AccentColor = "#123456");
        using (var first = AppSettingsStore.BlockWritesForReset(guarded))
        {
            using (AppSettingsStore.BlockWritesForReset(guarded))
                Check(Refused(() => AppSettingsStore.Update(guarded, s => s.FrameModeActive = true)), "嵌套恢复门禁阻止旧服务回写开启状态");
            Check(Refused(() => AppSettingsStore.Update(guarded, s => s.FrameModeActive = true)), "释放内层门禁不能越过外层门禁");
            Check(!AppSettingsStore.Read(guarded).FrameModeActive, "拒绝旧回调没有改变已停用配置");
            AppSettingsStore.Update(unrelated, s => s.AccentColor = "#abcdef");
            Check(AppSettingsStore.Read(unrelated).AccentColor == "#abcdef", "配置写入门禁仅针对正在重置的文件");
        }
        AppSettingsStore.Update(guarded, s => s.AccentColor = "#654321");
        Check(AppSettingsStore.Read(guarded).AccentColor == "#654321", "恢复失败释放门禁后可以重试或调整配置");
        Console.WriteLine($"Factory reset configuration checks: {count} passed. All files isolated; no actual user settings or system state changed.");
    }
}
