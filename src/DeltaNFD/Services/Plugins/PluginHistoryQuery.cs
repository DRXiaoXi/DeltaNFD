namespace DeltaNFD.Services.Plugins;

public static class PluginHistoryQuery
{
    public static string StatusKey(PluginLogEntry entry) => entry.Status switch
    {
        "ok" or "success" => "success",
        "partial" or "partial_success" => "partial",
        "pending_restore" => "pending_restore",
        "failed" or "blocked" => "failed",
        "cancelled" => "cancelled",
        "unknown" => "unknown",
        "" => "started",
        _ => "unknown",
    };

    public static string StatusText(PluginLogEntry entry) => StatusKey(entry) switch
    {
        "success" => "成功", "partial" => "部分成功", "pending_restore" => "待恢复",
        "failed" => "失败 / 被阻止", "cancelled" => "取消", "started" => "请求开始（未确认终态）", _ => "结果未知",
    };

    public static IReadOnlyList<PluginLogEntry> Filter(PluginHistorySnapshot snapshot, string? pluginId, string? status) =>
        snapshot.Entries.Where(e => (string.IsNullOrEmpty(pluginId) || e.PluginId == pluginId) &&
            (string.IsNullOrEmpty(status) || StatusKey(e) == status)).ToArray();

    public static IReadOnlyList<PluginLogEntry> Page(IReadOnlyList<PluginLogEntry> filtered, int offset, int count)
    {
        if (offset < 0 || count is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(count));
        return filtered.Skip(offset).Take(count).ToArray();
    }
}
