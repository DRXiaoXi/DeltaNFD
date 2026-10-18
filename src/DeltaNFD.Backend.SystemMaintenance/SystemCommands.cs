using DeltaNFD.Backends;
using DeltaNFD.Native;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;

namespace DeltaNFD.Backend.SystemMaintenance;

/// <summary>
/// 系统维护后端的命令实现：垃圾/着色器缓存清理、电源计划与休眠、着色器体检、虚拟内存，
/// 以及扩展优化库 / Defender / 内存整理 / 一键优化的实际执行（T3）。
/// 权限复核在各执行实现内完成；处理器只做参数校验、按内置 Id 解析与结果包装。
/// 后端只按自带的数据库/服务组 Id 解析目标，绝不接受前端传入的注册表路径、脚本或 shell 命令。
/// </summary>
internal static class SystemCommands
{
    /// <summary>一次批量状态读取的条目上限，防止超大请求耗尽内存。</summary>
    private const int MaxBxBatch = 2000;

    static SystemCommands()
    {
        // 后端 EXE 位于 Backends\<模块>\ 下，BaseDirectory 不是程序根：在首次读取数据库前定位 Assets。
        BxCatalog.ConfigureAssetRoot(BackendEnvironment.AppRoot);
    }

    public static readonly BackendHostOptions Options = new()
    {
        Module = BackendModules.Require(BackendModules.System),
        OnStopping = DefenderGuard.RestoreSessionAsync,
        Handlers = new Dictionary<string, Func<BackendCommandContext, Task<BackendResult>>>(StringComparer.Ordinal)
        {
            [BackendCommands.SystemCleanupScan] = CleanupScanAsync,
            [BackendCommands.SystemTweaksStatus] = async c => BackendResult.Ok("system.tweaks.status", "", await new SystemTweakService().GetStatusesAsync()),
            [BackendCommands.SystemTweaksBackups] = c => Task.FromResult(BackendResult.Ok("system.tweaks.backups", "", new SystemTweakService().GetBackedUpTweaks())),
            [BackendCommands.SystemTweaksHyperV] = async c => BackendResult.Ok("system.tweaks.hyperv", "", new SystemTweakState(await new SystemTweakService().IsHyperVFullyDisabledAsync())),
            [BackendCommands.SystemTweaksVbs] = async c => BackendResult.Ok("system.tweaks.vbs", "", await new SystemTweakService().GetVbsRuntimeSummaryAsync()),
            [BackendCommands.SystemTweaksApply] = c => TweakAsync(c, restore: false),
            [BackendCommands.SystemTweaksRestore] = c => TweakAsync(c, restore: true),
            [BackendCommands.SystemAdvancedStatus] = async c => BackendResult.Ok("system.advanced.status", "", await new AdvancedTweakService().GetGroupsAsync()),
            [BackendCommands.SystemAdvancedApply] = c => AdvancedAsync(c, restore: false),
            [BackendCommands.SystemAdvancedRestore] = c => AdvancedAsync(c, restore: true),
            [BackendCommands.SystemCleanupRun] = CleanupRunAsync,
            [BackendCommands.SystemCleanupShaderCache] = CleanupShaderCacheAsync,

            [BackendCommands.SystemPowerSchemes] = PowerSchemesAsync,
            [BackendCommands.SystemPowerSetScheme] = PowerSetSchemeAsync,
            [BackendCommands.SystemPowerImportNoPowerSave] = PowerImportNoPowerSaveAsync,
            [BackendCommands.SystemPowerEnsureName] = PowerEnsureNameAsync,
            [BackendCommands.SystemPowerHibernateGet] = PowerHibernateGetAsync,
            [BackendCommands.SystemPowerHibernateSet] = PowerHibernateSetAsync,
            [BackendCommands.SystemPowerHeteroList] = PowerHeteroListAsync,
            [BackendCommands.SystemPowerHeteroSet] = PowerHeteroSetAsync,

            [BackendCommands.SystemShaderStatus] = ShaderStatusAsync,
            [BackendCommands.SystemShaderDiagnose] = ShaderDiagnoseAsync,
            [BackendCommands.SystemShaderClear] = ShaderClearAsync,
            [BackendCommands.SystemShaderRecoveryClean] = async c => {
                var args = c.ArgsAs<ShaderRecoveryCleanArgs>();
                var result = await ShaderRecoveryCleanup.RunAsync(args.GameCache, args.NvidiaCache, c.CancellationToken);
                return new BackendResult(result.Success ? BackendResultStatuses.Ok : BackendResultStatuses.Failed,
                    c.Command, result.Message, [], false,
                    System.Text.Json.JsonSerializer.SerializeToElement(result, BackendJson.SerializerOptions));
            },

            [BackendCommands.SystemPagefileStatus] = PagefileStatusAsync,
            [BackendCommands.SystemPagefileSet] = PagefileSetAsync,
            [BackendCommands.SystemPagefileRestoreAuto] = PagefileRestoreAutoAsync,
            [BackendCommands.SystemPagefileRestoreOriginal] = PagefileRestoreOriginalAsync,
            [BackendCommands.SystemReconcile] = ReconcileAsync,
            [BackendCommands.SystemRecover] = RecoverAsync,

            // ---- 扩展优化库 / Defender / 内存 / 一键优化（T3）----
            [BackendCommands.SystemBxStatus] = BxStatusAsync,
            [BackendCommands.SystemBxGearInfo] = BxGearInfoAsync,
            [BackendCommands.SystemBxApply] = BxApplyAsync,
            [BackendCommands.SystemBxGearApply] = BxGearApplyAsync,
            [BackendCommands.SystemBxServiceGroupResolve] = BxServiceGroupResolveAsync,
            [BackendCommands.SystemBxServiceGroupStatus] = BxServiceGroupStatusAsync,
            [BackendCommands.SystemBxServiceGroupApply] = BxServiceGroupApplyAsync,
            [BackendCommands.SystemBxBackups] = BxBackupsAsync,
            [BackendCommands.SystemBxRestoreAll] = BxRestoreAllAsync,
            [BackendCommands.SystemBxCacheInvalidate] = BxCacheInvalidateAsync,
            [BackendCommands.SystemDefenderPreflight] = DefenderPreflightAsync,
            [BackendCommands.SystemDefenderProtectionState] = DefenderProtectionStateAsync,
            [BackendCommands.SystemDefenderExclusionsAdd] = DefenderExclusionsAddAsync,
            [BackendCommands.SystemDefenderPostApply] = DefenderPostApplyAsync,
            [BackendCommands.SystemMemoryStatus] = MemoryStatusAsync,
            [BackendCommands.SystemMemoryPurge] = MemoryPurgeAsync,
            [BackendCommands.SystemOptimizeRun] = OptimizeRunAsync,
            [BackendCommands.SystemProcessesScan] = c => Task.FromResult(BackendResult.Ok(c.Command, "已扫描后台进程。", new ProcessMaintenanceService().Scan(c.CancellationToken))),
            [BackendCommands.SystemProcessesKill] = c => ProcessesAsync(c, trim: false),
            [BackendCommands.SystemProcessesTrim] = c => ProcessesAsync(c, trim: true),
        },
    };

    // ---------------- 只读核对与按备份恢复（T1） ----------------

    /// <summary>只读状态核对：深度优化、高级优化、虚拟内存的当前状态与可用恢复范围。</summary>
    private static async Task<BackendResult> ReconcileAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在只读核对系统维护状态…", null, "reconcile");
        var snapshot = await BuildSnapshotAsync();
        return BackendResult.Ok("system.reconcile", snapshot.Summary, snapshot);
    }

    /// <summary>按可信备份恢复指定范围，恢复后再次只读复核；复核不通过不报告成功。</summary>
    private static async Task<BackendResult> RecoverAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<RecoverArgs>();
        var scope = (args.Scope ?? "").Trim().ToLowerInvariant();
        if (scope is not ("tweaks" or "advanced" or "pagefile" or "defender"))
            return BackendResult.Failed("system.recover", $"未知恢复范围“{args.Scope}”，已拒绝执行。");

        context.ReportProgress("正在按可信备份恢复…", null, "recover");
        var messages = new List<string>();
        var ok = true;
        switch (scope)
        {
            case "defender":
            {
                var result = await DefenderGuard.RestoreSessionAsync();
                messages.Add(result.Message);
                ok = result.Success;
                break;
            }
            case "tweaks":
            {
                var service = new SystemTweakService();
                foreach (var tweak in service.GetBackedUpTweaks())
                {
                    var result = await service.RestoreAsync(tweak);
                    messages.Add($"{tweak}：{result.Message}");
                    if (!result.Success) ok = false;
                }
                break;
            }
            case "advanced":
            {
                var service = new AdvancedTweakService();
                foreach (var group in (await service.GetGroupsAsync()).Where(g => g.IsApplied))
                {
                    var result = await service.RevertAsync(group.Id);
                    messages.Add($"{group.DisplayName}：{result.Message}");
                    if (!result.Success) ok = false;
                }
                break;
            }
            case "pagefile":
            {
                var result = await new PagefileService().RestoreOriginalAsync();
                messages.Add("虚拟内存：" + result.Message);
                if (!result.Success) ok = false;
                break;
            }
        }
        if (messages.Count == 0) messages.Add("没有需要恢复的项目（无可信备份）。");

        var snapshot = await BuildSnapshotAsync();
        var remaining = snapshot.RecoveryScopes.FirstOrDefault(s => s.Id == scope);
        var verified = ok && (remaining is null || !remaining.Available);
        var message = string.Join("；", messages) + (verified ? "复核通过：恢复范围已无可恢复项。" : "复核未通过，保留阻塞与原始证据。");
        return BackendResult.Ok("system.recover", message, new RecoverOutcome
        {
            Scope = scope, Success = ok, Verified = verified, Message = message, VerifiedSnapshot = snapshot,
        });
    }

    private static async Task<ReconcileSnapshot> BuildSnapshotAsync()
    {
        var items = new List<ReconcileItem>();
        var notes = new List<string>();
        var complete = true;

        var tweakService = new SystemTweakService();
        IReadOnlyList<SystemTweak> backups = [];
        try { backups = tweakService.GetBackedUpTweaks(); }
        catch (Exception ex) { complete = false; notes.Add("读取深度优化备份失败：" + ex.Message); }

        List<TweakStatus> statuses = [];
        try { statuses = await tweakService.GetStatusesAsync(); }
        catch (Exception ex) { complete = false; notes.Add("读取系统优化状态失败：" + ex.Message); }
        foreach (var status in statuses)
            items.Add(new ReconcileItem
            {
                Id = status.Tweak.ToString(), Kind = "tweak",
                State = status.IsOptimized ? "已优化" : "默认",
                HasBackup = backups.Contains(status.Tweak), Detail = status.Detail,
            });

        List<AdvancedTweakGroup> groups = [];
        try { groups = await new AdvancedTweakService().GetGroupsAsync(); }
        catch (Exception ex) { complete = false; notes.Add("读取高级优化状态失败：" + ex.Message); }
        foreach (var group in groups)
            items.Add(new ReconcileItem
            {
                Id = group.Id, Kind = "advanced",
                State = group.IsApplied ? "已应用" : "默认", Detail = group.Detail,
            });
        var appliedAdvanced = groups.Count(g => g.IsApplied);

        PagefileInfo? pagefile = null;
        var hasPagefileBackup = false;
        try
        {
            var service = new PagefileService();
            pagefile = await service.GetInfoAsync();
            hasPagefileBackup = service.HasOriginalBackup;
        }
        catch (Exception ex) { complete = false; notes.Add("读取虚拟内存状态失败：" + ex.Message); }
        if (pagefile is not null)
            items.Add(new ReconcileItem
            {
                Id = "pagefile", Kind = "pagefile", State = pagefile.Summary, HasBackup = hasPagefileBackup,
            });

        var prefix = notes.Count > 0 ? string.Join("；", notes) + "；" : "";
        return new ReconcileSnapshot
        {
            Module = BackendModules.System,
            Summary = prefix + $"深度优化备份 {backups.Count} 项；高级优化已应用 {appliedAdvanced} 组；虚拟内存原配置备份 {(hasPagefileBackup ? "有" : "无")}。",
            Complete = complete,
            Items = items,
            RecoveryScopes =
            [
                new RecoveryScope
                {
                    Id = "defender", Description = "清理本程序的 Defender 临时排除项并恢复会话防护",
                    Available = DefenderGuard.HasPendingSession, AffectedCount = DefenderGuard.HasPendingSession ? 1 : 0,
                },
                new RecoveryScope
                {
                    Id = "tweaks", Description = "按备份还原系统深度优化",
                    Available = backups.Count > 0, Reason = backups.Count > 0 ? "" : "没有可恢复的深度优化备份。",
                    AffectedCount = backups.Count,
                },
                new RecoveryScope
                {
                    Id = "advanced", Description = "按备份还原高级（高危）优化",
                    Available = appliedAdvanced > 0, Reason = appliedAdvanced > 0 ? "" : "没有已应用的高级优化。",
                    AffectedCount = appliedAdvanced,
                },
                new RecoveryScope
                {
                    Id = "pagefile", Description = "按备份还原虚拟内存原配置",
                    Available = hasPagefileBackup, Reason = hasPagefileBackup ? "" : "没有虚拟内存原配置备份。",
                    AffectedCount = hasPagefileBackup ? 1 : 0,
                },
            ],
        };
    }

    // ---------------- 清理 ----------------

    private static async Task<BackendResult> CleanupScanAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在扫描可清理项…", null, "cleanup-scan");
        var targets = await new CleanupService().ScanAsync();
        return BackendResult.Ok("system.cleanup.scan", $"发现 {targets.Count} 个清理目标。", targets);
    }

    private static async Task<BackendResult> CleanupRunAsync(BackendCommandContext context)
    {
        var progress = new RelayProgress(context, "cleanup");
        var (freedMb, skipped) = await new CleanupService().CleanAsync(progress);
        var message = $"清理完成：释放 {freedMb:0.0} MB" + (skipped.Count > 0 ? $"，跳过 {skipped.Count} 项" : "") + "。";
        return BackendResult.Ok("system.cleanup.run", message, new CleanupResultDto { FreedMb = freedMb, Skipped = skipped });
    }

    private static async Task<BackendResult> CleanupShaderCacheAsync(BackendCommandContext context)
    {
        var progress = new RelayProgress(context, "shader-cache");
        var (freedMb, skipped) = await new CleanupService().CleanShaderCachesAsync(progress);
        var message = $"着色器缓存清理完成：释放 {freedMb:0.0} MB" + (skipped.Count > 0 ? $"，跳过 {skipped.Count} 项" : "") + "。";
        return BackendResult.Ok("system.cleanup.shader-cache", message, new CleanupResultDto { FreedMb = freedMb, Skipped = skipped });
    }

    // ---------------- 电源 ----------------

    private static async Task<BackendResult> PowerSchemesAsync(BackendCommandContext context)
    {
        var schemes = await new PowerService().GetSchemesAsync();
        return BackendResult.Ok("system.power.schemes", $"读取到 {schemes.Count} 个电源计划。", schemes);
    }

    private static async Task<BackendResult> PowerSetSchemeAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<PowerSetSchemeArgs>();
        if (string.IsNullOrWhiteSpace(args.SchemeGuid))
            return BackendResult.Failed("system.power.set-scheme", "缺少 schemeGuid。");
        return Wrap("system.power.set-scheme", await new PowerService().SetSchemeAsync(args.SchemeGuid));
    }

    private static async Task<BackendResult> PowerImportNoPowerSaveAsync(BackendCommandContext context)
    {
        var service = new PowerService();
        var import = await service.ImportNoPowerSaveSchemeAsync();
        var name = import.Success ? await service.EnsureNoPowerSaveSchemeNameAsync() : null;
        var message = import.Message + (name is null ? "" : " " + name.Message);
        return import.Success
            ? BackendResult.Ok("system.power.import-nopowersave", message, import)
            : BackendResult.Failed("system.power.import-nopowersave", message);
    }

    private static async Task<BackendResult> PowerEnsureNameAsync(BackendCommandContext context) =>
        Wrap("system.power.ensure-name", await new PowerService().EnsureNoPowerSaveSchemeNameAsync());

    private static async Task<BackendResult> PowerHibernateGetAsync(BackendCommandContext context)
    {
        var enabled = await new PowerService().IsHibernateEnabledAsync();
        return BackendResult.Ok("system.power.hibernate.get", enabled ? "休眠已启用。" : "休眠已关闭。", new { enabled });
    }

    private static async Task<BackendResult> PowerHibernateSetAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<PowerHibernateSetArgs>();
        return Wrap("system.power.hibernate.set", await new PowerService().SetHibernateAsync(args.Enable));
    }

    private static async Task<BackendResult> PowerHeteroListAsync(BackendCommandContext context)
    {
        var policies = await new PowerService().GetHeteroPoliciesAsync();
        return BackendResult.Ok("system.power.hetero.list", $"读取到 {policies.Count} 项异构策略。", policies);
    }

    private static async Task<BackendResult> PowerHeteroSetAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<PowerHeteroSetArgs>();
        if (string.IsNullOrWhiteSpace(args.SettingGuid))
            return BackendResult.Failed("system.power.hetero.set", "缺少 settingGuid。");
        return Wrap("system.power.hetero.set",
            await new PowerService().SetHeteroPolicyValuesAsync(args.SettingGuid, args.AcValue, args.DcValue));
    }

    // ---------------- 着色器 ----------------

    private static async Task<BackendResult> ShaderStatusAsync(BackendCommandContext context)
    {
        var status = await new ShaderService().GetStatusAsync();
        return BackendResult.Ok("system.shader.status", status.DriverAdvice, status);
    }

    private static async Task<BackendResult> ShaderDiagnoseAsync(BackendCommandContext context)
    {
        var diagnosis = await new ShaderService().DiagnoseAsync();
        return BackendResult.Ok("system.shader.diagnose", diagnosis.Summary, diagnosis);
    }

    private static async Task<BackendResult> ShaderClearAsync(BackendCommandContext context) =>
        Wrap("system.shader.clear-cache", await new ShaderService().ClearPsoCacheAsync());

    // ---------------- 虚拟内存 ----------------

    private static async Task<BackendResult> PagefileStatusAsync(BackendCommandContext context)
    {
        var service = new PagefileService();
        var info = await service.GetInfoAsync();
        return BackendResult.Ok("system.pagefile.status", info.Summary, new PagefileStatusDto
        {
            Info = info,
            HasOriginalBackup = service.HasOriginalBackup,
        });
    }

    private static async Task<BackendResult> PagefileSetAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<PagefileSetArgs>();
        if (string.IsNullOrWhiteSpace(args.DriveLetter) || args.DriveLetter.Length != 1)
            return BackendResult.Failed("system.pagefile.set", "driveLetter 必须是单个盘符。");
        return Wrap("system.pagefile.set",
            await new PagefileService().SetCustomAsync(args.MinMb, args.MaxMb, char.ToUpperInvariant(args.DriveLetter[0])));
    }

    private static async Task<BackendResult> PagefileRestoreAutoAsync(BackendCommandContext context) =>
        Wrap("system.pagefile.restore-auto", await new PagefileService().RestoreAutomaticAsync());

    private static async Task<BackendResult> PagefileRestoreOriginalAsync(BackendCommandContext context) =>
        Wrap("system.pagefile.restore-original", await new PagefileService().RestoreOriginalAsync());

    private static BackendResult Wrap(string code, OperationResult result) =>
        result.Success
            ? BackendResult.Ok(code, result.Message, result)
            : BackendResult.Failed(code, result.Message);

    private sealed record TweakArgs(SystemTweak? Tweak);
    private static async Task<BackendResult> TweakAsync(BackendCommandContext context, bool restore)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var args = context.ArgsAs<TweakArgs>();
        if (args.Tweak is null || !Enum.IsDefined(args.Tweak.Value)) return BackendResult.Failed(context.Command, "优化项缺失或无效，未执行。");
        var service = new SystemTweakService();
        return Wrap(context.Command, restore ? await service.RestoreAsync(args.Tweak.Value) : await service.DisableAsync(args.Tweak.Value));
    }
    private static async Task<BackendResult> AdvancedAsync(BackendCommandContext context, bool restore)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var id = context.ArgString("groupId");
        if (string.IsNullOrWhiteSpace(id)) return BackendResult.Failed(context.Command, "分组 ID 缺失，未执行。");
        var service = new AdvancedTweakService();
        return Wrap(context.Command, restore ? await service.RevertAsync(id) : await service.ApplyAsync(id));
    }

    private sealed class RelayProgress(BackendCommandContext context, string stage) : IProgress<string>
    {
        public void Report(string value) => context.ReportProgress(value, null, stage);
    }

    // ---------------- 扩展优化库（system.bx.*） ----------------

    /// <summary>批量读取条目状态：只接受「栏目 Id + 条目名」，由后端自带数据库解析，拒绝伪造/未知 Id。</summary>
    private static async Task<BackendResult> BxStatusAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<BxStatusArgs>();
        if (args.Items is null || args.Items.Count == 0)
            return BackendResult.Failed(context.Command, "缺少条目列表，已拒绝。");
        if (args.Items.Count > MaxBxBatch)
            return BackendResult.Failed(context.Command, $"一次最多读取 {MaxBxBatch} 个条目。");

        var resolved = new List<BxItem>(args.Items.Count);
        var invalid = new List<string>();
        foreach (var reference in args.Items)
        {
            if (reference is null || string.IsNullOrWhiteSpace(reference.SectionId) || string.IsNullOrWhiteSpace(reference.Name))
            {
                invalid.Add("(空引用)");
                continue;
            }

            var item = BxCatalog.FindItem(reference.SectionId, reference.Name);
            if (item is null)
            {
                invalid.Add($"{reference.SectionId}/{reference.Name}");
                continue;
            }

            resolved.Add(item);
        }

        if (invalid.Count > 0)
            return BackendResult.Failed(context.Command, $"包含未知或伪造条目（{string.Join("、", invalid.Take(5))}），已拒绝执行。");

        var states = await new BxService().GetItemStatesAsync(resolved);
        var dto = resolved.Select(i => new BxItemStateDto { SectionId = i.SectionId, Name = i.Name, State = states[i] }).ToList();
        return BackendResult.Ok(context.Command, $"读取 {dto.Count} 个条目状态。", dto);
    }

    private static async Task<BackendResult> BxGearInfoAsync(BackendCommandContext context)
    {
        var item = ResolveBxItem(context, out var error);
        if (item is null) return error!;
        var gear = await new BxService().GetGearInfoAsync(item);
        return BackendResult.Ok(context.Command, gear is null ? "非多档位条目。" : "已读取档位信息。", gear);
    }

    private static async Task<BackendResult> BxApplyAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<BxApplyArgs>();
        var item = BxCatalog.FindItem(args.SectionId, args.Name);
        if (item is null)
            return BackendResult.Failed(context.Command, $"未知或伪造条目「{args.SectionId}/{args.Name}」，已拒绝执行。");

        context.ReportProgress($"正在应用：{item.Name}…", null, "bx.apply");
        var result = await new BxService().ApplyItemAsync(item, args.TurnOn);
        return MapBxResult(context.Command, result);
    }

    private static async Task<BackendResult> BxGearApplyAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<BxGearApplyArgs>();
        var item = BxCatalog.FindItem(args.SectionId, args.Name);
        if (item is null)
            return BackendResult.Failed(context.Command, $"未知或伪造条目「{args.SectionId}/{args.Name}」，已拒绝执行。");
        if (args.GearIndex < 0)
            return BackendResult.Failed(context.Command, "档位下标无效，已拒绝执行。");

        var result = await new BxService().ApplyGearAsync(item, args.GearIndex);
        return MapBxResult(context.Command, result);
    }

    private static async Task<BackendResult> BxServiceGroupResolveAsync(BackendCommandContext context)
    {
        var group = ResolveBxGroup(context, out var error);
        if (group is null) return error!;
        var services = await new BxService().ResolveServiceGroupAsync(group);
        return BackendResult.Ok(context.Command, $"服务组 {group.Id} 含 {services.Count} 个本机存在的服务。", services);
    }

    private static async Task<BackendResult> BxServiceGroupStatusAsync(BackendCommandContext context)
    {
        var group = ResolveBxGroup(context, out var error);
        if (group is null) return error!;
        var state = await new BxService().GetServiceGroupStateAsync(group);
        return BackendResult.Ok(context.Command, $"服务组 {group.Id} 状态：{state}。", state);
    }

    private static async Task<BackendResult> BxServiceGroupApplyAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<BxServiceGroupApplyArgs>();
        var group = BxCatalog.FindServiceGroup(args.GroupId);
        if (group is null)
            return BackendResult.Failed(context.Command, $"未知或伪造服务组「{args.GroupId}」，已拒绝执行。");

        context.ReportProgress($"正在应用服务组：{group.Id}…", null, "bx.service-group");
        var outcome = await new BxService().ApplyServiceGroupDetailedAsync(group, args.TurnOn);
        var data = outcome.Result ?? (outcome.Success ? OperationResult.Ok(outcome.Message) : OperationResult.Fail(outcome.Message));
        var items = outcome.Items.Select(i => new BackendResultItem(i.Id, i.Status, i.Message)).ToList();
        if (!outcome.Success && !outcome.HasPartial)
            return BackendResult.Failed(context.Command, outcome.Message);
        return outcome.HasPartial
            ? BackendResult.PartialResult(context.Command, outcome.Message, items, data, pendingRestore: true)
            : BackendResult.Ok(context.Command, outcome.Message, data);
    }

    private static async Task<BackendResult> BxBackupsAsync(BackendCommandContext context)
    {
        var status = await new BxService().GetBackupStatusAsync();
        // 读取失败时返回 Ok 但 Readable=false（明确「未知」，绝不冒充零/无备份）。
        return BackendResult.Ok(context.Command,
            status.Readable ? $"已读取 {status.Count} 条扩展库备份。" : "扩展库备份读取失败：" + status.Error,
            status);
    }

    private static async Task<BackendResult> BxRestoreAllAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在一键恢复扩展库备份…", null, "bx.restore-all");
        var summary = await new BxService().RestoreAllAsync();
        if (summary.Failed == 0)
            return BackendResult.Ok(context.Command, $"已恢复 {summary.Restored} 项。", summary);
        var items = summary.Errors.Select((e, index) => new BackendResultItem("error-" + index, "failed", e)).ToList();
        if (summary.Restored > 0)
            return BackendResult.PartialResult(context.Command, $"已恢复 {summary.Restored} 项，失败 {summary.Failed} 项。", items, summary);
        return BackendResult.Failed(context.Command, "一键恢复失败：" + string.Join("；", summary.Errors.Take(3)));
    }

    private static async Task<BackendResult> BxCacheInvalidateAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<BxCacheInvalidateArgs>();
        var what = (args.What ?? "").Trim().ToLowerInvariant();
        if (what is not ("appx" or "task" or "all"))
            return BackendResult.Failed(context.Command, $"未知缓存类型「{args.What}」，已拒绝。");
        var service = new BxService();
        if (what is "appx" or "all") await service.InvalidateAppxCacheAsync();
        if (what is "task" or "all") await service.InvalidateTaskCacheAsync();
        return BackendResult.Ok(context.Command, "扩展库缓存已作废。", new { invalidated = what });
    }

    private static BxItem? ResolveBxItem(BackendCommandContext context, out BackendResult? error)
    {
        var args = context.ArgsAs<BxItemArgs>();
        var item = BxCatalog.FindItem(args.SectionId, args.Name);
        if (item is null)
        {
            error = BackendResult.Failed(context.Command, $"未知或伪造条目「{args.SectionId}/{args.Name}」，已拒绝执行。");
            return null;
        }

        error = null;
        return item;
    }

    private static BxServiceGroup? ResolveBxGroup(BackendCommandContext context, out BackendResult? error)
    {
        var args = context.ArgsAs<BxServiceGroupArgs>();
        var group = BxCatalog.FindServiceGroup(args.GroupId);
        if (group is null)
        {
            error = BackendResult.Failed(context.Command, $"未知或伪造服务组「{args.GroupId}」，已拒绝执行。");
            return null;
        }

        error = null;
        return group;
    }

    private static BackendResult MapBxResult(string code, OperationResult result) =>
        result.Success
            ? BackendResult.Ok(code, result.Message, result)
            : BackendResult.Failed(code, result.Message);

    // ---------------- Defender（system.defender.*） ----------------

    private static async Task<BackendResult> DefenderPreflightAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<DefenderPreflightArgs>();
        if (args.Items is null || args.Items.Count == 0)
            return BackendResult.Failed(context.Command, "缺少批次条目，已拒绝。");
        if (args.Items.Count > MaxBxBatch)
            return BackendResult.Failed(context.Command, $"批次条目过多（>{MaxBxBatch}）。");

        var report = await DefenderGuard.PreflightAsync(args.Items);
        return BackendResult.Ok(context.Command, report is null ? "批次不含安全类条目，无需引导。" : "已生成 Defender 预检报告。", report);
    }

    private static async Task<BackendResult> DefenderProtectionStateAsync(BackendCommandContext context)
    {
        var (realTimeOn, tamperOn) = await DefenderGuard.RefreshProtectionStateAsync();
        return BackendResult.Ok(context.Command, "已查询防护状态。", new DefenderProtectionState { RealTimeOn = realTimeOn, TamperOn = tamperOn });
    }

    private static async Task<BackendResult> DefenderExclusionsAddAsync(BackendCommandContext context)
    {
        try
        {
            await DefenderGuard.AddSessionExclusionsAsync();
            // The durable session journal owns cleanup; a completed add must not block the optimization batch.
            return BackendResult.Ok(context.Command, "Defender 会话排除项已复核，恢复记录已持久化。", new { added = true });
        }
        catch (Exception ex)
        {
            return BackendResult.PartialResult(context.Command, ex.Message, [], new { added = false }, pendingRestore: true);
        }
    }

    private static async Task<BackendResult> DefenderPostApplyAsync(BackendCommandContext context)
    {
        var result = await DefenderGuard.PostApplyAsync();
        return result.CleanupSucceeded
            ? BackendResult.Ok(context.Command, result.Remind ? "请重新开启安全防护。" : "安全防护无需恢复提醒。", result)
            : BackendResult.PartialResult(context.Command, result.Message, [], result, pendingRestore: true);
    }

    private static async Task<BackendResult> ProcessesAsync(BackendCommandContext context, bool trim)
    {
        var report = await new ProcessMaintenanceService().ApplyAsync(context.ArgsAs<ProcessMaintenanceArgs>(),
            trim, context.CancellationToken, message => context.ReportProgress(message, null, "processes"));
        var message = $"完成 {report.Completed}/{report.Items.Count} 项" + (report.Cancelled ? "，后续已取消。" : "。");
        if (!trim) message += "结束进程的内存量为原工作集估算。";
        var items = report.Items.Select(p => new BackendResultItem(p.Pid.ToString(), p.Status, p.Message)).ToArray();
        // 进程已退出/身份变化/受保护拒绝都是已知结果；只有"已请求结束但未确认退出"才需要人工复核。
        var unknown = report.Items.Any(p => p.Status == "unknown");
        if (report.Completed == 0 && !unknown)
            return new BackendResult(report.Cancelled ? BackendResultStatuses.Cancelled : BackendResultStatuses.Failed,
                context.Command, message, items, false, System.Text.Json.JsonSerializer.SerializeToElement(report, BackendJson.SerializerOptions));
        return !report.Cancelled && report.Items.All(p => p.Status == "ok")
            ? BackendResult.Ok(context.Command, message, report)
            : BackendResult.PartialResult(context.Command, message, items, report, requiresReview: unknown);
    }

    // ---------------- 内存整理（system.memory.*） ----------------

    private static Task<BackendResult> MemoryStatusAsync(BackendCommandContext context)
    {
        var snapshot = MemoryPurge.QueryMemory();
        if (snapshot.TotalPhys == 0)
            return Task.FromResult(BackendResult.Failed(context.Command, "无法读取物理内存状态（GlobalMemoryStatusEx 失败）。"));
        return Task.FromResult(BackendResult.Ok(context.Command,
            $"物理内存 {MemoryPurge.FormatBytes(snapshot.TotalPhys)}，可用 {MemoryPurge.FormatBytes(snapshot.AvailPhys)}。",
            new MemoryStatusDto
            {
                TotalPhys = snapshot.TotalPhys,
                AvailPhys = snapshot.AvailPhys,
                LoadPercent = snapshot.LoadPercent,
                AvailRatio = snapshot.AvailRatio,
            }));
    }

    private static Task<BackendResult> MemoryPurgeAsync(BackendCommandContext context)
    {
        context.ReportProgress("正在清空待备内存列表…", null, "memory.purge");
        var result = MemoryPurge.PurgeStandbyList();
        var dto = new MemoryPurgeResultDto { Success = result.Success, Message = result.Message, FreedBytes = result.FreedBytes };
        return Task.FromResult(result.Success
            ? BackendResult.Ok(context.Command, result.Message, dto)
            : BackendResult.Failed(context.Command, result.Message));
    }

    // ---------------- 一键优化（system.optimize.run） ----------------

    private static async Task<BackendResult> OptimizeRunAsync(BackendCommandContext context)
    {
        var args = context.ArgsAs<OptimizeRunArgs>();
        if (!args.UltimatePower && !args.DisableMouseAcceleration && !args.PurgeStandbyList)
            return BackendResult.Failed(context.Command, "未选择任何优化步骤，已拒绝。");

        var report = new OptimizeRunReport();
        var failures = new List<string>();

        if (args.UltimatePower)
        {
            context.ReportProgress("正在切换卓越性能电源计划…", null, "optimize");
            var result = await new PowerService().SetSchemeAsync(PowerSchemes.UltimateSchemeTemplateGuid);
            if (result.Success) report.AppliedSteps.Add("电源计划 → 卓越性能");
            else failures.Add("电源计划：" + result.Message);
        }

        if (args.DisableMouseAcceleration)
        {
            context.ReportProgress("正在检查鼠标加速…", null, "optimize");
            var tweaks = new SystemTweakService();
            var statuses = await tweaks.GetStatusesAsync();
            var mouse = statuses.FirstOrDefault(s => s.Tweak == SystemTweak.MouseAccelerationOff);
            if (mouse is { IsOptimized: false })
            {
                var result = await tweaks.DisableAsync(SystemTweak.MouseAccelerationOff);
                if (result.Success) report.AppliedSteps.Add("鼠标加速已关闭");
                else failures.Add("鼠标加速：" + result.Message);
            }
        }

        if (args.PurgeStandbyList)
        {
            context.ReportProgress("正在整理待备内存…", null, "optimize");
            var result = MemoryPurge.PurgeStandbyList();
            if (result.Success)
            {
                report.ReclaimedMemoryMb += Math.Max(0, result.FreedBytes) / 1024.0 / 1024.0;
                report.AppliedSteps.Add("清空待备内存列表");
            }
            else
            {
                failures.Add("内存整理：" + result.Message);
            }
        }

        if (failures.Count == 0)
            return BackendResult.Ok(context.Command, $"优化完成，执行 {report.AppliedSteps.Count} 步。", report);
        var items = failures.Select((f, index) => new BackendResultItem("step-" + index, "failed", f)).ToList();
        return report.AppliedSteps.Count > 0
            ? BackendResult.PartialResult(context.Command, $"部分完成 {report.AppliedSteps.Count} 步，失败 {failures.Count} 步。", items, report,
                requiresReview: false)
            : BackendResult.Failed(context.Command, "优化未完成：" + string.Join("；", failures));
    }
}
