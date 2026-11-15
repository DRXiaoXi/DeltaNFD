using DeltaNFD.Services;

namespace DeltaNFD.Execution.OptimalClock;

/// <summary>One bounded idle Sleep(1) batch, with a separate requestless process and exact timer witnesses.</summary>
internal sealed class ClockValueTester(IOptimalClockMeasurementApi api)
{
    internal const int SampleCount = 200;
    internal ClockSleepResult Test(uint target, bool custom, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (target is < 5000 or > 156250) throw new ArgumentOutOfRangeException(nameof(target));
        var sample = api.MeasureGlobal(target, SampleCount, 0);
        token.ThrowIfCancellationRequested();
        var valid = OptimalClockScanner.ValidSample(sample, SampleCount);
        var matched = valid && DpcAssistedMeasurement.MatchesCandidate(sample, target);
        return new()
        {
            Custom = custom, Requested100ns = target, TargetVerified = matched,
            Granted100ns = sample?.Granted100ns ?? 0, Current100ns = sample?.Current100ns ?? 0,
            Before100ns = sample?.ObservedBefore100ns, After100ns = sample?.ObservedAfter100ns,
            Samples = valid ? sample!.Samples : 0, MedianMs = valid ? sample!.Median : null,
            MeanMs = valid ? sample!.Mean : null, P95Ms = valid ? sample!.P95 : null,
            MinMs = valid ? sample!.Min : null, MaxMs = valid ? sample!.Max : null,
            Over130Pct = valid ? sample!.Over130Pct : null, CompletedUtc = DateTimeOffset.UtcNow,
            Message = matched ? "独立进程 Sleep(1) 测试完成；未施加 CPU 负载。"
                : valid ? "请求、授予或测量前后读数不一致；样本不能代表所选目标的 Sleep。" : "Sleep 样本缺失或无效，未形成可用结果。",
        };
    }
}
