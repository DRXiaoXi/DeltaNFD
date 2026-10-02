using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DeltaNFD.Services;

internal sealed record RuntimePackageResult(string Package, int ExitCode, string LogFile, string ErrorDetails)
{
    public bool Success => ExitCode is 0 or 3010 or 1641;
    public bool RequiresReboot => ExitCode is 3010 or 1641;
}

/// <summary>每次修复独立归档；诊断缺失不得代替安装成功证据。</summary>
internal sealed class RuntimeRepairDiagnostics
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ConcurrentQueue<string> _issues = new();
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

    public async Task CaptureOutputAsync(StreamReader reader, string name, CancellationToken token)
    {
        StreamWriter? writer = null;
        try
        {
            try { writer = new StreamWriter(Path.Combine(DirectoryPath, name), false, new UTF8Encoding(false)) { AutoFlush = true }; }
            catch (Exception ex) { AddIssue($"{name} 创建失败：{ex.Message}"); }
            while (await reader.ReadLineAsync(token) is { } line)
            {
                // 同时归档原始输出和会话日志，不只保留最后 4000 字符。
                Log.Info($"运行库修复·{name}：{line}");
                if (writer is null) continue;
                try { await writer.WriteLineAsync(line); }
                catch (Exception ex)
                {
                    AddIssue($"{name} 写入失败：{ex.Message}");
                    writer.Dispose();
                    writer = null;
                }
            }
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
            results.Add(new RuntimePackageResult(fields[1], code, fields[2], details));
            Log.Info($"运行库修复·安装结果：包={fields[1]}；退出码={code}；需重启={code is 3010 or 1641}；详细日志={logPath}；错误摘要={details}");
        }
        var marker = Path.Combine(DirectoryPath, "completed.txt");
        if (!File.Exists(marker) || !int.TryParse(File.ReadAllText(marker).Trim(), out var count) || count != results.Count)
            throw new InvalidDataException("AIO 完成标记缺失或逐包结果数量不一致，不能确认安装流程完整结束。");
        return results;
    }

    private string ReadErrorDetails(string path)
    {
        if (!File.Exists(path)) { AddIssue("MSI 详细日志缺失：" + path); return "详细日志缺失"; }
        var lines = new Queue<string>();
        foreach (var line in File.ReadLines(path))
        {
            if (!Regex.IsMatch(line, @"1935|0x80070005|Return value 3|MainEngineThread.*returning", RegexOptions.IgnoreCase)) continue;
            lines.Enqueue(line);
            if (lines.Count > 12) lines.Dequeue();
        }
        return string.Join(Environment.NewLine, lines);
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
        failures.AddRange(packages.Where(p => !p.Success).Select(p => $"{p.Package}：退出码 {p.ExitCode}"));
        if (after is null) failures.Add("修复后复检失败，不能确认安装状态");
        else
        {
            failures.AddRange(after.Suboptimal.Select(s => string.IsNullOrEmpty(s.InstalledVersion)
                ? $"{s.Branch}：复检仍未检测到安装记录"
                : $"{s.Branch}：复检版本 {s.InstalledVersion}，要求至少 {s.RecommendedVersion}"));
            if (after.HasAbnormal) failures.Add($"复检仍有 {after.Abnormal.Count} 项异常 v14 条目");
        }
        reboot |= scriptCode == 3010 || packages.Any(p => p.RequiresReboot);
        var success = failures.Count == 0;
        var message = success ? "运行库重装流程及分支版本复检通过。" : "运行库修复未完成，部分组件可能已安装，但不能确认全系列修复成功。\n"
            + string.Join("\n", failures.Distinct().Take(12));
        if (failures.Distinct().Count() > 12) message += "\n其余问题见诊断归档。";
        if (assemblyErrors.Contains("0x80070005", StringComparison.OrdinalIgnoreCase)
            || packages.Any(p => p.ErrorDetails.Contains("1935", StringComparison.OrdinalIgnoreCase)
                && p.ErrorDetails.Contains("0x80070005", StringComparison.OrdinalIgnoreCase)))
            message += "\n检测到程序集缓存访问被拒绝（1935 / 0x80070005）。请提供诊断目录和 Windows CBS 日志；不要反复卸载重装或批量修改 WinSxS 权限。";
        if (reboot) message += "\n部分组件要求重启；重启后请重新检测，重启请求不代表失败项已修复。";
        message += "\n诊断目录：" + directory;
        return new OperationResult { Success = success, Message = message, RequiresReboot = reboot };
    }
}
