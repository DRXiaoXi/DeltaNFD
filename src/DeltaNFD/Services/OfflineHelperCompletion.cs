using System.Diagnostics;

namespace DeltaNFD.Services;

/// <summary>
/// 助手进程退出后的完成确认：只有在拿到单实例租约（即助手确实不再持锁）时，
/// 才把登记的运行标记收敛为完成状态并清理 PID 记录。
/// </summary>
public static class OfflineHelperCompletion
{
    /// <summary>
    /// 若登记的助手已不再持有单实例租约，则按其登记的完成标记收敛状态并清空 PID；
    /// 仍持租约时不动状态。返回是否已执行收敛。
    /// </summary>
    public static bool TryConfirmExited(OfflineModeStateStore store)
    {
        if (!store.TryRead(out var state, out _)) return false;
        if (state.HelperProcessId is not int pid || state.HelperStartTimeUtcTicks is not long startTicks)
            return false;
        var helperPath = Path.Combine(AppContext.BaseDirectory, "OfflineHelper", "DeltaNFD.OfflineHelper.exe");
        if (IsSameLiveProcess(pid, startTicks, helperPath)) return false;
        using var exitedLease = OfflineModeHelperInstanceLease.TryAcquire(store, out _);
        if (exitedLease is null) return false; // 身份读取失败不能把仍持租约的助手当成已退出。

        var changed = false;
        var saved = store.TryUpdate(current =>
        {
            if (current.HelperProcessId != pid || current.HelperStartTimeUtcTicks != startTicks || current.HelperRunId != state.HelperRunId) return;
            if (!current.CancelRequested && current.Status != OfflineModeStatus.RestorePending)
            {
                current.Status = current.HelperOperationCompleted ? current.HelperCompletionStatus : OfflineModeStatus.Failed;
                current.Message = current.HelperOperationCompleted
                    ? current.Message + " 助手进程已退出，本轮状态已确认。"
                    : "脱机助手意外退出；本轮未确认完成，保留现有恢复记录。";
            }
            current.HelperProcessId = null;
            current.HelperStartTimeUtcTicks = null;
            current.HelperOperationCompleted = false;
            changed = true;
        }, out _);
        return saved && changed;
    }

    private static bool IsSameLiveProcess(int pid, long startTicks, string expectedPath)
    {
        try
        {
            var expected = GameTargetService.CanonicalPath(expectedPath);
            using var process = Process.GetProcessById(pid);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startTicks) return false;
            var actual = GameTargetService.CanonicalPath(GameTargetService.ReadProcessPath(pid));
            return actual.Equals(expected, StringComparison.OrdinalIgnoreCase) && !process.HasExited &&
                   process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch { return false; }
    }
}
