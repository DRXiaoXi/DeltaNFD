using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>帧格总开关的前置检查；旧记录走核对流程，普通开启/退出不得覆盖它。</summary>
internal static class FrameBackendReadiness
{
    internal static IReadOnlyList<string> RequiredModules(bool includesGpu) => includesGpu
        ? [BackendModules.CpuFrame, BackendModules.Gpu] : [BackendModules.CpuFrame];

    internal static string DescribeBlocker(bool includesGpu)
    {
        var modules = RequiredModules(includesGpu);
        foreach (var module in modules) BackendUnconfirmedStore.ArchiveCompletedStaticBackup(module);
        var pending = BackendUnconfirmedStore.PendingModules().Where(modules.Contains)
            .Concat(BackendSessions.Snapshot().Where(c => modules.Contains(c.ModuleId) && c.RequiresAttention).Select(c => c.ModuleId))
            .Distinct().ToArray();
        return pending.Length == 0 ? "" : string.Join("、", pending.Select(BackendLifecycle.ModuleDisplayName))
            + "有上次尚未核对的操作，帧格暂时不能开启或退出。请核对并恢复旧状态，或在核对后确认保留当前状态，再重试。"
            + "\n也可以在「设置 → 核对未确认操作」中处理；旧记录和原值备份会保留。";
    }

    internal static async Task<OperationResult> RunAsync(Func<Task<OperationResult>> operation, string action, bool includesGpu)
    {
        try
        {
            BackendOperationGate.Check();
            var blocker = DescribeBlocker(includesGpu);
            if (blocker.Length > 0) return OperationResult.Fail(blocker);
            return await operation();
        }
        catch (Exception ex)
        {
            Log.Error("帧格" + action + "未完成", ex);
            var blocker = DescribeBlocker(includesGpu);
            return OperationResult.Fail(blocker.Length > 0 ? blocker : "帧格" + action + "未完成：" + ex.Message);
        }
    }
}
