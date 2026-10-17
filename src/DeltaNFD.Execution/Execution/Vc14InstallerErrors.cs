using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

internal static class Vc14InstallerErrors
{
    internal static string? RegistryAccessIssue(IEnumerable<string> logs)
    {
        foreach (var log in logs)
        {
            // Ordinary MSI "Note: 1: 1402 ... 3: 2" means a missing optional key, not denied access.
            var match = Regex.Match(log, @"(?im)^.*\bError 1402\.\s*Could not open key:\s*(?<key>[^\r\n]+?)\.\s*System error 5\.");
            if (!match.Success) continue;
            var key = match.Groups["key"].Value;
            if (key.Length > 2048 || key.Any(char.IsControl)) continue;
            return "Windows Installer 注册表访问被拒绝（错误 1402，系统错误 5），不是仅凭退出码 1603 就能判断的进程占用问题。\n"
                + "安装器报告的键：" + key + "\n"
                + "已停止后续步骤。请先核对该键的归属和访问权限；重复重装或仅进入安全模式不能保证解决。"
                + "工具不会自动取得 Installer 注册表所有权或批量重置权限，请保留本次安装日志供进一步处理。";
        }
        return null;
    }

    internal static Vc14StepResult Explain(Vc14StepResult result, ProcessStartInfo command, string diagnosticsDirectory)
    {
        if (result.Unknown || result.ExitCode != 1603) return result;
        try
        {
            var args = command.ArgumentList;
            string? logPath = null;
            for (var i = 0; i + 1 < args.Count; i++)
                if (args[i].Equals("/log", StringComparison.OrdinalIgnoreCase) || args[i].Equals("/L*V", StringComparison.OrdinalIgnoreCase))
                { logPath = Path.GetFullPath(args[i + 1]); break; }
            var directory = Path.GetFullPath(diagnosticsDirectory);
            if (logPath is null || !string.Equals(Path.GetDirectoryName(logPath), directory, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return result;
            var name = Path.GetFileName(logPath);
            var stem = Path.GetFileNameWithoutExtension(name);
            var files = Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
                .Where(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(p).StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal).Take(8)
                .Where(p => new FileInfo(p).Length <= 8 * 1024 * 1024 && (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0);
            var issue = RegistryAccessIssue(files.Select(Vc14DependencyPolicy.ReadInstallerLog));
            return issue is null ? result : result with { PostcheckPassed = false, Detail = result.Detail + "\n" + issue };
        }
        catch (Exception ex)
        {
            Log.Warn("安装失败细节未能读取，保留原失败结果：" + ex.Message);
            return result;
        }
    }
}
