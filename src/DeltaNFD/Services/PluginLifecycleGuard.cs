using DeltaNFD.Services.Plugins;

namespace DeltaNFD.Services;

/// <summary>
/// 插件生命周期拦截（规范第 7、8 节与待做清单第 9 项）：插件升级、宿主更新、卸载、
/// 模式或目标切换前统一处理——阻止新请求 → 查询排空 → 先 restore 再 stop → 复核已知进程退出。
/// 失败/未知阻止正常交接，保留包、数据、运行身份与恢复证据；不把删除文件冒充恢复。
/// </summary>
public static class PluginLifecycleGuard
{
    /// <summary>
    /// 交接前统一处理：所有受管理/已移交插件先恢复（若有待恢复备份）再停止。
    /// 返回空串表示可以继续交接；否则为阻止原因（面向用户，含失败明细）。
    /// </summary>
    public static async Task<string> PrepareHandoverAsync(PluginManagerService manager,
        PluginRuntimeService runtime, string what, bool restoreChanges = true, bool allowAutonomous = false)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(runtime);
        using var blocked = runtime.BlockNewOperations();
        if (runtime.HasInFlightOperations) return $"插件仍有在途操作或握手，已阻止{what}，请等待操作结束。";

        // Autonomous restore must use the existing authenticated process before its endpoint is stopped.
        if (restoreChanges)
        {
            if (!manager.Handoffs.TryList(out var autonomous, out var autonomousError)) return autonomousError;
            foreach (var record in autonomous)
            {
                if (!manager.Backups.TryListPending(record.PluginId, out var pending, out var pendingError)) return pendingError;
                if (pending.Count == 0) continue;
                if (!manager.Index.TryGet(record.PluginId, out var entry, out var entryError) || entry is null)
                    return "自主插件有待恢复记录但索引不可读，交接被阻止：" + entryError;
                var restored = await runtime.RestoreAsync(entry);
                if (!manager.Backups.TryListPending(record.PluginId, out var remaining, out var remainingError) || remaining.Count > 0)
                    return "自主插件恢复未确认，已阻止" + what + "：" + restored + " " + remainingError;
                if (!manager.TryConfirmRestored(record.PluginId, out var confirmationError)) return confirmationError;
            }
        }

        // 1. 排空已移交的自主插件（重连+身份复核+stop+确认退出）。
        if (allowAutonomous)
        {
            if (!manager.Handoffs.TryList(out var handed, out var readError)) return readError;
            if (handed.Any(e => e.State != PluginHandoffState.Autonomous)) return "存在未确认的插件移交，不能退出并宣称成功。";
        }
        var drainFailures = allowAutonomous ? Array.Empty<string>() : await runtime.DrainOfflinePluginsAsync();
        if (drainFailures.Count > 0)
            return $"以下插件无法确认停止，已阻止{what}（记录与证据保留，请人工处理）：{Environment.NewLine}" +
                string.Join(Environment.NewLine, drainFailures);

        // 2. 停止仍在运行的受管理插件（按操作/持续会话）。
        var stopFailed = await runtime.StopAllAsync();
        if (stopFailed.Count > 0)
            return $"以下插件未能停止，已阻止{what}：{string.Join("、", stopFailed)}";

        // 3. 有待恢复备份的插件：先恢复再允许交接（不把删除文件当恢复；恢复失败保留记录）。
        if (!manager.Backups.TryList(out var backups, out var backupError))
            return $"读取插件备份仓库失败，已阻止{what}：{backupError}";
        if (!restoreChanges) return runtime.RunningPluginIds.Count == 0 ? "" : "仍有插件运行，退出未确认。";
        var pendingByPlugin = backups
            .Where(b => b.Status is PluginBackupStatus.PendingRestore or PluginBackupStatus.Restoring or PluginBackupStatus.RestoreFailed)
            .GroupBy(b => b.PluginId)
            .ToList();
        foreach (var group in pendingByPlugin)
        {
            if (!manager.Index.TryGet(group.Key, out var entry, out var entryError) || entry is null)
                return $"插件“{group.Key}”有待恢复备份但索引不可读（{entryError}），已阻止{what}；请先人工恢复。";
            var summary = await runtime.RestoreAsync(entry);
            if (!manager.Backups.TryListPending(group.Key, out var remaining, out var remainingError) || remaining.Count > 0)
                return $"插件“{entry.Name}”（{entry.Id}）存在未完成的恢复：{Environment.NewLine}{summary}{Environment.NewLine}已阻止{what}；备份记录保留。";
            if (!manager.TryConfirmRestored(group.Key, out var confirmationError))
                return "恢复状态未确认，交接被阻止：" + confirmationError;
        }

        // 4. 复核：运行表必须已空（已知进程全部退出）。
        if (runtime.RunningPluginIds.Count > 0)
            return $"以下插件仍有运行记录未清理，已阻止{what}：{string.Join("、", runtime.RunningPluginIds)}";
        if (!manager.Index.TryRead(out var indexed, out var indexError)) return "插件索引不可读，交接被阻止：" + indexError;
        if (indexed.Any(e => e.State == PluginPackageState.PendingRestore))
            return "插件仍标记有待恢复/未知结果，交接被阻止；请确认全部记录后处理状态。";

        return "";
    }
}
