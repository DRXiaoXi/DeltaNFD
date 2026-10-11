namespace DeltaNFD.Services;

internal sealed record RuntimeRemovalResult(int Completed, RuntimeStepResult? Stopped, bool Reboot);

/// <summary>Both repair entry points share the same verified removal phase before installing anything.</summary>
internal static class RuntimePreUninstall
{
    internal static async Task<RuntimeRemovalResult> RunAsync(VcRedistScanReport before,
        Func<VcRedistEntry, TimeSpan, IProgress<string>?, CancellationToken, Task<RuntimeStepResult>> uninstall,
        Func<TimeSpan, TimeSpan> remaining, RuntimeRepairDiagnostics diagnostics, IProgress<string>? progress, CancellationToken token)
    {
        var targets = RuntimeBulkUninstall.Preview(before.All).Entries;
        diagnostics.Save("before-scan.json", before);
        diagnostics.Save("repair-uninstall-plan.json", targets);
        var results = new List<object>(); int completed = 0; bool reboot = false;
        try
        {
            foreach (var entry in targets)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report($"修复前 C++ 卸载 [{completed + 1}/{targets.Count}]：{entry.DisplayName}");
                var result = await uninstall(entry, remaining(TimeSpan.FromMinutes(3)), progress, token);
                reboot |= result.RequiresReboot;
                results.Add(new { Entry = entry, Result = result }); diagnostics.Save("uninstall-results.json", results);
                if (result.OutcomeUnknown || !result.Success) return new(completed, result, reboot);
                completed++;
                if (result.RequiresReboot) return new(completed, result with { PostcheckPassed = false,
                    Details = "卸载器要求重启，停止后续安装；重启后请重新执行修复。" }, true);
            }
        }
        catch (OperationCanceledException)
        {
            return new(completed, new(null, "已停止后续卸载或达到预算；取消不代表已卸载组件已恢复。", PostcheckPassed: false), reboot);
        }
        return new(completed, null, reboot);
    }
}
