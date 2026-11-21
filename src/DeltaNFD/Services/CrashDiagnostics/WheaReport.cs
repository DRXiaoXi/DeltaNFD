using System.Globalization;
using System.Text;

namespace DeltaNFD.Services.CrashDiagnostics;

internal static class WheaReport
{
    internal const string Provider = "Microsoft-Windows-WHEA-Logger";
    internal const string Filter = "Provider[@Name='" + Provider + "']";
    // WHEA-Logger manifest message IDs, not the generic Windows event level.
    private static string Kind(int id) => id switch
    {
        2 or 17 or 19 or 21 or 23 or 25 or 27 or 28 or 41 or 43 or 45 or 47 or 49 => "已纠正",
        18 or 29 or 40 or 42 or 44 or 46 or 48 => "严重",
        1 or 16 or 20 or 22 or 24 or 26 => "硬件错误（严重性未细分）",
        3 => "信息", _ => "未识别类型"
    };
    private static string Component(int id) => id switch
    {
        16 or 17 => "PCIe 链路或设备", 18 or 19 or 28 or 29 => "处理器核心",
        20 or 21 => "AMD 北桥", 22 or 23 or 46 or 47 or 48 or 49 => "内存子系统",
        24 or 25 or 42 or 43 => "PCI/PCI-X 总线", 26 or 27 or 44 or 45 => "PCI/PCI-X 设备",
        _ => "组件未细分"
    };
    internal static string Recent(CancellationToken token, CrashReport.ReadBatch? read = null, DateTimeOffset? clock = null)
    {
        var now = clock ?? DateTimeOffset.UtcNow;
        return Window(now.AddDays(-30), now, "近 30 天", token, read ?? CrashLogReader.Read);
    }
    internal static string Window(DateTimeOffset from, DateTimeOffset to, string scope, CancellationToken token, CrashReport.ReadBatch read)
    {
        token.ThrowIfCancellationRequested();
        var batch = read("System", Filter, from, to, 256, token);
        token.ThrowIfCancellationRequested();
        var rows = batch.Events.Where(e => e.Provider == Provider && e.Time >= from && e.Time <= to)
            .OrderByDescending(e => e.Time).ThenByDescending(e => e.Record).ToList();
        var sb = new StringBuilder($"WHEA 硬件记录 · 本机 System · {scope}\n");
        if (rows.Count == 0) sb.AppendLine(batch.Notice == null ? "此范围内未找到 WHEA 记录；这不等于硬件一定正常。" : "日志读取不完整，无法确认此范围内是否有 WHEA 记录。");
        else
        {
            sb.AppendLine("已读取 " + rows.Count + " 条：" + string.Join("；", rows.GroupBy(e => Kind(e.Id)).Select(g => $"{g.Key} {g.Count()} 条")) + "。");
            foreach (var e in rows.Take(12))
            {
                string location = e.Id is 18 or 19 && uint.TryParse(e["ApicId"], NumberStyles.None, CultureInfo.InvariantCulture, out var apic) ? $" · APIC ID {apic}" : "";
                sb.AppendLine($"• {e.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}　{Kind(e.Id)} · {Component(e.Id)}{location} · 事件 {e.Id}（#{e.Record}）");
            }
            if (rows.Count > 12) sb.AppendLine("仅列最近 12 条，汇总包含所有已读取记录。");
            sb.AppendLine("已纠正表示系统记录了可纠正的错误，不等于本次蓝屏原因。若同类错误反复出现或出现严重错误，可按时间和记录编号核对。报告组件不一定是损坏部件，事件也不能单独指认责任驱动。");
        }
        if (batch.Notice != null) sb.AppendLine("WHEA 读取限制：" + batch.Notice);
        return sb.ToString().TrimEnd();
    }
}
