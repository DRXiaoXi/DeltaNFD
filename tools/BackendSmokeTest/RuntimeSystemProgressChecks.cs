using System.Diagnostics;
using System.Text;
using DeltaNFD.Services;

internal static class RuntimeSystemProgressChecks
{
    private sealed class RecordingProgress : IProgress<string>
    {
        internal readonly List<string> Values = [];
        public void Report(string value) { lock (Values) Values.Add(value); }
        internal string[] Snapshot() { lock (Values) return Values.ToArray(); }
    }
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        foreach (var (line, expected) in new[] { ("[== 3.8% ]", 3.8), ("[=== 20.2% ]", 20.2),
            ("验证 100% 完成", 100d), ("進度 12,5％", 12.5), ("2\0.\05\0%\0", 2.5) })
            check(RuntimeSystemRepairProgress.ParsePercent(line) == expected, "系统工具实际百分比解析：" + line);
        foreach (var line in new[] { "", "10.0.26200.9457", "Error 0x800f081f", "[62.3]", "101%", "-1%", "1000%" })
            check(RuntimeSystemRepairProgress.ParsePercent(line) is null, "无效/无百分比输出不生成进度：" + line);
        var now = TimeSpan.Zero;
        var messages = new RecordingProgress();
        var display = new RuntimeSystemRepairProgress(RuntimeSystemRepairStep.ComponentStore, messages, () => now);
        check(display.Status().Contains("等待工具报告进度") && !display.Status().Contains("0%"), "初始化等待不伪造百分比");
        display.Observe("[== 20.2% ]");
        check(messages.Snapshot().Single().Contains("20.2%"), "实际百分比实时上报");
        now = TimeSpan.FromMinutes(2); display.Observe("20.2%");
        var stationary = display.Status();
        check(stationary.Contains("20.2%") && stationary.Contains("02:00 未收到新的百分比")
            && stationary.Contains("不等于卡死"), "重复输出不重置进度变化计时、不虚构增长");
        now = TimeSpan.FromMinutes(3); display.Observe("62.3%");
        check(display.Status().Contains("62.3%") && !display.Status().Contains("未收到新的百分比"), "收到新百分比后清除停留提示");
        display.Observe("100%");
        check(display.Status().Contains("100%") && display.Status().Contains("等待进程结束")
            && !display.Status().Contains("复检通过"), "100% 输出不等于进程成功退出或复检通过");
        var sfc = new RuntimeSystemRepairProgress(RuntimeSystemRepairStep.SystemFiles, null, () => now);
        sfc.Observe("Verification 50% complete");
        check(sfc.Status().Contains("2/3：SFC") && sfc.Status().Contains("50%"), "SFC 与 DISM 步骤独立，不继承旧进度");

        var diagnostics = new RuntimeRepairDiagnostics(root);
        var captured = new List<string>();
        using (var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("3.8%\r20.2%\rfinal\n"))))
            await diagnostics.CaptureOutputAsync(reader, "progress-cr-fixture.log", default, captured.Add);
        check(captured.SequenceEqual(new[] { "3.8%", "20.2%", "final" })
            && File.ReadAllText(Path.Combine(diagnostics.DirectoryPath, "progress-cr-fixture.log")).Contains("20.2%"),
            "回车刷新形式的百分比实时转发并完整归档");
        using (var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("one\ntwo\n"))))
            await diagnostics.CaptureOutputAsync(reader, "progress-bad-callback.log", default, _ => throw new IOException("fixture callback"));
        check(File.ReadAllLines(Path.Combine(diagnostics.DirectoryPath, "progress-bad-callback.log")).Length == 2,
            "显示回调失败不丢弃原始日志");

        var actualMessages = new RecordingProgress();
        var actualDisplay = new RuntimeSystemRepairProgress(RuntimeSystemRepairStep.ComponentStore, actualMessages);
        var runner = new RuntimeRepairProcessRunner(Path.Combine(root, "progress-child-runner"), () => false);
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("--runtime-progress-child");
        Task output = Task.CompletedTask;
        var result = await runner.RunAsync(info, "progress fixture", TimeSpan.FromSeconds(20), actualMessages, default,
            child => output = diagnostics.CaptureOutputAsync(child.StandardOutput, "progress-child.log", default, actualDisplay.Observe),
            actualDisplay.Status);
        await output;
        var actual = actualMessages.Snapshot();
        check(result.Success && actual.Any(s => s.Contains("3.8%")) && actual.Any(s => s.Contains("20.2%") && s.Contains("PID")),
            "自建子进程输出真实转发，5 秒心跳保留最新百分比而非覆盖为通用等待");
        check(!File.Exists(Path.Combine(root, "progress-child-runner", "runtime-repair-active.json")), "正常退出清除活动记录");
    }
}
