using System.Diagnostics;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DeltaNFD.Services;

public enum OfflineScheduledTaskState
{
    Missing,
    Owned,
    ForeignOrUnknown,
}

public sealed record OfflineScheduledTaskInspection(OfflineScheduledTaskState State, string ExecutablePath, bool Enabled, string Message);

/// <summary>脱机模式只查询/删除经 XML 动作确认属于本产品的登录任务。</summary>
public static class OfflineTaskOwnership
{
    private static readonly string[] KnownLegacyExecutables = BuildKnownLegacyExecutables();

    public static async Task<OfflineScheduledTaskInspection> InspectAsync(string taskName, string? currentExePath = null)
    {
        var query = await RunSchtasksAsync(["/Query", "/TN", taskName, "/XML"], TimeSpan.FromSeconds(15));
        if (query.Code != 0)
        {
            var text = query.StdOut + "\n" + query.StdErr;
            return IsNotFound(text)
                ? new(OfflineScheduledTaskState.Missing, "", false, "任务不存在。")
                : new(OfflineScheduledTaskState.ForeignOrUnknown, "", false, $"无法确认计划任务归属：{FirstLine(text)}");
        }

        try
        {
            var isOwned = IsOwnedTaskXml(query.StdOut, currentExePath ?? Environment.ProcessPath ?? "", KnownLegacyExecutables,
                out var executablePath, out var enabled, out var reason);
            return isOwned
                ? new(OfflineScheduledTaskState.Owned, executablePath, enabled, "任务动作指向本产品安装程序。")
                : new(OfflineScheduledTaskState.ForeignOrUnknown, executablePath, enabled, reason);
        }
        catch (Exception ex)
        {
            return new(OfflineScheduledTaskState.ForeignOrUnknown, "", false, "任务 XML 无法安全解析：" + ex.Message);
        }
    }

    public static async Task<OperationResult> RemoveOwnedTaskAsync(string taskName, string? currentExePath = null)
    {
        var inspection = await InspectAsync(taskName, currentExePath);
        if (inspection.State == OfflineScheduledTaskState.Missing)
            return OperationResult.Ok($"计划任务 {taskName} 不存在。");
        if (inspection.State != OfflineScheduledTaskState.Owned)
            return OperationResult.Fail($"为保护非本工具任务，未删除 {taskName}：{inspection.Message}");

        var result = await RunSchtasksAsync(["/Delete", "/TN", taskName, "/F"], TimeSpan.FromSeconds(30));
        if (result.Code != 0)
            return OperationResult.Fail($"删除本工具计划任务 {taskName} 失败：{FirstLine(result.StdErr.Length == 0 ? result.StdOut : result.StdErr)}");
        var after = await InspectAsync(taskName, currentExePath);
        return after.State == OfflineScheduledTaskState.Missing
            ? OperationResult.Ok($"已移除本工具计划任务 {taskName}。")
            : OperationResult.Fail($"删除命令后仍无法确认 {taskName} 已移除：{after.Message}");
    }

    public static async Task<OperationResult> EnsureOwnedTaskAsync(string taskName, string exePath, bool enabled = true)
    {
        string normalized;
        try
        {
            if (!Path.IsPathFullyQualified(exePath) || !File.Exists(exePath))
                return OperationResult.Fail("无法确认当前程序 EXE 路径，未创建计划任务。");
            normalized = Path.GetFullPath(exePath);
        }
        catch (Exception ex) { return OperationResult.Fail("程序路径无效：" + ex.Message); }

        var before = await InspectAsync(taskName, normalized);
        if (before.State == OfflineScheduledTaskState.Owned)
            return before.Enabled == enabled
                ? OperationResult.Ok($"本工具计划任务 {taskName} 已存在且状态匹配。")
                : await SetOwnedTaskEnabledAsync(taskName, normalized, enabled);
        if (before.State != OfflineScheduledTaskState.Missing)
            return OperationResult.Fail($"计划任务 {taskName} 已存在但无法确认归属，拒绝覆盖：{before.Message}");

        var result = await RunSchtasksAsync(
            ["/Create", "/TN", taskName, "/TR", $"\"{normalized}\"", "/SC", "ONLOGON", "/RL", "HIGHEST"],
            TimeSpan.FromSeconds(30));
        if (result.Code != 0)
            return OperationResult.Fail($"创建计划任务 {taskName} 失败：{FirstLine(result.StdErr.Length == 0 ? result.StdOut : result.StdErr)}");
        var after = await InspectAsync(taskName, normalized);
        if (after.State != OfflineScheduledTaskState.Owned)
            return OperationResult.Fail($"创建后无法确认任务归属：{after.Message}");
        return after.Enabled == enabled ? OperationResult.Ok($"已创建本工具计划任务 {taskName}。")
            : await SetOwnedTaskEnabledAsync(taskName, normalized, enabled);
    }

    internal static bool IsOwnedTaskXml(string xml, string currentExePath, IReadOnlyCollection<string> legacyPaths,
        out string executablePath, out bool enabled, out string reason)
    {
        executablePath = "";
        enabled = false;
        reason = "任务动作不是本工具可识别的 EXE。";
        var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 };
        using var reader = XmlReader.Create(new StringReader(xml), readerSettings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var actionContainers = document.Descendants().Where(x => x.Name.LocalName == "Actions").ToArray();
        if (actionContainers.Length != 1)
        {
            reason = "计划任务缺少唯一 Actions 区块。";
            return false;
        }
        var triggerContainers = document.Descendants().Where(x => x.Name.LocalName == "Triggers").ToArray();
        if (triggerContainers.Length != 1)
        {
            reason = "计划任务缺少唯一 Triggers 区块。";
            return false;
        }
        var triggers = triggerContainers[0].Elements().ToArray();
        if (triggers.Length != 1 || triggers[0].Name.LocalName != "LogonTrigger")
        {
            reason = "计划任务不是本工具使用的单一登录触发器。";
            return false;
        }
        var settings = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "Settings");
        var enabledText = settings?.Elements().FirstOrDefault(x => x.Name.LocalName == "Enabled")?.Value.Trim();
        if (enabledText is null) enabled = true;
        else if (!bool.TryParse(enabledText, out enabled))
        {
            reason = "计划任务启用状态无法解析。";
            return false;
        }
        var actions = actionContainers[0].Elements().ToArray();
        if (actions.Length != 1 || actions[0].Name.LocalName != "Exec")
        {
            reason = "计划任务必须且只能包含一个 Exec 动作，不能混入其他操作。";
            return false;
        }

        var command = actions[0].Elements().FirstOrDefault(x => x.Name.LocalName == "Command")?.Value.Trim() ?? "";
        var arguments = actions[0].Elements().FirstOrDefault(x => x.Name.LocalName == "Arguments")?.Value.Trim() ?? "";
        if (command.Length == 0 || arguments.Length != 0)
        {
            reason = "计划任务命令为空或包含额外参数。";
            return false;
        }

        var expanded = Environment.ExpandEnvironmentVariables(command.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(expanded))
        {
            reason = "计划任务命令不是绝对 EXE 路径。";
            return false;
        }
        executablePath = Path.GetFullPath(expanded);

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Path.IsPathFullyQualified(currentExePath)) allowed.Add(Path.GetFullPath(currentExePath));
        foreach (var legacy in legacyPaths)
            if (Path.IsPathFullyQualified(legacy)) allowed.Add(Path.GetFullPath(legacy));

        if (allowed.Contains(executablePath)) return true;
        reason = "计划任务命令未指向当前程序或已知旧版安装路径。";
        return false;
    }

    private static async Task<OperationResult> SetOwnedTaskEnabledAsync(string taskName, string exePath, bool enabled)
    {
        var before = await InspectAsync(taskName, exePath);
        if (before.State != OfflineScheduledTaskState.Owned)
            return OperationResult.Fail($"为避免修改他人任务，未更改 {taskName}：{before.Message}");
        if (before.Enabled == enabled) return OperationResult.Ok($"本工具计划任务 {taskName} 状态已匹配。");
        var command = enabled ? "/ENABLE" : "/DISABLE";
        var result = await RunSchtasksAsync(["/Change", "/TN", taskName, command], TimeSpan.FromSeconds(30));
        if (result.Code != 0)
            return OperationResult.Fail($"更改计划任务 {taskName} 状态失败：{FirstLine(result.StdErr.Length == 0 ? result.StdOut : result.StdErr)}");
        var after = await InspectAsync(taskName, exePath);
        return after.State == OfflineScheduledTaskState.Owned && after.Enabled == enabled
            ? OperationResult.Ok($"本工具计划任务 {taskName} 状态已恢复。")
            : OperationResult.Fail($"更改后无法复核 {taskName} 的启用状态：{after.Message}");
    }

    private static string[] BuildKnownLegacyExecutables()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentDirectory = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        if (!string.IsNullOrWhiteSpace(currentDirectory))
            paths.Add(Path.GetFullPath(Path.Combine(currentDirectory, "DeltaOptimizer.exe")));

        foreach (var baseDirectory in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                 })
        {
            if (string.IsNullOrWhiteSpace(baseDirectory)) continue;
            paths.Add(Path.GetFullPath(Path.Combine(baseDirectory, "DeltaOptimizer", "DeltaOptimizer.exe")));
        }
        return paths.ToArray();
    }

    private static async Task<(int Code, string StdOut, string StdErr)> RunSchtasksAsync(string[] arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process is null) return (-1, "", "无法启动 schtasks.exe");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(timeout); }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "查询/操作计划任务超时");
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    private static bool IsNotFound(string text)
    {
        var value = text.ToLowerInvariant();
        return value.Contains("cannot find the file specified", StringComparison.Ordinal) ||
               value.Contains("the system cannot find", StringComparison.Ordinal) ||
               value.Contains("找不到指定的文件", StringComparison.Ordinal) ||
               value.Contains("系统找不到指定", StringComparison.Ordinal);
    }

    private static string FirstLine(string text)
    {
        var value = text.Trim();
        var index = value.IndexOfAny(['\r', '\n']);
        return index > 0 ? value[..index] : value;
    }
}
