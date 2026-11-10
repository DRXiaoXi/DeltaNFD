using System.Text.Json;
using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

internal sealed record BackendRecoveryAdvice(string Title, string Explanation, string Steps);

/// <summary>Read-only presentation of existing recovery choices; never changes evidence or authorizes a write.</summary>
internal static class BackendRecoveryGuidance
{
    private const string FeedbackStep = "仍无法处理时，展开预览并复制下方诊断摘要，在“设置 → 反馈 BUG”中粘贴并附带本次日志。请保留工具数据与原始备份，等待对应修复。";
    private const string RestartStep = "相关安装、卸载或修复已结束且已保存工作后，可手动使用 Windows 的“重启”，再到“设置 → 核对未确认操作”复查。重启不代表设置已还原。";

    internal static BackendRecoveryAdvice ForReview(BackendUnconfirmedStore.Review review,
        ReconcileSnapshot? snapshot, string readError = "")
    {
        if (string.IsNullOrWhiteSpace(review.Fingerprint))
            return Advice("操作记录无法读取", "当前无法可靠识别原操作，不能按猜测恢复或清除记录。",
                "先保留现场，勿删除工具数据、备份或用其他版本覆盖原始记录。", FeedbackStep);
        if (review.ChildrenAlive)
            return Advice("关联程序仍未结束", "后台操作关联的程序仍在运行，此时不能开始恢复或解除阻塞。",
                "等待当前安装或修复完成；游戏或其他设置工具请通过各自界面正常退出，再点击“刷新并重新核对”。",
                "名单只用于核对关联，请勿按名单强制结束系统、游戏或安装进程。", FeedbackStep);
        if (review.ChildrenUnknown || !review.CanAcknowledge && !review.CanRestore)
            return Advice("执行者状态尚未确认", "原后端或关联程序的退出状态不完整，继续等待本身不会解决无法读取的问题。",
                "先正常退出游戏和其他设置工具，等待当前操作收尾，再点击“刷新并重新核对”。", RestartStep, FeedbackStep);
        if (BackendRecovery.IsRuntimeInstallation(review))
            return Advice("运行库安装需要专门核对", "安装或修复事务不能用普通设置备份代替核对。",
                "等待安装程序结束，使用下方“核对结束并恢复可用”。若提供“建立重启核对点”，先建立核对点，再按提示手动重启并回来复查。",
                "没有这些选项或仍核对失败时，请保留原事务，不要反复执行安装或卸载。", FeedbackStep);
        if (BackendRecovery.IsRuntimeFileReplacement(review))
            return Advice("系统文件替换需要核对原始备份", "普通运行库防护恢复不能还原系统 DLL 替换。",
                "按本次替换备份目录中的说明恢复并核对文件及重启排程；无法确认备份时停止操作。",
                "仅在确认文件状态和相关程序均结束后，才考虑界面提供的“确认保留当前状态”。", FeedbackStep);
        if (!string.IsNullOrWhiteSpace(readError)) return ForUnavailable();
        var scopes = BackendRecovery.RecoveryChoices(review, snapshot);
        if (scopes.Count > 0)
            return Advice("有可信备份可恢复", "当前已提供可用恢复范围，仍需等待恢复后的复核结果。",
                "确认下方恢复范围后点击“按可信备份恢复”，等待显示“恢复已复核通过”。",
                "复核失败时保留原记录，复制诊断摘要反馈；请勿连续重复原操作或把部分恢复当作完成。", FeedbackStep);
        if (BackendRecovery.CanKeepCurrentState(review, snapshot))
            return Advice("没有可用的自动恢复范围", "当前不能根据可信备份自动还原这次操作。原执行者退出不代表设置已经恢复。",
                "先到对应功能页或系统、驱动界面核对实际状态；只有你确认当前状态可以接受时，才选择“确认保留当前状态”。",
                "该选择会归档旧记录并保留当前设置，不会还原设置。无法判断时请选择“暂不处理”。", FeedbackStep);
        return ForUnavailable();
    }

    internal static BackendRecoveryAdvice ForUnavailable() => Advice("核对暂时无法完成",
        "当前没有足够信息提供可信恢复方案，请先保留错误与备份。",
        "先点击“重新核对”尝试读取当前状态。若提示文件缺失或版本不一致，请使用完整版本目录，保留原工具数据后再核对。",
        RestartStep, FeedbackStep);

    private static BackendRecoveryAdvice Advice(string title, string explanation, params string[] steps) =>
        new(title, explanation, string.Join("\n", steps.Select((step, index) => $"{index + 1}. {step}")));

    internal static string Diagnostic(BackendUnconfirmedStore.Review? review, ReconcileSnapshot? snapshot,
        string error = "")
    {
        // Sanitize before limiting fields so a truncated secret is never exposed by the copy action.
        static string Safe(string? value, int limit = 4000)
        {
            var text = BugFeedbackService.Redact(value ?? "");
            return text.Length <= limit ? text : text[..limit] + "\n[内容已截短]";
        }
        var matchingSnapshot = review is not null && snapshot?.Module == review.Module ? snapshot : null;
        return JsonSerializer.Serialize(new
        {
            reportType = "backend-recovery-diagnostic", appVersion = AppVersion.Text,
            osVersion = Environment.OSVersion.VersionString,
            module = Safe(review?.Module, 128), command = Safe(review?.Command, 256),
            phase = Safe(review?.Phase, 128), request = Safe(review?.RequestId, 128),
            session = Safe(review?.SessionId, 128), evidenceFingerprint = Safe(review?.Fingerprint, 128),
            reason = Safe(review?.Reason, 1000),
            canAcknowledgeRecord = review?.CanAcknowledge, canReadRecovery = review?.CanRestore,
            childrenAlive = review?.ChildrenAlive, childrenUnknown = review?.ChildrenUnknown,
            stateReadComplete = matchingSnapshot?.Complete,
            availableRecoveryScopes = review is null || error.Length > 0 ? Array.Empty<string>()
                : BackendRecovery.RecoveryChoices(review, matchingSnapshot).Select(s => Safe(s.Id, 128)).Take(32).ToArray(),
            keepCurrentAllowedByPolicy = review is not null && BackendRecovery.CanKeepCurrentState(review, matchingSnapshot),
            error = Safe(error), originalRecordSummary = Safe(review?.Description),
            note = "仅用于诊断；不表示修改已还原。请预览后决定是否反馈；生成和复制不会上传或处理原始记录。",
        }, new JsonSerializerOptions { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
