using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

namespace DeltaNFD.Backend.Procman;

/// <summary>
/// 进程管理后端的命令实现（procman）：通用进程枚举/详情/能力读取 + 手动属性修改/还原/释放 + 核对/恢复。
///
/// 处理器只做参数校验与结果包装；所有写入都在引擎内完成「身份复核→控制权→写前记录→执行→回读」。
/// 修改类命令落盘会话标记，后端停止时由 OnStopping 先还原全部通用控制。
/// </summary>
internal static class ProcmanCommands
{
    // 延迟创建：引擎读写 AppDataPaths 数据根，必须在 BackendHost 配置隔离数据根之后再实例化。
    private static readonly Lazy<ProcmanEngine> EngineLazy = new(() => new ProcmanEngine());
    private static ProcmanEngine Engine => EngineLazy.Value;
    private static bool _sessionModified;

    static ProcmanCommands()
    {
        var handlers = (Dictionary<string, Func<BackendCommandContext, Task<BackendResult>>>)Options.Handlers;
        foreach (var command in handlers.Keys.ToArray())
        {
            if (!BackendModules.IsMutating(BackendModules.Procman, command)) continue;
            var handler = handlers[command];
            handlers[command] = async context =>
            {
                // Recover 是按记录的恢复核对流程，不产生新的未知操作标记。
                if (context.Command is not (BackendCommands.ProcmanRecover or BackendCommands.ProcmanMemoryTrim)) _sessionModified = true;
                var result = await handler(context);
                if (context.Command == BackendCommands.ProcmanRestoreAll && result.Status == BackendResultStatuses.Ok)
                    _sessionModified = false;
                return result;
            };
        }
    }

    public static readonly BackendHostOptions Options = new()
    {
        Module = BackendModules.Require(BackendModules.Procman),
        InitialPriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal,
        OnStopping = () => EngineLazy.IsValueCreated && (_sessionModified || Engine.Snapshot().RulesEngineRunning)
            ? Engine.RestoreAllAsync() : Task.FromResult(OperationResult.Ok("只读会话已停止；遗留日志保持不变。")),
        StatusSource = BuildStatus,
        Handlers = new Dictionary<string, Func<BackendCommandContext, Task<BackendResult>>>(StringComparer.Ordinal)
        {
            [BackendCommands.ProcmanStatus] = StatusAsync,
            [BackendCommands.ProcmanActivate] = ActivateAsync,
            [BackendCommands.ProcmanList] = ListAsync,
            [BackendCommands.ProcmanDetail] = DetailAsync,
            [BackendCommands.ProcmanCapability] = DetailAsync,
            [BackendCommands.ProcmanCpuTopology] = TopologyAsync,
            [BackendCommands.ProcmanReconcile] = ReconcileAsync,
            [BackendCommands.ProcmanSet] = SetAsync,
            [BackendCommands.ProcmanReset] = ResetAsync,
            [BackendCommands.ProcmanRelease] = ReleaseAsync,
            [BackendCommands.ProcmanRestoreAll] = RestoreAllAsync,
            [BackendCommands.ProcmanRecover] = RecoverAsync,
            [BackendCommands.ProcmanRulesList] = RulesListAsync,
            [BackendCommands.ProcmanRulesSave] = RulesSaveAsync,
            [BackendCommands.ProcmanRulesDelete] = RulesDeleteAsync,
            [BackendCommands.ProcmanMemoryPreview] = MemoryPreviewAsync,
            [BackendCommands.ProcmanMemoryTrim] = MemoryTrimAsync,
            [BackendCommands.ProcmanBackgroundGet] = context => Task.FromResult(BackendResult.Ok(context.Command, "后台管理设置。", Engine.BackgroundStatus())),
            [BackendCommands.ProcmanBackgroundSave] = context =>
            {
                var result = Engine.SaveBackgroundConfig(context.ArgsAs<ProcmanBackgroundConfig>());
                return Task.FromResult(result.Success ? BackendResult.Ok(context.Command, result.Message, Engine.BackgroundStatus()) : BackendResult.Failed(context.Command, result.Message));
            },
            [BackendCommands.ProcmanFrameBackgroundSync] = context =>
            {
                var result = Engine.SyncFrameBackground();
                return Task.FromResult(result.Success ? BackendResult.Ok(context.Command, result.Message, result) : BackendResult.Failed(context.Command, result.Message));
            },
            [BackendCommands.ProcmanNativeProbe] = NativeProbeAsync,
        },
    };

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
        => Task.FromResult(BackendResult.Ok(context.Command, "进程管理后端就绪。", Engine.Snapshot()));

    private static async Task<BackendResult> MemoryPreviewAsync(BackendCommandContext context)
    {
        var preview = await Engine.PreviewMemoryAsync(context.CancellationToken);
        return BackendResult.Ok(context.Command, preview.Message, preview);
    }

    private static Task<BackendResult> MemoryTrimAsync(BackendCommandContext context)
    {
        var report = Engine.TrimMemory(context.ArgsAs<ProcmanMemoryTrimArgs>(), context.CancellationToken);
        // Every requested target settles separately; skipped/failed targets are known outcomes,
        // not an obligation to pretend that discarded working-set pages can be restored.
        return Task.FromResult(BackendResult.Ok(context.Command, report.Message, report));
    }

    private static async Task<BackendResult> ActivateAsync(BackendCommandContext context)
    {
        if (OfflineModeGuard.BlocksNormalAutomation) return BackendResult.Failed(context.Command, "旧版设置恢复完成前不启动通用进程规则。");
        var recovered = await Engine.RestoreAllAsync();
        if (!recovered.Success) return BackendResult.Failed(context.Command, "上轮恢复未完成，未启动规则：" + recovered.Message);
        var result = Engine.ActivateBackground();
        _sessionModified = Engine.Snapshot().RulesEngineRunning;
        return result.Success ? BackendResult.Ok(context.Command, result.Message) : BackendResult.Failed(context.Command, result.Message);
    }

    private static Task<BackendResult> ListAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<ProcmanListArgs>();
        var result = Engine.List(args);
        return Task.FromResult(BackendResult.Ok(context.Command,
            $"枚举到 {result.TotalCount} 个进程（本页 {result.Processes.Count} 个）。{result.Note}", result));
    }

    private static Task<BackendResult> DetailAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<ProcmanDetailArgs>();
        var result = Engine.Detail(args);
        return Task.FromResult(result.IdentityOk
            ? BackendResult.Ok(context.Command, $"PID {result.Process.Pid} 详情读取完成。", result)
            : BackendResult.Failed(context.Command, result.IdentityError));
    }

    private static Task<BackendResult> TopologyAsync(BackendCommandContext context)
    {
        var topology = ProcmanCpuTopologyReader.Read();
        return Task.FromResult(BackendResult.Ok(context.Command,
            $"本机 {topology.Groups.Count} 个处理器组，{topology.Processors.Count} 个逻辑 CPU。",
            topology));
    }

    private static Task<BackendResult> ReconcileAsync(BackendCommandContext context)
    {
        var snapshot = Engine.Reconcile();
        var generic = BuildRecoverySnapshot(snapshot);
        return Task.FromResult(BackendResult.Ok(context.Command, snapshot.Summary, new
        { snapshot.Entries, snapshot.Summary, generic.Module, generic.Complete, generic.Items, generic.RecoveryScopes }));
    }

    private static ReconcileSnapshot BuildRecoverySnapshot(ProcmanReconcileResult snapshot) => new()
    {
        Module = BackendModules.Procman, Summary = snapshot.Summary,
        Complete = !snapshot.Entries.Any(e => e.Message.Contains("无法读取") || e.Message.Contains("身份未知")),
        Items = snapshot.Entries.Select(e => new ReconcileItem { Id = e.Pid + ":" + e.Capability, Kind = "procman", State = e.Status, HasBackup = true, Detail = e.Message }).ToArray(),
        RecoveryScopes = [new() { Id = "all", Description = "停止规则并按日志恢复全部进程和应用设置", Available = Engine.Snapshot().HeldChangeCount > 0, AffectedCount = Engine.Snapshot().HeldChangeCount }],
    };

    private static Task<BackendResult> SetAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<ProcmanSetArgs>();
        var result = Engine.Set(args);
        if (!result.IdentityOk) return Task.FromResult(BackendResult.Failed(context.Command, result.IdentityError));
        if (result.Outcomes.Count == 0)
            return Task.FromResult(BackendResult.Failed(context.Command,
                string.IsNullOrEmpty(result.IdentityError) ? "没有可执行的属性修改。" : result.IdentityError));
        var applied = result.Outcomes.Count(o => o.Status == "applied");
        var rejected = result.Outcomes.Count(o => o.Status == "rejected");
        var failed = result.Outcomes.Count(o => o.Status == "failed");
        var message = $"修改完成：生效 {applied}，拒绝 {rejected}，失败 {failed}。";
        if (applied == result.Outcomes.Count)
            return Task.FromResult(BackendResult.Ok(context.Command, message, result));
        // 逐项结果全部已知（拒绝/已回滚/受保护零操作）不建立未确认阻塞；只有真正留下恢复义务的项 requiresReview=true。
        return Task.FromResult(BackendResult.PartialResult(context.Command, message,
            result.Outcomes.Select(o => new BackendResultItem(o.Capability, o.Status, o.Message)).ToArray(),
            result, requiresReview: result.RequiresReview));
    }

    private static Task<BackendResult> ResetAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<ProcmanResetArgs>();
        var result = Engine.Reset(args);
        if (!result.IdentityOk) return Task.FromResult(BackendResult.Failed(context.Command, result.IdentityError));
        return Task.FromResult(WrapOutcomes(context.Command, "还原", result));
    }

    private static Task<BackendResult> ReleaseAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<ProcmanReleaseArgs>();
        var result = Engine.Release(args);
        if (!result.IdentityOk) return Task.FromResult(BackendResult.Failed(context.Command, result.IdentityError));
        return Task.FromResult(WrapOutcomes(context.Command, "释放接管", result));
    }

    private static async Task<BackendResult> RestoreAllAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在还原通用进程控制…", null, "procman.restore-all");
        var result = await Engine.RestoreAllAsync();
        return result.Success
            ? BackendResult.Ok(context.Command, result.Message, result)
            : BackendResult.Failed(context.Command, result.Message);
    }

    private static async Task<BackendResult> RecoverAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<ProcmanRecoverArgs>();
        context.ReportProgress("正在按记录还原通用进程控制…", null, "procman.recover");
        var outcome = await Engine.RecoverAsync(args.Scope ?? "");
        var snapshot = BuildRecoverySnapshot(Engine.Reconcile());
        var result = new RecoverOutcome { Scope = string.IsNullOrEmpty(args.Scope) ? "all" : args.Scope,
            Success = outcome.Success, Verified = outcome.Success && snapshot.Complete && Engine.Snapshot().HeldChangeCount == 0,
            Message = outcome.Message, VerifiedSnapshot = snapshot };
        return outcome.Success ? BackendResult.Ok(context.Command, outcome.Message, result)
            : BackendResult.PartialResult(context.Command, outcome.Message, [], result);
    }

    private static BackendResult WrapOutcomes(string command, string what, ProcmanSetResult result)
    {
        if (result.Outcomes.Count == 0)
            return BackendResult.Ok(command, $"{what}：没有需要处理的修改记录。", result);
        var restored = result.Outcomes.Count(o => o.Status == "restored");
        var conflict = result.Outcomes.Count(o => o.Status == "conflict");
        var failed = result.Outcomes.Count(o => o.Status == "failed");
        var message = $"{what}完成：成功 {restored}，冲突 {conflict}，失败 {failed}。";
        return restored == result.Outcomes.Count
            ? BackendResult.Ok(command, message, result)
            : BackendResult.PartialResult(command, message,
                result.Outcomes.Select(o => new BackendResultItem(o.Capability, o.Status, o.Message)).ToArray(),
                result, requiresReview: result.RequiresReview);
    }

    // ---------------- P2：持久规则 ----------------

    private static Task<BackendResult> RulesListAsync(BackendCommandContext context)
    {
        var result = Engine.ListRules();
        return Task.FromResult(BackendResult.Ok(context.Command,
            $"读取到 {result.Rules.Count} 条进程规则。", result));
    }

    private static Task<BackendResult> RulesSaveAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<ProcmanRulesSaveArgs>();
        var result = Engine.SaveRule(args.Rule);
        return Task.FromResult(result.Success
            ? BackendResult.Ok(context.Command, result.Message, result)
            : BackendResult.Failed(context.Command, result.Message));
    }

    private static async Task<BackendResult> RulesDeleteAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<ProcmanRulesDeleteArgs>();
        var result = await Engine.DeleteRuleAsync(args.ExecutablePath, args.Capability);
        return result.Success
            ? BackendResult.Ok(context.Command, result.Message, result)
            : BackendResult.Failed(context.Command, result.Message);
    }

    private static Task<BackendResult> NativeProbeAsync(BackendCommandContext context)
    {
        // 只读探针：报告本构建上各能力的可用性判断依据（缺失接口/权限/支持）。
        var masks = ProcmanTopology.SystemGroupMasks();
        // I/O 优先级兼容层：用当前进程做一次只读回环探测（不修改值）。
        var ioRead = ProcmanCapabilities.ReadIoPriority(Environment.ProcessId);
        return Task.FromResult(BackendResult.Ok(context.Command, "Native 能力探针（只读）完成。",
            new ProcmanNativeProbeResult
            {
                ProcessorGroups = masks.Count,
                GroupMasks = masks.Select(m => new ProcmanGroupMask(m.Group, m.Mask)).ToList(),
                IoPrioritySupported = ioRead.Ok,
                GpuPrioritySupported = ProcmanCapabilities.ReadGpuPriority(Environment.ProcessId).Ok,
                Notes = (ioRead.Ok
                    ? "I/O 优先级（ntdll 兼容层）读取正常。"
                    : "I/O 优先级读取失败：" + ioRead.Error + "。") +
                    " GPU 调度通过公开 D3DKMT 接口探测；目标驱动或权限不支持时拒绝写入，实时档位不开放。",
            }));
    }
}
