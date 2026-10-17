using DeltaNFD.Backends;
using DeltaNFD.Services.Backends;

namespace DeltaNFD.Services;

/// <summary>安装/卸载的交接许可。许可成功后一直封锁新操作，直到进程退出。</summary>
internal sealed class DeploymentHandover : IDisposable
{
    private readonly IDisposable _backends;
    private readonly IDisposable _plugins;
    private DeploymentHandover()
    {
        _backends = BackendOperationGate.BlockNewOperations();
        try { _plugins = ServiceLocator.PluginRuntime.BlockNewOperations(); }
        catch { _backends.Dispose(); throw; }
    }
    public int ExitCode { get; private set; } = 2;
    public string Detail { get; private set; } = "交接尚未完成。";
    public bool Ready => ExitCode is 0 or 10;

    internal static bool CanPreserveHistory(BackendUnconfirmedStore.Review review) => review.CanAcknowledge;
    internal static Func<bool>? TargetRunningForTests { get; set; }

    private static bool VolatileCpuStateMayRemain(BackendUnconfirmedStore.Review review)
    {
        if (review.Command is not (BackendCommands.CpuFrameMonitorStart or BackendCommands.CpuFrameMonitorStop
            or BackendCommands.CpuFramePriorityApply or BackendCommands.CpuFrameAffinityApply
            or BackendCommands.CpuFrameCcdApply or BackendCommands.CpuFrameCcdRevert
            or BackendCommands.CpuFrameSingleCcdApply or BackendCommands.CpuFrameSingleCcdRevert
            or BackendCommands.CpuFrameBoostApply or BackendCommands.CpuFrameRestore or BackendCommands.CpuFrameRecover)) return false;
        if (Guid.TryParse(review.SessionId, out var session) && BackendSessions.Snapshot().Any(c =>
                c.ModuleId == BackendModules.CpuFrame && c.SessionId == session && c.ExitConfirmed && c.CpuRestoreConfirmed))
            return false;
        if (TargetRunningForTests is { } test) return test();
        var processes = System.Diagnostics.Process.GetProcessesByName("DeltaForceClient-Win64-Shipping");
        try { return processes.Any(p => !p.HasExited); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static Task<DeploymentHandover> PrepareAsync(bool uninstall) => PrepareAsync(uninstall, null);

    public static async Task<DeploymentHandover> PrepareAsync(bool uninstall, string? purpose)
    {
        var permit = new DeploymentHandover();
        try
        {
            using var control = BackendOperationGate.AllowControlOperations();
            var failures = new List<string>();
            var reason = purpose ?? (uninstall ? "卸载" : "安装更新");
            async Task Attempt(string name, Func<Task> action)
            {
                try { await action(); }
                catch (Exception ex) { Log.Error(reason + "：" + name, ex); failures.Add(name + "：" + ex.Message); }
            }
            await Attempt("插件交接", async () =>
            {
                var error = await PluginLifecycleGuard.PrepareHandoverAsync(ServiceLocator.Plugins,
                    ServiceLocator.PluginRuntime, reason, restoreChanges: uninstall);
                if (error.Length > 0) throw new InvalidOperationException(error);
            });
            // Stop first, even when a historical record exists. Otherwise recovery cannot start.
            IReadOnlyList<string> failedStops = [];
            await Attempt("停止官方后端", async () => { failedStops = await BackendLifecycle.PrepareHandoverAsync(reason); });
            // Refresh all persisted identities without starting another worker.
            foreach (var module in BackendUnconfirmedStore.PendingModules())
                await Attempt("核对 " + module, () => { BackendUnconfirmedStore.RefreshChildren(module); return Task.CompletedTask; });
            var reviews = BackendUnconfirmedStore.ReviewAll();
            foreach (var module in failedStops.Where(m => !reviews.Any(r => r.Module == m && CanPreserveHistory(r))))
                failures.Add(module + "：启动、停止或交接未完成，且没有可安全保留的历史证据。");
            foreach (var review in reviews.Where(r => !CanPreserveHistory(r)))
                failures.Add(BackendLifecycle.ModuleDisplayName(review.Module) + "：原进程、子进程或记录身份尚未确认，请在设置中核对。" + review.Description);
            foreach (var client in BackendSessions.Snapshot().Where(c => !c.ExitConfirmed
                         || c.RequiresAttention && !reviews.Any(r => r.Module == c.ModuleId && CanPreserveHistory(r))))
                failures.Add(client.ModuleId + "：活动执行者尚未停止，或恢复证据未能持久化。");

            if (failures.Count == 0)
            {
                // CPU history must complete its supported full recovery; never discard in-memory originals.
                if (uninstall && reviews.SingleOrDefault(r => r.Module == BackendModules.CpuFrame) is { } cpu)
                    await Attempt("CPU/帧格旧记录恢复", async () =>
                    {
                        if (VolatileCpuStateMayRemain(cpu))
                            throw new InvalidOperationException("原 CPU 执行者已退出，但游戏仍在运行，内存中的优先级/EcoQoS 原值可能已丢失。请先退出游戏再卸载，记录保留。");
                        var restored = await BackendRecovery.RestoreAsync(cpu, "all");
                        if (!restored.Verified) throw new InvalidOperationException(restored.Message);
                    });
                if (failures.Count == 0) await Attempt("脱机助手交接", async () =>
                {
                    var result = await new OfflineModeCoordinator().PrepareForRemovalAsync(restoreCpuSets: uninstall);
                    if (!result.Success) throw new InvalidOperationException(result.Message);
                });
                if (uninstall)
                {
                    if (failures.Count == 0) await Attempt("CPU/帧格持久状态恢复", async () => await RecoverAsync(BackendModules.CpuFrame, "all"));
                    await Attempt("运行库防护关闭", async () => await RecoverAsync(BackendModules.Runtime, "guard"));
                }
            }
            // Recovery creates short-lived workers. They must also finish before files are replaced.
            failedStops = [];
            await Attempt("恢复后排空", async () => { failedStops = await BackendLifecycle.PrepareHandoverAsync(reason); });
            foreach (var module in BackendUnconfirmedStore.PendingModules())
                await Attempt("最终核对 " + module, () => { BackendUnconfirmedStore.RefreshChildren(module); return Task.CompletedTask; });
            reviews = BackendUnconfirmedStore.ReviewAll();
            foreach (var module in failedStops.Where(m => !reviews.Any(r => r.Module == m && CanPreserveHistory(r))))
                failures.Add(module + "：最终交接未完成。");
            foreach (var review in reviews.Where(r => !CanPreserveHistory(r)))
                failures.Add(review.Module + "：最终进程/记录核对未通过。");
            foreach (var client in BackendSessions.Snapshot().Where(c => !c.ExitConfirmed
                         || c.RequiresAttention && !reviews.Any(r => r.Module == c.ModuleId && CanPreserveHistory(r))))
                failures.Add(client.ModuleId + "：最终退出尚未确认。");
            if (File.Exists(Path.Combine(AppDataPaths.Root, "defender-session.json")))
                failures.Add("Defender 临时排除项尚未恢复。");
            if (File.Exists(Path.Combine(AppDataPaths.Root, "safe-installer-active.json"))
                || Directory.Exists(Path.Combine(AppDataPaths.Root, "safe-installer-active.json")))
                failures.Add("临时 Installer 设置仍待恢复，请在运行库页点击“恢复临时 Installer 设置”；未允许替换或移除程序及恢复记录。");
            permit.ExitCode = failures.Count > 0 ? 2 : reviews.Count > 0 ? 10 : 0;
            permit.Detail = failures.Count > 0 ? string.Join(Environment.NewLine, failures.Distinct())
                : reviews.Count > 0 ? "执行者已停止，仍有历史操作待人工核对。保留全部数据和原始证据；这不代表历史操作已还原。\n"
                    + string.Join(Environment.NewLine, reviews.Select(r => r.Description)) : "安全交接完成。";
            Log.Info(reason + "交接结果=" + permit.ExitCode + "；" + permit.Detail);
            return permit;
        }
        catch { permit.Dispose(); throw; }
    }

    private static async Task RecoverAsync(string module, string scope)
    {
        var caller = new BackendCaller(module);
        try
        {
            var result = await caller.InvokeReconcileAsync(BackendModules.RecoverCommand(module)!,
                new RecoverArgs { Scope = scope }, null, CancellationToken.None);
            var outcome = BackendResults.RequireData<RecoverOutcome>(result, "卸载恢复");
            if (!outcome.Success || !outcome.Verified || outcome.Scope != scope
                || outcome.VerifiedSnapshot is not { Complete: true } snapshot || snapshot.Module != module
                || module == BackendModules.CpuFrame && snapshot.RecoveryScopes.Any(s => s.Available))
                throw new InvalidOperationException(outcome.Message);
            // Guard recovery does not establish the result of an earlier MSI/VC++ operation.
            // Leave that journal intact for preservation and later review.
        }
        finally
        {
            if (!await caller.ResetAsync()) throw new InvalidOperationException("恢复执行者尚未安全退出，证据保留。");
        }
    }

    public void Dispose() { _plugins.Dispose(); _backends.Dispose(); }
}
