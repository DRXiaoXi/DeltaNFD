using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class FrameFailureGuidanceChecks
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool ok, string why) { if (!ok) throw new IOException(why); checks++; }
        void Advice(string text, params string[] codes) => Check(FrameFailureGuidance.ForMessage(text)
            .Select(a => a.Code).SequenceEqual(codes), "错误类别或顺序不符：" + text);
        Advice("读取进程电源节流状态失败：Win32 错误 1；读取本进程精度请求策略失败（Win32 1）；读取游戏节能状态失败（Win32 1）", "policy-unavailable");
        Advice("读取本进程精度请求策略失败（Win32 87）", "policy-parameters");
        Advice("读取本进程精度请求策略失败（Win32 5）", "policy-access");
        Advice("精度请求保留策略回读不符", "policy-failed");
        Advice("读取本进程精度请求策略失败（Win32 10）", "policy-failed");
        Advice("安装程序失败：Win32 1", "unknown");
        Advice("全局精度已写入，需重启", "clock-reboot");
        Advice("系统精度被更细的请求覆盖", "clock-overridden");
        Advice("连接中断，结果未知，原值不完整", "backend-review", "power-backup");
        Advice("后端断线", "backend-review");
        Advice("完全未见过的错误", "unknown");
        var success = OperationResult.Ok("钟摆计划正常，未发生更细的请求");
        Check(FrameFailureGuidance.ForResult(success).Count == 0, "成功结果不误报处理建议");
        Check(FrameFailureGuidance.ForResult(new() { Success = true, IsPartial = true, Message = "后端断线" })
            .Any(a => a.Code == "backend-review"), "部分成功仍展示对应建议");
        Check(FrameFailureGuidance.ForResult(new() { Success = true, OutcomeUnknown = true, Message = "等待" })
            .Any(a => a.Code == "backend-review"), "结果未知标志优先于成功字段");
        Check(FrameFailureGuidance.ForResult(new() { IsSkipped = true, Message = "未执行" }).Count == 0, "安全跳过不报失败");
        Check(FrameFailureGuidance.ForResult(new() { IsSkipped = true, OutcomeUnknown = true }).Count > 0, "未知结果不能被跳过标志掩盖");
        var review = new BackendUnconfirmedStore.Review(BackendModules.CpuFrame, "fingerprint", "original", false)
            { Command = BackendCommands.CpuFramePowerUnlock, Reason = "disconnected", ChildrenAlive = true };
        var pending = FrameFailureGuidance.ForReview(review).Single();
        Check(pending.Explanation.Contains("电源计划解锁") && pending.Explanation.Contains("尚未确认退出"), "解锁断线说明对应操作和未知状态");
        Check(pending.Steps.Contains("勿逐个强制结束") && pending.Steps.Contains("PID 复用"), "异常名单不指引用户强杀系统进程");
        var exited = FrameFailureGuidance.ForReview(review with { CanAcknowledge = true }).Single();
        Check(exited.Explanation.Contains("退出不等于设置已恢复") && exited.Steps.Contains("可信恢复"), "确认退出仍不假报恢复");
        Check(FrameFailureGuidance.ForReview(review with { Module = BackendModules.Runtime }).Count == 0, "帧格指引不误用于运行库事务");
        Check(review.Description == "original" && !review.CanAcknowledge && review.ChildrenAlive, "生成建议不修改原始证据或门禁");
        Console.WriteLine($"Frame failure guidance: {checks} checks passed; no settings or system writes.");
    }
}
