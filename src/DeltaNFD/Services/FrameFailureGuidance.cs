using System.Text.RegularExpressions;
using DeltaNFD.Services.Backends;

namespace DeltaNFD.Services;

internal sealed record FrameFailureAdvice(string Code, string Title, string Explanation, string Steps);

/// <summary>只生成处理指引；不改变功能开关、系统状态、错误结论或恢复门禁。</summary>
internal static class FrameFailureGuidance
{
    internal static IReadOnlyList<FrameFailureAdvice> ForResult(OperationResult result) =>
        (result.Success || result.IsSkipped) && !result.IsPartial && !result.OutcomeUnknown
            ? [] : ForMessage(result.Message + (result.OutcomeUnknown ? "\n操作结果未知" : ""));

    internal static IReadOnlyList<FrameFailureAdvice> ForMessage(string message)
    {
        var advice = new List<FrameFailureAdvice>();
        bool Has(params string[] values) => values.Any(v => message.Contains(v, StringComparison.OrdinalIgnoreCase));
        bool Win32(int code) => Regex.IsMatch(message, @"Win32(?:\s*错误)?\s*[:：]?\s*" + code + @"(?!\d)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var policy = Has("精度请求策略", "精度请求保留策略", "进程电源节流状态", "游戏节能状态", "ProcessPowerThrottling");
        if (policy && Win32(1))
            advice.Add(new("policy-unavailable", "进程精度或节能策略调用失败",
                "系统返回 Win32 1，相关功能尚未确认生效；仅凭此错误不能判定所有同版本 Windows 都不支持。",
                "1. 保存工作，正常退出游戏，再重启工具后重试，用于核对是否与游戏运行环境有关。\n"
                + "2. 若仍失败，请向作者提供工具版本、Windows 完整版本、此处技术详情和本次日志，需要继续排查程序兼容性。\n"
                + "3. 切换钟摆算法不能绕过这一步；请勿把其余功能已开启当作精度锁定成功。"));
        else if (policy && Win32(87))
            advice.Add(new("policy-parameters", "进程策略参数未被系统接受",
                "Win32 87 表示参数错误，不能直接作为系统不支持的证明。",
                "保留工具与 Windows 版本、技术详情和日志，交给作者核对参数及兼容性；反复切换算法不能修正策略调用参数。"));
        else if (policy && Win32(5))
            advice.Add(new("policy-access", "进程策略访问被拒绝",
                "相关进程的策略未能读取或修改，尚未确认该项功能生效。",
                "通过工具正常的管理员启动流程重试；若游戏进程仍被拒绝，请保留日志反馈作者，由作者核查权限或进程保护限制。"));
        else if (policy)
            advice.Add(new("policy-failed", "进程精度或节能策略未确认",
                "策略读取、写入或回读核验未完成，工具保留原始失败结论。",
                "正常退出游戏后重试；持续失败时提供此处技术详情与日志，核查具体调用阶段。"));

        if (Has("需重启", "重启后生效", "重启电脑后") && Has("全局精度", "全局生效精度"))
            advice.Add(new("clock-reboot", "全局精度设置需要重启确认",
                "注册表已写入不等于当前启动会话已经生效。",
                "保存工作后正常重启电脑，再到钟摆计划核对全局精度状态；状态就绪后重新扫描。"));
        if (Has("更细的请求", "被更细", "FinerRequest"))
            advice.Add(new("clock-overridden", "系统精度被其他更细请求覆盖",
                "持续重复申请钟摆目标值不能压过仍然有效的更细请求；当前读数也不能直接证明来源应用。",
                "先正常退出最近启动的应用并观察读数；确认是普通应用后，可在进程管理页使用定时器忽略列表，并核对实际生效结果。"));
        if (Has("待核对", "未确认操作", "连接中断", "结果未知", "禁止解除阻塞", "后端断线"))
            advice.Add(new("backend-review", "后端操作需要核对",
                "连接中断后无法仅凭界面判断操作是否完成，也不能直接重放旧请求。",
                "1. 打开“核对未确认操作”，刷新并核对原执行者是否已退出。\n"
                + "2. 有可信备份时按界面提供的范围恢复；仅在核对清楚后选择保留当前状态。\n"
                + "3. 若仍无法确认，保存工作后正常重启电脑，再返回核对；重启本身不代表设置已还原。"));
        if (Has("原值不完整", "旧电源记录", "电源备份", "原值记录不可读"))
            advice.Add(new("power-backup", "电源原值记录需要处理",
                "缺少可信原值时无法精确恢复，不能用默认值代替原值。",
                "使用帧格提供的旧记录核对入口；仅在确认当前电源状态后选择保留当前值。无法确认时保留记录并反馈日志。"));
        if (advice.Count == 0)
            advice.Add(new("unknown", "这项操作尚未完成",
                "现有信息不足以给出确定修复办法，请保留原始错误。",
                "查看下方技术详情并提供本次日志；若提示有旧操作，先在“设置 → 核对未确认操作”中处理，再重试对应功能。"));
        return advice;
    }

    internal static IReadOnlyList<FrameFailureAdvice> ForReview(BackendUnconfirmedStore.Review review)
    {
        if (review.Module is not (DeltaNFD.Backends.BackendModules.CpuFrame or DeltaNFD.Backends.BackendModules.Procman)) return [];
        var operation = review.Command == DeltaNFD.Backends.BackendCommands.CpuFramePowerUnlock
            ? "电源计划解锁请求没有取得可信的最终结果。" : "上一次帧格操作的最终状态需要核对。";
        return [new("frame-review", "建议处理",
            operation + (review.CanAcknowledge ? "原执行者已确认退出；退出不等于设置已恢复。" : "原执行者或关联进程尚未确认退出。"),
            (review.CanAcknowledge
                ? "1. 有可信恢复选项时，按备份恢复并等待复核；否则核对当前状态后，再决定是否保留。\n"
                : "1. 正常退出游戏，等待当前操作收尾，再点击“刷新并重新核对”。\n")
            + "2. 子进程名单是关联线索，请勿逐个强制结束桌面、系统或游戏进程；旧版可能因 PID 复用误收集名单，更新后重新核对。\n"
            + "3. 仍无法确认时保存工作并正常重启电脑，再回到此处核对；保留原记录与日志，重启不代表已经还原。")];
    }
}
