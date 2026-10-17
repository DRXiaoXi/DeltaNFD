using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DeltaNFD.Services.AlphaRestore;

internal sealed record RuntimePackageResult(string Package, int ExitCode, string LogFile, string ErrorDetails,
    bool PostcheckPassed = true)
{
    public bool Success => PostcheckPassed && ExitCode is 0 or 3010 or 1641;
    public bool RequiresReboot => ExitCode is 3010 or 1641;
}

/// <summary>每次修复独立归档；诊断缺失不得代替安装成功证据。</summary>
internal sealed class RuntimeRepairDiagnostics
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ConcurrentQueue<string> _issues = new();
    private readonly object _eventGate = new();
    public string DirectoryPath { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public RuntimeRepairDiagnostics(string root)
    {
        DirectoryPath = Path.Combine(root, "runtime-repair-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")
            + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DirectoryPath);
        // 必须在卸载前确认诊断文件可写。
        foreach (var name in new[] { "install-results.txt", "aio-stdout.log", "aio-stderr.log" })
            File.WriteAllText(Path.Combine(DirectoryPath, name), "", new UTF8Encoding(false));
        Save("session.json", new { SchemaVersion = 2, StartedUtc, OperationId = Guid.NewGuid(),
            Version = typeof(RuntimeRepairDiagnostics).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
            OS = Environment.OSVersion.VersionString, Runtime = Environment.Version.ToString(), Pid = Environment.ProcessId,
            ExecutionSha256 = ExecutionFingerprint() });
        File.WriteAllText(Path.Combine(DirectoryPath, "请发送整个诊断文件夹.txt"),
            "请将本次 runtime-repair-* 文件夹整体压缩后反馈，不要只发送 settings.json 或单个恢复计划。\r\n" +
            "events.jsonl 是执行时间线；operation-result.json 是最终结果；unified-plan 是计划，不能证明已执行成功。\r\n" +
            "MSI 日志、依赖快照、command 参数和 result 复检共同用于定位原因。尚未启动的阶段可能没有日志。\r\n" +
            "若事务尚未确认结束，请先按工具提示核对，不要强杀安装器或立即重复修复。", new UTF8Encoding(false));
    }

    private static string? ExecutionFingerprint()
    {
        try
        {
            var path = typeof(RuntimeRepairDiagnostics).Assembly.Location;
            if (string.IsNullOrEmpty(path)) return null;
            using var file = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file));
        }
        catch { return null; }
    }

    public void Event(string stage, object data)
    {
        try
        {
            lock (_eventGate) File.AppendAllText(Path.Combine(DirectoryPath, "events.jsonl"),
                JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, Stage = stage, Data = data }) + "\n", new UTF8Encoding(false));
        }
        catch (Exception ex) { AddIssue("执行时间线写入失败：" + ex.Message); }
    }

    public OperationResult Complete(OperationResult result)
    {
        var attached = result.WithDiagnostics(DirectoryPath);
        Event("operation-completed", attached);
        Save("operation-result.json", attached);
        return attached;
    }

    public void Save(string name, object value)
    {
        try { File.WriteAllText(Path.Combine(DirectoryPath, name), JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false)); }
        catch (Exception ex) { AddIssue($"诊断文件 {name} 写入失败：{ex.Message}"); }
    }

    private void AddIssue(string issue)
    {
        _issues.Enqueue(issue);
        Log.Warn("运行库修复：" + issue);
    }

    public async Task CaptureOutputAsync(StreamReader reader, string name, CancellationToken token, Action<string>? onLine = null)
    {
        StreamWriter? writer = null;
        try
        {
            try { writer = new StreamWriter(Path.Combine(DirectoryPath, name), false, new UTF8Encoding(false)) { AutoFlush = true }; }
            catch (Exception ex) { AddIssue($"{name} 创建失败：{ex.Message}"); }
            async Task ArchiveLine(string line)
            {
                // 同时归档原始输出和会话日志，不只保留最后 4000 字符。
                Log.Info($"运行库修复·{name}：{line}");
                // 进度回调不能中断完整日志归档，也不影响安装器结果判定。
                try { onLine?.Invoke(line); }
                catch (Exception ex) { Log.Warn("运行库进度显示未更新：" + ex.Message); }
                if (writer is null) return;
                try { await writer.WriteLineAsync(line); }
                catch (Exception ex)
                {
                    AddIssue($"{name} 写入失败：{ex.Message}");
                    writer.Dispose();
                    writer = null;
                }
            }
            // DISM/SFC 用单独的 CR 刷新百分比。ReadLineAsync 在 CR 位于当前缓冲区末尾时
            // 可能等待下一字符才返回，改用流式切分，收到 CR 就立刻发布。
            var buffer = new char[2048];
            var line = new StringBuilder();
            var afterCr = false;
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                for (var index = 0; index < read; index++)
                {
                    var ch = buffer[index];
                    if (ch == '\n' && afterCr) { afterCr = false; continue; }
                    if (ch is '\r' or '\n')
                    {
                        await ArchiveLine(line.ToString()); line.Clear(); afterCr = ch == '\r';
                    }
                    else { afterCr = false; line.Append(ch); }
                }
            }
            if (line.Length > 0) await ArchiveLine(line.ToString());
        }
        catch (OperationCanceledException) { AddIssue($"{name} 输出采集被中止，可能不完整"); }
        catch (Exception ex) { AddIssue($"{name} 输出采集失败：{ex.Message}"); }
        finally { writer?.Dispose(); }
    }

    public List<RuntimePackageResult> ReadPackageResults()
    {
        var results = new List<RuntimePackageResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(Path.Combine(DirectoryPath, "install-results.txt"), Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Windows 文件名不能包含 |，固定三列，不接受任意诊断文件路径。
            var fields = line.Split('|');
            if (fields.Length != 3 || !int.TryParse(fields[0], out var code)
                || Path.IsPathRooted(fields[1]) || fields[1].Split('\\', '/').Any(p => p == "..")
                || !fields[1].EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                || !Regex.IsMatch(fields[2], @"\Amsi-[1-9][0-9]*\.log\z") || !seen.Add(fields[1]))
                throw new InvalidDataException("逐包安装结果格式无效或重复：" + line);
            var logPath = Path.Combine(DirectoryPath, fields[2]);
            var details = ReadErrorDetails(logPath);
            // 注意：AIO 脚本不执行安装后验证，PostcheckPassed 在此恒为 true，由受控安装逐步验证。
            results.Add(new RuntimePackageResult(fields[1], code, fields[2], details));
            Log.Info($"运行库修复·安装结果：包={fields[1]}；退出码={code}；需重启={code is 3010 or 1641}；详细日志={logPath}；错误摘要={details}");
        }
        var marker = Path.Combine(DirectoryPath, "completed.txt");
        if (!File.Exists(marker))
        {
            AddIssue("AIO 完成标记缺失，安装流程可能未完整结束");
            throw new InvalidDataException("AIO 完成标记缺失，不能确认全部安装步骤已结束。");
        }
        else if (!int.TryParse(File.ReadAllText(marker).Trim(), out var count))
        {
            AddIssue("AIO 完成标记格式无效");
            throw new InvalidDataException("AIO 完成标记格式无效。");
        }
        else if (count != results.Count)
        {
            AddIssue($"AIO 完成标记预期 {count} 个包，实际读取 {results.Count} 个结果");
            throw new InvalidDataException("AIO 完成标记与逐包结果数量不一致。");
        }
        return results;
    }

    internal string ReadErrorDetails(string path)
    {
        if (!File.Exists(path)) { AddIssue("MSI 详细日志缺失：" + path); return "详细日志缺失"; }
        var lines = new Queue<string>();
        var causes = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            if (!Regex.IsMatch(line, @"1935|0x[0-9A-F]{8}|Error\s+(?:1603|1618|1612|1706)|Return value [35]|WixDependencyCheck|Found dependent|MainEngineThread.*returning", RegexOptions.IgnoreCase)) continue;
            if (causes.Count < 8 && line.Contains("WixDependencyCheck", StringComparison.OrdinalIgnoreCase)) causes.Add(line);
            if (causes.Count < 8 && Regex.IsMatch(line, @"1935|0x8[0-9A-F]{7}", RegexOptions.IgnoreCase)) causes.Add(line);
            lines.Enqueue(line);
            if (lines.Count > 12) lines.Dequeue();
        }
        return string.Join(Environment.NewLine, causes.Concat(lines).Distinct());
    }

    public string CollectInstallerEvents()
    {
        try
        {
            var since = StartedUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture);
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[Provider[@Name='MsiInstaller'] and TimeCreated[@SystemTime >= '{since}']]]");
            using var reader = new EventLogReader(query);
            var events = new List<object>();
            var errors = new StringBuilder();
            while (reader.ReadEvent() is { } entry)
            {
                using (entry)
                {
                    var xml = XDocument.Parse(entry.ToXml());
                    XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
                    var data = xml.Descendants(ns + "EventData").Elements(ns + "Data").Select(d => d.Value).ToArray();
                    if (!data.Any(d => d.Contains("Visual C++", StringComparison.OrdinalIgnoreCase))) continue;
                    events.Add(new { entry.Id, entry.RecordId, TimeUtc = entry.TimeCreated?.ToUniversalTime(), Data = data });
                    if (entry.Id == 11935)
                    {
                        errors.AppendLine(string.Join(" | ", data));
                        Log.Warn("运行库修复·程序集错误事件：" + string.Join(" | ", data));
                    }
                    if (events.Count >= 256) { AddIssue("安装事件超过 256 项，事件归档已截断"); break; }
                }
            }
            Save("msi-events.json", events);
            return errors.ToString();
        }
        catch (Exception ex) { AddIssue("MsiInstaller 事件采集失败（不影响安装）：" + ex.Message); return ""; }
    }

    public string[] Issues => _issues.ToArray();

    internal static OperationResult BuildResult(int scriptCode, IReadOnlyList<RuntimePackageResult> packages,
        IEnumerable<string> issues, VcRedistScanReport? after, bool reboot, string directory, string assemblyErrors)
    {
        var failures = issues.ToList();
        if (scriptCode is not (0 or 3010)) failures.Add($"AIO 脚本退出码 {scriptCode}");
        failures.AddRange(packages.Where(p => !p.Success).Select(p => $"{p.Package}：退出码 {p.ExitCode}"
            + (p.PostcheckPassed ? "" : "，安装登记复核未通过")));
        if (after is null) failures.Add("修复后复检失败，不能确认安装状态");
        else
        {
            failures.AddRange(after.Suboptimal.Where(s => !s.IsRecommendationOnly).Select(s => s.Reason == "version-unknown"
                ? $"{s.Branch}：已检测到登记，但版本无法核对"
                : string.IsNullOrEmpty(s.InstalledVersion)
                ? $"{s.Branch}：复检仍未检测到安装记录"
                : $"{s.Branch}：复检版本 {s.InstalledVersion}，要求至少 {s.RecommendedVersion}"));
            if (after.HasAbnormal) failures.Add($"复检仍有 {after.Abnormal.Count} 项异常 v14 条目");
        }
        reboot |= scriptCode == 3010 || packages.Any(p => p.RequiresReboot);
        var success = failures.Count == 0;
        var message = success ? "运行库重装流程及分支版本复检通过。" : "运行库修复未完成，部分组件可能已安装，但不能确认全系列修复成功。\n"
            + string.Join("\n", failures.Distinct().Take(12));
        if (failures.Distinct().Count() > 12) message += "\n其余问题见诊断归档。";
        var recommendations = after?.Suboptimal.Where(s => s.IsRecommendationOnly).ToArray() ?? [];
        if (recommendations.Length > 0)
            message += $"\n另有 {recommendations.Length} 项推荐版本提醒（不作为修复失败）："
                + string.Join("；", recommendations.Select(s => $"{s.Branch} 当前 {s.InstalledVersion}，推荐 {s.RecommendedVersion}"));
        if (assemblyErrors.Contains("0x80070005", StringComparison.OrdinalIgnoreCase)
            || packages.Any(p => p.ErrorDetails.Contains("1935", StringComparison.OrdinalIgnoreCase)
                && p.ErrorDetails.Contains("0x80070005", StringComparison.OrdinalIgnoreCase)))
            message += "\n检测到程序集缓存访问被拒绝（1935 / 0x80070005）。请提供诊断目录和 Windows CBS 日志；不要反复卸载重装或批量修改 WinSxS 权限。";
        if (reboot) message += "\n部分组件要求重启；重启后请重新检测，重启请求不代表失败项已修复。";
        foreach (var tip in packages.Where(p => !p.Success).Select(p => RuntimeRepairErrorClassifier.Describe(p.ExitCode, p.ErrorDetails)).Distinct().Take(4))
            message += "\n" + tip;
        if (!success && packages.Count > 0 && packages.All(p => p.Success) && after?.Suboptimal.Count > 0)
            message += "\n安装器已正常结束，但安装记录/版本未达检测基线；不等于 DLL 一定缺失，不应立即重复全量卸载。";
        message += "\n诊断目录：" + directory;
        return new OperationResult { Success = success, Message = message, RequiresReboot = reboot };
    }
}
