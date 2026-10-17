using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

internal static class ProcmanRepairChecks
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-procman-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var testId = Guid.NewGuid(); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), testId.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, testId);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", testId.ToString("D"));
        var fixture = Path.Combine(root, "unique-exe"); Directory.CreateDirectory(fixture);
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(typeof(ProcmanRepairChecks).Assembly.Location)!))
            File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)));
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(fixture, "BackendSmokeTest.exe"), "--procman-test-child") { UseShellExecute = false, CreateNoWindow = true })!;
        // Hosted CI can inherit AboveNormal/BelowNormal from its runner. Give the dedicated fixture
        // a declared baseline; restoration assertions must not assume the host's priority is Normal.
        child.PriorityClass = ProcessPriorityClass.Normal;
        var count = 0;
        bool completed = false;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
        var identity = ProcmanIdentity.TryIdentify(child.Id)!;
        var request = new ProcessIdentityArgs { Pid = identity.Pid, StartTimeUtcTicks = identity.StartTimeUtcTicks, ExecutablePath = identity.ExecutablePath };
        string Priority() => ProcmanCapabilities.ReadPriority(child.Id).Value!;
        ProcmanEngine Engine(string name) => new(Path.Combine(root, name + ".json"))
        {
            ForegroundForTests = () => Environment.ProcessId,
            FrameModeActiveForTests = () => true,
            FrameSuppressionSelectedForTests = () => true,
            FrameMemorySelectedForTests = () => true,
            // Hosted Windows CI runs in service session 0. Model only this unique fixture as a user app;
            // the native system classifier is verified separately below and never bypassed in production.
            ResponsivenessExcludedForTests = candidate => candidate != identity,
        };
        try
        {
            const string userSid = "S-1-5-21-100-200-300-1001";
            const string app = @"C:\Apps\ordinary.exe";
            var projectedIdentity = new ProcessIdentityArgs { Pid = identity.Pid, StartTimeUtcTicks = identity.StartTimeUtcTicks, ExecutablePath = identity.ExecutablePath };
            IReadOnlyList<ProcmanRuleEntry> Project(ProcmanSetArgs set, bool takeover = false)
            { set.Identity = projectedIdentity; return ProcmanRuleProjection.Create(set, takeover); }
            var priorityRule = Project(new() { CpuPriority = "AboveNormal" }).Single();
            Check(priorityRule.Capability == "priority" && priorityRule.Value == "AboveNormal" && priorityRule.Enabled
                && priorityRule.ExecutablePath == identity.ExecutablePath && !priorityRule.AutoTakeover, "总是模式保存完整路径及当前属性，不自动授权接管");
            Check(Project(new() { CpuPriority = "Normal" }, true).Single().AutoTakeover, "总是规则仅按显式勾选保存游戏控制接管权限");
            var toggles = Project(new() { EcoQosEnabled = false, PriorityBoost = true });
            Check(toggles.Count == 2 && toggles.Single(r => r.Capability == "ecoQos").Value == "false"
                && toggles.Single(r => r.Capability == "priorityBoost").Value == "True", "总是开关使用后端要求的布尔格式，未提供属性不保存");
            Check(Project(new() { AffinityMasks = [new(1, 5), new(0, 3)] }).Single().Value == "g0:0x3;g1:0x5", "总是 CPU 规则保存多组掩码，排序且不丢组");
            Check(Project(new() { CpuSetMasks = [] }).Single().Value.Length == 0, "总是清除 CPU 集合保存为明确的解除规则");
            Check(Project(new() { MemoryPriority = 5 }).Single().Value == "5", "文字内存档位映射为原数值规则");
            bool gpuProjectionRejected = false;
            try { Project(new() { GpuPreference = "高性能" }); } catch (InvalidDataException) { gpuProjectionRejected = true; }
            Check(gpuProjectionRejected, "Windows 持久 GPU 首选项不冒充当前或总是运行时规则");
            bool emptyProjectionRejected = false;
            try { Project(new()); } catch (InvalidDataException) { emptyProjectionRejected = true; }
            Check(emptyProjectionRejected, "没有有效属性时不创建空总是规则");
            bool Excluded(string path = app, int session = 1, string sid = userSid, bool critical = false) =>
                ProcmanResponsivenessFilter.ExcludedByIdentity(path, session, sid, critical, @"C:\Windows");
            Check(!Excluded(), "普通用户应用参与响应性保护");
            Check(!Excluded(@"C:\Apps\AnotherGame.exe"), "其他游戏不按名称整体排除");
            Check(Excluded(@"C:\Apps\DeltaNFD-extra.exe"), "三角帧工具命名前缀统一排除");
            Check(Excluded(@"D:\Delta NFD\DeltaNFD.exe") && Excluded(@"D:\Delta NFD\Backends\DeltaNFD.Backend.Runtime.exe")
                && Excluded(@"D:\Delta NFD\Backends\DeltaNFD.OfflineHelper.exe"), "工具主程序、官方后端、脱机助手全部排除");
            Check(Excluded(@"D:\Games\DeltaForceClient-Win64-Shipping.exe"), "三角洲主进程在任何目录均排除");
            Check(Excluded(@"D:\Games\DELTAFORCECLIENT-WIN64-SHIPPING.EXE"), "游戏主进程名称判断忽略大小写");
            Check(Excluded(@"C:\Windows\System32\svchost.exe"), "系统目录中的所有进程排除");
            Check(Excluded(@"C:\Windows\SystemApps\app.exe"), "系统应用目录也排除");
            Check(!Excluded(@"C:\WindowsGames\game.exe"), "目录边界不误排除 Windows 同前缀应用");
            Check(Excluded(sid: "S-1-5-18"), "Windows 目录外 LocalSystem 进程也排除");
            Check(Excluded(sid: "S-1-5-19"), "LocalService 进程排除");
            Check(Excluded(sid: "S-1-5-20"), "NetworkService 进程排除");
            Check(Excluded(sid: "S-1-5-80-1-2-3-4-5"), "虚拟服务账户进程排除");
            Check(Excluded(session: 0), "使用普通账户运行的服务会话进程排除");
            Check(Excluded(critical: true), "Windows 目录外的关键系统进程排除");
            Check(Excluded(sid: "") && Excluded(path: "") && Excluded(session: -1), "身份不完整不能误当成普通应用");
            using (var owner = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var expectedExcluded = ProcmanResponsivenessFilter.ExcludedByIdentity(identity.ExecutablePath, child.SessionId,
                    owner.User?.Value ?? "", false, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                Check(ProcmanResponsivenessFilter.IsExcluded(identity) == expectedExcluded,
                    "真实夹具账户与会话核对符合分类；CI 服务会话也保持排除");
            }
            using (var engine = Engine("manual"))
            {
                Check(!identity.ExecutablePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase), "唯一夹具 EXE，规则不会匹配共享 dotnet.exe");
                Check(engine.Set(new() { Identity = request, CpuPriority = "BelowNormal" }).Outcomes.Single().Status == "applied", "CPU 优先级设置并回读");
                Check(engine.Detail(new() { Identity = request }).Capabilities.Single(c => c.Capability == "priority").Owner == "manual", "详情显示真实控制方");
                Check(engine.Reset(new() { Identity = request }).Outcomes.Single().Status == "restored" && Priority() == "Normal", "CPU 优先级恢复");
                Check(!engine.Set(new() { Identity = request with { StartTimeUtcTicks = request.StartTimeUtcTicks + 1 }, CpuPriority = "High" }).IdentityOk, "旧身份不能修改进程");
                Check(engine.Set(new() { Identity = request, CpuPriority = "RealTime" }).Outcomes.Single().Status == "rejected", "实时 CPU 档位拒绝");
                var boostOriginal = ProcmanCapabilities.ReadPriorityBoost(child.Id).Value;
                Check(engine.Set(new() { Identity = request, PriorityBoost = boostOriginal != "True" }).Outcomes.Single().Status == "applied", "动态提升读写回读");
                Check(engine.Reset(new() { Identity = request }).Outcomes.Single().Status == "restored" && ProcmanCapabilities.ReadPriorityBoost(child.Id).Value == boostOriginal, "动态提升精确恢复");
                var eco = ProcmanCapabilities.ReadEcoQos(child.Id);
                Check(eco.Ok, "EcoQoS 公开读取原始控制/状态掩码");
                Check(engine.Set(new() { Identity = request, EcoQosEnabled = true }).Outcomes.Single().Status == "applied", "EcoQoS 设置并回读");
                Check(engine.Reset(new() { Identity = request }).Outcomes.Single().Status == "restored" && ProcmanCapabilities.ReadEcoQos(child.Id).Value == eco.Value, "EcoQoS 精确恢复原始状态");
                Check(engine.Set(new() { Identity = request, IoPriority = "Low" }).Outcomes.Single().Status == "applied", "I/O 优先级写入回读");
                Check(engine.Reset(new() { Identity = request }).Outcomes.Single().Status == "restored", "I/O 优先级恢复");
                var originalAffinity = ProcmanCapabilities.ReadAffinity(child.Id);
                if (originalAffinity.Ok)
                {
                    var parser = typeof(ProcmanEngine).GetMethod("ParseGroupMasks", BindingFlags.NonPublic | BindingFlags.Static)!;
                    var originalMasks = (List<ProcmanGroupMaskValue>)parser.Invoke(null, [originalAffinity.Value!])!;
                    var first = originalMasks[0]; var bit = first.Mask & (~first.Mask + 1);
                    Check(engine.Set(new() { Identity = request, AffinityMasks = [new(first.Group, bit)] }).Outcomes.Single().Status == "applied", "单组硬亲和性使用有效公开 API");
                    Check(engine.Reset(new() { Identity = request }).Outcomes.Single().Status == "restored" && ProcmanCapabilities.ReadAffinity(child.Id).Value == originalAffinity.Value, "硬亲和性精确恢复");
                    Check(engine.Set(new() { Identity = request, CpuSetMasks = [new(first.Group, bit)] }).Outcomes.Single().Status == "applied", "CPU 集合写入并读取已分配集合");
                    Check(engine.Reset(new() { Identity = request }).Outcomes.Single().Status == "restored", "CPU 集合恢复");
                }
                var multiple = (List<ProcmanGroupMaskValue>)typeof(ProcmanEngine).GetMethod("ParseGroupMasks", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, ["g0:0x3;g1:0x5"])!;
                Check(multiple.Count == 2, "多组掩码解析保留所有组");
                Check(engine.Set(new() { Identity = request, MemoryPriority = 2 }).Outcomes.Single().Status == "applied", "内存优先级公开 API 写入回读");
                Check(engine.Reset(new() { Identity = request }).Outcomes.Single().Status == "restored", "内存优先级恢复");
                var gpu = ProcmanCapabilities.ReadGpuPriority(child.Id);
                if (gpu.Ok)
                {
                    var result = engine.Set(new() { Identity = request, GpuPriority = int.Parse(gpu.Value!) });
                    Check(result.Outcomes.Single().Status is "applied" or "failed", "GPU 调度以 Native 实际结果报告支持/权限");
                    await engine.RestoreAllAsync();
                }
                Check(engine.Set(new() { Identity = request, GpuPriority = 5 }).Outcomes.Single().Status == "rejected", "GPU 实时档位拒绝");
            }
            using (var engine = Engine("rules"))
            {
                ProcmanRuleEntry Rule(string value, bool enabled = true, string capability = "priority") => new() { ExecutablePath = identity.ExecutablePath, Capability = capability, Value = value, Enabled = enabled };
                Check(engine.SaveRule(Rule("BelowNormal")).Success, "保存隔离优先级规则");
                await engine.ApplyRulesOnceAsync();
                Check(Priority() == "BelowNormal", "规则作用于唯一夹具");
                Check(engine.SaveRule(Rule("AboveNormal")).Success, "编辑规则先恢复旧值");
                await engine.ApplyRulesOnceAsync(); Check(Priority() == "AboveNormal", "编辑规则应用新值");
                Check(engine.SaveRule(Rule("AboveNormal", false)).Success && Priority() == "Normal", "停用规则即恢复");
                engine.SaveRule(Rule("BelowNormal")); await engine.ApplyRulesOnceAsync();
                Check(engine.Set(new() { Identity = request, CpuPriority = "High" }).Outcomes.Single().Status == "rejected", "无确认不接管已有规则");
                Check(engine.Set(new() { Identity = request, CpuPriority = "High", ConfirmedTakeover = true }).Outcomes.Single().Status == "applied", "手动确认接管规则");
                Check(engine.Release(new() { Identity = request }).Outcomes.Single().Status == "restored" && Priority() == "BelowNormal", "释放手动控制恢复并归还原规则");
                var restored = await engine.RestoreAllAsync();
                await engine.ApplyRulesOnceAsync();
                Check(restored.Success && !engine.Snapshot().RulesEngineRunning && Priority() == "Normal", "全部还原先停循环，后续 tick 不重新写回");
                var originalBoost = ProcmanCapabilities.ReadPriorityBoost(child.Id).Value;
                engine.SaveRule(Rule(originalBoost == "True" ? "False" : "True", true, "priorityBoost"));
                await engine.ApplyRulesOnceAsync();
                Check(engine.Snapshot().HeldChangeCount > 0, "动态提升规则写前记录存在");
                Check((await engine.DeleteRuleAsync(identity.ExecutablePath, "priorityBoost")).Success && ProcmanCapabilities.ReadPriorityBoost(child.Id).Value == originalBoost, "删除动态提升规则精确恢复");
                await engine.RestoreAllAsync();
            }
            using (var game = ProcessPropertyGate.TryEnterGame(child.Id, "priority")) Check(game is not null, "游戏写入方登记同一跨进程门禁");
            using (var engine = Engine("game-takeover"))
            {
                Check(engine.Set(new() { Identity = request, CpuPriority = "BelowNormal" }).Outcomes.Single().Status == "rejected", "已有游戏控制须显式接管");
                Check(engine.Set(new() { Identity = request, CpuPriority = "BelowNormal", ConfirmedTakeover = true }).Outcomes.Single().Status == "applied", "确认接管游戏控制");
                using (var denied = ProcessPropertyGate.TryEnterGame(child.Id, "priority")) Check(denied is null, "原写入方暂停冲突属性");
                engine.Reset(new() { Identity = request });
                using (var returned = ProcessPropertyGate.TryEnterGame(child.Id, "priority", true)) Check(returned is not null, "释放后交回游戏控制");
            }
            using (var engine = Engine("resp"))
            {
                var now = DateTime.UtcNow;
                engine.ClockForTests = () => now;
                double total = 90;
                engine.SampleForTests = () => new(total, [(identity, 20)]);
                Check(engine.ConfigureResponsiveness(new() { Enabled = true, Exclusions = [identity.ExecutablePath] }).Success && engine.Snapshot().RulesEngineRunning, "单独开启保护即运行循环；旧额外排除列表不再生效");
                await engine.ResponsivenessOnceForTests(); now = now.AddSeconds(7); await engine.ResponsivenessOnceForTests();
                Check(Priority() == "BelowNormal", "持续高负载调整合适的后台应用，旧额外路径排除不生效");
                total = 20; await engine.ResponsivenessOnceForTests(); now = now.AddSeconds(9); await engine.ResponsivenessOnceForTests();
                Check(Priority() == "Normal", "持续低负载恢复");
                Check(engine.ConfigureResponsiveness(new() { Enabled = false }).Success && !engine.ResponsivenessEnabled, "停用保护等待旧循环并恢复");
            }
            using (var engine = Engine("resp-other-property"))
            {
                var now = DateTime.UtcNow;
                engine.ClockForTests = () => now;
                engine.SampleForTests = () => new(90, [(identity, 20)]);
                Check(engine.Set(new() { Identity = request, MemoryPriority = 2 }).Outcomes.Single().Status == "applied", "夹具设置其他属性用于检查整体排除");
                engine.ConfigureResponsiveness(new() { Enabled = true });
                await engine.ResponsivenessOnceForTests(); now = now.AddSeconds(7); await engine.ResponsivenessOnceForTests();
                Check(Priority() == "Normal" && !engine.ResponsivenessEvents().Any(e => e.Action == "demote"), "已有内存手动设置时整进程排除");
                engine.ConfigureResponsiveness(new() { Enabled = false });
                Check(Priority() == "Normal" && ProcmanCapabilities.ReadMemoryPriority(child.Id).Value == "2", "关闭保护只恢复 CPU 调整，保留内存手动设置");
                await engine.RestoreAllAsync();
            }
            using (var engine = Engine("resp-foreground"))
            {
                var now = DateTime.UtcNow;
                engine.ClockForTests = () => now;
                engine.ForegroundForTests = () => child.Id;
                engine.SampleForTests = () => new(90, [(identity, 20)]);
                engine.ConfigureResponsiveness(new() { Enabled = true });
                await engine.ResponsivenessOnceForTests(); now = now.AddSeconds(7); await engine.ResponsivenessOnceForTests();
                Check(Priority() == "Normal" && engine.ResponsivenessEvents().Count == 0, "前台应用持续高占用也不降级");
                engine.ForegroundForTests = () => -1;
                now = now.AddSeconds(7); await engine.ResponsivenessOnceForTests();
                Check(Priority() == "Normal", "前台身份查询失败时不新增调整");
                engine.ForegroundForTests = () => Environment.ProcessId;
                now = now.AddSeconds(7); await engine.ResponsivenessOnceForTests();
                Check(Priority() == "BelowNormal", "普通应用离开前台后才参与响应性调整");
                engine.FrameModeActiveForTests = () => false;
                await engine.ResponsivenessOnceForTests();
                Check(Priority() == "Normal" && !engine.Snapshot().ResponsivenessEnabled,
                    "帧格关闭即还原自身后台抑制，不等待新的独立开关操作");
                await engine.RestoreAllAsync();
            }
            using (var engine = Engine("resp-tool-exclusion"))
            {
                var now = DateTime.UtcNow; var classified = 0;
                engine.ClockForTests = () => now;
                engine.ResponsivenessExcludedForTests = _ => { classified++; return false; };
                engine.ConfigureResponsiveness(new() { Enabled = true });
                foreach (var path in new[] { @"D:\Delta NFD\DeltaNFD.exe", @"D:\Delta NFD\Backends\DeltaNFD.Backend.Runtime.exe",
                    @"D:\Delta NFD\Backends\DeltaNFD.OfflineHelper.exe", Path.Combine(BackendEnvironment.AppRoot, "plugins", "OtherName.exe") })
                {
                    engine.SampleForTests = () => new(90, [(identity with { ExecutablePath = path }, 20)]);
                    await engine.ResponsivenessOnceForTests(); now = now.AddSeconds(7); await engine.ResponsivenessOnceForTests();
                    Check(classified == 0 && engine.ResponsivenessEvents().Count == 0,
                        "后台抑制在任何属性操作前明确保护工具进程：" + Path.GetFileName(path));
                }
                await engine.RestoreAllAsync();
            }
            using (var engine = Engine("resp-shared-exclusion-restore"))
            {
                engine.BackgroundLoopDisabledForTests = true;
                var now = DateTime.UtcNow;
                engine.ClockForTests = () => now;
                engine.SampleForTests = () => new(95, [(identity, 90)]);
                engine.ConfigureResponsiveness(new() { Enabled = true, CpuBusySeconds = 1 });
                await engine.ResponsivenessOnceForTests(); now = now.AddSeconds(2); await engine.ResponsivenessOnceForTests();
                Check(Priority() == "BelowNormal", "共用排除测试：专用夹具先受后台抑制");
                engine.MemoryReadForTests = _ => 600L * 1024 * 1024;
                engine.MemorySampleDelayForTests = TimeSpan.Zero;
                var preview = await engine.PreviewMemoryAsync();
                Check(preview.Candidates.Count == 1, "本工具临时后台抑制不冒充用户手动属性排除");
                var trimmed = false;
                engine.MemoryTrimForTests = (target, guard) =>
                {
                    trimmed = guard(); return new(target.Pid, "fixture", trimmed ? "completed" : "skipped", "simulated");
                };
                engine.TrimMemory(new() { PreviewId = preview.Id, Processes = preview.Candidates.Select(c => c.Identity).ToList() });
                Check(trimmed && Priority() == "BelowNormal", "已有本工具抑制优先级时仍可回收，不改动优先级控制权");
                Check(engine.SaveBackgroundConfig(new() { ExcludedExecutablePaths = [identity.ExecutablePath] }).Success
                    && Priority() == "Normal" && engine.Detail(new() { Identity = request }).HeldChanges.Count == 0,
                    "新增排除项立即恢复已抑制的专用夹具，记录及所有权正确释放");
                now = now.AddSeconds(10); await engine.ResponsivenessOnceForTests();
                Check(Priority() == "Normal", "加入共用名单后持续高 CPU 也不会重新抑制");
                await engine.RestoreAllAsync();
            }
            using (var engine = Engine("resp-other-rule"))
            {
                var now = DateTime.UtcNow;
                engine.ClockForTests = () => now;
                engine.SampleForTests = () => new(90, [(identity, 20)]);
                Check(engine.SaveRule(new() { ExecutablePath = identity.ExecutablePath, Capability = "memoryPriority", Value = "2" }).Success,
                    "保存其他属性规则用于检查进程排除");
                await engine.ApplyRulesOnceAsync();
                engine.ConfigureResponsiveness(new() { Enabled = true });
                await engine.ResponsivenessOnceForTests(); now = now.AddSeconds(7); await engine.ResponsivenessOnceForTests();
                Check(Priority() == "Normal" && !engine.ResponsivenessEvents().Any(e => e.Action == "demote"), "其他属性持久规则同样排除整个进程");
                await engine.RestoreAllAsync();
            }
            foreach (var ruleOwnsPriority in new[] { false, true })
            using (var engine = Engine(ruleOwnsPriority ? "resp-priority-rule" : "resp-priority-manual"))
            {
                var now = DateTime.UtcNow;
                engine.ClockForTests = () => now;
                engine.SampleForTests = () => new(90, [(identity, 20)]);
                if (ruleOwnsPriority)
                {
                    engine.SaveRule(new() { ExecutablePath = identity.ExecutablePath, Capability = "priority", Value = "Normal" });
                    await engine.ApplyRulesOnceAsync();
                }
                else engine.Set(new() { Identity = request, CpuPriority = "Normal" });
                engine.ConfigureResponsiveness(new() { Enabled = true });
                await engine.ResponsivenessOnceForTests(); now = now.AddSeconds(7); await engine.ResponsivenessOnceForTests();
                Check(Priority() == "Normal" && !engine.ResponsivenessEvents().Any(e => e.Action == "demote"),
                    ruleOwnsPriority ? "同一 CPU 优先级的显式规则不被保护覆盖" : "同一 CPU 优先级的手动控制不被保护覆盖");
                await engine.RestoreAllAsync();
            }
            var corrupt = Path.Combine(root, "corrupt.json"); File.WriteAllText(corrupt, "{bad");
            bool rejected = false; try { _ = new ProcmanChangeLog(corrupt); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && File.ReadAllText(corrupt) == "{bad", "损坏日志拒绝新操作且原文件保留");
            var saved = new ProcmanChangeLog(Path.Combine(root, "procman-changes.json"));
            saved.Add(new(child.Id, identity.StartTimeUtcTicks, identity.ExecutablePath, "priority", "Normal", "BelowNormal", "manual", DateTimeOffset.UtcNow));
            ProcmanCapabilities.SetPriority(child.Id, ProcessPriorityClass.BelowNormal);
            await using (var readonlyBackend = await DeltaNFD.Services.Backends.BackendClient.StartAsync(DeltaNFD.Backends.BackendModules.Procman, CancellationToken.None))
            {
                var background = await readonlyBackend.InvokeAsync(DeltaNFD.Backends.BackendCommands.ProcmanBackgroundGet, new { }, null, CancellationToken.None);
                Check(background.Status == DeltaNFD.Backends.BackendResultStatuses.Ok
                    && !DeltaNFD.Backends.BackendJson.Deserialize<ProcmanBackgroundStatus>(background.Data!.Value).Config.AutomaticMemoryEnabled,
                    "真实 IPC 后台配置只读查询，默认关闭自动回收");
                await readonlyBackend.InvokeAsync(DeltaNFD.Backends.BackendCommands.ProcmanReconcile, new { }, null, CancellationToken.None);
                await readonlyBackend.DrainAsync(TimeSpan.FromSeconds(15));
                Check(Priority() == "BelowNormal" && new ProcmanChangeLog(Path.Combine(root, "procman-changes.json")).Records.Count == 1,
                    "只读核对后停止不偷偷恢复遗留修改");
            }
            await using (var recoveryBackend = await DeltaNFD.Services.Backends.BackendClient.StartAsync(DeltaNFD.Backends.BackendModules.Procman, CancellationToken.None))
            {
                var recovered = await recoveryBackend.InvokeAsync(DeltaNFD.Backends.BackendCommands.ProcmanRecover, new RecoverArgs { Scope = "all" }, null, CancellationToken.None);
                var outcome = DeltaNFD.Backends.BackendJson.Deserialize<RecoverOutcome>(recovered.Data!.Value);
                Check(outcome.Success && outcome.Verified && outcome.VerifiedSnapshot is { Complete: true } && Priority() == "Normal",
                    "明确恢复命令返回统一 RecoverOutcome 并复核零遗留");
                await recoveryBackend.DrainAsync(TimeSpan.FromSeconds(15));
            }
            await using (var backend = await DeltaNFD.Services.Backends.BackendClient.StartAsync(DeltaNFD.Backends.BackendModules.Procman, CancellationToken.None))
            {
                var set = await backend.InvokeAsync(DeltaNFD.Backends.BackendCommands.ProcmanSet,
                    new ProcmanSetArgs { Identity = request, CpuPriority = "BelowNormal" }, null, CancellationToken.None);
                Check(set.Status == DeltaNFD.Backends.BackendResultStatuses.Ok && Priority() == "BelowNormal", "真实官方后端 IPC 设置（仅唯一夹具）");
                var backgroundSave = await backend.InvokeAsync(DeltaNFD.Backends.BackendCommands.ProcmanBackgroundSave,
                    new ProcmanBackgroundConfig { ExcludedExecutablePaths = [identity.ExecutablePath] }, null, CancellationToken.None);
                var backgroundSaved = DeltaNFD.Backends.BackendJson.Deserialize<ProcmanBackgroundStatus>(backgroundSave.Data!.Value);
                Check(backgroundSave.Status == DeltaNFD.Backends.BackendResultStatuses.Ok && backgroundSaved.Config.ExcludedExecutablePaths.Contains(identity.ExecutablePath)
                    && Priority() == "BelowNormal", "真实 IPC 保存共用排除名单，不撤销手动属性");
                var snapshot = await backend.InvokeAsync(DeltaNFD.Backends.BackendCommands.ProcmanReconcile, new { }, null, CancellationToken.None);
                var generic = DeltaNFD.Backends.BackendJson.Deserialize<ReconcileSnapshot>(snapshot.Data!.Value);
                Check(generic.Module == "procman" && generic.RecoveryScopes.Any(s => s.Id == "all" && s.Available), "核对兼容统一恢复合同");
                var restore = await backend.InvokeAsync(DeltaNFD.Backends.BackendCommands.ProcmanRestoreAll, new { }, null, CancellationToken.None);
                Check(restore.Status == DeltaNFD.Backends.BackendResultStatuses.Ok && Priority() == "Normal", "真实 IPC 全量还原");
                await backend.DrainAsync(TimeSpan.FromSeconds(15));
                Check(backend.ExitConfirmed, "真实后端正常停止并确认退出");
            }
            await using (var retiredFrame = await DeltaNFD.Services.Backends.BackendClient.StartAsync(DeltaNFD.Backends.BackendModules.CpuFrame, CancellationToken.None))
            {
                var retired = await retiredFrame.InvokeAsync(DeltaNFD.Backends.BackendCommands.CpuFrameMemoryClean, new { }, null, CancellationToken.None);
                Check(retired.Status == DeltaNFD.Backends.BackendResultStatuses.Failed && retired.Message.Contains("已下线"),
                    "真实旧帧格 IPC 清理命令明确拒绝，不再执行全机清理");
                await retiredFrame.DrainAsync(TimeSpan.FromSeconds(15));
                Check(retiredFrame.ExitConfirmed, "旧帧格命令拒绝后仍正常停止后端");
            }
            const string gpuKey = @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences";
            using (var key = Registry.CurrentUser.OpenSubKey(gpuKey)) Check(key?.GetValue(identity.ExecutablePath) is null, "GPU 测试路径没有用户原值");
            try
            {
                var blocked = Path.Combine(root, "file-parent"); File.WriteAllText(blocked, "fixture");
                using (var engine = new ProcmanEngine(Path.Combine(blocked, "changes.json")))
                    Check(engine.Set(new() { Identity = request, GpuPreference = "高性能" }).Outcomes.Single().Status == "failed"
                        && !ProcmanGpuPreference.Read(identity.ExecutablePath).Exists, "日志落盘失败不修改 GPU 设置");
                using var gpuEngine = Engine("gpu");
                Check(gpuEngine.Set(new() { Identity = request, GpuPreference = "高性能" }).Outcomes.Single().Status == "applied", "GPU 首选项写前记录和回读");
                Check(gpuEngine.Reconcile().Entries.Single().Status == "applied", "GPU 首选项即时核对不误报冲突");
                child.Kill(); await child.WaitForExitAsync();
                Check((await gpuEngine.RestoreAllAsync()).Success && !ProcmanGpuPreference.Read(identity.ExecutablePath).Exists, "进程退出后仍恢复持久 GPU 设置");
            }
            finally { using var key = Registry.CurrentUser.CreateSubKey(gpuKey); key.DeleteValue(identity.ExecutablePath, false); }
            Console.WriteLine($"Procman repair checks: {count} passed. Unique EXE, isolated stores, injected workload, no user processes modified.");
            completed = true;
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
            var full = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (completed && full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("DeltaNFD-procman-repair-", StringComparison.Ordinal)) Directory.Delete(full, true);
        }
    }
}
