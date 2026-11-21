using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;

namespace DeltaNFD.Services.CrashDiagnostics;

internal sealed record CrashBatch(List<CrashEvent> Events, string? Notice = null);
internal static class CrashLogReader
{
    internal const string Anchors = "((Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001) or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or (Provider[@Name='EventLog'] and EventID=6008))";
    internal const string AppReports = "(Provider[@Name='Windows Error Reporting'] and EventID=1001)";
    internal const string Timeline = "(EventID=1 or EventID=7 or EventID=17 or EventID=18 or EventID=19 or EventID=20 or EventID=41 or EventID=46 or EventID=47 or EventID=51 or EventID=129 or EventID=153 or EventID=161 or EventID=1001 or EventID=4101 or EventID=6008)";
    internal static string Query(string filter, DateTimeOffset from, DateTimeOffset to)
    {
        static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
        var extra = filter == AppReports ? " and EventData[Data[@Name='EventName']='BlueScreen']" : "";
        return $"*[System[{filter} and TimeCreated[@SystemTime >= '{Utc(from)}' and @SystemTime <= '{Utc(to)}']]{extra}]";
    }

    public static CrashBatch Read(string channel, string filter, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken token)
    {
        List<CrashEvent> rows = []; List<string> notes = [];
        try
        {
            token.ThrowIfCancellationRequested();
            var query = new EventLogQuery(channel, PathType.LogName, Query(filter, from, to)) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            using var registration = token.Register(() => { try { reader.CancelReading(); } catch (EventLogException) { } });
            for (var i = 0; ; i++)
            {
                token.ThrowIfCancellationRequested();
                using var record = reader.ReadEvent();
                if (record == null) break;
                if (i == limit) { notes.Add($"{channel} 达到 {limit} 条读取上限，较早记录可能未显示。"); break; }
                try { rows.Add(CrashEvent.Parse(record.ToXml())); }
                catch (Exception ex) when (ex is FormatException or OverflowException or XmlException) { if (notes.Count == 0) notes.Add($"{channel} 有记录格式无法解析。"); }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or EventLogException)
        {
            notes.Add(ex is UnauthorizedAccessException or SecurityException ? $"没有读取 {channel} 日志的权限。" : $"{channel} 日志查询未完成（{ex.GetType().Name}）。");
        }
        token.ThrowIfCancellationRequested();
        return new(rows, notes.Count == 0 ? null : string.Join("\n", notes));
    }
}

internal static class CrashReport
{
    internal delegate CrashBatch ReadBatch(string channel, string filter, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken token);
    public static string Analyze(string debugger, CancellationToken token, ReadBatch? read = null, DateTimeOffset? clock = null)
    {
        read ??= CrashLogReader.Read;
        var now = clock ?? DateTimeOffset.UtcNow;
        List<string> notes = [];
        List<CrashEvent> Get(string channel, string filter, DateTimeOffset from, DateTimeOffset to, int limit)
        {
            var batch = read(channel, filter, from, to, limit, token);
            if (batch.Notice != null) notes.Add(batch.Notice);
            return batch.Events;
        }
        var records = Get("System", CrashLogReader.Anchors, now.AddDays(-30), now, 192).OrderByDescending(e => e.Time).ThenByDescending(e => e.Record).ToList();
        var crash = records.Select(CrashAnalysis.Identify).FirstOrDefault(c => c != null);
        bool unreadable = records.Any(e => e is { Provider: CrashAnalysis.Wer, Id: 1001 } && CrashAnalysis.Identify(e) == null);
        if (unreadable) notes.Add("存在错误检查报告，但部分停止代码未能解析。");
        string Finish(string text) => notes.Count == 0 ? text : text + "\n\n读取限制\n" + string.Join("\n", notes.Distinct());
        static string Local(CrashEvent e) => e.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
        if (crash == null)
        {
            string finding = records.Count > 0 ? $"最近记录：{Local(records[0])} · {records[0].Provider} / {records[0].Id}。没有可核对的非零停止代码，不能据此确认蓝屏原因或驱动。"
                : notes.Count == 0 ? "近 30 天未找到蓝屏或异常重启记录。日志可能已轮转或清空。" : "日志读取不完整，无法判断是否发生过蓝屏。";
            return Finish(finding + "\n若曾看到蓝屏，可核对 Windows\\Minidump 中对应时间的转储。\n\n" + WheaReport.Recent(token, read, now));
        }
        var anchor = crash.Event;
        if (records.Any(r => r.Time > anchor.Time && CrashAnalysis.Identify(r) == null)) notes.Add("另有更新的记录未提供可解析的停止代码；本报告不将两次记录视为同一故障。");
        var sb = new StringBuilder().AppendLine($"最近可确认的蓝屏 · 0x{crash.Code:X8}").AppendLine($"记录时间：{Local(anchor)} · System / {anchor.Provider} / {anchor.Id}（#{anchor.Record}）")
            .AppendLine(CrashAnalysis.Meaning(crash.Code));
        if (crash.Args.Length == 4) sb.AppendLine("参数：" + string.Join("，", crash.Args.Select(a => $"0x{a:X}")));
        if (anchor["param2"] is { Length: > 0 } path && Path.GetFileName(path) is { } dump && System.Text.RegularExpressions.Regex.IsMatch(dump, @"\A[\w.-]{1,120}\.[dD][mM][pP]\z")) sb.AppendLine($"事件记录的转储：{dump}（未打开或验证文件）");
        sb.AppendLine().AppendLine("驱动线索");
        var drivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (crash.ReportId != Guid.Empty)
            foreach (var report in Get("Application", CrashLogReader.AppReports, now.AddDays(-30), now, 128))
                if (CrashAnalysis.BucketDriver(crash, report) is { } module && drivers.Add(module)) sb.AppendLine($"• {module} — 同一报告 ID 的 Windows 错误报告点名了该映像。");
        var (driver, note) = CrashAnalysis.DebuggerDriver(crash, debugger);
        if (driver != null) { drivers.Add(driver); sb.AppendLine($"• {driver} — 所粘贴 WinDbg 文本的 IMAGE_NAME，停止代码和四参数一致。请确认使用的是本次转储。"); }
        if (note != null) sb.AppendLine(note);
        sb.AppendLine(drivers.Count switch { 0 => "现有证据没有指向具体驱动。若要继续，可查看所选转储中是否包含可用的驱动模块和调用栈；本页不会猜测。", 1 => "这是排查对象，不是根因定论。系统框架或被其他驱动破坏的模块也可能被点名；请结合调用栈核对。", _ => "报告点名了不同的模块，不能确定责任驱动；请核对原始转储与调用栈。" });
        sb.AppendLine().AppendLine("相关系统事件 · 记录时间前后 10 分钟");
        var start = anchor.Time.AddMinutes(-10); var end = anchor.Time.AddMinutes(10);
        var events = Get("System", CrashLogReader.Timeline, start, end > now ? now : end, 256)
            .Where(e => e.Time >= start && e.Time <= end && e.Record != anchor.Record && CrashAnalysis.Context(e) != null)
            .OrderBy(e => (e.Time - anchor.Time).Duration()).ThenBy(e => e.Record).ToList();
        if (CrashAnalysis.Corroboration(crash.Code, events) is { } hint) sb.AppendLine("交叉核对：" + hint);
        if (events.Count == 0) sb.AppendLine("在已读取的记录中未见其他匹配事件。");
        foreach (var e in events.Take(12).OrderBy(e => e.Time)) sb.AppendLine($"• {Local(e)}  {e.Provider} / {e.Id}：{CrashAnalysis.Context(e)}");
        if (events.Count > 12) sb.AppendLine($"共匹配 {events.Count} 条，仅列距记录时间最近的 12 条。");
        sb.Append("记录时间可能是重启后的补记时间，不一定是蓝屏时刻。相邻事件仅供对照，不作为驱动归因依据。");
        sb.AppendLine().AppendLine().Append(WheaReport.Window(start, end > now ? now : end, "蓝屏记录时间前后 10 分钟", token, read));
        return Finish(sb.ToString());
    }

    public static string AnalyzeDump(KernelDump dump, CancellationToken token, ReadBatch? read = null)
    {
        read ??= CrashLogReader.Read;
        token.ThrowIfCancellationRequested();
        var when = dump.CrashTime ?? new DateTimeOffset(File.GetLastWriteTimeUtc(dump.Path));
        List<string> notes = [];
        CrashBatch Get(string channel, string filter, int limit)
        {
            var batch = read(channel, filter, when.AddMinutes(-10), when.AddMinutes(10), limit, token);
            if (batch.Notice is not null) notes.Add(batch.Notice);
            return batch;
        }
        static string Local(DateTimeOffset e) => e.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
        var basename = Path.GetFileName(dump.Path);
        var nearby = Get("System", CrashLogReader.Anchors, 192).Events;
        bool Match(CrashCase c) => c.Code == dump.Code && c.Args.Length == 4 && c.Args.SequenceEqual(dump.Args)
            && string.Equals(Path.GetFileName(c.Event["param2"]), basename, StringComparison.OrdinalIgnoreCase);
        var matches = nearby.Select(CrashAnalysis.Identify).OfType<CrashCase>().Where(Match).Take(2).ToArray();
        var correlated = matches.Length == 1 ? matches[0] : null;
        var sb = new StringBuilder().AppendLine($"转储分析 · 0x{dump.Code:X8}")
            .AppendLine($"文件：{basename}（仅分析固定文件头；未加载转储内存页）")
            .AppendLine($"格式：{dump.Format} · {dump.Architecture}")
            .AppendLine(dump.CrashTime is { } exact ? $"转储时间：{Local(exact)}（文件头）" : $"转储时间：{Local(when)}（文件修改时间，仅作近似）")
            .AppendLine(CrashAnalysis.Meaning(dump.Code))
            .AppendLine("参数：" + string.Join("，", dump.Args.Select(a => $"0x{a:X}")))
            .AppendLine().AppendLine("驱动线索")
            .AppendLine("此轻量本地解析器不含符号引擎，也不从内存/调用栈反推责任模块；转储头只证明停止代码及参数，不能单独证明某个驱动有责。");
        var drivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (correlated is not null)
        {
            sb.AppendLine("系统日志交叉核对：转储文件名、停止代码及四个参数均匹配系统错误报告。此匹配仍是佐证，不替代转储内存和调用栈分析。");
            if (correlated.ReportId != Guid.Empty)
            {
                var apps = read("Application", CrashLogReader.AppReports, when.AddMinutes(-10), when.AddMinutes(10), 128, token);
                if (apps.Notice is not null) notes.Add(apps.Notice);
                foreach (var report in apps.Events)
                    if (CrashAnalysis.BucketDriver(correlated, report) is { } module && drivers.Add(module)) sb.AppendLine($"• {module} — 与本次错误检查报告 ID 匹配的 Windows 错误报告映像；是线索，不是根因定论。");
            }
        }
        else sb.AppendLine("本机附近日志中没有唯一匹配转储文件名、停止代码和四个参数的记录；不引用其他报告的驱动名。");
        if (drivers.Count == 0) sb.AppendLine("没有可确认的责任驱动。要深入分析调用栈通常需要符号支持；本报告不会猜测。");
        var start = when.AddMinutes(-10); var end = when.AddMinutes(10);
        var batch = read("System", CrashLogReader.Timeline, start, end, 256, token);
        if (batch.Notice is not null) notes.Add(batch.Notice);
        var events = batch.Events.Where(e => e.Time >= start && e.Time <= end && CrashAnalysis.Context(e) != null)
            .OrderBy(e => (e.Time - when).Duration()).ThenBy(e => e.Record).ToList();
        if (CrashAnalysis.Corroboration(dump.Code, events) is { } hint) sb.AppendLine("交叉核对：" + hint);
        sb.AppendLine().AppendLine("相关系统事件 · 转储时间前后 10 分钟");
        if (events.Count == 0) sb.AppendLine("在已读取的记录中未见其他匹配事件。");
        foreach (var e in events.Take(12).OrderBy(e => e.Time)) sb.AppendLine($"• {Local(e.Time)}  {e.Provider} / {e.Id}：{CrashAnalysis.Context(e)}");
        if (events.Count > 12) sb.AppendLine($"共匹配 {events.Count} 条，仅列距转储时间最近的 12 条。");
        sb.Append("时间线来自当前电脑。如果文件来自另一台电脑，这些事件与转储可能无关；时间相近不能证明因果。");
        sb.AppendLine().AppendLine().Append(WheaReport.Window(start, end, "转储参考时间前后 10 分钟", token, read));
        sb.Append("\n这些 WHEA 记录取自本机日志，不是从 .dmp 内部解码的硬件错误记录。外来转储不能据此关联本机硬件。");
        return notes.Count == 0 ? sb.ToString() : sb + "\n\n读取限制\n" + string.Join("\n", notes.Distinct());
    }
}
