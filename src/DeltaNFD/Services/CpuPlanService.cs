using System.Globalization;
using System.Text;

namespace DeltaNFD.Services;

/// <summary>
/// CPU实验室「优化方案」的保存 / 导入（OpenAlphaV0.83 新增）。
///
/// 设计要点：
/// 1. **纯文本 txt**（UTF-8 无 BOM），人可以打开看、可以改、可以贴到群里分享——
///    这也是用户明确要求的形态（不用 JSON，避免新手看不懂）。
/// 2. 方案记录「当前方案的 CPU 型号 / 核心数 / 线程数」+「异类调度策略」+「CPU 亲和性」的全部内容。
/// 3. **导入必须严格匹配 CPU 型号 + 核心数 + 线程数**：三者有任一不同就拒绝导入并说明差异。
///    原因是亲和性掩码里的 CPU 序号是与本机拓扑绑定的，换机器直接套用会把游戏锁到不存在的核上，
///    比不导入更糟——所以宁可拒绝也不"差不多就行"。
/// </summary>
public static class CpuPlanService
{
    /// <summary>方案文件标识行。用于识别"这是不是本工具的方案文件"。</summary>
    public const string FileHeader = "# Delta NFD CPU 优化方案 v1";

    /// <summary>方案文件的扩展名（保存对话框默认用）。</summary>
    public const string FileExtension = ".txt";

    /// <summary>一条异类调度策略记录。</summary>
    public sealed record HeteroEntry(string SettingGuid, string Title, string AcValue, string DcValue);

    /// <summary>一份完整方案。</summary>
    public sealed class CpuPlan
    {
        public string CpuName { get; set; } = "";
        public int PhysicalCores { get; set; }
        public int LogicalProcessors { get; set; }

        /// <summary>核心亲和性掩码（十六进制字符串，如 0x0000FFFF；16 位宽便于阅读）。</summary>
        public string AffinityMask { get; set; } = "0x0";

        /// <summary>亲和性规则开关。</summary>
        public bool AffinityRuleEnabled { get; set; }

        /// <summary>已勾选的核心序号（便于人读与人改；导入时以此为准重建掩码）。</summary>
        public List<int> AffinityCores { get; set; } = new();

        public List<HeteroEntry> Hetero { get; set; } = new();

        /// <summary>导出时间（仅作记录，不参与匹配）。</summary>
        public string SavedAt { get; set; } = "";

        /// <summary>生成方案的机器（仅作记录，不参与匹配）。</summary>
        public string MachineName { get; set; } = "";

        /// <summary>工具版本（仅作记录，不参与匹配）。</summary>
        public string AppVersion { get; set; } = "";
    }

    /// <summary>导入结果。失败时 <see cref="Message"/> 给出可读原因（含具体差异）。</summary>
    public sealed record ImportResult(bool Success, CpuPlan? Plan, string Message);

    // ---------------- 序列化 ----------------

    /// <summary>把方案序列化成 txt 文本（UTF-8 无 BOM 写出）。</summary>
    public static string Serialize(CpuPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FileHeader);
        sb.AppendLine("# 本文件由「三角帧不掉洲（Delta NFD）」CPU实验室生成，可直接分享给使用相同 CPU 的人。");
        sb.AppendLine("# 导入时会校验 CPU 型号、核心数、线程数三者完全一致，不一致将拒绝导入。");
        sb.AppendLine();
        sb.AppendLine("[处理器]");
        sb.AppendLine($"CPU型号={plan.CpuName}");
        sb.AppendLine($"核心数={plan.PhysicalCores.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"线程数={plan.LogicalProcessors.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("[CPU亲和性]");
        sb.AppendLine($"规则开关={(plan.AffinityRuleEnabled ? "开" : "关")}");
        sb.AppendLine($"掩码={plan.AffinityMask}");
        sb.AppendLine($"已勾选核心={string.Join(",", plan.AffinityCores.Select(c => c.ToString(CultureInfo.InvariantCulture)))}");
        sb.AppendLine();
        sb.AppendLine("[异类调度策略]");
        if (plan.Hetero.Count == 0)
        {
            sb.AppendLine("# （本机未读取到异类调度策略）");
        }
        else
        {
            foreach (var entry in plan.Hetero)
            {
                // GUID 是机器的权威标识；中文标题与取值一并写出，方便人读
                sb.AppendLine($"{entry.SettingGuid}|{entry.Title}|交流={entry.AcValue}|直流={entry.DcValue}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[记录信息]");
        sb.AppendLine($"保存时间={plan.SavedAt}");
        sb.AppendLine($"计算机名={plan.MachineName}");
        sb.AppendLine($"工具版本={plan.AppVersion}");
        return sb.ToString();
    }

    /// <summary>解析方案文本。格式非法时返回 null 并给出原因。</summary>
    public static bool TryParse(string text, out CpuPlan? plan, out string error)
    {
        plan = null;
        error = "";

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "方案文件是空的。";
            return false;
        }

        var result = new CpuPlan();
        var section = "";
        var sawHeader = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (line.StartsWith(FileHeader, StringComparison.Ordinal))
                {
                    sawHeader = true;
                }

                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            if (section == "处理器")
            {
                var (key, value) = SplitKeyValue(line);
                switch (key)
                {
                    case "CPU型号": result.CpuName = value; break;
                    case "核心数": result.PhysicalCores = ParseInt(value); break;
                    case "线程数": result.LogicalProcessors = ParseInt(value); break;
                }
            }
            else if (section == "CPU亲和性")
            {
                var (key, value) = SplitKeyValue(line);
                switch (key)
                {
                    case "规则开关": result.AffinityRuleEnabled = value == "开"; break;
                    case "掩码": result.AffinityMask = value; break;
                    case "已勾选核心":
                        result.AffinityCores = value
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(ParseInt)
                            .Where(i => i is >= 0 and < 64)
                            .Distinct()
                            .OrderBy(i => i)
                            .ToList();
                        break;
                }
            }
            else if (section == "异类调度策略")
            {
                var parts = line.Split('|');
                if (parts.Length >= 4)
                {
                    var ac = parts[2].StartsWith("交流=", StringComparison.Ordinal) ? parts[2][3..] : parts[2];
                    var dc = parts[3].StartsWith("直流=", StringComparison.Ordinal) ? parts[3][3..] : parts[3];
                    result.Hetero.Add(new HeteroEntry(parts[0].Trim(), parts[1].Trim(), ac.Trim(), dc.Trim()));
                }
            }
            else if (section == "记录信息")
            {
                var (key, value) = SplitKeyValue(line);
                switch (key)
                {
                    case "保存时间": result.SavedAt = value; break;
                    case "计算机名": result.MachineName = value; break;
                    case "工具版本": result.AppVersion = value; break;
                }
            }
        }

        if (!sawHeader)
        {
            error = "这不是本工具生成的方案文件（缺少文件标识行）。";
            return false;
        }

        if (result.PhysicalCores <= 0 || result.LogicalProcessors <= 0)
        {
            error = "方案文件缺少核心数或线程数，无法校验是否与本机一致。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(result.CpuName))
        {
            error = "方案文件缺少 CPU 型号，无法校验是否与本机一致。";
            return false;
        }

        plan = result;
        return true;
    }

    /// <summary>校验方案是否适用于本机（型号 + 核心数 + 线程数三者必须完全一致）。</summary>
    public static ImportResult ValidateForLocalMachine(
        CpuPlan plan, string localCpuName, int localCores, int localThreads)
    {
        var diffs = new List<string>();

        if (!string.Equals(Normalize(plan.CpuName), Normalize(localCpuName), StringComparison.OrdinalIgnoreCase))
        {
            diffs.Add($"CPU 型号：方案「{plan.CpuName}」/ 本机「{localCpuName}」");
        }

        if (plan.PhysicalCores != localCores)
        {
            diffs.Add($"核心数：方案 {plan.PhysicalCores} / 本机 {localCores}");
        }

        if (plan.LogicalProcessors != localThreads)
        {
            diffs.Add($"线程数：方案 {plan.LogicalProcessors} / 本机 {localThreads}");
        }

        if (diffs.Count > 0)
        {
            return new ImportResult(false, null,
                "方案与本机 CPU 不一致，已拒绝导入（不同 CPU 的核心编号不同，套用会锁错核心）：\n· "
                + string.Join("\n· ", diffs));
        }

        return new ImportResult(true, plan, "方案与本机 CPU 完全一致，可以导入。");
    }

    // ---------------- 辅助 ----------------

    /// <summary>型号比较前归一：去掉多余空格、统一大小写（"(R)" 之类的差异不做容忍，保持严格）。</summary>
    private static string Normalize(string value) =>
        string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static (string Key, string Value) SplitKeyValue(string line)
    {
        var index = line.IndexOf('=');
        return index < 0
            ? (line.Trim(), "")
            : (line[..index].Trim(), line[(index + 1)..].Trim());
    }

    private static int ParseInt(string value) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
