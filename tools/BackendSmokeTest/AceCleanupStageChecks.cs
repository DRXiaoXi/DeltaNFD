using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class AceCleanupStageChecks
{
    public static void Run()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); }
        var done = new List<BackendResultItem>();
        var active = false;
        var unknown = false;
        var elevated = true;
        var custom = false;
        var guard = new AceCleanupGuard(() => custom, () => elevated,
            () => unknown ? throw new IOException("fixture") : active ? new[] { "fixture-game" } : Array.Empty<string>());
        Check(guard.Check("first", done, default) is null, "合法条件可进入阶段");
        active = true;
        Check(guard.Check("first", done, default)?.Status == BackendResultStatuses.Failed, "运行中首阶段不执行");
        active = false; unknown = true;
        Check(guard.Check("first", done, default)?.Code == "ace.state-unknown", "未知状态不能当作游戏未运行");
        unknown = false; custom = true;
        Check(guard.Check("first", done, default)?.Code == "ace.custom-target", "自定义模式拒绝 ACE 操作");
        custom = false; elevated = false;
        Check(guard.Check("first", done, default)?.Code == "ace.not-elevated", "权限不足拒绝");
        elevated = true;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Check(guard.Check("first", done, cancelled.Token)?.Status == BackendResultStatuses.Cancelled, "取消不进入阶段");
        var executed = new List<string>();
        foreach (var stage in new[] { "mock-process", "mock-service", "mock-registry", "mock-files" })
        {
            if (guard.Check(stage, done, default) is not null) break;
            executed.Add(stage); done.Add(new(stage, BackendResultStatuses.Ok, "fixture only"));
            if (stage == "mock-service") active = true;
        }
        Check(executed.SequenceEqual(new[] { "mock-process", "mock-service" }), "阶段之间启动游戏后不继续注册表/文件清理");
        Check(guard.Check("later", done, default)?.Status == BackendResultStatuses.Partial, "已改动后中止必须部分成功");
        active = false; unknown = true;
        Check(guard.Check("later", done, default)?.Items.Count == 2, "失败保留已完成明细");
        Check(guard.Check("later", done, cancelled.Token)?.Items.Count == 2, "取消也保留已完成明细");
        Console.WriteLine("ACE cleanup phase guard: 10 checks passed; all mutations simulated. Real cleanup scope and GUI still unverified.");
    }
}
