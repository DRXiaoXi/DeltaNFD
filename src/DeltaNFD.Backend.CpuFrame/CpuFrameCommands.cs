using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Execution.CpuFrame;
using DeltaNFD.Services;

namespace DeltaNFD.Backend.CpuFrame;

/// <summary>
/// CPU/帧格后端的命令实现（T4）：两秒共享监控、优先级、硬亲和性/CPU Sets、双单 CCD 调度、
/// DWM、计时器、帧格电源守护、临时优化与只读核对/按备份恢复。
///
/// 后端持有唯一的进程监控与运行态（<see cref="CpuFrameEngine"/>）；前端只通过状态快照读取实际
/// 生效状态，不再自行建立第二份监控。所有写操作前都在引擎内重新复核目标代次/PID/启动时间/路径。
/// 处理器只做参数校验与结果包装，绝不接受前端传入的注册表路径、脚本或 shell 命令。
/// </summary>
internal static class CpuFrameCommands
{
    // 延迟创建：引擎会读取 AppDataPaths / 设置，必须在 BackendHost 配置隔离数据根之后再实例化。
    private static readonly Lazy<CpuFrameEngine> EngineLazy = new(() => new CpuFrameEngine());
    private static CpuFrameEngine Engine => EngineLazy.Value;
    private static bool _sessionModified;

    static CpuFrameCommands()
    {
        var handlers = (Dictionary<string, Func<BackendCommandContext, Task<BackendResult>>>)Options.Handlers;
        foreach (var command in handlers.Keys.ToArray())
        {
            if (!BackendModules.IsMutating(BackendModules.CpuFrame, command)) continue;
            var handler = handlers[command];
            handlers[command] = async context =>
            {
                // Recover rejects unknown scopes before touching the engine. Preserve that read-only rejection.
                if (context.Command != BackendCommands.CpuFrameRecover) _sessionModified = true;
                var result = await handler(context);
                if (context.Command == BackendCommands.CpuFrameRestore && result.Status == BackendResultStatuses.Ok
                    && !result.PendingRestore && result.Data is { } data && BackendJson.Deserialize<OperationResult>(data).Success)
                    _sessionModified = false;
                return result;
            };
        }
    }

    public static readonly BackendHostOptions Options = new()
    {
        Module = BackendModules.Require(BackendModules.CpuFrame),
        OnStopping = () => _sessionModified ? Engine.RestoreAllAsync() : Task.FromResult(OperationResult.Ok("只读会话已停止。")),
        StatusSource = BuildStatus,
        Handlers = new Dictionary<string, Func<BackendCommandContext, Task<BackendResult>>>(StringComparer.Ordinal)
        {
            [BackendCommands.CpuFrameStatus] = StatusAsync,
            [BackendCommands.CpuFrameMonitorStart] = MonitorStartAsync,
            [BackendCommands.CpuFrameMonitorStop] = MonitorStopAsync,
            [BackendCommands.CpuFramePriorityApply] = PriorityApplyAsync,
            [BackendCommands.CpuFrameAffinityApply] = AffinityApplyAsync,
            [BackendCommands.CpuFrameCcdApply] = CcdApplyAsync,
            [BackendCommands.CpuFrameCcdRevert] = CcdRevertAsync,
            [BackendCommands.CpuFrameSingleCcdApply] = SingleCcdApplyAsync,
            [BackendCommands.CpuFrameSingleCcdRevert] = SingleCcdRevertAsync,
            [BackendCommands.CpuFrameRestore] = RestoreAsync,
            [BackendCommands.CpuFrameDwmRestart] = DwmRestartAsync,
            [BackendCommands.CpuFramePowerLock] = PowerLockAsync,
            [BackendCommands.CpuFramePowerUnlock] = PowerUnlockAsync,
            [BackendCommands.CpuFrameTimerApply] = TimerApplyAsync,
            [BackendCommands.CpuFrameTimerRestore] = TimerRestoreAsync,
            [BackendCommands.CpuFrameTweaksApply] = TweaksApplyAsync,
            [BackendCommands.CpuFrameTweaksRevert] = TweaksRevertAsync,
            [BackendCommands.CpuFrameBoostApply] = BoostApplyAsync,
            [BackendCommands.CpuFrameMemoryClean] = MemoryCleanAsync,
            [BackendCommands.CpuFrameTopology] = TopologyAsync,
            [BackendCommands.CpuFrameAffinityCheck] = AffinityCheckAsync,
            [BackendCommands.CpuFrameAffinityPending] = AffinityPendingAsync,
            [BackendCommands.CpuFrameMemoryLimitDetect] = MemoryLimitDetectAsync,
            [BackendCommands.CpuFrameMemoryLimitRemove] = MemoryLimitRemoveAsync,
            [BackendCommands.CpuFramePowerIncomplete] = PowerIncompleteAsync,
            [BackendCommands.CpuFramePowerPreserve] = PowerPreserveAsync,
            [BackendCommands.CpuFrameReconcile] = ReconcileAsync,
            [BackendCommands.CpuFrameRecover] = RecoverAsync,
            [BackendCommands.CpuFrameOfflineRestore] = async c =>
            {
                var result = await OfflineCpuSetRecovery.RestoreAsync(c.CancellationToken);
                return result.Success ? BackendResult.Ok(c.Command, result.Message, result)
                    : BackendResult.Failed(c.Command, result.Message);
            },
        },
    };

    /// <summary>空闲状态推送：后端空闲时由宿主轮询写入 status 帧，携带真实生效状态快照。</summary>
    private static BackendStatus? BuildStatus()
    {
        try
        {
            var snapshot = Engine.Snapshot();
            return new BackendStatus(BackendMessages.StateIdle, null,
                JsonSerializer.SerializeToElement(snapshot, BackendJson.SerializerOptions));
        }
        catch
        {
            return null; // 状态生成失败不阻断命令执行，下一轮重试。
        }
    }

    private static Task<BackendResult> StatusAsync(BackendCommandContext context)
    {
        var snapshot = Engine.Snapshot();
        return Task.FromResult(BackendResult.Ok(context.Command,
            $"监控{(snapshot.MonitorRunning ? "运行中" : "已停止")}；游戏{(snapshot.GameRunning ? "运行中" : "未运行")}。", snapshot));
    }

    private static async Task<BackendResult> MonitorStartAsync(BackendCommandContext context)
    {
        var result = await Engine.StartMonitorAsync();
        return Wrap(context.Command, result);
    }

    private static async Task<BackendResult> MonitorStopAsync(BackendCommandContext context)
    {
        var result = await Engine.StopMonitorAsync();
        return Wrap(context.Command, result);
    }

    private static Task<BackendResult> PriorityApplyAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<CpuFrameToggleArgs>();
        var result = Engine.ApplyPriorityAsync(args.Enabled);
        return Task.FromResult(Wrap(context.Command, result));
    }

    private static async Task<BackendResult> AffinityApplyAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<CpuFrameAffinityArgs>();
        var result = await Engine.ApplyAffinityAsync(args.Mask);
        return Wrap(context.Command, result);
    }

    private static async Task<BackendResult> CcdApplyAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<CpuFrameCcdArgs>();
        if (args.GameCcdIndex is < 0 or > 1)
            return BackendResult.Failed(context.Command, "gameCcdIndex 必须是 0 或 1。");
        var result = await Engine.ApplyDualCcdAsync(args.GameCcdIndex);
        return Wrap(context.Command, result);
    }

    private static async Task<BackendResult> CcdRevertAsync(BackendCommandContext context)
    {
        var result = await Engine.RevertDualCcdAsync();
        return Wrap(context.Command, result);
    }

    private static async Task<BackendResult> SingleCcdApplyAsync(BackendCommandContext context)
    {
        var result = await Engine.ApplySingleCcdAsync();
        return Wrap(context.Command, result);
    }

    private static async Task<BackendResult> SingleCcdRevertAsync(BackendCommandContext context)
    {
        var result = await Engine.RevertSingleCcdAsync();
        return Wrap(context.Command, result);
    }

    private static async Task<BackendResult> RestoreAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在还原帧格执行状态…", null, "restore");
        var result = await Engine.RestoreAllAsync();
        return Wrap(context.Command, result);
    }

    private static Task<BackendResult> DwmRestartAsync(BackendCommandContext context)
        => Task.FromResult(Wrap(context.Command, Engine.RestartDwmAsync()));

    private static async Task<BackendResult> PowerLockAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<CpuFramePowerLockArgs>();
        var result = await Engine.SetPowerLockAsync(args.Enabled, args.TargetGuid ?? "");
        return Wrap(context.Command, result);
    }

    private static async Task<BackendResult> PowerUnlockAsync(BackendCommandContext context)
    {
        var result = await Engine.SetPowerLockAsync(false, "");
        return Wrap(context.Command, result);
    }

    private static async Task<BackendResult> TimerApplyAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<CpuFrameToggleArgs>();
        var result = await Engine.ApplyTimerAsync(args.Enabled);
        return Wrap(context.Command, result);
    }

    private static async Task<BackendResult> TimerRestoreAsync(BackendCommandContext context)
    {
        var result = await Engine.ApplyTimerAsync(false);
        return Wrap(context.Command, result);
    }

    private static async Task<BackendResult> TweaksApplyAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<CpuFrameTempArgs>();
        context.ReportProgress("正在应用帧格临时优化…", null, "tweaks.apply");
        var result = await Engine.ApplyTweaksAsync(args);
        return Wrap(context.Command, result);
    }

    private static async Task<BackendResult> TweaksRevertAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在还原帧格临时优化…", null, "tweaks.revert");
        var result = await Engine.RevertTweaksAsync();
        return Wrap(context.Command, result);
    }

    private static async Task<BackendResult> BoostApplyAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<CpuFrameBoostArgs>();
        if (args.CoreParking)
            return Wrap(context.Command, OperationResult.Fail("核心常驻已移除，请使用新版帧格设置。"));
        var result = await Engine.ApplyBoostAsync(args.Timer, args.EcoQos);
        return Wrap(context.Command, result);
    }

    private static Task<BackendResult> MemoryCleanAsync(BackendCommandContext context)
    {
        var (success, message) = Engine.CleanMemoryNow();
        var result = success ? OperationResult.Ok(message) : OperationResult.Fail(message);
        return Task.FromResult(Wrap(context.Command, result));
    }

    private static async Task<BackendResult> TopologyAsync(BackendCommandContext context)
    {
        var topology = await Engine.GetTopologyAsync();
        return BackendResult.Ok(context.Command, $"CPU：{topology.CpuName}（{topology.PhysicalCores} 核 / {topology.LogicalProcessors} 线程）。", topology);
    }

    private static Task<BackendResult> AffinityCheckAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<CpuFrameAffinityCheckArgs>();
        var (ok, reason) = Engine.CanRestoreAffinityRule(args.Mask);
        return Task.FromResult(BackendResult.Ok(context.Command,
            ok ? "可以安全还原 CPU 亲和性原值。" : "CPU 亲和性归属无法确认：" + reason,
            new CpuFrameAffinityCheckResult { Ok = ok, Reason = reason }));
    }

    private static Task<BackendResult> AffinityPendingAsync(BackendCommandContext context)
        => Task.FromResult(BackendResult.Ok(context.Command,
            Engine.HasAffinityRuleRestorePending() ? "仍有待还原的 CPU 亲和性记录。" : "没有待还原的 CPU 亲和性记录。",
            new CpuFramePendingResult { Pending = Engine.HasAffinityRuleRestorePending() }));

    private static async Task<BackendResult> MemoryLimitDetectAsync(BackendCommandContext context)
    {
        var (detected, removedMb, raw) = await Engine.DetectMemoryLimitAsync();
        return BackendResult.Ok(context.Command,
            detected ? $"检测到 bcdedit removememory 限制 {removedMb} MB。" : "未检测到 bcdedit removememory 限制。",
            new CpuFrameMemoryLimitResult { Detected = detected, RemovedMb = removedMb, RawOutput = raw });
    }

    private static async Task<BackendResult> MemoryLimitRemoveAsync(BackendCommandContext context)
    {
        var result = await Engine.RemoveMemoryLimitAsync();
        return Wrap(context.Command, result);
    }

    private static async Task<BackendResult> PowerIncompleteAsync(BackendCommandContext context)
    {
        var backups = await Engine.GetIncompletePowerBackupsAsync();
        return BackendResult.Ok(context.Command, $"读取到 {backups.Count} 条不完整帧格电源备份。", backups);
    }

    private static async Task<BackendResult> PowerPreserveAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<CpuFramePreservePowerArgs>();
        if (args.Confirmed is null || args.Confirmed.Count == 0)
            return BackendResult.Failed(context.Command, "缺少确认清单，已拒绝。");
        context.ReportProgress("正在按用户确认归档旧帧格电源记录…", null, "power.preserve");
        var result = await Engine.PreserveCurrentPowerValuesAsync(args.Confirmed);
        return Wrap(context.Command, result);
    }

    private static Task<BackendResult> ReconcileAsync(BackendCommandContext context)
    {
        var snapshot = Engine.Reconcile();
        return Task.FromResult(BackendResult.Ok(context.Command, snapshot.Summary, snapshot));
    }

    private static async Task<BackendResult> RecoverAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<RecoverArgs>();
        context.ReportProgress("正在按记录还原帧格执行状态…", null, "recover");
        var outcome = await Engine.RecoverAsync(args.Scope ?? "");
        return outcome.Success
            ? BackendResult.Ok(context.Command, outcome.Message, outcome)
            : BackendResult.Failed(context.Command, outcome.Message);
    }

    private static BackendResult Wrap(string command, OperationResult result) =>
        result.Success
            ? BackendResult.Ok(command, result.Message.Length > 0 ? result.Message : "操作已完成。", result)
            : BackendResult.Failed(command, result.Message.Length > 0 ? result.Message : "操作失败。");
}
