using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class DeploymentHandoverChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        // Exercise the running frontend's actual wire dispatch; a wire-only fixture cannot catch inversion.
        foreach (var (request, expected) in new[] { ("INSTALL/1", "update"), ("UNINSTALL/1", "full"),
                     ("UNINSTALL/2", "keep"), ("INSTALL/2", "reject"), ("", "reject") })
        {
            string selected = "reject";
            Task<DeploymentHandover> Record(string mode) { selected = mode; return Task.FromResult<DeploymentHandover>(null!); }
            var prepare = DeltaNFD.Native.DeploymentPipe.SelectPreparation(request,
                uninstall => Record(uninstall ? "full" : "update"), () => Record("keep"));
            if (prepare != null) await prepare();
            check(selected == expected, $"部署：{request} 选择 {expected}，实际 {selected}");
        }
        var env = Environment.GetEnvironmentVariable("DELTANFD_TEST_DEPLOYMENT_RECOVERY");
        try
        {
            Environment.SetEnvironmentVariable("DELTANFD_TEST_DEPLOYMENT_RECOVERY", "ok");
            AppSettingsStore.Update(s => { s.FrameModeActive = false; s.GamePriorityEnabled = false;
                s.GameAffinityRuleEnabled = false; s.SingleCcdExcludeCpu0Enabled = false; s.DualCcdImmediateEnabled = false; });
            using (var permit = await DeploymentHandover.PrepareAsync(uninstall: true))
            {
                check(permit.Ready && permit.ExitCode == 0, "部署：无历史记录时完整恢复并排空后允许卸载：" + permit.Detail);
                var blocked = false;
                try { BackendOperationGate.Check(); } catch (InvalidOperationException) { blocked = true; }
                check(blocked, "部署：成功许可持有门禁，退出前不再接收官方请求");
                check(BackendSessions.Snapshot().All(c => c.ExitConfirmed), "部署：恢复产生的新后端也已确认退出");
            }
            BackendOperationGate.Check();

            var installerRecord = Path.Combine(AppDataPaths.Root, "safe-installer-active.json");
            File.WriteAllText(installerRecord, "{fixture pending evidence}");
            using (var permit = await DeploymentHandover.PrepareAsync(uninstall: false))
                check(!permit.Ready && permit.Detail.Contains("临时 Installer") && File.Exists(installerRecord), "临时 Installer 恢复记录阻止更新且保留证据");
            using (var permit = await DeploymentHandover.PrepareAsync(uninstall: true))
                check(!permit.Ready && File.Exists(installerRecord), "临时 Installer 恢复记录阻止卸载/重置丢失证据");
            using (var permit = await DeploymentHandover.PrepareForUninstallAsync())
                check(!permit.Ready && File.Exists(installerRecord), "保留设置卸载仍等待临时 Installer 操作收尾，不删除其记录");
            File.Delete(installerRecord); // Isolated fixture only.

            foreach (var module in new[] { BackendModules.Runtime, BackendModules.Ace })
            {
                var path = WriteHistory(module, module == BackendModules.Runtime ? BackendCommands.RuntimeRepair : BackendCommands.AceClean);
                var bytes = File.ReadAllBytes(path);
                using (var permit = await DeploymentHandover.PrepareAsync(uninstall: true))
                    check(permit.Ready && permit.ExitCode == 10, "部署：已退出的 " + module + " 历史记录允许保留全部数据后卸载");
                check(File.ReadAllBytes(path).SequenceEqual(bytes), "部署：" + module + " 原始证据没有被关闭防护/删除文件冒充恢复");
                await BackendLifecycle.AcknowledgeCurrentStateAsync(BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == module));
            }
            foreach (var command in new[] { BackendCommands.CpuFramePowerLock, "cpuframe.timer.apply", "cpuframe.timer.restore" })
            {
                WriteHistory(BackendModules.CpuFrame, command);
                using (var permit = await DeploymentHandover.PrepareAsync(uninstall: true))
                    check(permit.Ready && permit.ExitCode == 0, "部署：CPU 旧记录先完整恢复，再恢复脱机和防护：" + command);
                check(!BackendUnconfirmedStore.PendingModules().Contains(BackendModules.CpuFrame), "部署：完整复核后才归档旧证据：" + command);
            }

            foreach (var command in new[] { BackendCommands.CpuFrameMonitorStart, "cpuframe.boost.apply" })
            {
                var volatileCpu = WriteHistory(BackendModules.CpuFrame, command);
                DeploymentHandover.TargetRunningForTests = () => true;
                using (var permit = await DeploymentHandover.PrepareAsync(uninstall: true))
                    check(!permit.Ready && File.Exists(volatileCpu), "部署：旧执行者丢失原值且游戏仍运行时，不冒充还原：" + command);
                DeploymentHandover.TargetRunningForTests = () => false;
                using (var permit = await DeploymentHandover.PrepareAsync(uninstall: true))
                    check(permit.Ready && !File.Exists(volatileCpu), "部署：游戏退出后完整恢复通过才允许卸载：" + command);
                DeploymentHandover.TargetRunningForTests = null;
            }

            var runtime = WriteHistory(BackendModules.Runtime, BackendCommands.RuntimeRepair);
            Environment.SetEnvironmentVariable("DELTANFD_TEST_DEPLOYMENT_RECOVERY", "failed");
            using (var permit = await DeploymentHandover.PrepareAsync(uninstall: true))
                check(!permit.Ready && permit.Detail.Contains("运行库防护关闭"), "部署：防护恢复失败继续阻止卸载，并报告具体子系统");
            check(File.Exists(runtime), "部署：恢复失败仍保留运行库原始记录");
            Environment.SetEnvironmentVariable("DELTANFD_TEST_DEPLOYMENT_RECOVERY", "incomplete");
            using (var permit = await DeploymentHandover.PrepareAsync(uninstall: true))
                check(!permit.Ready, "部署：仅 Success=true 而复核不完整不能获准卸载");
            Environment.SetEnvironmentVariable("DELTANFD_TEST_DEPLOYMENT_RECOVERY", "ok");
            using (var permit = await DeploymentHandover.PrepareAsync(uninstall: true))
                check(permit.Ready && permit.ExitCode == 10, "部署：失败后可重试完整恢复，仍保留不属于防护恢复范围的运行库证据");
            await BackendLifecycle.AcknowledgeCurrentStateAsync(BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.Runtime));

            // Advanced startup uses the non-uninstall path: no guard/system recovery writes.
            Environment.SetEnvironmentVariable("DELTANFD_TEST_DEPLOYMENT_RECOVERY", "failed");
            using (var permit = await DeploymentHandover.PrepareAsync(false, "进入高级启动"))
            {
                check(permit.Ready, "高级启动：仅排空执行者，不触发卸载恢复或关闭防护");
                var gated = false;
                try { BackendOperationGate.Check(); } catch (InvalidOperationException) { gated = true; }
                check(gated, "高级启动：排空成功后持续封锁新操作");
            }
            BackendOperationGate.Check();
            Environment.SetEnvironmentVariable("DELTANFD_TEST_DEPLOYMENT_RECOVERY", "ok");

            // A removal permit never calls persistent recovery, even when that recovery would fail.
            Environment.SetEnvironmentVariable("DELTANFD_TEST_DEPLOYMENT_RECOVERY", "failed");
            var sessionCount = BackendSessions.Snapshot().Count;
            using (var permit = await DeploymentHandover.PrepareForUninstallAsync())
            {
                check(permit.Ready && permit.ExitCode == 10 && permit.Detail.Contains("不额外执行"),
                    "保留设置卸载不要求先恢复防护／CPU，且必须保留恢复数据：" + permit.Detail);
                check(BackendSessions.Snapshot().Count == sessionCount, "无活动执行者时，保留设置卸载不额外启动持久恢复后端");
                var gated = false;
                try { BackendOperationGate.Check(); } catch (InvalidOperationException) { gated = true; }
                check(gated, "保留设置卸载成功后，仍封锁新操作直到退出");
            }
            BackendOperationGate.Check();
            foreach (var command in new[] { BackendCommands.CpuFramePowerLock, "cpuframe.timer.apply", "cpuframe.boost.apply" })
            {
                var cpuRecord = WriteHistory(BackendModules.CpuFrame, command);
                var runtimeRecord = WriteHistory(BackendModules.Runtime, BackendCommands.RuntimeRepair);
                var cpuBytes = File.ReadAllBytes(cpuRecord);
                var runtimeBytes = File.ReadAllBytes(runtimeRecord);
                DeploymentHandover.TargetRunningForTests = () => true;
                using (var permit = await DeploymentHandover.PrepareForUninstallAsync())
                    check(permit.Ready && permit.ExitCode == 10 && permit.Detail.Contains("仍待核对"),
                        "历史设置未恢复时仍可卸载，并如实保留待核对说明：" + command);
                check(File.ReadAllBytes(cpuRecord).SequenceEqual(cpuBytes) && File.ReadAllBytes(runtimeRecord).SequenceEqual(runtimeBytes),
                    "保留设置卸载不改写／归档／删除 CPU 和运行库历史原始证据：" + command);
                DeploymentHandover.TargetRunningForTests = null;
                foreach (var review in BackendUnconfirmedStore.ReviewAll())
                    await BackendLifecycle.AcknowledgeCurrentStateAsync(review);
            }
            Environment.SetEnvironmentVariable("DELTANFD_TEST_DEPLOYMENT_RECOVERY", "ok");

            using (var current = Process.GetCurrentProcess())
            {
                var path = WriteHistory(BackendModules.Ace, BackendCommands.AceClean, current.Id, current.StartTime.ToUniversalTime().Ticks);
                using (var permit = await DeploymentHandover.PrepareAsync(uninstall: false))
                    check(!permit.Ready, "部署：历史记录的原执行者仍存活时禁止更新，不以保留证据绕过");
                using (var permit = await DeploymentHandover.PrepareForUninstallAsync())
                    check(!permit.Ready && File.Exists(path), "保留设置卸载仍拒绝删除活动执行者使用的程序，并保留身份记录");
                File.Delete(path); // This fixture is owned by this test and did not execute an operation.
            }
            var malformed = BackendUnconfirmedStore.RecordPathForTests(BackendModules.Ace);
            File.WriteAllText(malformed, "{invalid test evidence");
            var rejected = false;
            try { using var denied = await DeploymentHandover.PrepareAsync(uninstall: true); rejected = !denied.Ready; }
            catch { rejected = true; }
            check(rejected && File.Exists(malformed), "部署：损坏证据不能成为可删除的历史记录");
            rejected = false;
            try { using var denied = await DeploymentHandover.PrepareForUninstallAsync(); rejected = !denied.Ready; }
            catch { rejected = true; }
            check(rejected && File.Exists(malformed), "保留设置卸载仍保留损坏证据，不能用它证明执行者已退出");
            BackendOperationGate.Check();
            check(true, "部署：准备失败会释放门禁，用户仍可进入恢复页面");
            File.Delete(malformed);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DELTANFD_TEST_DEPLOYMENT_RECOVERY", env);
            DeploymentHandover.TargetRunningForTests = null;
        }
    }

    private static string WriteHistory(string module, string command, int pid = int.MaxValue, long ticks = 100)
    {
        var path = BackendUnconfirmedStore.RecordPathForTests(module);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Module = module, Phase = "unconfirmed", Command = command, RequestId = Guid.NewGuid().ToString(),
            SessionId = Guid.NewGuid().ToString(), Pid = pid, StartTicks = ticks,
            Path = Environment.ProcessPath!, CreatedUtc = DateTimeOffset.UtcNow, Reason = "isolated deployment history",
            Children = Array.Empty<object>(), ChildrenUnknown = false,
        }));
        return path;
    }
}
