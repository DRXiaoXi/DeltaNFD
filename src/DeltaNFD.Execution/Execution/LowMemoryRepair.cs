using DeltaNFD.Inspection;

namespace DeltaNFD.Services;

internal interface ILowMemoryRepairEnvironment
{
    string? Blocker();
    Task<LowMemoryStatus> ReadAsync();
    Task<OperationResult> ApplyAsync(LowMemoryAction action);
}
internal static class LowMemoryRepairEngine
{
    internal static async Task<OperationResult> RunAsync(ILowMemoryRepairEnvironment env, LowMemoryApplyArgs args, CancellationToken token)
    {
        if (!Enum.IsDefined(args.Action)) return OperationResult.Fail("未知的低内存配置项。");
        token.ThrowIfCancellationRequested();
        if (env.Blocker() is { } blocker) return OperationResult.Fail(blocker);
        var before = await env.ReadAsync();
        if (!before.IsLowCapacity || !before.CanCheckTarget(args.Action)) return OperationResult.Fail("物理内存或该项配置未确认，不执行低内存修正。");
        if (args.Fingerprint != before.Fingerprint) return OperationResult.Fail("配置已变化，请重新检测并确认。");
        if (before.Needs(args.Action) != true) return OperationResult.Ok("该项已符合建议，无需修改。");
        token.ThrowIfCancellationRequested();
        if (env.Blocker() is { } changed) return OperationResult.Fail(changed);
        try
        {
            var result = await env.ApplyAsync(args.Action);
            if (!result.Success) return args.Action == LowMemoryAction.Prefetch ? Unconfirmed(result.Message) : result;
            var after = await env.ReadAsync();
            if (!after.IsLowCapacity || !after.CanCheckTarget(args.Action) || after.Needs(args.Action) != false)
                return Unconfirmed("已提交设置，但复检未通过。");
        }
        catch (Exception ex) { return Unconfirmed("步骤未完成：" + ex.Message); }
        return OperationResult.Ok("该项配置已复检通过；全部处理后请保存工作并手动重启电脑。", requiresReboot: true);
    }
    private static OperationResult Unconfirmed(string message) => new() { Success = false,
        Message = message + " 原始备份保留，请重新核对；未继续后续步骤。", OutcomeUnknown = true, RequiresReboot = true };
}
public static class LowMemoryRepair
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task<OperationResult> RunAsync(LowMemoryApplyArgs args, CancellationToken token)
    {
        if (!await Gate.WaitAsync(0, token)) return OperationResult.Fail("低内存配置正在处理，请等待结束。");
        try { return await LowMemoryRepairEngine.RunAsync(new WindowsEnvironment(), args, token); }
        finally { Gate.Release(); }
    }
    private sealed class WindowsEnvironment : ILowMemoryRepairEnvironment
    {
        public string? Blocker()
        {
            if (!ElevationHelper.IsElevated) return ElevationHelper.NotElevatedMessage;
            try { return DeltaForceLocator.IsGameRunning() ? "请先退出三角洲再调整内存配置。" : null; }
            catch { return "无法确认游戏运行状态，未修改。"; }
        }
        public Task<LowMemoryStatus> ReadAsync() => LowMemoryInspection.ReadAsync();
        public Task<OperationResult> ApplyAsync(LowMemoryAction action) => action == LowMemoryAction.Prefetch
            ? new SystemTweakService().DisableAsync(SystemTweak.Prefetch)
            : new SystemTweakService().EnableMemorySupportAsync(action);
    }
}
