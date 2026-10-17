using DeltaNFD.Backends;

namespace BackendSmokeTest;

/// <summary>
/// T5 生命周期检查用的「临时测试后端」宿主：本进程被复制并重命名为某个官方后端 EXE
/// （DeltaNFD.Backend.&lt;模块&gt;.exe）后，按该模块的真实白名单注册处理器，完成真实握手/协议，
/// 但处理器只做纯内存动作，绝不触碰系统。参数 <c>testDelayMs</c> 可让命令在途指定毫秒，
/// 用于验证「在途操作阻止交接」「断线保留未知证据」。
/// </summary>
internal static class TestBackendFixture
{
    public static async Task<int> RunAsync(string[] args)
    {
        var moduleId = GetArgument(args, "--backend");
        if (string.IsNullOrWhiteSpace(moduleId) || !BackendModules.All.ContainsKey(moduleId))
        {
            Console.Error.WriteLine("测试后端夹具：缺少或未知 --backend 模块。");
            return 2;
        }

        var spec = BackendModules.Require(moduleId);
        var failRestoreOnce = Environment.GetEnvironmentVariable("DELTANFD_TEST_RESTORE_FAIL_ONCE") == "1";
        _ = int.TryParse(Environment.GetEnvironmentVariable("DELTANFD_TEST_RESTORE_DELAY_MS"), out var restoreDelay);
        _ = int.TryParse(Environment.GetEnvironmentVariable("DELTANFD_TEST_RESTORE_CHILD_MS"), out var restoreChildDelay);
        var recoveryScenario = Environment.GetEnvironmentVariable("DELTANFD_TEST_RECOVERY_SCENARIO");
        var offlineScenario = Environment.GetEnvironmentVariable("DELTANFD_TEST_OFFLINE_RESTORE");
        var aceScenario = Environment.GetEnvironmentVariable("DELTANFD_TEST_ACE_RESULT");
        _ = int.TryParse(Environment.GetEnvironmentVariable("DELTANFD_TEST_OFFLINE_DELAY_MS"), out var offlineDelay);
        var recovered = false;
        DeltaNFD.Services.ReconcileSnapshot RecoverySnapshot() => new()
        {
            Module = moduleId, Summary = "隔离恢复快照。", Complete = recoveryScenario != "incomplete",
            RecoveryScopes = [new DeltaNFD.Services.RecoveryScope { Id = "all", Description = "完整恢复", Available = !recovered }],
        };
        var handlers = new Dictionary<string, Func<BackendCommandContext, Task<BackendResult>>>(StringComparer.Ordinal);
        foreach (var command in spec.Commands)
        {
            handlers[command] = async context =>
            {
                if (context.Command == BackendCommands.CpuFrameRestore && failRestoreOnce)
                {
                    failRestoreOnce = false;
                    return BackendResult.Failed("fixture.restore.failed", "模拟还原失败，记录仍需恢复。");
                }
                // 夹具操作：仅在内存中等待，可被停止以外的取消忽略（模拟不可中断的系统操作）。
                if (context.Command == BackendCommands.CpuFrameRestore && restoreChildDelay > 0)
                {
                    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT")))
                        throw new InvalidOperationException("子进程夹具必须隔离。");
                    using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!,
                        "--deployment-test-child " + restoreChildDelay) { UseShellExecute = false, CreateNoWindow = true });
                }
                var delay = context.ArgInt("testDelayMs");
                if (context.Command == BackendCommands.CpuFrameRestore) delay = Math.Max(delay, restoreDelay);
                if (delay > 0) await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
                if (context.ArgInt("testPartial") == 1)
                    return BackendResult.PartialResult(context.Command, "模拟部分完成", [], pendingRestore: true);
                if (moduleId == BackendModules.Ace)
                {
                    if (context.Command == BackendCommands.AceScan)
                        return BackendResult.Ok(context.Command, "隔离 ACE 扫描", new DeltaNFD.Services.AceScanResult
                        {
                            BlockedByGame = false, BlockingProcesses = [], RunningProcesses = [], RegistryKeys = [], Paths = [],
                            Services = [new DeltaNFD.Services.AceServiceInfo { Name = "fixture", Exists = true }],
                        });
                    if (context.Command == BackendCommands.AceCoreCheck)
                        return BackendResult.Ok(context.Command, "隔离 ACE-CORE 检测", new DeltaNFD.Services.AceCoreCheckResult
                        { Level = DeltaNFD.Services.AceCoreCheckLevel.None, SysCount = 0, Files = [], Summary = "隔离检测", Advice = "" });
                    if (context.Command == BackendCommands.AceReconcile)
                        return BackendResult.Ok(context.Command, "隔离 ACE 核对", new DeltaNFD.Services.ReconcileSnapshot
                        {
                            Module = moduleId, Summary = "不可逆清理，仅核对当前状态。", Complete = aceScenario != "blocked-reconcile",
                            RecoveryScopes = [new DeltaNFD.Services.RecoveryScope { Id = "none", Description = "无自动恢复", Available = false }],
                        });
                    if (context.Command is BackendCommands.AceClean or BackendCommands.AceCoreClean)
                    {
                        var isolated = Environment.GetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT");
                        if (string.IsNullOrWhiteSpace(isolated)) throw new InvalidOperationException("ACE 清理夹具必须使用隔离目录。");
                        File.AppendAllText(Path.Combine(isolated, "ace-fixture-clean.log"), context.Command + "\n");
                        return aceScenario == "partial"
                            ? BackendResult.PartialResult("ace.clean-partial", "模拟遗留后端部分完成", [new("fixture-step", "ok", "已完成模拟项")],
                                DeltaNFD.Services.OperationResult.Ok("旧格式成功数据", requiresReboot: true))
                            : BackendResult.Ok(context.Command, "隔离清理已完成", DeltaNFD.Services.OperationResult.Ok("已完成"));
                    }
                }
                if (context.Command == BackendCommands.CpuFrameAffinityPending)
                    return BackendResult.Ok(context.Command, "隔离亲和性核对", new DeltaNFD.Services.CpuFramePendingResult { Pending = false });
                if (context.Command == BackendCommands.CpuFrameAffinityCheck)
                    return BackendResult.Ok(context.Command, "隔离亲和性归属", new DeltaNFD.Services.CpuFrameAffinityCheckResult { Ok = true });
                if (context.Command == BackendCommands.CpuFrameOfflineRestore && offlineScenario is not null)
                {
                    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT")))
                        throw new InvalidOperationException("脱机恢复夹具必须使用隔离数据根。");
                    if (offlineDelay > 0) await Task.Delay(offlineDelay, CancellationToken.None);
                    var restored = await DeltaNFD.Services.OfflineCpuSetRecovery.RestoreAsync(
                        DeltaNFD.Services.OfflineModeStateStore.Default, TimeSpan.FromSeconds(5), context.CancellationToken,
                        () => true, () =>
                        {
                            if (offlineScenario == "failed") return DeltaNFD.Services.OperationResult.Fail("模拟 CPU Sets 恢复失败");
                            var store = DeltaNFD.Services.OfflineModeStateStore.Default;
                            return store.TryUpdate(s => s.CpuSetChanges.Clear(), out var error)
                                ? DeltaNFD.Services.OperationResult.Ok("隔离 CPU Sets 恢复完成") : DeltaNFD.Services.OperationResult.Fail(error);
                        });
                    return restored.Success ? BackendResult.Ok(context.Command, restored.Message, restored)
                        : BackendResult.Failed(context.Command, restored.Message);
                }
                if (moduleId == BackendModules.CpuFrame && recoveryScenario is not null)
                {
                    if (context.Command == BackendCommands.CpuFrameReconcile)
                        return BackendResult.Ok(context.Command, "隔离核对", RecoverySnapshot());
                    if (context.Command == BackendCommands.CpuFrameRecover)
                    {
                        var scope = context.ArgsAs<DeltaNFD.Services.RecoverArgs>().Scope;
                        recovered = recoveryScenario != "failed";
                        var snapshot = RecoverySnapshot();
                        if (recoveryScenario == "remaining") snapshot = new DeltaNFD.Services.ReconcileSnapshot
                        {
                            Module = moduleId, Summary = "仍有未恢复范围。", Complete = true,
                            RecoveryScopes = [new DeltaNFD.Services.RecoveryScope { Id = "power", Description = "未恢复电源", Available = true }],
                        };
                        if (recoveryScenario == "wrong-module") snapshot = new DeltaNFD.Services.ReconcileSnapshot
                        { Module = BackendModules.System, Summary = "错误模块", Complete = true };
                        return BackendResult.Ok(context.Command, "隔离恢复结果。", new DeltaNFD.Services.RecoverOutcome
                        {
                            Scope = scope, Success = recoveryScenario != "failed", Verified = recoveryScenario != "failed",
                            Message = "隔离恢复结果。", VerifiedSnapshot = snapshot,
                        });
                    }
                }
                if (Environment.GetEnvironmentVariable("DELTANFD_TEST_DEPLOYMENT_RECOVERY") is { } deployment
                    && context.Command is BackendCommands.RuntimeRecover or BackendCommands.CpuFrameRecover)
                {
                    var scope = context.ArgsAs<DeltaNFD.Services.RecoverArgs>().Scope;
                    return BackendResult.Ok(context.Command, "隔离部署恢复结果", new DeltaNFD.Services.RecoverOutcome
                    {
                        Scope = scope, Success = deployment != "failed", Verified = deployment != "failed",
                        Message = "模拟部署恢复：" + deployment,
                        VerifiedSnapshot = new DeltaNFD.Services.ReconcileSnapshot
                        { Module = moduleId, Complete = deployment != "incomplete", Summary = "隔离复核，不执行真实恢复" },
                    });
                }
                if (context.Command == BackendCommands.CpuFrameRestore)
                    return BackendResult.Ok(context.Command, "模拟完整还原已复核。", DeltaNFD.Services.OperationResult.Ok("已还原"));
                if (context.Command == BackendCommands.SystemDefenderPostApply)
                    return BackendResult.Ok(context.Command, "模拟 Defender 清理已复核。", new DeltaNFD.Services.DefenderPostApplyResult());
                if (moduleId == BackendModules.CpuFrame && BackendModules.IsMutating(moduleId, context.Command))
                    return BackendResult.Ok(context.Command, "模拟 CPU 操作已完成。", DeltaNFD.Services.OperationResult.Ok("已完成"));
                return BackendResult.Ok("fixture.ok", "临时测试后端已完成内存操作。", new { Fixture = true },
                    pendingRestore: context.ArgInt("testRetainedBackup") == 1);
            };
        }

        return await BackendHost.RunAsync(new BackendHostOptions
        {
            Module = spec,
            Handlers = handlers,
            OnStopping = Environment.GetEnvironmentVariable("DELTANFD_TEST_STOP_FAIL") == "1"
                ? () => Task.FromResult(DeltaNFD.Services.OperationResult.Fail("模拟停止收尾失败")) : null,
        }, args);
    }

    private static string? GetArgument(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.Ordinal));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
