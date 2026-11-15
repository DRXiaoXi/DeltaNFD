namespace DeltaNFD.Services;

internal static class ClockValuePresentation
{
    internal static string Sleep(ClockSleepResult? result)
    {
        if (result is null) return "尚未进行单值 Sleep 测试。测试不会覆盖自动扫描结果或切换帧格使用值。";
        static string Ms(uint? ticks) => ticks is { } value && value > 0 ? (value / 10000.0).ToString("0.0000") + "ms" : "未读取";
        var text = $"最近测试 {result.CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {(result.Custom ? "手填值" : "自动最优值")}\n"
            + $"请求 {Ms(result.Requested100ns)}，授予 {Ms(result.Granted100ns)}；测量前 {Ms(result.Before100ns)} / 后 {Ms(result.After100ns)}。\n";
        if (result.Samples > 0 && result.MedianMs is { } median && result.MeanMs is { } mean && result.P95Ms is { } p95)
            text += $"Sleep(1) · {result.Samples} 个样本：中位 {median:0.0000}ms，平均 {mean:0.0000}ms，P95 {p95:0.0000}ms，"
                + $"最小 {result.MinMs:0.0000}ms，最大 {result.MaxMs:0.0000}ms；超过1.30ms {result.Over130Pct:0.0}%。\n";
        text += (result.TargetVerified ? "目标读数核对通过。" : "目标值未确认；样本不能当作所选分辨率的结果。")
            + (result.ReleaseConfirmed ? "临时请求已确认释放。" : "释放未确认，请先核对后端恢复状态。") + "\n" + result.Message;
        return text;
    }
}
