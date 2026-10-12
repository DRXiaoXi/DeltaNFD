namespace DeltaNFD.Services;

internal sealed record RuntimeRemovalResult(int Completed, RuntimeStepResult? Stopped, bool Reboot);

/// <summary>Both repair entry points share the same verified removal phase before installing anything.</summary>
internal static class RuntimePreUninstall
{
    internal static async Task<RuntimeRemovalResult> RunAsync(VcRedistScanReport before,
        Func<IReadOnlyList<VcRedistEntry>, CancellationToken, Task<IReadOnlyList<VcRedistEntry>>> prepare,
        Func<VcRedistEntry, TimeSpan, IProgress<string>?, CancellationToken, Task<RuntimeStepResult>> uninstall,
        Func<CancellationToken, Task<VcRedistScanReport>> rescan,
        Func<TimeSpan, TimeSpan> remaining, RuntimeRepairDiagnostics diagnostics, IProgress<string>? progress, CancellationToken token)
    {
        var candidates = RuntimeBulkUninstall.Preview(before.All).Entries;
        diagnostics.Save("before-scan.json", before);
        var results = new List<object>(); int completed = 0; bool reboot = false;
        try
        {
            progress?.Report("正在核验整批 C++ 卸载关系、缓存和恢复计划…");
            var targets = await prepare(candidates, token);
            diagnostics.Save("repair-uninstall-plan.json", targets);
            foreach (var entry in targets)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report($"修复前 C++ 卸载 [{completed + 1}/{targets.Count}]：{entry.DisplayName}");
                var result = await uninstall(entry, remaining(RuntimeUnifiedUninstaller.MaximumEntryBudget), progress, token);
                reboot |= result.RequiresReboot;
                results.Add(new { Entry = entry, Result = result }); diagnostics.Save("uninstall-results.json", results);
                if (result.OutcomeUnknown || !result.Success) return new(completed, result, reboot);
                completed++;
                if (result.RequiresReboot) return new(completed, result with { PostcheckPassed = false,
                    Details = "卸载器要求重启，停止后续安装；重启后请重新执行修复。" }, true);
            }
            var after = await rescan(token);
            diagnostics.Save("after-uninstall-scan.json", after);
            var left = RuntimeBulkUninstall.Preview(after.All).Entries;
            if (left.Count != 0) return new(completed, new(null,
                $"卸载复检仍发现 {left.Count} 项 C++ 运行库登记，未启动安装。请核对保留依赖或新增安装。", PostcheckPassed: false), reboot);
        }
        catch (OperationCanceledException)
        {
            return new(completed, new(null, "已停止后续卸载或达到预算；取消不代表已卸载组件已恢复。", PostcheckPassed: false), reboot);
        }
        catch (Exception ex)
        {
            return new(completed, new(null, "整批卸载未完成，未启动后续安装：" + ex.Message,
                PostcheckPassed: false), reboot);
        }
        return new(completed, null, reboot);
    }
}
