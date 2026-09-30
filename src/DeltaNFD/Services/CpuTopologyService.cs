using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using DeltaNFD.Native;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>CPU 拓扑检测与核心锁定的真实实现。</summary>
public sealed class CpuTopologyService : ICpuTopologyService
{
    private readonly TweakBackupStore _backups;

    public CpuTopologyService() : this(TweakBackupStore.Default) { }
    public CpuTopologyService(TweakBackupStore backups) { _backups = backups; }

    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;
    private const int RelationGroup = 4;
    private const int RelationProcessorDie = 5;
    private const int CacheLevelL3 = 3;

    // ---------------- GetTopologyAsync ----------------

    public Task<CpuTopology> GetTopologyAsync() => Task.Run(() =>
    {
        var (vendorId, cpuName) = ReadCpuIdentity();
        var isAmd = vendorId.Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase);
        var isIntel = vendorId.Contains("GenuineIntel", StringComparison.OrdinalIgnoreCase);
        var vendor = isAmd ? "AMD" : isIntel ? "Intel" : "其他";

        var (cores, groupLpCounts, l3Entries, dieEntries) = ReadTopology();
        var detectedCores = cores.ToList();

        // 兜底：拓扑 API 解析失败时用 Environment.ProcessorCount 构建核心列表
        var systemLogical = Environment.ProcessorCount;
        if (cores.Count == 0 && systemLogical > 0)
        {
            for (var i = 0; i < Math.Min(systemLogical, 64); i++)
            {
                cores.Add(new CpuCoreInfo
                {
                    Group = 0, Mask = 1UL << i,
                    EfficiencyClass = 0, Smt = i % 2 == 1,
                    LogicalProcessors = [i],
                });
            }
            if (groupLpCounts.Count == 0) groupLpCounts.Add(systemLogical);
        }

        // 大小核（P/E 核）判定。
        // ⚠ Intel 语义：GetLogicalProcessorInformationEx 的 EfficiencyClass 对 **P 核返回 0**、
        //    对 E 核返回 1（而 AMD 上该字段恒为 0，不以 0 代表 P 核）。
        //    因此「EfficiencyClass > 0 = P 核」是错的——在 Intel 上会把 P 核和 E 核完全判反。
        //    正确做法：把类值升序排名，**等于最大值的那一档才是 P 核**，其余为 E 核。
        //    单档（非大小核）时全部核心同类，isHybrid = false。
        var hybrid = ClassifyHybridCores(cores);

        var pCores = cores.Where(c => (hybrid.PCoreMask & c.Mask) != 0).ToList();
        var eCores = cores.Where(c => (hybrid.ECoreMask & c.Mask) != 0).ToList();
        var isHybrid = hybrid.IsHybrid;
        var hasHt = cores.Any(c => c.Smt);
        var logicalProcessors = cores.Sum(c => c.LogicalProcessors.Count);

        var (ccds, ccdSource) = isAmd
            ? CcdTopologyDetector.Detect(detectedCores, dieEntries, l3Entries)
            : (new List<CcdInfo>(), CcdDetectionSource.Unknown);
        var isMultiCcd = isAmd && ccds.Count >= 2;

        var allMask = GetSingleGroupAffinityMask(detectedCores, groupLpCounts);

        var ccdAdvice = BuildCcdAdvice(isAmd, ccds, ccdSource);
        var hybridAdvice = isHybrid
            ? $"大小核架构：{pCores.Count} 性能核（P）+ {eCores.Count} 效率核（E）。可把游戏锁定到 P 核降低调度延迟。"
            : "";

        return new CpuTopology
        {
            Vendor = vendor, CpuName = cpuName,
            PhysicalCores = cores.Count, LogicalProcessors = logicalProcessors,
            GroupCount = groupLpCounts.Count,
            IsHybrid = isHybrid, PCoreCount = pCores.Count, ECoreCount = eCores.Count,
            HasHyperThreading = hasHt,
            IsAmd = isAmd, IsAmdMultiCcd = isMultiCcd, CcdCount = isAmd ? ccds.Count : 0,
            Ccds = ccds, CcdAdvice = ccdAdvice, CcdDetectionSource = ccdSource,
            AllMask = allMask,
            PCoreMask = hybrid.PCoreMask,
            ECoreMask = hybrid.ECoreMask,
            HybridAdvice = hybridAdvice,
        };
    });

    /// <summary>大小核分类结果。</summary>
    internal readonly record struct HybridClassification(
        bool IsHybrid, ulong PCoreMask, ulong ECoreMask, int DistinctClassCount);

    /// <summary>
    /// 按 EfficiencyClass 分类 P 核 / E 核（纯函数，便于单测）。
    ///
    /// 这是全项目最容易搞反的一处，务必看清语义：
    ///   **Intel 12 代及以后，P 核 EfficiencyClass = 0，E 核 = 1。**
    ///   （AMD 上该字段恒为 0，不以 0 代表 P 核。）
    /// 因此：
    ///   · 不能按「&gt; 0 = P 核」归类 —— 在 Intel 上会把 P 核和 E 核完全判反；
    ///   · **类值最小**的那一档才是 P 核（Intel 语义：更"高效"的 E 核排在后面）。
    /// 单档（所有核心同类，如普通 AMD / Intel 非混合架构）→ IsHybrid = false，两个掩码均为 0。
    /// </summary>
    internal static HybridClassification ClassifyHybridCores(IReadOnlyList<CpuCoreInfo> cores)
    {
        if (cores.Count == 0)
        {
            return new HybridClassification(false, 0, 0, 0);
        }

        var distinctClasses = cores.Select(c => c.EfficiencyClass).Distinct().OrderBy(v => v).ToList();
        if (distinctClasses.Count < 2)
        {
            // 同构架构：没有大小核之分，绝不能把「类值 0」当成 P 核或 E 核
            return new HybridClassification(false, 0, 0, distinctClasses.Count);
        }

        // Intel 语义：类值最小的档 = P 核（P 核为 0，E 核为 1）
        var pClass = distinctClasses[0];
        ulong pMask = 0, eMask = 0;
        foreach (var core in cores)
        {
            if (core.EfficiencyClass == pClass)
            {
                pMask |= core.Mask;
            }
            else
            {
                eMask |= core.Mask;
            }
        }

        return new HybridClassification(true, pMask, eMask, distinctClasses.Count);
    }

    // ---------------- 亲和性 ----------------

    /// <summary>双CCD 专属调度是否处于激活状态（互斥：激活时 CPU 亲和性规则让位）。</summary>
    public static bool DualCcdSchedulingActive { get; private set; }

    /// <summary>单CCD 排除 CPU0 是否处于激活状态（互斥：激活时 CPU 亲和性规则让位）。</summary>
    public static bool SingleCcdExcludeActive { get; private set; }

    private static readonly Lazy<ulong> RuleAvailableMask = new(() =>
    {
        var data = ReadTopology();
        return GetSingleGroupAffinityMask(data.Cores, data.GroupLpCounts);
    });
    private static readonly object AffinityRuleLock = new();
    private static readonly Dictionary<int, (DateTime StartTime, nint OriginalMask)> AffinityRuleOriginals = new();

    internal static ulong GetSingleGroupAffinityMask(IReadOnlyList<CpuCoreInfo> cores, IReadOnlyList<int> groupLpCounts)
    {
        if (groupLpCounts.Count != 1 || groupLpCounts[0] is < 1 or > 64 || cores.Count == 0)
        {
            return 0;
        }

        ulong mask = 0;
        foreach (var core in cores)
        {
            if (core.Group != 0 || core.Mask == 0 || (mask & core.Mask) != 0)
            {
                return 0;
            }

            mask |= core.Mask;
        }

        return BitOperations.PopCount(mask) == groupLpCounts[0] ? mask : 0;
    }

    /// <summary>
    /// 硬锁核统一入口（HANDOFF §27 技术标准对齐）：直写 ProcessorAffinity，幂等（已一致则跳过）。
    /// 双CCD 调度 / 单CCD 排除 CPU0 / CPU 亲和性规则三处统一走本入口。
    /// </summary>
    /// <returns>true = 实际发生了变更。</returns>
    public static bool ApplyHardAffinity(Process process, ulong mask)
    {
        var target = (nint)mask;
        if (process.ProcessorAffinity == target)
        {
            return false;
        }

        process.ProcessorAffinity = target;
        return true;
    }

    /// <summary>
    /// CPU 亲和性规则 tick（HANDOFF §27）：把掩码持续保持到三角洲进程（硬锁核）。
    /// 互斥：双CCD / 单CCD 调度激活时让位（避免两套锁核互相覆盖）。
    /// </summary>
    public static void ApplyAffinityRuleTick(ulong mask) =>
        ApplyAffinityRuleTick(mask, () => Process.GetProcessesByName(DeltaForceLocator.GameProcessName));

    internal static void ApplyAffinityRuleTick(ulong mask, Func<Process[]> getProcesses)
    {
        var availableMask = RuleAvailableMask.Value;
        if (mask == 0 || availableMask == 0 || (mask & ~availableMask) != 0)
        {
            RestoreAffinityRule();
            return;
        }

        lock (AffinityRuleLock)
        {
            if (DualCcdSchedulingActive || SingleCcdExcludeActive)
            {
                AffinityRuleOriginals.Clear();
                return; // 专属调度激活中，亲和性规则让位
            }

            var livePids = new HashSet<int>();
            foreach (var process in getProcesses())
            {
                using (process)
                {
                    try
                    {
                        var pid = process.Id;
                        livePids.Add(pid);
                        var startTime = process.StartTime.ToUniversalTime();
                        if (AffinityRuleOriginals.TryGetValue(pid, out var original) && original.StartTime != startTime)
                            AffinityRuleOriginals.Remove(pid);
                        if (!AffinityRuleOriginals.ContainsKey(pid) && process.ProcessorAffinity != (nint)mask)
                        {
                            AffinityRuleOriginals[pid] = (startTime, process.ProcessorAffinity);
                        }

                        if (ApplyHardAffinity(process, mask))
                        {
                            Log.Info($"CPU亲和性：三角洲 PID {pid} 已锁定掩码 0x{mask:X}");
                        }
                    }
                    catch (Exception ex) { Log.Warn($"CPU亲和性：应用失败：{ex.Message}"); }
                }
            }

            foreach (var pid in AffinityRuleOriginals.Keys.Where(pid => !livePids.Contains(pid)).ToArray())
                AffinityRuleOriginals.Remove(pid);
        }
    }

    public static void RestoreAffinityRule()
    {
        lock (AffinityRuleLock)
        {
            if (DualCcdSchedulingActive || SingleCcdExcludeActive)
            {
                AffinityRuleOriginals.Clear();
                return;
            }

            foreach (var (pid, original) in AffinityRuleOriginals.ToArray())
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (process.StartTime.ToUniversalTime() == original.StartTime)
                    {
                        process.ProcessorAffinity = original.OriginalMask;
                        Log.Info($"CPU亲和性：三角洲 PID {pid} 已恢复原掩码 0x{(ulong)original.OriginalMask:X}");
                    }
                    AffinityRuleOriginals.Remove(pid);
                }
                catch (ArgumentException) { AffinityRuleOriginals.Remove(pid); }
                catch (Exception ex) { Log.Warn($"CPU亲和性：PID {pid} 恢复失败：{ex.Message}"); }
            }
        }
    }

    public Task<bool> IsGameRunningAsync() => Task.Run(DeltaForceLocator.IsGameRunning);

    // ---------------- 双CCD 调度 ----------------

    private static (ulong GameMask, ulong OtherMask) ComputeDualCcdMasks(IReadOnlyList<CcdInfo> ccds, int gameCcd)
    {
        var gameMask = ccds[gameCcd].Mask;
        var otherMask = ccds[1 - gameCcd].Mask;
        if (gameCcd == 0) gameMask &= ~1UL;
        else otherMask &= ~1UL;
        return (gameMask, otherMask);
    }

    /// <summary>
    /// 双CCD 激活期间的软锁核豁免名单（用户指定：System / Registry / Memory Compression）；
    /// Idle（PID 0）与 System（PID 4）按 PID 一并豁免。游戏进程由硬锁核分支单独处理，不做软锁。
    /// </summary>
    private static readonly HashSet<string> DualCcdExemptNames = new(StringComparer.OrdinalIgnoreCase)
    { "System", "Registry", "Memory Compression", "Idle" };

    /// <summary>双CCD 软锁核已覆盖的进程 PID（激活期间新进程由 tick 补齐；PID 复用概率低，可接受）。</summary>
    private static readonly HashSet<int> DualCcdCoveredPids = new();

    private static ulong _dualCcdGameMask;
    private static ulong _dualCcdOtherMask;

    /// <summary>Apply / Revert / Tick 互斥锁（Apply 在 UI 任务线程、Tick 在帧格轮询线程）。</summary>
    private static readonly object DualCcdLock = new();

    private static bool IsDualCcdExempt(Process process) =>
        process.Id <= 4 ||
        DualCcdExemptNames.Contains(process.ProcessName) ||
        process.ProcessName.Equals(DeltaForceLocator.GameProcessName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把进程软锁核到掩码（CPU 集 = 调度器偏好，忙时可溢出）：
    /// 先设进程默认集（覆盖之后新建的线程），再遍历既有线程逐个设线程所选集
    /// （SetProcessDefaultCpuSets 不影响既有线程，必须逐线程补齐）。
    /// 进程句柄打不开（拒绝访问 / 受保护进程）返回 false，调用方按用户要求跳过该进程。
    /// </summary>
    private static bool TryApplyCcdSoft(Process process, ulong mask)
    {
        var handle = Native.CpuSets.OpenProcessForCpuSets(process.Id);
        if (handle == nint.Zero)
        {
            return false;
        }

        try
        {
            if (!Native.CpuSets.SetDefaultByMask(handle, mask))
            {
                return false;
            }

            try
            {
                foreach (ProcessThread thread in process.Threads)
                {
                    var threadHandle = Native.CpuSets.OpenThreadForCpuSets(thread.Id);
                    if (threadHandle == nint.Zero)
                    {
                        continue; // 被拒线程跳过
                    }

                    try
                    {
                        Native.CpuSets.SetThreadSelectedByMask(threadHandle, mask);
                    }
                    finally
                    {
                        Native.CpuSets.CloseHandleSafe(threadHandle);
                    }
                }
            }
            catch
            {
                // 线程快照枚举中途失效（进程退出竞态）：默认集已设置成功，
                // 既有线程锁核部分跳过——之后新建的线程仍会继承默认集
            }

            return true;
        }
        finally
        {
            Native.CpuSets.CloseHandleSafe(handle);
        }
    }

    public Task<OperationResult> ApplyDualCcdSchedulingAsync(int gameCcdIndex) => Task.Run(async () =>
    {
        Log.Info($"双CCD调度：应用（游戏→CCD{gameCcdIndex}）");
        var topology = await GetTopologyAsync();
        if (!topology.IsAmdMultiCcd || topology.Ccds.Count != 2 ||
            topology.GroupCount != 1 || topology.Ccds.Any(c => c.Group != 0) ||
            gameCcdIndex is < 0 or > 1)
        {
            return OperationResult.Fail("未取得可用的双CCD拓扑掩码，已拒绝应用调度。");
        }

        var (gameMask, otherMask) = ComputeDualCcdMasks(topology.Ccds, gameCcdIndex);
        if (gameMask == 0 || otherMask == 0)
        {
            return OperationResult.Fail("CCD 掩码无效，已拒绝应用调度。");
        }

        lock (DualCcdLock)
        {
            _dualCcdGameMask = gameMask;
            _dualCcdOtherMask = otherMask;
            DualCcdCoveredPids.Clear();
        }

        // 游戏进程：硬锁核（对齐 CPU 亲和性规则的技术标准，HANDOFF §27）
        var gameApplied = 0;
        foreach (var process in Process.GetProcessesByName(DeltaForceLocator.GameProcessName))
        {
            using (process)
            {
                try
                {
                    ApplyHardAffinity(process, gameMask);
                    gameApplied++;
                }
                catch (Exception ex)
                {
                    return OperationResult.Fail($"三角洲硬锁核失败：{ex.Message}");
                }
            }
        }

        // 其余全部进程软锁核到非游戏 CCD（豁免名单见 DualCcdExemptNames）；
        // 打开被拒绝访问的进程直接跳过
        var locked = 0;
        var denied = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (IsDualCcdExempt(process))
                    {
                        continue;
                    }

                    if (TryApplyCcdSoft(process, otherMask))
                    {
                        lock (DualCcdLock) DualCcdCoveredPids.Add(process.Id);
                        locked++;
                    }
                    else
                    {
                        denied++;
                    }
                }
                catch
                {
                    denied++; // 进程中途退出等竞态：按跳过计数，不中断整体应用
                }
            }
        }

        // 双CCD调度优先于单CCD排除 CPU0；撤销双CCD时由上层恢复保存的单CCD规则。
        SingleCcdExcludeActive = false;
        DualCcdSchedulingActive = true;

        // 强制覆写异类策略（生效=4，线程/短线程=0）；失败只记日志不影响调度
        await ForceGamingHeteroPolicyAsync();

        var gameNote = gameApplied > 0 ? "" : "（三角洲当前未运行，启动后将由持续覆盖自动补锁）";
        return OperationResult.Ok(
            $"双CCD调度已生效：三角洲 → 硬锁核 CCD{gameCcdIndex}（0x{gameMask:X}）{gameNote}，其他进程 → 软锁核 CCD{1 - gameCcdIndex}（0x{otherMask:X}）"
            + $"（本次已锁 {locked} 个进程，{denied} 个因拒绝访问跳过，新进程由轮询持续补锁）。CPU0 留给系统。");
    });

    /// <summary>
    /// CPU 调度持续维持 tick（帧格 2 秒轮询调用；两者都未激活时仅一次轻量检查）：
    /// ① 游戏硬锁核维持——单CCD「排除 CPU0」优先（避免双CCD把排除覆盖回去），其次双CCD游戏掩码，
    ///    游戏晚启动由本 tick 自动补锁；② 双CCD激活期间给新出现的进程补软锁核（已覆盖 PID 跳过）。
    /// </summary>
    public static void ApplyCpuSchedulingTick()
    {
        lock (DualCcdLock)
        {
            if (SingleCcdExcludeActive)
            {
                var excludeCpu0Mask = RuleAvailableMask.Value & ~1UL;
                if (excludeCpu0Mask == 0) return;
                foreach (var process in Process.GetProcessesByName(DeltaForceLocator.GameProcessName))
                {
                    using (process)
                    {
                        try { ApplyHardAffinity(process, excludeCpu0Mask); } catch { }
                    }
                }
            }
            else if (DualCcdSchedulingActive)
            {
                foreach (var process in Process.GetProcessesByName(DeltaForceLocator.GameProcessName))
                {
                    using (process)
                    {
                        try { ApplyHardAffinity(process, _dualCcdGameMask); } catch { }
                    }
                }
            }

            if (!DualCcdSchedulingActive)
            {
                return; // 全系统软锁核只属于双CCD调度
            }

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (DualCcdCoveredPids.Contains(process.Id) || IsDualCcdExempt(process))
                    {
                        continue;
                    }

                    try
                    {
                        if (TryApplyCcdSoft(process, _dualCcdOtherMask))
                        {
                            DualCcdCoveredPids.Add(process.Id);
                        }
                    }
                    catch
                    {
                        // 进程恰好在枚举中途退出等竞态：跳过该进程，绝不中断 tick
                        //（中断会连带跳过 PollOnce 后续的响应加速与内存清理）
                    }
                }
            }
        }
    }

    public Task<OperationResult> RevertDualCcdSchedulingAsync() => Task.Run(async () =>
    {
        Log.Info("双CCD调度：撤销");
        var allMask = RuleAvailableMask.Value;
        if (allMask == 0)
            return OperationResult.Fail("当前处理器拓扑无法安全恢复进程亲和性，请检查处理器组配置。");

        lock (DualCcdLock)
        {
            DualCcdSchedulingActive = false;
            DualCcdCoveredPids.Clear();
        }

        var reverted = 0;

        // 所有进程恢复：默认集与既有线程都回到全核掩码（= 无偏好）；受保护进程打开失败自然跳过
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (IsDualCcdExempt(process))
                    {
                        continue;
                    }

                    if (TryApplyCcdSoft(process, allMask))
                    {
                        reverted++;
                    }
                }
                catch
                {
                    // 进程中途退出等竞态：跳过，不中断整体撤销
                }
            }
        }

        // 游戏硬锁核还原全核（旧版撤销不还原 ProcessorAffinity，游戏会一直锁在游戏 CCD 上）
        foreach (var process in Process.GetProcessesByName(DeltaForceLocator.GameProcessName))
        {
            using (process)
            {
                try { ApplyHardAffinity(process, allMask); } catch { }
            }
        }

        // 单CCD/双CCD 都已不在生效：还原异类策略原值
        await RestoreHeteroPolicyIfIdleAsync();

        return OperationResult.Ok($"已撤销双CCD调度：{reverted} 个进程恢复全核调度，三角洲硬锁核已还原，异类策略已还原。");
    });

    // ---------------- 单CCD 排除 CPU0 ----------------

    public async Task<OperationResult> ExcludeCpu0FromGameAsync()
    {
        Log.Info("单CCD优化：排除 CPU0");
        var excludeCpu0Mask = RuleAvailableMask.Value & ~1UL;
        if (excludeCpu0Mask == 0)
            return OperationResult.Fail("当前处理器拓扑无法安全排除 CPU0（需要单处理器组且至少两个可用逻辑处理器）。");

        var gameRunning = DeltaForceLocator.IsGameRunning();
        var applied = 0;
        var problems = new List<string>();
        foreach (var process in Process.GetProcessesByName(DeltaForceLocator.GameProcessName))
        {
            using (process)
            {
                try
                {
                    // 硬锁核统一入口（对齐 CPU 亲和性规则的技术标准，HANDOFF §27）
                    ApplyHardAffinity(process, excludeCpu0Mask);
                    applied++;
                }
                catch (Exception ex) { problems.Add(ex.Message); }
            }
        }

        // 游戏在运行却一个都没锁上 = 真失败；游戏未运行 = 登记规则，由轮询在游戏启动后持续应用
        if (gameRunning && applied == 0)
        {
            SingleCcdExcludeActive = false;
            return OperationResult.Fail($"排除 CPU0 失败：{string.Join("；", problems)}");
        }

        SingleCcdExcludeActive = true;
        await ForceGamingHeteroPolicyAsync();

        return gameRunning
            ? OperationResult.Ok("已将三角洲排除 CPU0（硬锁核，对当前会话立即生效）。")
            : OperationResult.Ok("三角洲当前未运行——排除 CPU0 规则已就绪，游戏启动后由轮询自动应用并持续保持。");
    }

    public async Task<OperationResult> RestoreGameFullCoresAsync()
    {
        Log.Info("单CCD优化：恢复全部核心");
        var allMask = RuleAvailableMask.Value;
        if (allMask == 0)
            return OperationResult.Fail("当前处理器拓扑无法安全恢复进程亲和性，请检查处理器组配置。");

        var gameRunning = DeltaForceLocator.IsGameRunning();
        SingleCcdExcludeActive = false;
        var applied = 0;
        foreach (var process in Process.GetProcessesByName(DeltaForceLocator.GameProcessName))
        {
            using (process)
            {
                try { ApplyHardAffinity(process, allMask); applied++; }
                catch { }
            }
        }

        await RestoreHeteroPolicyIfIdleAsync();

        return gameRunning
            ? (applied > 0
                ? OperationResult.Ok("已恢复三角洲到全部核心的默认调度。")
                : OperationResult.Fail("恢复失败。"))
            : OperationResult.Ok("已恢复：三角洲未运行，排除 CPU0 规则已解除（异类策略已还原原值）。");
    }

    // ---------------- 异类策略联动（单CCD/双CCD 调度启用时强制覆写，解除时还原） ----------------

    /// <summary>调度启用期间强制写入的异类策略：生效的异类策略=4，异类线程调度策略=0，异类短线程调度策略=0。</summary>
    private static readonly (string Guid, int Value)[] GamingHeteroOverrides =
    [
        (PowerService.HeteroPolicySettings[0].Guid, 4),
        (PowerService.HeteroPolicySettings[1].Guid, 0),
        (PowerService.HeteroPolicySettings[2].Guid, 0),
    ];

    private sealed record HeteroValueBackup(string Guid, int AcValue, int DcValue);

    /// <summary>覆写前的原值备份（null = 当前没有调度在覆写异类策略）。</summary>
    private static List<HeteroValueBackup>? _heteroOverrideBackup;
    private static readonly object HeteroOverrideLock = new();

    /// <summary>
    /// 强制覆写异类策略为调度配置。首次启用前备份原值（单CCD/双CCD 任一方已在覆盖中则跳过，
    /// 保留最初原值）；覆写失败只记日志不阻断调度本身。
    /// </summary>
    private async Task ForceGamingHeteroPolicyAsync()
    {
        lock (HeteroOverrideLock)
        {
            if (_heteroOverrideBackup is not null)
            {
                return;
            }
        }

        try
        {
            var backup = new List<HeteroValueBackup>();
            foreach (var info in await ServiceLocator.Power.GetHeteroPoliciesAsync())
            {
                if (info.Supported && info.AcValue is not null && info.DcValue is not null
                    && GamingHeteroOverrides.Any(o => o.Guid.Equals(info.SettingGuid, StringComparison.OrdinalIgnoreCase)))
                {
                    backup.Add(new HeteroValueBackup(info.SettingGuid, info.AcValue.Value, info.DcValue.Value));
                }
            }

            foreach (var (guid, value) in GamingHeteroOverrides)
            {
                var result = await ServiceLocator.Power.SetHeteroPolicyAsync(guid, value);
                if (!result.Success)
                {
                    Log.Error("异类策略：强制覆写失败 —— " + result.Message);
                    return;
                }
            }

            lock (HeteroOverrideLock) _heteroOverrideBackup = backup;
            Log.Info("异类策略：已强制覆写为调度配置（生效=4，线程/短线程调度=0），原值已备份");
        }
        catch (Exception ex)
        {
            Log.Error("异类策略：强制覆写异常（不影响调度本身）", ex);
        }
    }

    /// <summary>解除调度后还原异类策略原值（仅当单CCD与双CCD都已不在生效时执行一次）。</summary>
    private async Task RestoreHeteroPolicyIfIdleAsync()
    {
        List<HeteroValueBackup>? backup;
        lock (HeteroOverrideLock)
        {
            if (_heteroOverrideBackup is null || DualCcdSchedulingActive || SingleCcdExcludeActive)
            {
                return;
            }

            backup = _heteroOverrideBackup;
            _heteroOverrideBackup = null;
        }

        try
        {
            foreach (var item in backup)
            {
                await ServiceLocator.Power.SetHeteroPolicyValuesAsync(item.Guid, item.AcValue, item.DcValue);
            }

            Log.Info("异类策略：已还原为调度启用前的原值");
        }
        catch (Exception ex)
        {
            Log.Error("异类策略：还原原值失败（可在异类策略卡手动设置）", ex);
        }
    }

    // ---------------- 内存限制 ----------------

    public async Task<(bool Detected, int RemovedMb, string RawOutput)> DetectMemoryLimitAsync()
    {
        var (code, stdout, _) = await RunCaptureAsync("bcdedit.exe", "/enum {current}", TimeSpan.FromSeconds(20));
        if (code != 0 || stdout is null) return (false, 0, "bcdedit 查询失败");

        foreach (var line in SplitLines(stdout))
        {
            if (!line.Contains("removememory", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var lastPart = parts.LastOrDefault();
            if (lastPart is not null && int.TryParse(lastPart, out var removedMb))
                return (true, removedMb, line.Trim());
        }
        return (false, 0, "");
    }

    public async Task<OperationResult> RemoveMemoryLimitAsync()
    {
        Log.Info("内存限制：移除 bcdedit removememory");
        if (!ElevationHelper.IsElevated)
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);

        var (code, stdout, stderr) = await RunCaptureAsync("bcdedit.exe", "/deletevalue removememory", TimeSpan.FromSeconds(20));
        if (code != 0)
        {
            var reason = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return OperationResult.Fail($"移除内存限制失败：{FirstLine(reason)}（可能本就未设置过此值）");
        }
        return OperationResult.Ok(
            "已移除 bcdedit removememory 内存限制。重启电脑后恢复全部可用内存。建议改用「显卡伪装」来影响游戏行为——不会损失任何内存。",
            requiresReboot: true);
    }

    // ---------------- 工具 ----------------

    private static (string VendorId, string Name) ReadCpuIdentity()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString") as string ?? "未知处理器";
            var vendorId = key?.GetValue("VendorIdentifier") as string ?? "";
            return (vendorId, string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        }
        catch { return ("", "未知处理器"); }
    }

    private static int PopCount(ulong value)
    {
        var count = 0;
        while (value != 0) { value &= value - 1; count++; }
        return count;
    }

    private static string[] SplitLines(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var br = trimmed.IndexOfAny(['\r', '\n']);
        return br > 0 ? trimmed[..br] : trimmed;
    }

    private static async Task<(int Code, string StdOut, string StdErr)> RunCaptureAsync(
        string fileName, string arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName, Arguments = arguments,
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
        };
        using var process = Process.Start(psi);
        if (process is null) return (-1, "", $"无法启动 {fileName}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(timeout); }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "执行超时");
        }
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    // ---------------- 原生 API ----------------

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, nint buffer, ref int returnedLength);

    private static (int Code, nint Buffer, int Length) QueryTopology(int relation)
    {
        var length = 0;
        _ = GetLogicalProcessorInformationEx(relation, nint.Zero, ref length);
        if (length <= 0) return (-1, nint.Zero, 0);
        var buffer = Marshal.AllocHGlobal(length);
        if (!GetLogicalProcessorInformationEx(relation, buffer, ref length))
        {
            Marshal.FreeHGlobal(buffer);
            return (-1, nint.Zero, 0);
        }
        return (0, buffer, length);
    }

    private sealed record TopologyData(List<CpuCoreInfo> Cores, List<int> GroupLpCounts,
        List<CpuDomain> L3Entries, List<CpuDomain> DieEntries);

    private static void WalkTopology(int relation, Action<nint, int, int> visit)
    {
        var (code, buffer, length) = QueryTopology(relation);
        if (code != 0) return;
        try
        {
            var offset = 0;
            while (offset + 8 <= length)
            {
                var size = Marshal.ReadInt32(buffer, offset + 4);
                if (size < 8 || size > length - offset) break;
                if (Marshal.ReadInt32(buffer, offset) == relation) visit(buffer, offset, size);
                offset += size;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static TopologyData ReadTopology()
    {
        var cores = new List<CpuCoreInfo>();
        var l3Entries = new List<CpuDomain>();
        var dieEntries = new List<CpuDomain>();
        var groupLpCounts = new List<int>();

        WalkTopology(RelationProcessorCore, (buffer, offset, size) =>
        {
            if (size < 48) return;
            var flags = Marshal.ReadByte(buffer, offset + 8);
            var efficiencyClass = Marshal.ReadByte(buffer, offset + 9);
            var groupCount = (ushort)Marshal.ReadInt16(buffer, offset + 30);
            if (groupCount == 0 || groupCount > (size - 32) / 16) return;
            for (var g = 0; g < groupCount; g++)
            {
                var affinity = offset + 32 + 16 * g;
                var mask = (ulong)Marshal.ReadInt64(buffer, affinity);
                var group = (ushort)Marshal.ReadInt16(buffer, affinity + 8);
                if (mask == 0) continue;
                var lps = new List<int>();
                for (var bit = 0; bit < 64; bit++)
                {
                    if ((mask & (1UL << bit)) != 0) lps.Add(bit);
                }
                while (groupLpCounts.Count <= group) groupLpCounts.Add(0);
                groupLpCounts[group] += lps.Count;
                cores.Add(new CpuCoreInfo { Group = group, Mask = mask,
                    EfficiencyClass = efficiencyClass, Smt = (flags & 1) != 0 && lps.Count >= 2,
                    LogicalProcessors = lps });
            }
        });

        WalkTopology(RelationProcessorDie, (buffer, offset, size) =>
        {
            if (size < 48 || (ushort)Marshal.ReadInt16(buffer, offset + 30) != 1) return;
            dieEntries.Add(new CpuDomain(
                (ushort)Marshal.ReadInt16(buffer, offset + 40),
                (ulong)Marshal.ReadInt64(buffer, offset + 32)));
        });

        WalkTopology(RelationCache, (buffer, offset, size) =>
        {
            if (size < 56 || Marshal.ReadByte(buffer, offset + 8) != CacheLevelL3 ||
                (ushort)Marshal.ReadInt16(buffer, offset + 38) != 1) return;
            l3Entries.Add(new CpuDomain(
                (ushort)Marshal.ReadInt16(buffer, offset + 48),
                (ulong)Marshal.ReadInt64(buffer, offset + 40),
                Marshal.ReadInt32(buffer, offset + 12)));
        });

        WalkTopology(RelationGroup, (buffer, offset, size) =>
        {
            if (size < 32) return;
            var activeGroups = (ushort)Marshal.ReadInt16(buffer, offset + 10);
            if (activeGroups > (size - 32) / 48) return;
            for (var g = 0; g < activeGroups; g++)
            {
                var mask = (ulong)Marshal.ReadInt64(buffer, offset + 72 + 48 * g);
                while (groupLpCounts.Count <= g) groupLpCounts.Add(0);
                if (groupLpCounts[g] == 0) groupLpCounts[g] = PopCount(mask);
            }
        });

        return new TopologyData(cores, groupLpCounts, l3Entries, dieEntries);
    }

    private static string BuildCcdAdvice(bool isAmd, List<CcdInfo> ccds, CcdDetectionSource source)
    {
        if (!isAmd) return "非 AMD 处理器，CCD 判定不适用。";
        if (ccds.Count == 0) return "未能可靠识别 CCD 结构，相关双 CCD 调度不可用。";
        var sourceText = source == CcdDetectionSource.ProcessorDie ? "处理器 Die 拓扑" : "L3 覆盖掩码兜底";
        if (ccds.Count == 1) return $"单 CCD 设计（{ccds[0].CoreCount} 核，L3 {ccds[0].L3CacheMb:0} MB；依据：{sourceText}）。";
        var ccdText = string.Join("；", ccds.Select(c => $"CCD{c.Index}（{c.CoreCount} 核，L3 {c.L3CacheMb:0} MB）"));
        return $"检测到 {ccds.Count} 个 CCD（依据：{sourceText}）：{ccdText}。可通过核心锁定把游戏绑定到指定 CCD。";
    }
}
