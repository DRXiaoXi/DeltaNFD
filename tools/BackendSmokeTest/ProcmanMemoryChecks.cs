using System.Diagnostics;
using DeltaNFD.Backends;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class ProcmanMemoryChecks
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-memory-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var id = Guid.NewGuid(); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
        var now = DateTime.UtcNow;
        var ordinary = new ProcmanProcessIdentity(10101, now, @"C:\Apps\ordinary.exe");
        var sample = new ProcmanCpuSample(20, [(ordinary, 0.1)]);
        var foreground = 20202;
        var blocked = false; long? size = 600L * 1024 * 1024;
        var writes = new List<int>();
        ProcmanEngine Engine(string name, bool manual = false)
        {
            var log = Path.Combine(root, name + ".json");
            if (manual) new ProcmanChangeLog(log).Add(new(ordinary.Pid, ordinary.StartTimeUtcTicks, ordinary.ExecutablePath,
                "memoryPriority", "5", "2", "manual", DateTimeOffset.UtcNow));
            return new ProcmanEngine(log)
            {
                ClockForTests = () => now, SampleForTests = () => sample, ForegroundForTests = () => foreground,
                BackgroundLoopDisabledForTests = true,
                FrameModeActiveForTests = () => true,
                FrameSuppressionSelectedForTests = () => true,
                FrameMemorySelectedForTests = () => true,
                MemoryPressureWindowForTests = TimeSpan.Zero,
                ResponsivenessExcludedForTests = _ => blocked,
                MemoryReadForTests = _ => size, MemorySampleDelayForTests = TimeSpan.Zero,
                MemoryTrimForTests = (identity, guard) =>
                {
                    if (!guard()) return new(identity.Pid, "fixture", "skipped", "Eligibility changed.");
                    writes.Add(identity.Pid); return new(identity.Pid, "fixture", "completed", "simulated", size, size / 2);
                },
            };
        }
        ProcmanMemoryTrimArgs Confirm(ProcmanMemoryPreview preview) => new()
        { PreviewId = preview.Id, Processes = preview.Candidates.Select(c => c.Identity).ToList() };
        try
        {
            using (var engine = Engine("basic"))
            {
                var preview = await engine.PreviewMemoryAsync();
                Check(preview.Candidates.Count == 1 && writes.Count == 0, "预览只列出候选，不进行任何回收");
                Check(engine.TrimMemory(new() { PreviewId = "forged", Processes = Confirm(preview).Processes }).Items.Count == 0 && writes.Count == 0, "伪造预览令牌拒绝");
                Check(engine.TrimMemory(new() { PreviewId = preview.Id, Processes = [Confirm(preview).Processes[0] with { StartTimeUtcTicks = 1 }] }).Items.Count == 0, "更改进程启动时间不能扩大确认范围");
                var duplicate = Confirm(preview); duplicate.Processes.Add(duplicate.Processes[0]);
                Check(engine.TrimMemory(duplicate).Items.Count == 0, "重复 PID 请求拒绝");
                Check(engine.TrimMemory(new() { PreviewId = preview.Id, Processes = [] }).Items.Count == 0, "空确认不会回收");
                var result = engine.TrimMemory(Confirm(preview));
                Check(result.Items.Single().Status == "completed" && writes.Count == 1, "确认后仅回收授权候选");
                Check(engine.TrimMemory(Confirm(preview)).Items.Count == 0 && writes.Count == 1, "预览单次使用，重复点击不重放");
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 0, "10 分钟内同一身份不会重复回收");
                now = now.AddMinutes(11);
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 1, "冷却结束后才可再次预览");
            }
            using (var engine = Engine("filters"))
            {
                sample = new(95, [(ordinary, 90)]);
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 1, "CPU 高占用后台应用也可回收");
                sample = new(null, [(ordinary, 0.1)]);
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 1, "系统 CPU 读数缺失不会阻止身份和工作集可确认的回收候选");
                sample = new(20, [(ordinary, 0.1)]); size = 10;
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 1, "小工作集也能进入候选，无256MiB门槛");
                size = 0;
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 1, "零工作集读数也允许请求，不按MB筛除");
                size = null;
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 0, "工作集无法读取时跳过");
                size = 600L * 1024 * 1024; foreground = ordinary.Pid;
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 0, "前台应用排除");
                foreground = -1;
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 0, "前台无法确认时不回收");
                foreground = 20202; blocked = true;
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 0, "系统/受保护进程排除");
                blocked = false;
                foreach (var path in new[] { @"D:\Apps\DeltaNFD.exe", @"D:\Apps\DeltaNFD.Backend.Procman.exe",
                    @"D:\Apps\DeltaNFD.OfflineHelper.exe", @"D:\Games\DeltaForceClient-Win64-Shipping.exe",
                    Path.Combine(BackendEnvironment.AppRoot, "plugins", "OtherName.exe") })
                {
                    sample = new(20, [(ordinary with { ExecutablePath = path }, 0.1)]);
                    Check((await engine.PreviewMemoryAsync()).Candidates.Count == 0, "必须保护游戏或工具进程：" + Path.GetFileName(path));
                }
                sample = new(20, [(ordinary, 0.1)]);
            }
            using (var engine = Engine("manual", manual: true))
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 0, "任何属性有手动记录时整个进程排除");
            using (var engine = Engine("rule"))
            {
                new ProcmanRuleStore(Path.Combine(root, "rule.json.rules.json")).Save(new()
                { ExecutablePath = ordinary.ExecutablePath, Capability = "memoryPriority", Value = "2" });
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 0, "其他属性有已启用规则时同样排除");
            }
            using (var engine = Engine("changed"))
            {
                var preview = await engine.PreviewMemoryAsync(); var before = writes.Count;
                foreground = ordinary.Pid;
                Check(engine.TrimMemory(Confirm(preview)).Items.Single().Status == "skipped" && writes.Count == before, "预览后转前台，执行时再次跳过");
                foreground = 20202; preview = await engine.PreviewMemoryAsync();
                sample = new(20, [(ordinary with { StartTimeUtc = ordinary.StartTimeUtc.AddTicks(1) }, 0.1)]);
                Check(engine.TrimMemory(Confirm(preview)).Items.Single().Status == "skipped" && writes.Count == before, "PID 复用不误操作新进程");
                sample = new(20, [(ordinary, 0.1)]); preview = await engine.PreviewMemoryAsync();
                now = now.AddMinutes(3);
                Check(engine.TrimMemory(Confirm(preview)).Items.Count == 0 && writes.Count == before, "超过 2 分钟的确认清单失效");
            }
            using (var engine = Engine("batch"))
            {
                sample = new(20, Enumerable.Range(1, 8).Select(i => (ordinary with { Pid = ordinary.Pid + i }, 0.1)).ToArray());
                var preview = await engine.PreviewMemoryAsync();
                Check(preview.Candidates.Count == 5, "默认每批最多五个候选");
                using var cancel = new CancellationTokenSource(); cancel.Cancel(); var before = writes.Count;
                Check(engine.TrimMemory(Confirm(preview), cancel.Token).Items.Count == 0 && writes.Count == before, "开始前取消不会回收任何进程");
            }
            using (var engine = Engine("configurable-batch"))
            {
                sample = new(20, Enumerable.Range(1, 8).Select(i => (ordinary with { Pid = ordinary.Pid + i }, 0.1)).ToArray());
                Check(engine.BackgroundStatus().Config.MemoryBatchLimit == 5, "新配置默认单次回收五个进程");
                var preview = await engine.PreviewMemoryAsync(); var before = writes.Count;
                Check(engine.SaveBackgroundConfig(new() { MemoryBatchLimit = 2 }).Success, "允许用户降低回收数量");
                Check(engine.TrimMemory(Confirm(preview)).Items.Count == 0 && writes.Count == before, "修改数量使旧预览失效，避免确认清单过期");
                preview = await engine.PreviewMemoryAsync();
                Check(preview.Candidates.Count == 2 && preview.Message.Contains("最多 2 个"), "手动预览采用自定义数量和正确说明");
                Check(engine.TrimMemory(Confirm(preview)).Items.Count == 2 && writes.Count == before + 2, "手动执行遵守自定义两进程上限");
                now = now.AddMinutes(11);
                engine.SaveBackgroundConfig(new() { MemoryBatchLimit = 7 });
                preview = await engine.PreviewMemoryAsync();
                Check(preview.Candidates.Count == 7, "允许提升回收数量，不再被旧三进程常量限制");
                Check(engine.TrimMemory(Confirm(preview)).Items.Count == 7, "手动确认可实际处理七个模拟候选");
                now = now.AddMinutes(11);
                engine.MemoryPressureForTests = () => 95;
                engine.SaveBackgroundConfig(new() { MemoryBatchLimit = 4 }); before = writes.Count;
                engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before + 4, "自动回收也采用自定义四进程上限");
                foreach (var invalid in new[] { 0, -1, 257, int.MaxValue })
                {
                    try { engine.SaveBackgroundConfig(new() { MemoryBatchLimit = invalid }); Check(false, "非法数量被接受"); }
                    catch (ArgumentException) { Check(engine.BackgroundStatus().Config.MemoryBatchLimit == 4, "非法数量保留原配置：" + invalid); }
                }
                engine.SaveBackgroundConfig(new() { MemoryBatchLimit = 256 }); now = now.AddMinutes(11);
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 8, "候选不足时不补充受保护进程凑数");
            }
            using (var reload = Engine("configurable-batch"))
                Check(reload.BackgroundStatus().Config.MemoryBatchLimit == 256, "用户数量跨引擎重建持久保存");
            File.WriteAllText(Path.Combine(root, "old-batch.json.background.json"), "{\"SchemaVersion\":1,\"MemoryPressurePercent\":89}");
            using (var old = Engine("old-batch"))
                Check(old.BackgroundStatus().Config is { MemoryBatchLimit: 5, MemoryPressurePercent: 89 }, "旧配置缺少新字段时使用五进程默认且保留其他设置");
            sample = new(95, [(ordinary, 90)]);
            using (var engine = Engine("cooldown-manual"))
            {
                Check(engine.BackgroundStatus().Config.MemoryCooldownSeconds == 600, "默认清理间隔仍为十分钟");
                engine.SaveBackgroundConfig(new() { MemoryCooldownSeconds = 60 });
                Check(engine.BackgroundStatus().EffectiveMemoryCooldownSeconds == 60, "无紧急策略时允许一分钟间隔，不再被旧十分钟常量覆盖");
                engine.TrimMemory(Confirm(await engine.PreviewMemoryAsync()));
                now = now.AddSeconds(59);
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 0, "同进程在自定义间隔结束前不重复回收");
                now = now.AddSeconds(1);
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 1, "同进程满一分钟后可重新预览");
                engine.SaveBackgroundConfig(new() { MemoryCooldownSeconds = 3600 }); now = now.AddMinutes(10);
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 0, "自定义一小时间隔可延长同进程冷却");
                now = now.AddHours(1);
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 1, "长间隔结束后允许重新评估");
                foreach (var invalid in new[] { -1, 0, 59, 86401 })
                {
                    try { engine.SaveBackgroundConfig(new() { MemoryCooldownSeconds = invalid }); Check(false, "invalid cooldown accepted"); }
                    catch (ArgumentException) { Check(engine.BackgroundStatus().Config.MemoryCooldownSeconds == 3600, "非法间隔保留原配置：" + invalid); }
                }
            }
            using (var reload = Engine("cooldown-manual"))
                Check(reload.BackgroundStatus().Config.MemoryCooldownSeconds == 3600, "清理间隔重启后仍持久保存");
            using (var old = Engine("old-batch"))
                Check(old.BackgroundStatus().Config.MemoryCooldownSeconds == 600, "旧配置缺少清理间隔字段时采用十分钟默认");
            using (var engine = Engine("cooldown-automatic"))
            {
                engine.MemoryPressureForTests = () => 95;
                engine.SaveBackgroundConfig(new() { MemoryCooldownSeconds = 60 }); var before = writes.Count;
                engine.AutomaticMemoryOnceForTests();
                now = now.AddSeconds(30); engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before + 1, "自动批次自定义间隔内不重复");
                now = now.AddSeconds(30); engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before + 2, "自动批次满一分钟后可再次执行");
                engine.SaveBackgroundConfig(new() { MemoryCooldownSeconds = 3600 }); now = now.AddMinutes(10);
                engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before + 2, "自动批次也采用一小时间隔");
                now = now.AddMinutes(50); engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before + 3, "长自动批次间隔结束后才执行");
                engine.MemoryPolicyForTests = () => new([new EmergencyPolicy { MemoryCleanCooldownSeconds = 1200 }]);
                engine.SaveBackgroundConfig(new() { MemoryCooldownSeconds = 60 });
                Check(engine.BackgroundStatus().EffectiveMemoryCooldownSeconds == 1200, "紧急策略延长实际间隔并回显，不能被一分钟设置绕过");
            }
            using (var engine = Engine("automatic"))
            {
                uint? pressure = 95;
                engine.MemoryPressureForTests = () => pressure;
                var before = writes.Count;
                engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before && !engine.BackgroundStatus().Config.AutomaticMemoryEnabled, "自动回收默认关闭");
                engine.SaveBackgroundConfig(new() { AutomaticMemoryEnabled = true });
                pressure = 84; engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before, "内存压力低于 85% 时不回收");
                pressure = 95; now = now.AddSeconds(10); engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before, "检查间隔内不重复触发");
                var preview = await engine.PreviewMemoryAsync();
                now = now.AddSeconds(21); engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before + 1, "达到内存压力阈值后自动回收高 CPU 应用");
                Check(engine.TrimMemory(Confirm(preview)).Items.Single().Status == "skipped", "自动批次保留手动预览令牌，并共享单进程冷却");
                now = now.AddMinutes(1); engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before + 1, "持续高内存压力不导致反复回收");
                now = now.AddMinutes(10); pressure = null; engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before + 1, "内存压力查询失败不回收");
                pressure = 95; now = now.AddSeconds(31);
                foreground = ordinary.Pid; engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before + 1, "自动回收沿用前台保护"); foreground = 20202;
                now = now.AddSeconds(31); engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before + 2, "冷却结束后恢复自动回收");
                engine.FrameModeActiveForTests = () => false;
                engine.SaveBackgroundConfig(new() { AutomaticMemoryEnabled = false });
                now = now.AddMinutes(11); engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before + 2, "关闭自动模式后不再回收");
                engine.FrameModeActiveForTests = () => true;
                engine.SaveBackgroundConfig(new() { AutomaticMemoryEnabled = true });
                await engine.RestoreAllAsync(); now = now.AddMinutes(11); engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before + 2 && !engine.BackgroundStatus().AutomaticMemoryActive
                    && engine.BackgroundStatus().Config.AutomaticMemoryEnabled, "退出还原停止自动任务且保留用户偏好");
            }
            using (var engine = Engine("shared-exclusion"))
            {
                engine.MemoryPressureForTests = () => 95;
                var preview = await engine.PreviewMemoryAsync(); var before = writes.Count;
                engine.SaveBackgroundConfig(new() { AutomaticMemoryEnabled = true, ExcludedExecutablePaths = [ordinary.ExecutablePath] });
                Check(engine.TrimMemory(Confirm(preview)).Items.Single().Status == "skipped" && writes.Count == before, "预览后加入共用排除项立即阻止手动回收");
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 0, "共用排除项从手动预览移除");
                engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before, "共用排除项阻止自动回收");
                engine.ConfigureResponsiveness(new() { Enabled = true, CpuBusySeconds = 1 });
                await engine.ResponsivenessOnceForTests(); now = now.AddSeconds(2); await engine.ResponsivenessOnceForTests();
                Check(engine.ResponsivenessEvents().Count == 0 && engine.Snapshot().LastError == "", "共用排除项阻止后台抑制，写入前即跳过");
                engine.SaveBackgroundConfig(new() { ExcludedExecutablePaths = [] });
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 1, "移除排除项后再次符合候选条件");
            }
            using (var engine = Engine("persist"))
                engine.SaveBackgroundConfig(new() { AutomaticMemoryEnabled = true, MemoryPressurePercent = 90,
                    ExcludedExecutablePaths = [ordinary.ExecutablePath, ordinary.ExecutablePath.ToUpperInvariant()] });
            using (var engine = Engine("persist"))
            {
                var saved = engine.BackgroundStatus();
                Check(saved.Config.AutomaticMemoryEnabled && saved.Config.MemoryPressurePercent == 90 && saved.Config.ExcludedExecutablePaths.Count == 1,
                    "自动配置和去重后的完整路径排除名单持久化");
                Check(!saved.AutomaticMemoryActive, "只读创建引擎不会擅自启动持久自动任务");
                engine.ActivateBackground();
                Check(engine.BackgroundStatus().AutomaticMemoryActive, "显式启动恢复保存的自动偏好");
                saved.Config.ExcludedExecutablePaths.Clear();
                Check(engine.BackgroundStatus().Config.ExcludedExecutablePaths.Count == 1, "状态合同列表副本不能绕过保存修改排除项");
                try { engine.SaveBackgroundConfig(new() { MemoryPressurePercent = 101 }); Check(false, "invalid threshold accepted"); }
                catch (ArgumentException) { Check(engine.BackgroundStatus().Config.MemoryPressurePercent == 90, "无效阈值保留原配置"); }
                try { engine.SaveBackgroundConfig(new() { ExcludedExecutablePaths = ["*.exe"] }); Check(false, "wildcard accepted"); }
                catch (ArgumentException) { Check(engine.BackgroundStatus().Config.ExcludedExecutablePaths.Count == 1, "拒绝名称通配和非完整路径"); }
            }
            File.WriteAllText(Path.Combine(root, "corrupt.json.background.json"), "{corrupt");
            using (var bad = Engine("corrupt"))
            {
                Check(bad.BackgroundStatus().Note.Length > 0 && (await bad.PreviewMemoryAsync()).Candidates.Count == 0,
                    "配置损坏时暂停操作，避免遗失排除范围");
                Check((await bad.RestoreAllAsync()).Success, "配置损坏不阻塞还原和退出");
                try { bad.SaveBackgroundConfig(new()); Check(false, "corrupt overwrite accepted"); }
                catch (InvalidDataException) { Check(File.ReadAllText(Path.Combine(root, "corrupt.json.background.json")) == "{corrupt", "配置损坏文件保留，拒绝覆盖"); }
            }
            using (var engine = Engine("pressure-race"))
            {
                uint pressure = 95; var before = writes.Count;
                engine.MemoryPressureForTests = () => pressure;
                engine.MemoryTrimForTests = (identity, guard) =>
                {
                    pressure = 20;
                    Check(!guard(), "执行前内存压力回落，最后复核阻止回收");
                    return new(identity.Pid, "fixture", "skipped", "pressure changed");
                };
                engine.SaveBackgroundConfig(new() { AutomaticMemoryEnabled = true }); engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before, "压力变化不会发生真实写入");
            }
            using (var engine = Engine("emergency"))
            {
                var policy = new EmergencyPolicy { MemoryCleanMode = "manual-only" };
                engine.MemoryPolicyForTests = () => new([policy]); engine.MemoryPressureForTests = () => 95;
                engine.SaveBackgroundConfig(new() { AutomaticMemoryEnabled = true }); var before = writes.Count;
                engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before && !engine.BackgroundStatus().AutomaticMemoryActive, "紧急策略 manual-only 暂停自动回收并显示暂停状态");
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 1, "manual-only 仍允许手动预览");
                policy = new() { BlockedCommands = [BackendCommands.CpuFrameMemoryClean] };
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 0, "紧急停用指令同时阻止进程工作集回收");
                now = now.AddSeconds(31); engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before, "紧急停用指令阻止自动操作");
                policy = new() { MemoryCleanThresholdPercent = 95, MemoryCleanCooldownSeconds = 1200 };
                engine.MemoryPressureForTests = () => 90; now = now.AddSeconds(31); engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before, "紧急策略提高的内存阈值优先于用户配置");
                engine.MemoryPressureForTests = () => 95; now = now.AddSeconds(31); engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before + 1, "满足紧急策略阈值后才执行");
                now = now.AddMinutes(11); engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before + 1 && (await engine.PreviewMemoryAsync()).Candidates.Count == 0, "紧急策略延长的冷却同时用于手动与自动回收");
            }
            using (var engine = Engine("offline"))
            {
                engine.MemoryPressureForTests = () => 95; engine.SaveBackgroundConfig(new() { AutomaticMemoryEnabled = true }); var before = writes.Count;
                File.WriteAllText(OfflineModeStateStore.Default.PathName, "{invalid");
                engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before && !engine.BackgroundStatus().AutomaticMemoryActive, "脱机状态无法确认时自动模式停止");
                Check(!engine.SaveBackgroundConfig(new() { AutomaticMemoryEnabled = true }).Success, "脱机保护中拒绝重新启用自动模式");
                File.Delete(OfflineModeStateStore.Default.PathName);
            }
            using (var engine = Engine("sustained-pressure"))
            {
                engine.MemoryPressureWindowForTests = TimeSpan.FromSeconds(30);
                uint pressure = 95; engine.MemoryPressureForTests = () => pressure;
                engine.SaveBackgroundConfig(new() { AutomaticMemoryEnabled = true }); var before = writes.Count;
                engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before, "瞬间内存峰值不会立即回收");
                now = now.AddSeconds(31); pressure = 20; engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before, "压力回落取消持续高压窗口");
                now = now.AddSeconds(31); pressure = 95; engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before, "压力再升高重新计算窗口");
                now = now.AddSeconds(31); engine.AutomaticMemoryOnceForTests();
                Check(writes.Count == before + 1, "连续两次高压采样后执行回收");
            }
            using (var engine = Engine("rebound"))
            {
                engine.MemoryPressureWindowForTests = TimeSpan.FromSeconds(30);
                engine.MemoryPressureForTests = () => 95;
                engine.SaveBackgroundConfig(new() { AutomaticMemoryEnabled = true }); engine.AutomaticMemoryOnceForTests();
                now = now.AddSeconds(31); engine.AutomaticMemoryOnceForTests();
                now = now.AddSeconds(31); engine.AutomaticMemoryOnceForTests();
                Check(engine.BackgroundStatus().Feedback.Contains("不因无明显效果或快速回涨额外延长"), "回涨不再追加长时间冷却");
                now = now.AddMinutes(11);
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 1, "回涨身份满用户十分钟间隔即可重新回收");
                now = now.AddMinutes(20);
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 1, "不保留旧三十分钟回涨退避");
            }
            using (var engine = Engine("ineffective"))
            {
                engine.MemoryPressureForTests = () => 95;
                var attempts = 0;
                engine.MemoryTrimForTests = (target, guard) =>
                { if (guard()) attempts++; return new(target.Pid, "fixture", "completed", "no change", size, size); };
                engine.SaveBackgroundConfig(new() { AutomaticMemoryEnabled = true }); engine.AutomaticMemoryOnceForTests();
                now = now.AddMinutes(11); engine.AutomaticMemoryOnceForTests();
                Check(attempts == 2, "无明显效果时满用户间隔即可再次尝试，不追加二十分钟退避");
                now = now.AddMinutes(10); engine.AutomaticMemoryOnceForTests();
                Check(attempts == 3, "无效果多轮仍只遵循用户设置的间隔");
            }
            using (var engine = Engine("late-observation"))
            {
                engine.MemoryPressureForTests = () => 95;
                engine.SaveBackgroundConfig(new() { AutomaticMemoryEnabled = true }); engine.AutomaticMemoryOnceForTests();
                now = now.AddMinutes(5); engine.AutomaticMemoryOnceForTests();
                now = now.AddMinutes(6);
                Check((await engine.PreviewMemoryAsync()).Candidates.Count == 1, "睡眠或长时间暂停后的读数不误判为快速回涨");
            }
            using (var engine = Engine("telemetry"))
            {
                var telemetryReads = 0;
                engine.MemoryTelemetryForTests = () => new(8UL << 30, (++telemetryReads == 1 ? 1UL : 2UL) << 30, 85, 10UL << 30, 16UL << 30);
                var report = engine.TrimMemory(Confirm(await engine.PreviewMemoryAsync()));
                Check(report.Before?.AvailablePhysicalBytes == 1UL << 30 && report.After?.AvailablePhysicalBytes == 2UL << 30
                    && report.After.CommitBytes == 10UL << 30, "前后可用物理内存和提交量独立报告，不按工作集求和");
            }
            var nativeTelemetry = ProcmanMemoryTelemetryReader.Read();
            Check(nativeTelemetry is { TotalPhysicalBytes: > 0 } && nativeTelemetry.AvailablePhysicalBytes <= nativeTelemetry.TotalPhysicalBytes
                && nativeTelemetry.CommitLimitBytes > 0, "真实只读 Win32 内存遥测结构布局和页面数换算有效");
            Check(!DeltaNFD.Native.MemoryPurge.PurgeStandbyList().Success, "旧全机清理入口已拒绝执行");
            using (var oldFrame = new DeltaNFD.Execution.CpuFrame.CpuFrameEngine())
                Check(!oldFrame.CleanMemoryNow().Success && oldFrame.CleanMemoryNow().Message.Contains("已下线"), "旧帧格兼容命令拒绝执行，不触发新任务");
            Check(BackendModules.IsReadOnly(BackendModules.Procman, BackendCommands.ProcmanBackgroundGet)
                && !BackendModules.IsReadOnly(BackendModules.Procman, BackendCommands.ProcmanBackgroundSave), "后台配置查询只读、保存为修改操作");
            Check(BackendModules.IsReadOnly(BackendModules.Procman, BackendCommands.ProcmanMemoryPreview)
                && !BackendModules.IsReadOnly(BackendModules.Procman, BackendCommands.ProcmanMemoryTrim), "预览只读与执行命令明确区分");
            var review = new BackendUnconfirmedStore.Review(BackendModules.Procman, "fixture", "fixture", true) { Command = BackendCommands.ProcmanMemoryTrim };
            Check(BackendRecovery.RecoveryChoices(review, new ReconcileSnapshot { Module = BackendModules.Procman, Summary = "fixture", Complete = true,
                RecoveryScopes = [new() { Id = "all", Description = "fixture", Available = true }] }).Count == 0, "工作集回收未知结果不伪称可从属性备份撤销");
            using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--procman-test-child") { UseShellExecute = false, CreateNoWindow = true })!;
            try
            {
                var identity = ProcmanIdentity.TryIdentify(child.Id)!;
                Check(ProcmanMemoryTrimmer.Trim(identity, () => false).Status == "skipped", "真实写入前最后一次保护复核仍可阻止");
                var trimmed = ProcmanMemoryTrimmer.Trim(identity, () => true);
                Check(trimmed.Status == "completed" && trimmed.BeforeBytes > 0 && trimmed.AfterBytes >= 0 && !child.HasExited,
                    "仅自有睡眠夹具调用公开 API 并回读，不结束进程，不假设必然减少内存");
                Check(ProcmanMemoryTrimmer.Trim(identity with { StartTimeUtc = identity.StartTimeUtc.AddTicks(1) }, () => true).Status == "skipped",
                    "固定句柄复核拒绝错误启动身份");
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
            Console.WriteLine($"Procman memory checks: {count} passed. Only one dedicated child working set trimmed; other effects simulated.");
        }
        finally
        {
            var target = Path.GetFullPath(root);
            if (target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(target).StartsWith("DeltaNFD-memory-checks-", StringComparison.Ordinal)) Directory.Delete(target, true);
        }
    }
}
