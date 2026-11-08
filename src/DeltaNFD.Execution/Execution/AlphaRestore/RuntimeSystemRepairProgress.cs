using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services.AlphaRestore;

/// <summary>只展示系统工具真实输出的单步百分比；等待时间不会转换为虚构进度。</summary>
internal sealed class RuntimeSystemRepairProgress(RuntimeSystemRepairStep step, IProgress<string>? progress,
    Func<TimeSpan>? elapsedForTests = null)
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double? _percent;
    private TimeSpan _lastChanged, _lastReport = TimeSpan.FromDays(-1);
    private TimeSpan Elapsed => elapsedForTests?.Invoke() ?? _clock.Elapsed;
    private string Stage => step == RuntimeSystemRepairStep.ComponentStore
        ? "强力修复 1/3：DISM 系统组件修复" : "强力修复 2/3：SFC 系统文件修复";

    internal static double? ParsePercent(string output)
    {
        var matches = Regex.Matches(output.Replace("\0", ""), @"(?<![\d.,-])(?<percent>\d{1,3}(?:[.,]\d+)?)\s*[%％]");
        if (matches.Count == 0) return null;
        var text = matches[^1].Groups["percent"].Value.Replace(',', '.');
        return double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            && value is >= 0 and <= 100 ? value : null;
    }
    internal void Observe(string output)
    {
        var value = ParsePercent(output);
        if (value is null) return;
        string? status = null;
        lock (_gate)
        {
            if (_percent == value) return;
            _percent = value;
            var now = Elapsed;
            _lastChanged = now;
            if (now - _lastReport >= TimeSpan.FromSeconds(1))
            { _lastReport = now; status = FormatLocked(now); }
        }
        if (status is not null) progress?.Report(status);
    }
    internal string Status()
    { lock (_gate) return FormatLocked(Elapsed); }

    private string FormatLocked(TimeSpan now)
    {
        var status = Stage + (_percent is { } percent ? $" · 工具报告 {percent.ToString("0.#", CultureInfo.InvariantCulture)}%" : " · 等待工具报告进度")
            + $" · 已用时 {(int)now.TotalMinutes:00}:{now.Seconds:00}";
        if (_percent is not null)
        {
            var unchanged = now - _lastChanged;
            if (unchanged >= TimeSpan.FromSeconds(15))
                status += $" · {(int)unchanged.TotalMinutes:00}:{unchanged.Seconds:00} 未收到新的百分比";
        }
        return status + "。系统工具可能耗时数十分钟，百分比停留不等于卡死；正在等待进程结束。"
            + "停止等待后工具可能继续运行，其结束前不能重试。";
    }
}
