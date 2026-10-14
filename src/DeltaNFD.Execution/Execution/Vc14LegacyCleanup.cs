namespace DeltaNFD.Services;

internal interface IVc14LegacyEnvironment
{
    Vc14LegacyReport Detect();
    string? Blocker();
    string CaptureCore();
    string Backup();
    void Remove(Vc14LegacyEntry entry);
}
internal static class Vc14LegacyCleanup
{
    internal static OperationResult Run(IVc14LegacyEnvironment environment, string fingerprint, IProgress<string>? progress, CancellationToken token)
    {
        var changed = false; var backup = "";
        OperationResult Fail(string text, bool unknown = false) => new() { Success = false, IsPartial = changed && !unknown,
            OutcomeUnknown = unknown, DiagnosticsDirectory = backup,
            Message = text + (changed ? "\n已执行部分旧包装处理，备份保留，请核对当前状态。" : "\n未清理任何旧包装。") };
        try
        {
            token.ThrowIfCancellationRequested();
            if (environment.Blocker() is { } blocker) return Fail(blocker);
            var before = environment.Detect();
            if (before.Fingerprint != fingerprint) return Fail("旧包装状态已变化，请重新检测并确认。");
            if (before.Issues.Count > 0 || before.Entries.Any(e => !e.CanProcess))
                return Fail(string.Join("\n", before.Issues.Concat(before.Entries.Where(e => !e.CanProcess).Select(e => e.Detail))));
            if (before.Entries.Count == 0) return OperationResult.Ok("未发现需要处理的旧 INF 包装。");
            var core = environment.CaptureCore();
            backup = environment.Backup();
            // Backup must succeed before any mutation. The backend intent record separately covers process crashes.
            foreach (var entry in before.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (environment.Blocker() is { } blocked) return Fail(blocked);
                if (environment.CaptureCore() != core) return Fail("运行库组件或 DLL 在处理期间发生变化，已停止。");
                var current = environment.Detect();
                if (current.Issues.Count > 0 || !current.Entries.Contains(entry)) return Fail("旧包装身份发生变化，已停止。");
                progress?.Report("备份完成，处理已核验旧包装：" + entry.Name);
                changed = true;
                environment.Remove(entry);
                if (environment.CaptureCore() != core) return Fail("处理后运行库或 DLL 变化，未报告成功。", true);
            }
            var after = environment.Detect();
            if (after.Entries.Count > 0 || after.Issues.Count > 0) return Fail("仍有旧包装或状态无法确认，请查看备份和检测详情。");
            return new() { Success = true, DiagnosticsDirectory = backup,
                Message = "已备份并清理核验过的旧 14.x INF 包装登记及对应 INF/PNF；14.42 MSI 与核心 DLL 保持不变。\n备份：" + backup };
        }
        catch (OperationCanceledException) { return Fail("已停止后续旧包装处理。", changed); }
        catch (Exception ex) { return Fail("旧包装处理未完成：" + ex.Message, changed); }
    }
}
