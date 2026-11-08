using DeltaNFD.Services;

namespace DeltaNFD.Execution.OptimalClock;

/// <summary>Win10 实验准入：独立持有与无请求测量，A/B/A 检验实际等待是否响应。</summary>
internal static class PendulumGlobalProbe
{
    internal static OperationResult Verify(IOptimalClockMeasurementApi api, CancellationToken token,
        Action<string, object?>? diagnostics = null)
    {
        var results = new List<OptimalClockSample>();
        foreach (var resolution in new uint[] { 10_000, 40_000, 10_000 })
        {
            token.ThrowIfCancellationRequested();
            var sample = api.MeasureGlobal(resolution, 61, 0);
            PendulumDiagnosticLog.Emit(diagnostics, "compatibility-probe", new { resolution, sample });
            if (!OptimalClockScanner.ValidSample(sample, 61))
                return OperationResult.Fail("跨进程兼容检测未完成：独立测量缺失或无效；未启用新的全局锁定。请查看钟摆日志后重试。");
            if (sample!.Requested100ns != resolution || sample.Granted100ns == 0
                || Math.Abs((long)sample.Current100ns - resolution) > 50
                || sample.ObservedBefore100ns is not { } before || Math.Abs((long)before - resolution) > 50
                || sample.ObservedAfter100ns is not { } after || Math.Abs((long)after - resolution) > 50)
                return OperationResult.Fail("跨进程兼容检测受其他精度请求或系统限制影响，无法核实全局生效。请暂时退出其他计时器工具及申请高精度的应用后重试；本次未授权新的锁定。");
            results.Add(sample);
        }
        var fine = Math.Max(results[0].Median, results[2].Median);
        var coarse = results[1].Median;
        var effective = fine <= 3.0 && coarse is >= 3.0 and <= 10.0
            && coarse - fine >= 0.75 && Math.Abs(results[0].Median - results[2].Median) <= 1.0;
        PendulumDiagnosticLog.Emit(diagnostics, "compatibility-result", new { effective, fine, coarse });
        return effective
            ? OperationResult.Ok("跨进程兼容检测通过：独立无请求进程的等待随请求变化；继续扫描最佳值。")
            : OperationResult.Fail("兼容检测未确认跨进程生效：系统分辨率变化不能证明其他进程受益。已释放检测请求，不保存全局锁定值；可查看日志，核对系统版本与其他精度工具。");
    }
}
