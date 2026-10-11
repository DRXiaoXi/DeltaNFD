using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>
/// 官方后端生命周期协调（C5）：退出、更新、卸载、目标切换前统一排空后端，
/// 只有确认退出后才允许交接；排空失败必须阻止交接，不得当作已完成。
/// </summary>
internal static class BackendLifecycle
{
    public static async Task<string> AcknowledgeCurrentStateAsync(BackendUnconfirmedStore.Review confirmed)
    {
        // 只检查「结果未知且未确认退出」的会话；正常只读会话不阻止人工核对。
        var attention = BackendSessions.Snapshot().Where(c => c.ModuleId == confirmed.Module && c.RequiresAttention).ToArray();
        if (attention.Any(c => !c.ExitConfirmed)) throw new InvalidOperationException("该模块仍有结果未知的活动会话，禁止解除阻塞。");
        var archive = BackendUnconfirmedStore.AcknowledgeCurrentState(confirmed);
        foreach (var client in attention) await client.ReleaseAcknowledgedSessionAsync();
        Log.Warn("玩家已核对并选择保留当前状态；不是自动恢复：module=" + confirmed.Module + "；证据=" + archive);
        return archive;
    }
    /// <summary>排空全部后端并返回未确认退出者（空 = 全部排空）。异步版本供 UI 调用。</summary>
    public static async Task<IReadOnlyList<string>> PrepareHandoverAsync(string reason, TimeSpan? perBackendTimeout = null)
    {
        using var blocked = BackendOperationGate.BlockNewOperations();
        using var control = BackendOperationGate.AllowControlOperations();
        
        // 修复：为交接锁添加超时保护，避免永久阻塞
        using var handoverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await BackendOperationGate.Handover.WaitAsync(handoverTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Error("获取交接锁超时（30秒），可能存在死锁或资源泄漏，已阻止交接");
            return BackendModules.All.Keys.ToArray();
        }
        
        try
        {
        using var starts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await BackendOperationGate.WaitForStartsAsync(starts.Token); }
        catch (OperationCanceledException) { Log.Warn("官方后端启动尚未收尾，交接暂缓。"); return BackendModules.All.Keys.ToArray(); }
        foreach (var module in BackendUnconfirmedStore.PendingModules())
            BackendUnconfirmedStore.ArchiveCompletedStaticBackup(module);
        if (File.Exists(Path.Combine(AppDataPaths.Root, "defender-session.json"))
            && !BackendSessions.Snapshot().Any(c => c.ModuleId == BackendModules.System && !c.ExitConfirmed)
            && !BackendUnconfirmedStore.HasLiveBackend(BackendModules.System))
        {
            try { await BackendClient.StartAsync(BackendModules.System, CancellationToken.None); }
            catch (Exception ex)
            {
                Log.Warn("Defender 临时状态恢复后端未能启动，交接暂缓：" + ex.Message);
                return BackendUnconfirmedStore.PendingModules().Append(BackendModules.System).Distinct().ToArray();
            }
        }
        if (!BackendSessions.AnyActive)
        {
            return BackendUnconfirmedStore.PendingModules();
        }

        Log.Info($"前后端分离：{reason}前开始排空官方后端。");
        foreach (var client in BackendSessions.Snapshot().Where(c => c.ModuleId == BackendModules.Procman && c.IsUsable && !c.RequiresAttention))
        {
            try
            {
                var result = await client.InvokeAsync(BackendCommands.ProcmanRestoreAll, new { }, null, CancellationToken.None);
                if (result.Status != BackendResultStatuses.Ok) Log.Warn("进程管理还原未完成，后续停止保留证据：" + result.Message);
            }
            catch (Exception ex) { Log.Warn("进程管理交接还原失败：" + ex.Message); }
        }
        foreach (var client in BackendSessions.Snapshot().Where(c => c.ModuleId == BackendModules.CpuFrame && c.IsUsable && !c.RequiresAttention))
        {
            try
            {
                using var restoreTimeout = new CancellationTokenSource(perBackendTimeout ?? BackendModules.TimeoutFor(BackendModules.CpuFrame, BackendCommands.CpuFrameRestore));
                var restored = await client.InvokeAsync(BackendCommands.CpuFrameRestore, new { }, null, restoreTimeout.Token);
                if (restored.Status != BackendResultStatuses.Ok || restored.PendingRestore || !client.CpuRestoreConfirmed)
                {
                    Log.Warn("CPU/帧格还原未确认，阻止交接：" + restored.Message);
                    client.RetainCpuRestoreFailure();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("CPU/帧格还原调用失败，阻止交接：" + ex.Message);
                client.RetainCpuRestoreFailure();
            }
        }
        // Evidence blocks handing off, not sending stop. Stop active workers first so review can proceed.
        var failed = (await BackendSessions.DrainAllAsync(perBackendTimeout))
            .Concat(BackendUnconfirmedStore.PendingModules()).Distinct().ToArray();
        if (failed.Length > 0)
        {
            Log.Warn($"前后端分离：{reason}前后端未确认退出：{string.Join("、", failed)}");
        }
        else
        {
            Log.Info($"前后端分离：{reason}前排空完成。");
        }

        return failed;
        }
        finally { BackendOperationGate.Handover.Release(); }
    }

    internal static async Task<IReadOnlyList<BackendUnconfirmedStore.Review>> PrepareReviewAsync(IReadOnlyCollection<string>? modules = null)
    {
        // The review entry point can itself stop workers; it never depends on a prior exit attempt.
        if (modules is null) await PrepareHandoverAsync("核对操作");
        else
        {
            using var blocked = BackendOperationGate.BlockNewOperations();
            using var control = BackendOperationGate.AllowControlOperations();
            await BackendOperationGate.Handover.WaitAsync();
            try
            {
                using var starts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await BackendOperationGate.WaitForStartsAsync(starts.Token);
                foreach (var client in BackendSessions.Snapshot().Where(c => modules.Contains(c.ModuleId)))
                {
                    try { await client.QuiesceAsync(BackendSessions.StopTimeoutFor(client.ModuleId)); }
                    catch (Exception ex) { Log.Warn("核对前停止模块失败，保留证据：" + client.ModuleId + "；" + ex.Message); }
                }
            }
            finally { BackendOperationGate.Handover.Release(); }
        }
        foreach (var module in BackendUnconfirmedStore.PendingModules().Where(m => modules is null || modules.Contains(m)))
        {
            try { BackendUnconfirmedStore.RefreshChildren(module); }
            catch (Exception ex) { Log.Warn("重新核对子进程失败，保留证据：" + ex.Message); }
        }
        return BackendUnconfirmedStore.ReviewAll().Where(r => modules is null || modules.Contains(r.Module)).ToArray();
    }

    internal static async Task ReleaseRecoveredSessionsAsync(string module)
    {
        foreach (var client in BackendSessions.Snapshot().Where(c => c.ModuleId == module && c.RequiresAttention && c.ExitConfirmed))
            await client.ReleaseAcknowledgedSessionAsync();
    }

    /// <summary>进程退出时的同步兜底排空（有界等待，失败只记录）。</summary>
    public static void DrainOnExit(TimeSpan timeout)
    {
        try
        {
            var task = Task.Run(() => PrepareHandoverAsync("退出", timeout));
            if (!task.Wait(timeout + TimeSpan.FromSeconds(2)))
            {
                Log.Warn("前后端分离：退出排空超时，不能确认剩余后端已退出；保留证据，禁止宣称交接完成。");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("前后端分离：退出排空异常：" + ex.Message);
        }
    }

    /// <summary>把排空结果转成交接阻塞说明；空字符串表示可以继续。</summary>
    public static string DescribeBlocker(IReadOnlyList<string> failed, string action)
    {
        if (failed.Count == 0) return "";
        var pending = BackendUnconfirmedStore.PendingModules();
        if (failed.Any(pending.Contains))
            return $"以下模块有待核对的操作记录（{string.Join("、", failed.Select(ModuleDisplayName))}），已阻止{action}。"
                + "这不等于后端仍在运行。请在设置中点击「核对未确认操作」查看操作、原因及恢复证据；单纯等待不会清除记录。";
        return $"以下官方后端未确认退出（{string.Join("、", failed.Select(ModuleDisplayName))}），已阻止{action}。请稍后重试。";
    }

    public static string ModuleDisplayName(string moduleId) =>
        BackendModules.All.TryGetValue(moduleId, out var spec) ? spec.DisplayName : moduleId;
}
