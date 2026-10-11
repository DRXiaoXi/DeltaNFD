using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

internal interface IRuntimeBulkUninstallEnvironment
{
    Task<string?> PreflightAsync(CancellationToken token);
    Task<List<VcRedistEntry>> ScanAsync(CancellationToken token);
    Task<RuntimeStepResult> ExecuteAsync(VcRedistEntry entry, IProgress<string>? progress, CancellationToken token);
}

/// <summary>Confirmed, bounded bulk uninstall; all native execution stays behind the existing verified MSI uninstaller.</summary>
internal sealed class RuntimeBulkUninstall(IRuntimeBulkUninstallEnvironment environment, RuntimeRepairDiagnostics diagnostics)
{
    internal static bool IsCandidate(VcRedistEntry entry) => Regex.IsMatch(entry.DisplayName,
        @"^Microsoft Visual C\+\+ (?:2005|2008|2010|2012|2013|2015|2017|2019|2022|2026|v14)\b", RegexOptions.IgnoreCase)
        && Regex.IsMatch(entry.DisplayName, @"\b(?:Redistributable|Minimum Runtime|Additional Runtime)\b", RegexOptions.IgnoreCase)
        && !Regex.IsMatch(entry.DisplayName, @"\b(?:Debug|SDK|VSTO|Tools)\b", RegexOptions.IgnoreCase);

    internal static RuntimeUninstallPreview Preview(IEnumerable<VcRedistEntry> entries)
    {
        var targets = entries.Where(IsCandidate).OrderBy(e => e.KeyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Architecture, StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count > 200 || targets.Select(e => e.KeyName.ToUpperInvariant()).Distinct().Count() != targets.Count)
            throw new InvalidDataException("运行库登记数量异常或重复，未创建卸载清单。");
        return new(targets, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(targets))));
    }

    internal async Task<OperationResult> RunAsync(string confirmedFingerprint, IProgress<string>? progress, CancellationToken token)
    {
        int completed = 0; bool reboot = false;
        var results = new List<object>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(80));
        token = deadline.Token;
        OperationResult Fail(string message, bool unknown = false) => new()
        { Success = false, IsPartial = completed > 0, OutcomeUnknown = unknown, RequiresReboot = reboot,
            Message = $"全量卸载未完成；已确认处理 {completed} 项。" + message + "\n诊断目录：" + diagnostics.DirectoryPath };
        try
        {
            token.ThrowIfCancellationRequested();
            if (await environment.PreflightAsync(token) is { } blocker) return Fail(blocker);
            var preview = Preview(await environment.ScanAsync(token));
            if (string.IsNullOrWhiteSpace(confirmedFingerprint) || preview.Fingerprint != confirmedFingerprint)
                return Fail("安装清单已变化或没有明确确认，请重新预览。未卸载任何条目。");
            diagnostics.Save("uninstall-all-preview.json", preview);
            if (preview.Entries.Count == 0) return OperationResult.Ok("未检测到可确认的 C++ 可再发行运行库，无需卸载。");
            foreach (var entry in preview.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (await environment.PreflightAsync(token) is { } changedBlocker) return Fail(changedBlocker);
                progress?.Report($"全量卸载进度 {completed}/{preview.Entries.Count}：{entry.DisplayName}");
                var result = await environment.ExecuteAsync(entry, progress, token);
                reboot |= result.RequiresReboot;
                results.Add(new { Entry = entry, Result = result });
                diagnostics.Save("uninstall-all-results.json", results);
                if (!result.Success || result.OutcomeUnknown) return Fail(entry.DisplayName + "：" + result.Details, result.OutcomeUnknown);
                completed++;
                if (result.RequiresReboot) return Fail("安装器要求重启；已停止后续卸载，请重启后重新检测。不会自动重启。");
            }
            progress?.Report("全量卸载复检中…");
            var remaining = Preview(await environment.ScanAsync(token));
            diagnostics.Save("uninstall-all-after.json", remaining);
            if (remaining.Entries.Count != 0) return Fail($"复检仍发现 {remaining.Entries.Count} 项（可能有保留的共享依赖或新登记），未报告全部完成。");
            return OperationResult.Ok($"全部 C++ 可再发行运行库卸载及复检通过，共处理 {completed} 项。\n依赖这些组件的应用需要重装运行库才能运行。\n诊断目录：" + diagnostics.DirectoryPath);
        }
        catch (OperationCanceledException) { return Fail("已停止后续步骤或达到预算；当前安装事务不强杀，需核对日志。取消不等于还原。"); }
        catch (Exception ex) { return Fail("未继续卸载，证据保留：" + ex.Message); }
    }
}

internal sealed class RuntimeBulkUninstallEnvironment(RuntimeRepairEnvironment environment, Func<Task<string?>> protectionBlocker) : IRuntimeBulkUninstallEnvironment
{
    public async Task<string?> PreflightAsync(CancellationToken token) => await protectionBlocker().WaitAsync(token)
        ?? (await environment.PreflightAsync(token)).Blocker;
    public async Task<List<VcRedistEntry>> ScanAsync(CancellationToken token) => (await environment.ScanAsync(token)).All;
    public Task<RuntimeStepResult> ExecuteAsync(VcRedistEntry entry, IProgress<string>? progress, CancellationToken token) =>
        environment.ExecuteAsync(new("uninstall-all-" + Guid.NewGuid().ToString("N"), entry.DisplayName, "", "uninstall", "", entry),
            TimeSpan.FromMinutes(3), progress, token);
}
