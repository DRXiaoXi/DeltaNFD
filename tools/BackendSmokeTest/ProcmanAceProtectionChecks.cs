using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

internal static class ProcmanAceProtectionChecks
{
    internal static async Task RunAsync()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-ace-protection-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var images = Path.Combine(root, "fixture-images"); Directory.CreateDirectory(images);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(images, Path.GetFileName(file)));
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        foreach (var name in new[] { "ACE-Tray.exe", "SGuard64.exe", "SGuardSvc64.exe" })
        {
            var path = Path.Combine(images, name);
            Check(ProcmanProcessProtection.IsAceProtected(path) && ProcmanProcessProtection.IsAceProtected(name.ToUpperInvariant()), "精确文件名保护且忽略大小写：" + name);
            Check(ProcmanIdentity.IsProtectedPath(path), "后端固定保护覆盖任意目录：" + name);
            Check(ProcmanBackgroundFilter.ExcludedByIdentity(path, 1, "S-1-5-21-1-2-3-1001", false, @"C:\Windows"), "普通用户身份也不能绕过保护：" + name);
            File.Copy(Path.Combine(images, "BackendSmokeTest.exe"), path);
            using var child = Process.Start(new ProcessStartInfo(path, "--procman-test-child") { UseShellExecute = false, CreateNoWindow = true })!;
            try
            {
                child.PriorityClass = ProcessPriorityClass.Normal;
                var identity = ProcmanIdentity.TryIdentify(child.Id) ?? throw new Exception("Cannot identify own fixture.");
                var args = new ProcessIdentityArgs { Pid = identity.Pid, StartTimeUtcTicks = identity.StartTimeUtcTicks, ExecutablePath = identity.ExecutablePath };
                var changes = Path.Combine(root, name + ".changes.json");
                using var engine = new ProcmanEngine(changes)
                {
                    BackgroundLoopDisabledForTests = true, FrameModeActiveForTests = () => true, FrameMemorySelectedForTests = () => true,
                    ForegroundForTests = () => Environment.ProcessId, BackgroundExcludedForTests = _ => false, MemorySystemProcessForTests = _ => false,
                    MemoryIdentitiesForTests = () => [identity], MemoryReadForTests = _ => throw new Exception("Protected target must be excluded before working-set reads."),
                };
                var result = engine.Set(new() { Identity = args, CpuPriority = "BelowNormal", AffinityMasks = [new(0, 1)], CpuSetMasks = [],
                    MemoryPriority = 1, GpuPriority = 1, IoPriority = "Low", EcoQosEnabled = true, PriorityBoost = false, GpuPreference = "高性能", ConfirmedTakeover = true });
                Check(result.IdentityOk && result.Outcomes.Count == 0 && !result.RequiresReview && result.IdentityError.Contains("ACE"), "所有属性及确认接管均明确拒绝：" + name);
                Check(engine.Snapshot().HeldChangeCount == 0 && ProcmanCapabilities.ReadPriority(child.Id).Value == "Normal", "拒绝不写入修改记录或优先级：" + name);
                var detail = engine.Detail(new() { Identity = args });
                Check(detail.IdentityOk && detail.Capabilities.Count == 9 && detail.Capabilities.All(c => c.Support == "readOnly"), "详情允许查看，所有能力含 GPU 首选项只读：" + name);
                Check(!engine.SaveRule(new() { ExecutablePath = path, Capability = "priority", Value = "BelowNormal" }).Success, "引擎拒绝新总是规则：" + name);
                var store = new ProcmanRuleStore(changes + ".rules.json");
                var denied = false;
                try { store.Save(new() { ExecutablePath = path, Capability = "priority", Value = "BelowNormal" }); }
                catch (ArgumentException) { denied = true; }
                Check(denied && !File.Exists(changes + ".rules.json"), "底层规则仓库也拒绝直接保存：" + name);
                File.WriteAllText(changes + ".rules.json", JsonSerializer.Serialize(new ProcmanRuleStore.RuleFile
                { Rules = [new() { ExecutablePath = path, Capability = "priority", Value = "BelowNormal", Enabled = true, AutoTakeover = true }] }));
                Check(store.Load().Count == 1 && store.EnabledRules().Count == 0, "旧 ACE 规则保留可查但不执行：" + name);
                await engine.ApplyRulesOnceAsync();
                Check(ProcmanCapabilities.ReadPriority(child.Id).Value == "Normal" && engine.Snapshot().HeldChangeCount == 0, "旧自动接管规则不能修改 ACE：" + name);
                foreach (var mode in new[] { ProcmanMemoryModes.Normal, ProcmanMemoryModes.DeltaStable })
                {
                    Check(engine.SaveBackgroundConfig(new() { MemoryMode = mode }).Success, "保存隔离内存模式：" + mode);
                    Check((await engine.PreviewMemoryAsync()).Candidates.Count == 0, mode + "排除受保护 ACE：" + name);
                }
                Check((await engine.DeleteRuleAsync(path, "priority")).Success, "允许删除不再执行的旧规则：" + name);
                new ProcmanChangeLog(changes).Add(new(child.Id, identity.StartTimeUtcTicks, path, "priority", "Normal", "BelowNormal", "manual", DateTimeOffset.UtcNow));
                ProcmanCapabilities.SetPriority(child.Id, ProcessPriorityClass.BelowNormal); // Own fixture only: simulate an older version's journal.
                using var recovery = new ProcmanEngine(changes) { BackgroundLoopDisabledForTests = true };
                Check((await recovery.RecoverAsync("all")).Success && ProcmanCapabilities.ReadPriority(child.Id).Value == "Normal", "保留按历史记录精确恢复的责任：" + name);
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
        }
        foreach (var ordinary in new[] { "ACE-Tray-helper.exe", "SGuard640.exe", "SGuardSvc64-test.exe", "NotACE-Tray.exe", "SGuard64.dll", "SGuard64.exe.bak" })
            Check(!ProcmanProcessProtection.IsAceProtected(Path.Combine(root, ordinary)), "不误伤近似文件名：" + ordinary);
        using var activeAceFixture = Process.Start(new ProcessStartInfo(Path.Combine(images, "ACE-Tray.exe"), "--procman-test-child") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            using var aggressive = new ProcmanEngine(Path.Combine(root, "aggressive.json")) { BackgroundLoopDisabledForTests = true,
                MemoryIdentitiesForTests = () => throw new Exception("Aggressive must not enumerate targets."),
                SampleForTests = () => throw new Exception("Aggressive must not sample CPU."),
                MemoryGlobalTrimForTests = guard => guard() ? new([new(0, "fixture", "completed", "simulated")], "simulated global") : throw new Exception("Unexpected guard refusal.") };
            Check(aggressive.SaveBackgroundConfig(new() { MemoryMode = ProcmanMemoryModes.Aggressive }).Success, "激进模式仍可启用");
            var preview = await aggressive.PreviewMemoryAsync();
            Check(preview.Mode == ProcmanMemoryModes.Aggressive && preview.Candidates.Count == 0 && preview.Message.Contains("忽略逐进程保护"), "激进预览明确保护例外且不枚举进程");
            Check(!activeAceFixture.HasExited && aggressive.TrimMemory(new() { PreviewId = preview.Id }).Items.Count == 1, "ACE 同名夹具运行时，激进模式仍直达模拟全局回收");
        }
        finally { if (!activeAceFixture.HasExited) { activeAceFixture.Kill(); await activeAceFixture.WaitForExitAsync(); } }
        Console.WriteLine($"ACE process protection: {count} assertions passed. Only renamed copies of our own fixture were used; no actual ACE process or global memory was modified.");
    }
}
