using DeltaNFD.Backends;

namespace DeltaNFD.Services;

/// <summary>ACE 清理结果的纯映射；执行侧和 IPC 前端使用相同的外层状态判定。</summary>
public static class AceCleanupResults
{
    public static OperationResult ToOperationResult(BackendResult result)
    {
        var data = result.Data is { } value ? BackendJson.Deserialize<OperationResult>(value) : null;
        var partial = result.Status == BackendResultStatuses.Partial || data?.IsPartial == true
            || result.Status != BackendResultStatuses.Ok && result.Items.Count > 0;
        var unknown = result.PendingRestore || data?.OutcomeUnknown == true;
        var success = result.Status == BackendResultStatuses.Ok && !partial && !unknown && (data?.Success ?? true);
        var message = string.IsNullOrWhiteSpace(result.Message) ? data?.Message ?? "ACE 清理未返回结果说明。" : result.Message;
        if (partial) message = "ACE 清理仅部分完成，不能视为已全部清理。\n" + message;
        var details = result.Items.Where(i => !string.IsNullOrWhiteSpace(i.Message)).Select(i => i.Message).ToArray();
        if (!success && details.Length > 0) message += "\n已完成项：" + string.Join("；", details);
        return new OperationResult
        {
            Success = success, IsPartial = partial, OutcomeUnknown = unknown, Message = message,
            IsSkipped = success && data?.IsSkipped == true,
            RequiresReboot = data?.RequiresReboot == true, RequiresLogoff = data?.RequiresLogoff == true,
            RequiresDisplayConfirm = data?.RequiresDisplayConfirm == true,
        };
    }
}
