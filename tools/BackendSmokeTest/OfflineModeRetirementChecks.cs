using System.Diagnostics;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.Services.Plugins;

internal static class OfflineModeRetirementChecks
{
    internal static async Task RunAsync()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-offline-retirement-" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", id.ToString("D"));
        var fixtureRoot = Path.Combine(root, "backends");
        var spec = DeltaNFD.Backends.BackendModules.Require(DeltaNFD.Backends.BackendModules.CpuFrame);
        var fixtureDirectory = Path.Combine(fixtureRoot, spec.Id);
        Directory.CreateDirectory(fixtureDirectory);
        foreach (var source in Directory.GetFiles(AppContext.BaseDirectory))
        {
            var name = Path.GetFileName(source);
            if (name.Equals(Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase)) name = spec.ExecutableName;
            File.Copy(source, Path.Combine(fixtureDirectory, name));
        }
        Environment.SetEnvironmentVariable(BackendPaths.BackendRootEnvironmentVariable, fixtureRoot);
        Environment.SetEnvironmentVariable("DELTANFD_TEST_OFFLINE_RESTORE", "success");
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
        try
        {
            var store = OfflineModeStateStore.Default;
            var coordinator = new OfflineModeCoordinator();
            Check(!(await coordinator.EnableAsync()).Success && !File.Exists(store.PathName), "移除后拒绝启用且不生成状态");
            Check(!(await coordinator.StartHelperAsync()).Success && !File.Exists(store.PathName), "移除后拒绝启动助手");
            Check((await ServiceLocator.PluginRuntime.HandoffToOfflineAsync(new PluginIndexEntry { Id = "fixture" })).Contains("已移除"), "插件不再允许脱机移交");
            AppSettingsStore.Update(s => s.AllowPluginsInOfflineMode = true);
            Check((await OfflineModeRetirement.RecoverAsync()).Success && !OfflineModeRetirement.NeedsRecovery, "无遗留记录正常启动且不创建阻塞");
            Check(!AppSettingsStore.Read().AllowPluginsInOfflineMode, "清除旧版插件自主运行偏好");
            Check(store.TryUpdate(s =>
            { s.OfflineModeEnabled = true; s.Status = OfflineModeStatus.Ready; s.SavedPreferences = new() { GameAffinityRuleMask = 3 }; }, out _), "隔离播种旧版就绪记录");
            var saved = File.ReadAllBytes(store.PathName);
            Check(!(await coordinator.EnableAsync()).Success && !(await coordinator.StartHelperAsync()).Success
                && File.ReadAllBytes(store.PathName).SequenceEqual(saved), "旧记录存在也拒绝重启功能且不覆盖证据");
            var restored = await OfflineModeRetirement.RecoverAsync();
            Check(restored.Success && !store.Read().BlocksNormalAutomation
                && store.Read().PreviousNormalPreferences?.GameAffinityRuleMask == 3, "旧版记录经恢复路径收敛，原参数另存：" + restored.Message);
            Check(!AppSettingsStore.Read().FrameModeActive && !AppSettingsStore.Read().GamePriorityEnabled, "恢复后不擅自启用普通自动功能");
            using var self = Process.GetCurrentProcess();
            Check(store.TryUpdate(s =>
            {
                s.SavedPreferences = new(); s.Status = OfflineModeStatus.Kept; s.OfflineModeEnabled = true;
                s.HelperProcessId = self.Id; s.HelperStartTimeUtcTicks = self.StartTime.ToUniversalTime().Ticks; s.HelperRunId = Guid.NewGuid();
            }, out _), "隔离播种仍存活助手身份");
            Check(!(await OfflineModeRetirement.RecoverAsync()).Success && store.Read().HelperProcessId == self.Id
                && OfflineModeRetirement.NeedsRecovery, "登记进程仍存活时保留恢复义务，不因新目录缺少助手而清空记录");
            store.TryUpdate(s => { s.HelperProcessId = null; s.HelperStartTimeUtcTicks = null; s.HelperRunId = Guid.Empty; }, out _);
            Check((await OfflineModeRetirement.RecoverAsync()).Success, "助手确认退出后允许重试恢复");
            var valid = File.ReadAllBytes(store.PathName);
            File.WriteAllText(store.PathName, "{corrupt");
            Check(!(await OfflineModeRetirement.RecoverAsync()).Success && File.ReadAllText(store.PathName) == "{corrupt",
                "损坏记录保留并报告，不能静默绕过");
            File.WriteAllBytes(store.PathName, valid);
            Check((await OfflineModeRetirement.RecoverAsync()).Success && !OfflineModeRetirement.NeedsRecovery, "修复记录后重新核对，解除旧版恢复提示");
            Check((await BackendLifecycle.PrepareHandoverAsync("移除功能隔离检查结束", TimeSpan.FromSeconds(5))).Count == 0
                && !BackendSessions.AnyActive, "恢复测试完成后还原并确认后端退出");
            Console.WriteLine($"Offline retirement checks: {count} passed. Isolated records; no user CPU settings changed.");
        }
        finally
        {
            var failures = await BackendLifecycle.PrepareHandoverAsync("移除功能隔离检查清理", TimeSpan.FromSeconds(5));
            if (failures.Count > 0) throw new Exception("隔离后端未安全退出，测试目录保留：" + root);
            var target = Path.GetFullPath(root);
            if (target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(target).StartsWith("DeltaNFD-offline-retirement-", StringComparison.Ordinal))
                for (var retry = 0; ; retry++)
                {
                    try { Directory.Delete(target, true); break; }
                    catch (IOException) when (retry < 10) { await Task.Delay(100); }
                }
        }
    }
}
