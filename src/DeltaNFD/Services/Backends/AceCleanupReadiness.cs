using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

internal static class AceCleanupReadiness
{
    internal static bool RequiresReview => BackendUnconfirmedStore.PendingModules().Contains(BackendModules.Ace)
        || BackendSessions.Snapshot().Any(c => c.ModuleId == BackendModules.Ace && c.RequiresAttention);

    internal static string DescribeBlocker() => !RequiresReview ? "" :
        "上次 ACE 清理仍有待核对的记录，尚未开始新的清理。请先核对当前 ACE 组件和旧执行者是否已退出。"
        + "\nACE 清理不可逆，不能自动还原；核对后选择「确认保留当前状态」会归档旧记录，再由你决定是否重新清理。"
        + "\n可点击「核对上次清理」，或到「设置 → 核对未确认操作」处理。";
}
