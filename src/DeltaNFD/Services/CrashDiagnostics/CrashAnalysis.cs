using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DeltaNFD.Services.CrashDiagnostics;

// No localized Message formatting: provider + ID + named XML fields define each contract.
internal sealed record CrashEvent(string Provider, int Id, DateTimeOffset Time, long Record, Dictionary<string, string> Data)
{
    public string this[string field] => Data.GetValueOrDefault(field, "");
    public static CrashEvent Parse(string xml)
    {
        var root = XElement.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        var sys = root.Element(ns + "System") ?? throw new FormatException();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in root.Element(ns + "EventData")?.Elements(ns + "Data") ?? [])
            if (node.Attribute("Name") is { } name && node.Value.Length <= 2048 && !fields.TryAdd(name.Value, node.Value.Trim()))
                throw new FormatException();
        var provider = (string?)sys.Element(ns + "Provider")?.Attribute("Name") ?? throw new FormatException();
        var eventId = (int)(sys.Element(ns + "EventID") ?? throw new FormatException());
        if (provider == "Display" && eventId == 4101 && root.Element(ns + "EventData")?.Elements(ns + "Data").FirstOrDefault() is { } first
            && first.Attribute("Name") == null && Regex.IsMatch(first.Value, @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,95}\z"))
            fields["DisplayModule"] = first.Value;
        return new(provider, eventId,
            (DateTimeOffset)(sys.Element(ns + "TimeCreated")?.Attribute("SystemTime") ?? throw new FormatException()),
            (long?)sys.Element(ns + "EventRecordID") ?? 0, fields);
    }
}

internal sealed record CrashCase(CrashEvent Event, uint Code, ulong[] Args, Guid ReportId);
internal static class CrashAnalysis
{
    internal const string Wer = "Microsoft-Windows-WER-SystemErrorReporting", Power = "Microsoft-Windows-Kernel-Power", AppWer = "Windows Error Reporting";
    static readonly Regex Signature = new(@"^0x([0-9a-fA-F]{1,8})(?:\s*\(\s*0x([0-9a-fA-F]{1,16})\s*,\s*0x([0-9a-fA-F]{1,16})\s*,\s*0x([0-9a-fA-F]{1,16})\s*,\s*0x([0-9a-fA-F]{1,16})\s*\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static bool Hex(string text, out ulong value) => ulong.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.AsSpan(2) : text.AsSpan(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    public static CrashCase? Identify(CrashEvent e)
    {
        if (e is { Provider: Wer, Id: 1001 })
        {
            var m = Signature.Match(e["param1"]);
            if (!m.Success || !Hex(m.Groups[1].Value, out var code) || code == 0) return null;
            Guid.TryParse(e["param3"], out var id);
            return new(e, (uint)code, m.Groups[2].Success ? Enumerable.Range(2, 4).Select(i => Convert.ToUInt64(m.Groups[i].Value, 16)).ToArray() : [], id);
        }
        if (e is { Provider: Power, Id: 41 } && uint.TryParse(e["BugcheckCode"], NumberStyles.None, CultureInfo.InvariantCulture, out var stop) && stop != 0)
        {
            var args = new ulong[4];
            for (var i = 0; i < 4; i++)
                if (!Hex(e[$"BugcheckParameter{i + 1}"], out args[i])) return new(e, stop, [], Guid.Empty);
            return new(e, stop, args, Guid.Empty);
        }
        return null;
    }

    public static string Meaning(uint code) => code switch
    {
        0x116 => "VIDEO_TDR_FAILURE：显示驱动超时后，Windows 尝试恢复图形设备失败。可先核对显卡驱动版本及近期变更。",
        0x119 => "VIDEO_SCHEDULER_INTERNAL_ERROR：图形调度程序报告严重错误，不能据此锁定显卡驱动。",
        0x9F => "DRIVER_POWER_STATE_FAILURE：设备或驱动的电源状态转换异常。核对是否发生在睡眠、唤醒或关机时。",
        0xD1 => "DRIVER_IRQL_NOT_LESS_OR_EQUAL：驱动在不允许的中断级别访问了内存。需用转储确认访问位置。",
        0xA => "IRQL_NOT_LESS_OR_EQUAL：内核或驱动在较高的中断级别访问了无效或可分页内存。",
        0x50 => "PAGE_FAULT_IN_NONPAGED_AREA：内核访问了无效内存；驱动或内存问题都值得排查。",
        0x1A => "MEMORY_MANAGEMENT：内存管理器检测到严重错误，不等于内存条已损坏。",
        0x124 => "WHEA_UNCORRECTABLE_ERROR：Windows 收到不可纠正的硬件错误报告，优先核对 WHEA 记录。",
        0x133 => "DPC_WATCHDOG_VIOLATION：系统在较高中断级别停留过久，需结合转储区分单次超时与累计超时。",
        0x7A => "KERNEL_DATA_INPAGE_ERROR：内核数据未能从分页文件读入，可核对磁盘与控制器事件。",
        0xEF => "CRITICAL_PROCESS_DIED：关键系统进程终止。停止代码本身不能说明哪个驱动引发。",
        0x3B => "SYSTEM_SERVICE_EXCEPTION：执行系统服务时发生异常，需要转储中的异常上下文。",
        _ => "此停止代码尚无内置释义。请保留代码与对应转储，不能仅凭代码判断原因。"
    };

    // A bucket from another crash (even one with the same stop code) is not evidence.
    public static string? BucketDriver(CrashCase crash, CrashEvent app)
    {
        if (app is not { Provider: AppWer, Id: 1001 } || app["EventName"] != "BlueScreen" || crash.ReportId == Guid.Empty ||
            !Guid.TryParse(app["ReportId"], out var id) || id != crash.ReportId || !Hex(app["P1"], out var code) || code != crash.Code) return null;
        for (var i = 0; i < crash.Args.Length; i++)
            if (app[$"P{i + 2}"] is { Length: > 0 } arg && (!Hex(arg, out var v) || v != crash.Args[i])) return null;
        var m = Regex.Match(app["Bucket"], @"(?:^|_)IMAGE_([a-z0-9][a-z0-9_.-]{0,95}\.sys)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static (string? Driver, string? Note) DebuggerDriver(CrashCase crash, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        if (text.Length > 65536) return (null, "调试文本过长；请只粘贴一次分析的字段。");
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line[..colon].Trim().ToUpperInvariant();
            if (key is not ("BUGCHECK_CODE" or "BUGCHECK_P1" or "BUGCHECK_P2" or "BUGCHECK_P3" or "BUGCHECK_P4" or "IMAGE_NAME")) continue;
            if (!fields.TryAdd(key, line[(colon + 1)..].Trim())) return (null, "调试字段重复；请仅粘贴同一份转储的一次分析。");
        }
        if (!Hex(fields.GetValueOrDefault("BUGCHECK_CODE", ""), out var code) || code != crash.Code || crash.Args.Length != 4 ||
            !crash.Args.Select((arg, i) => Hex(fields.GetValueOrDefault($"BUGCHECK_P{i + 1}", "").Replace("`", ""), out var v) && arg == v).All(x => x))
            return (null, "调试文本与事件的停止代码及四个参数未能全部核对，不引用模块。");
        var image = fields.GetValueOrDefault("IMAGE_NAME", "");
        return Regex.IsMatch(image, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,95}\.[sS][yY][sS]\z") ? (image, null) : (null, "调试结果未点名 .sys 驱动；内核映像或堆栈中的文件名不能单独归因。");
    }

    public static string? Corroboration(uint code, IEnumerable<CrashEvent> events) => code switch
    {
        0x116 or 0x119 when events.Any(e => e is { Provider: "Display", Id: 4101 }) => "停止代码和显示驱动超时记录都指向图形子系统；可优先核对显卡驱动的版本与近期变更。",
        0x124 when events.Any(e => e.Provider == "Microsoft-Windows-WHEA-Logger" && Context(e) != null) => "窗口内同时有 WHEA 硬件报告；可在事件查看器中核对错误源和设备。已纠正的错误不等于本次致命错误。",
        0x7A when events.Any(e => (e.Provider is "disk" or "storahci" or "stornvme" or "storport") && Context(e) != null) => "分页读入失败与存储事件指向同一排查方向；可核对事件所指的磁盘或控制器是否承载分页文件。",
        _ => null
    };

    public static string? Context(CrashEvent e) => (e.Provider, e.Id) switch
    {
        (Power, 41) => "上次关机不正常",
        ("EventLog", 6008) => "上次关机异常（重启后补记）",
        (Wer, 1001) => "系统记录了错误检查报告",
        ("Display", 4101) => e["DisplayModule"] is { Length: > 0 } module ? $"显示驱动 {module} 曾超时并恢复（不是蓝屏归因）" : "显示驱动曾超时并恢复",
        ("Microsoft-Windows-WHEA-Logger", 17 or 19) => "已纠正的硬件错误",
        ("Microsoft-Windows-WHEA-Logger", 1 or 18 or 20 or 46 or 47) => "硬件错误报告",
        ("disk", 7 or 51 or 153) => "磁盘 I/O 异常或重试",
        ("storahci" or "stornvme" or "storport", 129) => "存储设备重置",
        ("volmgr", 46 or 161) => "崩溃转储初始化或写入失败",
        _ => null
    };
}
