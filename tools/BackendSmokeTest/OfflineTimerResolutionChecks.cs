using DeltaNFD.Native;

internal static class OfflineTimerResolutionChecks
{
    public static void Run()
    {
        Require(ProcessPerf.ConvertTimerResolution100nsToMilliseconds(5_000), 0.5, "0.5ms request");
        Require(ProcessPerf.ConvertTimerResolution100nsToMilliseconds(10_000), 1.0, "1ms resolution");
        Require(ProcessPerf.ConvertTimerResolution100nsToMilliseconds(156_250), 15.625, "15.625ms resolution");

        var diagnostic = new TimerResolutionRequestResult(true, 0, 5_000, 7_000, 1.0).DiagnosticText("unit");
        Require(diagnostic.Contains("请求=0.5ms/5000×100ns", StringComparison.Ordinal), "diagnostic should show the requested value");
        Require(diagnostic.Contains("NTSTATUS=0x00000000", StringComparison.Ordinal), "diagnostic should show the NTSTATUS return");
        Require(diagnostic.Contains("返回实际=0.7ms/7000×100ns", StringComparison.Ordinal), "diagnostic should show the API output value");
        Require(diagnostic.Contains("系统读数=1ms", StringComparison.Ordinal), "diagnostic should show the queried system value");

        Console.WriteLine("100ns → 毫秒换算与定时器诊断字段检查通过；未发起计时器请求。");
    }

    private static void Require(double actual, double expected, string name)
    {
        if (Math.Abs(actual - expected) > 0.000001)
            throw new InvalidOperationException($"{name} 换算错误：期望 {expected}ms，实际 {actual}ms。");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"{name}诊断字段缺失。");
    }
}
