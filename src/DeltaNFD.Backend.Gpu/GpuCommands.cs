using DeltaNFD.Backends;
using DeltaNFD.Services;

namespace DeltaNFD.Backend.Gpu;

/// <summary>
/// 显卡后端的命令实现：型号伪装、伪装失效检测、NVAPI 驱动配置与 nvdrsdb 只读锁定。
/// 参数校验与权限复核在执行库内完成；处理器只做调用与结果包装。
/// </summary>
internal static class GpuCommands
{
    public static readonly BackendHostOptions Options = new()
    {
        Module = BackendModules.Require(BackendModules.Gpu),
        Handlers = new Dictionary<string, Func<BackendCommandContext, Task<BackendResult>>>(StringComparer.Ordinal)
        {
            [BackendCommands.GpuSpoofStatus] = SpoofStatusAsync,
            [BackendCommands.GpuSpoofApply] = SpoofApplyAsync,
            [BackendCommands.GpuSpoofRestore] = SpoofRestoreAsync,
            [BackendCommands.GpuSpoofReload] = SpoofReloadAsync,
            [BackendCommands.GpuWatchEvaluate] = WatchEvaluateAsync,
            [BackendCommands.GpuDriverStatus] = DriverStatusAsync,
            [BackendCommands.GpuDriverRead] = DriverReadAsync,
            [BackendCommands.GpuDriverApply] = DriverApplyAsync,
            [BackendCommands.GpuDriverRestore] = DriverRestoreAsync,
            [BackendCommands.GpuDriverLock] = DriverLockAsync,
            [BackendCommands.GpuReconcile] = ReconcileAsync,
            [BackendCommands.GpuRecover] = RecoverAsync,
        },
    };

    /// <summary>只读状态核对：适配器当前型号、伪装与否、可恢复原值，以及失效检测结果。</summary>
    private static async Task<BackendResult> ReconcileAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在只读核对显卡状态…", null, "reconcile");
        var snapshot = await BuildSnapshotAsync();
        return BackendResult.Ok("gpu.reconcile", snapshot.Summary, snapshot);
    }

    /// <summary>按可信备份恢复显卡原始型号，恢复后再次只读复核；复核不通过不报告成功。</summary>
    private static async Task<BackendResult> RecoverAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<RecoverArgs>();
        var scope = (args.Scope ?? "").Trim().ToLowerInvariant();
        if (scope != "spoof")
            return BackendResult.Failed("gpu.recover", $"未知恢复范围“{args.Scope}”，已拒绝执行。");

        context.ReportProgress("正在按备份恢复显卡原始型号…", null, "recover");
        var service = new GpuSpoofService();
        var targets = (await service.GetAdaptersAsync()).Where(a => a.HasBackup).ToList();
        var messages = new List<string>();
        var ok = true;
        foreach (var adapter in targets)
        {
            var restored = await service.RestoreByPathAsync(adapter.RegistryPath);
            messages.Add($"{adapter.DisplayName}：{restored.Message}");
            if (!restored.Success) ok = false;
        }
        if (targets.Count == 0) messages.Add("没有需要恢复的显卡伪装（无可信原值备份）。");

        var snapshot = await BuildSnapshotAsync();
        var stillMasked = snapshot.Items.Any(i => i.Kind == "adapter" && i.State.StartsWith("已伪装", StringComparison.Ordinal));
        var verified = ok && !stillMasked;
        var message = string.Join("；", messages) + (verified ? "复核通过：已恢复原始型号（注销或重启后生效）。" : "复核未通过，保留阻塞与原始证据。");
        return BackendResult.Ok("gpu.recover", message, new RecoverOutcome
        {
            Scope = "spoof", Success = ok, Verified = verified, Message = message, VerifiedSnapshot = snapshot,
        });
    }

    private static async Task<ReconcileSnapshot> BuildSnapshotAsync()
    {
        var service = new GpuSpoofService();
        var adapters = await service.GetAdaptersAsync();
        var invalidations = await service.CheckInvalidationAsync();
        var items = adapters.Select(a => new ReconcileItem
        {
            Id = a.RegistryPath, Kind = "adapter",
            State = a.IsMasked ? "已伪装：" + a.DisplayName : "原始：" + a.DisplayName,
            HasBackup = a.HasBackup, Detail = a.DeviceDesc,
        }).ToList();
        var backedUp = adapters.Count(a => a.HasBackup);
        return new ReconcileSnapshot
        {
            Module = BackendModules.Gpu,
            Summary = $"显示适配器 {adapters.Count} 个：{adapters.Count(a => a.IsMasked)} 个处于伪装状态，{backedUp} 个有可恢复原值；" +
                      $"伪装失效检测 {invalidations.Count} 项。",
            Complete = true,
            Items = items,
            RecoveryScopes =
            [
                new RecoveryScope
                {
                    Id = "spoof",
                    Description = "恢复显卡原始型号（需注销或重启生效）",
                    Available = backedUp > 0,
                    Reason = backedUp > 0 ? "" : "没有可恢复的原值备份。",
                    AffectedCount = backedUp,
                },
            ],
        };
    }

    private static async Task<BackendResult> SpoofStatusAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在枚举显示适配器…", null, "adapters");
        var adapters = await new GpuSpoofService().GetAdaptersAsync();
        return BackendResult.Ok("gpu.spoof.status", $"发现 {adapters.Count} 个显示适配器。", adapters);
    }

    private static async Task<BackendResult> SpoofApplyAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<GpuSpoofApplyArgs>();
        if (string.IsNullOrWhiteSpace(args.RegistryPath))
            return BackendResult.Failed("gpu.spoof.apply", "缺少 registryPath。");
        context.ReportProgress("正在写入伪装型号…", null, "spoof");
        var result = await new GpuSpoofService().SpoofByPathAsync(args.RegistryPath, args.FakeName);
        return Wrap("gpu.spoof.apply", result);
    }

    private static async Task<BackendResult> SpoofRestoreAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<GpuSpoofPathArgs>();
        if (string.IsNullOrWhiteSpace(args.RegistryPath))
            return BackendResult.Failed("gpu.spoof.restore", "缺少 registryPath。");
        context.ReportProgress("正在恢复原始型号…", null, "restore");
        var result = await new GpuSpoofService().RestoreByPathAsync(args.RegistryPath);
        return Wrap("gpu.spoof.restore", result);
    }

    private static async Task<BackendResult> SpoofReloadAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<GpuSpoofReloadArgs>();
        if (string.IsNullOrWhiteSpace(args.InstanceId))
            return BackendResult.Failed("gpu.spoof.reload", "缺少 instanceId（请由注册表路径解析后传入）。");
        context.ReportProgress("正在重载显卡设备…", null, "reload");
        var result = await GpuDeviceReloadService.ReloadAsync(args.InstanceId, context.CancellationToken);
        return Wrap("gpu.spoof.reload", result);
    }

    private static async Task<BackendResult> WatchEvaluateAsync(BackendCommandContext context)
    {
        var invalidations = await new GpuSpoofService().CheckInvalidationAsync();
        return BackendResult.Ok("gpu.watch.evaluate", $"检测到 {invalidations.Count} 项伪装失效。", invalidations);
    }

    private static async Task<BackendResult> DriverStatusAsync(BackendCommandContext context)
    {
        var environment = await new NvProfileService().GetEnvironmentAsync();
        return BackendResult.Ok("gpu.driver.status", environment.IsNvidia ? "已检测到 NVIDIA 环境。" : "未检测到 NVIDIA 显卡。", environment);
    }

    private static async Task<BackendResult> DriverReadAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<NvDriverRestoreArgs>();
        var values = await new NvProfileService().GetGameSettingValuesAsync();
        if (values is null)
            return BackendResult.Ok("gpu.driver.read", "未读取到驱动配置（目标 EXE 不可用或无覆盖）。", null);
        var wire = values
            .Where(pair => args.Ids.Count == 0 || args.Ids.Contains(pair.Key))
            .ToDictionary(
                pair => pair.Key.ToString("X8"),
                pair => new NvSettingValueDto { HasOverride = pair.Value.HasOverride, Value = pair.Value.Value },
                StringComparer.Ordinal);
        return BackendResult.Ok("gpu.driver.read", $"读取到 {wire.Count} 项覆盖状态。", wire);
    }

    private static async Task<BackendResult> DriverApplyAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<NvDriverApplyArgs>();
        var changes = args.Changes.Select(c => (c.Id, c.Value)).ToList();
        if (changes.Count == 0)
            return BackendResult.Failed("gpu.driver.apply", "没有要应用的设置。");
        context.ReportProgress("正在写入驱动设置…", null, "driver");
        var result = await new NvProfileService().ApplyGameSettingsAsync(changes);
        return Wrap("gpu.driver.apply", result);
    }

    private static async Task<BackendResult> DriverRestoreAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<NvDriverRestoreArgs>();
        if (args.Ids.Count == 0)
            return BackendResult.Failed("gpu.driver.restore", "没有要恢复的设置。");
        context.ReportProgress("正在恢复驱动设置…", null, "driver-restore");
        var changes = args.Ids.Distinct().Select(id => (id, (uint?)null)).ToList();
        var result = await new NvProfileService().ApplyGameSettingsAsync(changes);
        return Wrap("gpu.driver.restore", result);
    }

    private static async Task<BackendResult> DriverLockAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<NvDriverReadOnlyArgs>();
        context.ReportProgress(args.ReadOnly ? "正在锁定驱动配置数据库…" : "正在解除只读锁定…", null, "driver-lock");
        var result = await new NvProfileService().SetDrsFilesReadOnlyAsync(args.ReadOnly);
        return Wrap("gpu.driver.lock", result);
    }

    private static BackendResult Wrap(string code, OperationResult result) =>
        result.Success
            ? BackendResult.Ok(code, result.Message, result)
            : BackendResult.Failed(code, result.Message);
}
