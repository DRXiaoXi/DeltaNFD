namespace DeltaNFD.Backends;

/// <summary>官方后端命令名常量。命令名 = 模块前缀.动作；只有出现在模块白名单里的命令才允许执行。</summary>
public static class BackendCommands
{
    // ---- ACE（模块 ace）----
    public const string AceScan = "ace.scan";
    public const string AceCoreCheck = "ace.core";
    public const string AceClean = "ace.clean";
    public const string AceCoreClean = "ace.core.clean";
    public const string AceReconcile = "ace.reconcile";
    public const string AceVirtCheck = "ace.vircheck";

    // ---- 显卡（模块 gpu）----
    public const string GpuSpoofStatus = "gpu.spoof.status";
    public const string GpuSpoofApply = "gpu.spoof.apply";
    public const string GpuSpoofRestore = "gpu.spoof.restore";
    public const string GpuSpoofReload = "gpu.spoof.reload";
    public const string GpuWatchEvaluate = "gpu.watch.evaluate";
    public const string GpuDriverStatus = "gpu.driver.status";
    public const string GpuDriverRead = "gpu.driver.read";
    public const string GpuDriverApply = "gpu.driver.apply";
    public const string GpuDriverRestore = "gpu.driver.restore";
    public const string GpuDriverLock = "gpu.driver.lock";
    public const string GpuShaderCacheApply = "gpu.shader-cache.apply";
    public const string SystemShaderRecoveryClean = "system.shader.recovery-clean";
    public const string GpuReconcile = "gpu.reconcile";
    public const string GpuRecover = "gpu.recover";

    // ---- 运行库（模块 runtime）----
    public const string RuntimeStatus = "runtime.status";
    public const string RuntimeFileUseScan = "runtime.file-use.scan";
    public const string RuntimeFileUseClose = "runtime.file-use.close";
    public const string RuntimeHighFpsPreview = "runtime.high-fps.preview";
    public const string RuntimeHighFpsRepair = "runtime.high-fps.repair";
    public const string Vc14Scan = "runtime.vc14.scan";
    public const string Vc14Preview = "runtime.vc14.preview";
    public const string Vc14Repair = "runtime.vc14.repair";
    public const string Vc14LegacyClean = "runtime.vc14.legacy-clean";
    public const string RuntimeGuardEnable = "runtime.guard.enable";
    public const string RuntimeGuardDisable = "runtime.guard.disable";
    public const string RuntimeGuardSelectMode = "runtime.guard.select-mode";
    public const string RuntimeGuardCleanupLegacy = "runtime.guard.cleanup-legacy";
    public const string RuntimeUe4RepairAcl = "runtime.ue4.repair-acl";
    public const string RuntimeRedistScan = "runtime.redist.scan";
    public const string RuntimeRepairEnvironment = "runtime.repair.environment";
    public const string ProcmanMemoryPreview = "procman.memory.preview";
    public const string ProcmanMemoryTrim = "procman.memory.trim";
    public const string ProcmanBackgroundGet = "procman.background.get";
    public const string ProcmanBackgroundSave = "procman.background.save";
    public const string ProcmanFrameBackgroundSync = "procman.background.frame.sync";
    public const string RuntimeRedistListV14 = "runtime.redist.list-v14";
    // Historical command identifiers only: never registered or dispatched.
    public const string RuntimeRepair = "runtime.repair";
    public const string RuntimeCompatibilityRepair = "runtime.compatibility.repair";
    public const string RuntimeRedistUninstall = "runtime.redist.uninstall";
    public const string RuntimeRedistPreviewAll = "runtime.redist.preview-all";
    public const string RuntimeRedistUninstallAll = "runtime.redist.uninstall-all";
    public const string RuntimeReconcile = "runtime.reconcile";
    public const string RuntimeRecover = "runtime.recover";

    // ---- CPU/帧格（模块 cpuframe）----
    public const string CpuFrameStatus = "cpuframe.status";
    public const string CpuFrameMonitorStart = "cpuframe.monitor.start";
    public const string CpuFrameMonitorStop = "cpuframe.monitor.stop";
    public const string CpuFramePriorityApply = "cpuframe.priority.apply";
    public const string CpuFrameAffinityApply = "cpuframe.affinity.apply";
    public const string CpuFrameCcdApply = "cpuframe.ccd.apply";
    public const string CpuFrameCcdRevert = "cpuframe.ccd.revert";
    public const string CpuFrameSingleCcdApply = "cpuframe.single-ccd.apply";
    public const string CpuFrameSingleCcdRevert = "cpuframe.single-ccd.revert";
    public const string CpuFrameRestore = "cpuframe.restore";
    public const string CpuFrameDwmRestart = "cpuframe.dwm.restart";
    public const string CpuFramePowerLock = "cpuframe.power.lock";
    public const string CpuFramePowerUnlock = "cpuframe.power.unlock";
    public const string CpuFrameTimerApply = "cpuframe.timer.apply";
    public const string CpuFrameTimerRestore = "cpuframe.timer.restore";
    public const string CpuFrameTweaksApply = "cpuframe.tweaks.apply";
    public const string CpuFrameTweaksRevert = "cpuframe.tweaks.revert";
    public const string CpuFrameBoostApply = "cpuframe.boost.apply";
    public const string CpuFrameMemoryClean = "cpuframe.memory.clean";
    public const string CpuFrameTopology = "cpuframe.topology";
    public const string CpuFrameAffinityCheck = "cpuframe.affinity.check";
    public const string CpuFrameAffinityPending = "cpuframe.affinity.pending";
    public const string CpuFrameMemoryLimitDetect = "cpuframe.memorylimit.detect";
    public const string CpuFrameMemoryLimitRemove = "cpuframe.memorylimit.remove";
    public const string CpuFramePowerIncomplete = "cpuframe.power.incomplete";
    public const string CpuFramePowerPreserve = "cpuframe.power.preserve";
    public const string CpuFrameReconcile = "cpuframe.reconcile";
    public const string CpuFrameRecover = "cpuframe.recover";
    public const string CpuFrameOfflineRestore = "cpuframe.offline.restore";

    // ---- 进程管理（模块 procman）----
    public const string ProcmanStatus = "procman.status";
    public const string ProcmanActivate = "procman.activate";
    public const string ProcmanList = "procman.process.list";
    public const string ProcmanDetail = "procman.process.detail";
    public const string ProcmanCapability = "procman.process.capability";
    public const string ProcmanCpuTopology = "procman.cpu.topology";
    public const string ProcmanReconcile = "procman.reconcile";
    public const string ProcmanSet = "procman.process.set";
    public const string ProcmanReset = "procman.process.reset";
    public const string ProcmanRelease = "procman.process.release";
    public const string ProcmanRestoreAll = "procman.restore-all";
    public const string ProcmanRecover = "procman.recover";
    public const string ProcmanRulesList = "procman.rules.list";
    public const string ProcmanRulesSave = "procman.rules.save";
    public const string ProcmanRulesDelete = "procman.rules.delete";
    public const string ProcmanResponsivenessConfig = "procman.responsiveness.config";
    public const string ProcmanResponsivenessEvents = "procman.responsiveness.events";
    public const string ProcmanNativeProbe = "procman.native.probe";

    // ---- 系统维护（模块 system）----
    public const string SystemCleanupScan = "system.cleanup.scan";
    public const string SystemTweaksStatus = "system.tweaks.status";
    public const string SystemTweaksApply = "system.tweaks.apply";
    public const string SystemTweaksRestore = "system.tweaks.restore";
    public const string SystemTweaksBackups = "system.tweaks.backups";
    public const string SystemTweaksHyperV = "system.tweaks.hyperv";
    public const string SystemTweaksVbs = "system.tweaks.vbs";
    public const string SystemAdvancedStatus = "system.advanced.status";
    public const string SystemAdvancedApply = "system.advanced.apply";
    public const string SystemAdvancedRestore = "system.advanced.restore";
    public const string SystemCleanupRun = "system.cleanup.run";
    public const string SystemCleanupShaderCache = "system.cleanup.shader-cache";
    public const string SystemPowerSchemes = "system.power.schemes";
    public const string SystemPowerSetScheme = "system.power.set-scheme";
    public const string SystemPowerImportNoPowerSave = "system.power.import-nopowersave";
    public const string SystemPowerEnsureName = "system.power.ensure-name";
    public const string SystemPowerHibernateGet = "system.power.hibernate.get";
    public const string SystemPowerHibernateSet = "system.power.hibernate.set";
    public const string SystemPowerHeteroList = "system.power.hetero.list";
    public const string SystemPowerHeteroSet = "system.power.hetero.set";
    public const string SystemShaderStatus = "system.shader.status";
    public const string SystemShaderDiagnose = "system.shader.diagnose";
    public const string SystemShaderClear = "system.shader.clear-cache";
    public const string SystemPagefileStatus = "system.pagefile.status";
    public const string SystemPagefileSet = "system.pagefile.set";
    public const string SystemPagefileRestoreAuto = "system.pagefile.restore-auto";
    public const string SystemPagefileRestoreOriginal = "system.pagefile.restore-original";
    public const string SystemReconcile = "system.reconcile";
    public const string SystemRecover = "system.recover";

    // ---- 系统维护：扩展优化库 / Defender / 内存 / 一键优化（T3）----
    public const string SystemBxStatus = "system.bx.status";
    public const string SystemBxGearInfo = "system.bx.gear.info";
    public const string SystemBxApply = "system.bx.apply";
    public const string SystemBxGearApply = "system.bx.gear.apply";
    public const string SystemBxServiceGroupResolve = "system.bx.service-group.resolve";
    public const string SystemBxServiceGroupStatus = "system.bx.service-group.status";
    public const string SystemBxServiceGroupApply = "system.bx.service-group.apply";
    public const string SystemBxBackups = "system.bx.backups";
    public const string SystemBxRestoreAll = "system.bx.restore-all";
    public const string SystemBxCacheInvalidate = "system.bx.cache.invalidate";
    public const string SystemDefenderPreflight = "system.defender.preflight";
    public const string SystemDefenderProtectionState = "system.defender.protection-state";
    public const string SystemDefenderExclusionsAdd = "system.defender.exclusions.add";
    public const string SystemDefenderPostApply = "system.defender.post-apply";
    public const string SystemMemoryStatus = "system.memory.status";
    public const string SystemMemoryPurge = "system.memory.purge";
    public const string SystemOptimizeRun = "system.optimize.run";
    public const string SystemProcessesScan = "system.processes.scan";
    public const string SystemProcessesKill = "system.processes.kill";
    public const string SystemProcessesTrim = "system.processes.trim";
}

/// <summary>一个官方后端的静态规格：id、EXE 名、命令白名单、超时、退出语义。</summary>
public sealed record BackendModuleSpec(
    string Id,
    string DisplayName,
    string ExecutableName,
    IReadOnlySet<string> Commands,
    IReadOnlyDictionary<string, TimeSpan> CommandTimeouts,
    TimeSpan DefaultTimeout,
    IReadOnlySet<string> ExitAfterCommands,
    bool Persistent);

/// <summary>官方后端模块注册表：模块 id → 规格。宿主与后端共用同一份白名单。</summary>
public static class BackendModules
{
    public const string Ace = "ace";
    public const string Gpu = "gpu";
    public const string Runtime = "runtime";
    public const string CpuFrame = "cpuframe";
    public const string Procman = "procman";
    public const string System = "system";

    private static readonly TimeSpan TwoMinutes = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ThreeMinutes = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FifteenMinutes = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ThirtyMinutes = TimeSpan.FromMinutes(30);

    /// <summary>按需任务模块（清理等）在完成指定命令后自行退出。</summary>
    private static readonly HashSet<string> AceExitAfter = new(StringComparer.Ordinal) { BackendCommands.AceClean, BackendCommands.AceCoreClean };

    private static BackendModuleSpec Make(
        string id, string display, string exe, IReadOnlyDictionary<string, TimeSpan>? timeouts,
        TimeSpan defaultTimeout, IReadOnlySet<string>? exitAfter = null, bool persistent = true)
        => new(id, display, exe, new HashSet<string>(timeouts?.Keys ?? [], StringComparer.Ordinal),
            timeouts ?? new Dictionary<string, TimeSpan>(StringComparer.Ordinal), defaultTimeout,
            exitAfter ?? new HashSet<string>(StringComparer.Ordinal), persistent);

    public static readonly IReadOnlyDictionary<string, BackendModuleSpec> All =
        new Dictionary<string, BackendModuleSpec>(StringComparer.Ordinal)
        {
            [Ace] = Make(Ace, "ACE 清理后端", "DeltaNFD.Backend.Ace.exe",
                new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
                {
                    [BackendCommands.AceScan] = ThreeMinutes,
                    [BackendCommands.AceCoreCheck] = TwoMinutes,
                    [BackendCommands.AceClean] = FifteenMinutes,
                    [BackendCommands.AceCoreClean] = TwoMinutes,
                    [BackendCommands.AceReconcile] = ThreeMinutes,
                    [BackendCommands.AceVirtCheck] = ThreeMinutes,
                }, ThreeMinutes, AceExitAfter),

            [Gpu] = Make(Gpu, "显卡后端", "DeltaNFD.Backend.Gpu.exe",
                new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
                {
                    [BackendCommands.GpuSpoofStatus] = TwoMinutes,
                    [BackendCommands.GpuSpoofApply] = FiveMinutes,
                    [BackendCommands.GpuSpoofRestore] = FiveMinutes,
                    [BackendCommands.GpuSpoofReload] = FiveMinutes,
                    [BackendCommands.GpuWatchEvaluate] = TwoMinutes,
                    [BackendCommands.GpuDriverStatus] = TwoMinutes,
                    [BackendCommands.GpuDriverRead] = TwoMinutes,
                    [BackendCommands.GpuDriverApply] = FiveMinutes,
                    [BackendCommands.GpuDriverRestore] = FiveMinutes,
                    [BackendCommands.GpuDriverLock] = TwoMinutes,
                    [BackendCommands.GpuShaderCacheApply] = TwoMinutes,
                    [BackendCommands.GpuReconcile] = FiveMinutes,
                    [BackendCommands.GpuRecover] = FifteenMinutes,
                }, FiveMinutes),

            [Runtime] = Make(Runtime, "运行库后端", "DeltaNFD.Backend.Runtime.exe",
                new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
                {
                    [BackendCommands.RuntimeStatus] = TwoMinutes,
                    [BackendCommands.RuntimeFileUseScan] = TwoMinutes,
                    [BackendCommands.RuntimeFileUseClose] = TwoMinutes,
                    [BackendCommands.RuntimeHighFpsPreview] = TimeSpan.FromMinutes(5),
                    [BackendCommands.RuntimeHighFpsRepair] = TimeSpan.FromMinutes(150),
                    [BackendCommands.Vc14Scan] = TwoMinutes,
                    [BackendCommands.Vc14Preview] = TwoMinutes,
                    [BackendCommands.Vc14Repair] = TimeSpan.FromMinutes(42),
                    [BackendCommands.Vc14LegacyClean] = FiveMinutes,
                    [BackendCommands.RuntimeGuardEnable] = FiveMinutes,
                    [BackendCommands.RuntimeGuardDisable] = FiveMinutes,
                    [BackendCommands.RuntimeGuardSelectMode] = TwoMinutes,
                    [BackendCommands.RuntimeGuardCleanupLegacy] = FiveMinutes,
                    [BackendCommands.RuntimeUe4RepairAcl] = FiveMinutes,
                    [BackendCommands.RuntimeReconcile] = FiveMinutes,
                    [BackendCommands.RuntimeRecover] = FifteenMinutes,
                }, FiveMinutes),

            [CpuFrame] = Make(CpuFrame, "CPU/帧格后端", "DeltaNFD.Backend.CpuFrame.exe",
                new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
                {
                    [BackendCommands.CpuFrameStatus] = TwoMinutes,
                    [BackendCommands.CpuFrameMonitorStart] = TwoMinutes,
                    [BackendCommands.CpuFrameMonitorStop] = TwoMinutes,
                    [BackendCommands.CpuFramePriorityApply] = TwoMinutes,
                    [BackendCommands.CpuFrameAffinityApply] = TwoMinutes,
                    [BackendCommands.CpuFrameCcdApply] = TwoMinutes,
                    [BackendCommands.CpuFrameCcdRevert] = TwoMinutes,
                    [BackendCommands.CpuFrameSingleCcdApply] = TwoMinutes,
                    [BackendCommands.CpuFrameSingleCcdRevert] = TwoMinutes,
                    [BackendCommands.CpuFrameRestore] = TwoMinutes,
                    [BackendCommands.CpuFrameDwmRestart] = TwoMinutes,
                    [BackendCommands.CpuFramePowerLock] = TwoMinutes,
                    [BackendCommands.CpuFramePowerUnlock] = TwoMinutes,
                    [BackendCommands.CpuFrameTimerApply] = TwoMinutes,
                    [BackendCommands.CpuFrameTimerRestore] = TwoMinutes,
                    [BackendCommands.CpuFrameTweaksApply] = FiveMinutes,
                    [BackendCommands.CpuFrameTweaksRevert] = FiveMinutes,
                    [BackendCommands.CpuFrameBoostApply] = TwoMinutes,
                    [BackendCommands.CpuFrameMemoryClean] = TwoMinutes,
                    [BackendCommands.CpuFrameTopology] = TwoMinutes,
                    [BackendCommands.CpuFrameAffinityCheck] = TwoMinutes,
                    [BackendCommands.CpuFrameAffinityPending] = TwoMinutes,
                    [BackendCommands.CpuFrameMemoryLimitDetect] = TwoMinutes,
                    [BackendCommands.CpuFrameMemoryLimitRemove] = TwoMinutes,
                    [BackendCommands.CpuFramePowerIncomplete] = TwoMinutes,
                    [BackendCommands.CpuFramePowerPreserve] = TwoMinutes,
                    [BackendCommands.CpuFrameReconcile] = TwoMinutes,
                    [BackendCommands.CpuFrameRecover] = FifteenMinutes,
                    [BackendCommands.CpuFrameOfflineRestore] = FiveMinutes,
                }, TwoMinutes),

            [Procman] = Make(Procman, "进程管理后端", "DeltaNFD.Backend.Procman.exe",
                new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
                {
                    [BackendCommands.ProcmanActivate] = FiveMinutes,
                    [BackendCommands.ProcmanStatus] = TwoMinutes,
                    [BackendCommands.ProcmanList] = TwoMinutes,
                    [BackendCommands.ProcmanDetail] = TwoMinutes,
                    [BackendCommands.ProcmanCapability] = TwoMinutes,
                    [BackendCommands.ProcmanCpuTopology] = TwoMinutes,
                    [BackendCommands.ProcmanReconcile] = TwoMinutes,
                    [BackendCommands.ProcmanSet] = TwoMinutes,
                    [BackendCommands.ProcmanReset] = TwoMinutes,
                    [BackendCommands.ProcmanRelease] = TwoMinutes,
                    [BackendCommands.ProcmanRestoreAll] = FiveMinutes,
                    [BackendCommands.ProcmanRecover] = FifteenMinutes,
                    [BackendCommands.ProcmanRulesList] = TwoMinutes,
                    [BackendCommands.ProcmanRulesSave] = TwoMinutes,
                    [BackendCommands.ProcmanRulesDelete] = TwoMinutes,
                    [BackendCommands.ProcmanResponsivenessConfig] = TwoMinutes,
                    [BackendCommands.ProcmanResponsivenessEvents] = TwoMinutes,
                    [BackendCommands.ProcmanMemoryPreview] = TwoMinutes,
                    [BackendCommands.ProcmanMemoryTrim] = TwoMinutes,
                    [BackendCommands.ProcmanBackgroundGet] = TwoMinutes,
                    [BackendCommands.ProcmanBackgroundSave] = TwoMinutes,
                    [BackendCommands.ProcmanFrameBackgroundSync] = TwoMinutes,
                    [BackendCommands.ProcmanNativeProbe] = TwoMinutes,
                }, TwoMinutes),

            [System] = Make(System, "系统维护后端", "DeltaNFD.Backend.SystemMaintenance.exe",
                new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
                {
                    [BackendCommands.SystemCleanupScan] = TwoMinutes,
                    [BackendCommands.SystemCleanupRun] = TimeSpan.FromMinutes(10),
                    [BackendCommands.SystemTweaksStatus] = FiveMinutes,
                    [BackendCommands.SystemTweaksApply] = FifteenMinutes,
                    [BackendCommands.SystemTweaksRestore] = FifteenMinutes,
                    [BackendCommands.SystemTweaksBackups] = TwoMinutes,
                    [BackendCommands.SystemTweaksHyperV] = FiveMinutes,
                    [BackendCommands.SystemTweaksVbs] = FiveMinutes,
                    [BackendCommands.SystemAdvancedStatus] = FiveMinutes,
                    [BackendCommands.SystemAdvancedApply] = FifteenMinutes,
                    [BackendCommands.SystemAdvancedRestore] = FifteenMinutes,
                    [BackendCommands.SystemCleanupShaderCache] = FiveMinutes,
                    [BackendCommands.SystemPowerSchemes] = TwoMinutes,
                    [BackendCommands.SystemPowerSetScheme] = TwoMinutes,
                    [BackendCommands.SystemPowerImportNoPowerSave] = TwoMinutes,
                    [BackendCommands.SystemPowerEnsureName] = TwoMinutes,
                    [BackendCommands.SystemPowerHibernateGet] = TwoMinutes,
                    [BackendCommands.SystemPowerHibernateSet] = TwoMinutes,
                    [BackendCommands.SystemPowerHeteroList] = TwoMinutes,
                    [BackendCommands.SystemPowerHeteroSet] = TwoMinutes,
                    [BackendCommands.SystemShaderStatus] = TwoMinutes,
                    [BackendCommands.SystemShaderDiagnose] = TwoMinutes,
                    [BackendCommands.SystemShaderClear] = FiveMinutes,
                    [BackendCommands.SystemShaderRecoveryClean] = FiveMinutes,
                    [BackendCommands.SystemPagefileStatus] = TwoMinutes,
                    [BackendCommands.SystemPagefileSet] = FiveMinutes,
                    [BackendCommands.SystemPagefileRestoreAuto] = FiveMinutes,
                    [BackendCommands.SystemPagefileRestoreOriginal] = FiveMinutes,
                    [BackendCommands.SystemReconcile] = FiveMinutes,
                    [BackendCommands.SystemRecover] = FifteenMinutes,

                    [BackendCommands.SystemBxStatus] = FiveMinutes,
                    [BackendCommands.SystemBxGearInfo] = TwoMinutes,
                    [BackendCommands.SystemBxApply] = FifteenMinutes,
                    [BackendCommands.SystemBxGearApply] = FiveMinutes,
                    [BackendCommands.SystemBxServiceGroupResolve] = TwoMinutes,
                    [BackendCommands.SystemBxServiceGroupStatus] = TwoMinutes,
                    [BackendCommands.SystemBxServiceGroupApply] = TimeSpan.FromMinutes(10),
                    [BackendCommands.SystemBxBackups] = TwoMinutes,
                    [BackendCommands.SystemBxRestoreAll] = FifteenMinutes,
                    [BackendCommands.SystemBxCacheInvalidate] = TwoMinutes,
                    [BackendCommands.SystemDefenderPreflight] = ThreeMinutes,
                    [BackendCommands.SystemDefenderProtectionState] = ThreeMinutes,
                    [BackendCommands.SystemDefenderExclusionsAdd] = ThreeMinutes,
                    [BackendCommands.SystemDefenderPostApply] = FiveMinutes,
                    [BackendCommands.SystemMemoryStatus] = TwoMinutes,
                    [BackendCommands.SystemMemoryPurge] = TwoMinutes,
                    [BackendCommands.SystemOptimizeRun] = FifteenMinutes,
                    [BackendCommands.SystemProcessesScan] = TwoMinutes,
                    [BackendCommands.SystemProcessesKill] = FiveMinutes,
                    [BackendCommands.SystemProcessesTrim] = FiveMinutes,
                }, FiveMinutes),
        };

    public static BackendModuleSpec Require(string moduleId) =>
        All.TryGetValue(moduleId, out var spec)
            ? spec
            : throw new BackendContractException($"未知官方后端模块“{moduleId}”。");

    /// <summary>命令是否属于该模块白名单（禁止任意命令、任意 shell）。</summary>
    public static bool IsAllowed(string moduleId, string command) =>
        All.TryGetValue(moduleId, out var spec) && spec.Commands.Contains(command);

    /// <summary>
    /// 只读命令集合（不写入系统）：这些命令不写入写前执行意图，失败也不需要恢复核对。
    /// 未在集合内的白名单命令一律视为「可能写入系统」，必须先落盘执行意图（写前日志）。
    /// </summary>
    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.Ordinal)
    {
        BackendCommands.ProcmanMemoryPreview,
        BackendCommands.ProcmanBackgroundGet,
        BackendCommands.AceScan, BackendCommands.AceCoreCheck, BackendCommands.AceReconcile,
        BackendCommands.AceVirtCheck,
        BackendCommands.GpuSpoofStatus, BackendCommands.GpuWatchEvaluate,
        BackendCommands.GpuDriverStatus, BackendCommands.GpuDriverRead, BackendCommands.GpuReconcile,
        BackendCommands.RuntimeStatus, BackendCommands.RuntimeReconcile, BackendCommands.Vc14Scan, BackendCommands.Vc14Preview,
        BackendCommands.RuntimeHighFpsPreview, BackendCommands.RuntimeFileUseScan,
                BackendCommands.CpuFrameStatus, BackendCommands.CpuFrameReconcile,
        BackendCommands.CpuFrameTopology, BackendCommands.CpuFrameAffinityCheck,
        BackendCommands.CpuFrameAffinityPending, BackendCommands.CpuFrameMemoryLimitDetect,
        BackendCommands.CpuFramePowerIncomplete,
        BackendCommands.ProcmanStatus, BackendCommands.ProcmanList,
        BackendCommands.ProcmanDetail, BackendCommands.ProcmanCapability,
        BackendCommands.ProcmanCpuTopology, BackendCommands.ProcmanReconcile,
        BackendCommands.ProcmanNativeProbe,
        BackendCommands.ProcmanRulesList, BackendCommands.ProcmanResponsivenessEvents,
        BackendCommands.SystemCleanupScan, BackendCommands.SystemTweaksStatus,
        BackendCommands.SystemTweaksBackups, BackendCommands.SystemTweaksHyperV, BackendCommands.SystemTweaksVbs,
        BackendCommands.SystemAdvancedStatus, BackendCommands.SystemPowerSchemes,
        BackendCommands.SystemPowerHibernateGet, BackendCommands.SystemPowerHeteroList,
        BackendCommands.SystemShaderStatus, BackendCommands.SystemShaderDiagnose,
        BackendCommands.SystemPagefileStatus, BackendCommands.SystemReconcile,
        // T3：扩展优化库/Defender/内存的只读命令
        BackendCommands.SystemBxStatus, BackendCommands.SystemBxGearInfo,
        BackendCommands.SystemBxServiceGroupResolve, BackendCommands.SystemBxServiceGroupStatus,
        BackendCommands.SystemBxBackups, BackendCommands.SystemBxCacheInvalidate,
        BackendCommands.SystemDefenderPreflight, BackendCommands.SystemDefenderProtectionState,
        BackendCommands.SystemMemoryStatus,
        BackendCommands.SystemProcessesScan,
    };

    public static bool IsReadOnly(string moduleId, string command) =>
        All.ContainsKey(moduleId) && ReadOnlyCommands.Contains(command);

    /// <summary>true = 该命令可能写入系统状态，调用前必须落盘执行意图。</summary>
    public static bool IsMutating(string moduleId, string command) =>
        All.ContainsKey(moduleId) && All[moduleId].Commands.Contains(command) && !IsReadOnly(moduleId, command);

    /// <summary>
    /// 恢复专用命令：本身会写入系统，但属于「恢复核对」流程，不作为新的未知操作登记——
    /// 执行前的未确认记录仍在盘上充当证据，由恢复协调器在复核通过后归档。
    /// 因此这些命令不写新的执行意图，否则会与待核对记录冲突而无法恢复。
    /// </summary>
    private static readonly HashSet<string> NoIntentCommands = new(StringComparer.Ordinal)
    {
        BackendCommands.GpuRecover, BackendCommands.RuntimeRecover, BackendCommands.SystemRecover,
        BackendCommands.CpuFrameRecover, BackendCommands.ProcmanRecover,
        BackendCommands.SystemDefenderPostApply,
    };

    /// <summary>true = 发送前必须落盘写前执行意图。</summary>
    public static bool RequiresWriteIntent(string moduleId, string command) =>
        IsMutating(moduleId, command) && !NoIntentCommands.Contains(command);

    /// <summary>这些成功的静态优化可在退出后保留，原值备份不是未确认事务。</summary>
    public static bool RetainsStaticBackup(string moduleId, string command) => moduleId == System && command is
        BackendCommands.SystemBxApply or BackendCommands.SystemBxGearApply or BackendCommands.SystemBxServiceGroupApply;

    /// <summary>公开只读集合（只读命令的负向校验用）。</summary>
    public static IReadOnlySet<string> ReadOnly => ReadOnlyCommands;

    /// <summary>该模块的只读状态核对命令；未提供时为 null。</summary>
    public static string? ReconcileCommand(string moduleId) => moduleId switch
    {
        Ace => BackendCommands.AceReconcile,
        Gpu => BackendCommands.GpuReconcile,
        Runtime => BackendCommands.RuntimeReconcile,
        CpuFrame => BackendCommands.CpuFrameReconcile,
        Procman => BackendCommands.ProcmanReconcile,
        System => BackendCommands.SystemReconcile,
        _ => null,
    };

    /// <summary>该模块的按备份恢复命令；无可信备份路径时为 null（如 ACE 清理不可逆）。</summary>
    public static string? RecoverCommand(string moduleId) => moduleId switch
    {
        Gpu => BackendCommands.GpuRecover,
        Runtime => BackendCommands.RuntimeRecover,
        CpuFrame => BackendCommands.CpuFrameRecover,
        Procman => BackendCommands.ProcmanRecover,
        System => BackendCommands.SystemRecover,
        _ => null,
    };

    public static TimeSpan TimeoutFor(string moduleId, string command)
    {
        if (!All.TryGetValue(moduleId, out var spec))
            throw new BackendContractException($"未知官方后端模块“{moduleId}”。");
        if (!spec.Commands.Contains(command))
            throw new BackendContractException($"命令“{command}”不在模块 {moduleId} 的白名单内。");
        return spec.CommandTimeouts.TryGetValue(command, out var timeout) ? timeout : spec.DefaultTimeout;
    }
}
