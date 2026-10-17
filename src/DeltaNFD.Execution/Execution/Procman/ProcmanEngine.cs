using System.Diagnostics;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

/// <summary>
/// 通用进程管理引擎（procman 模块）：枚举/详情/能力读取 + 手动属性修改 + 还原 + 核对。
///
/// 与 CpuFrameEngine 的分工：CpuFrameEngine 只管理「当前游戏目标」；本引擎管理任意进程，
/// 二者通过 <see cref="ProcmanControlCoordinator"/>（按进程身份+属性）协调控制权。
/// 写入铁律：身份复核（PID+启动时间+路径）→ 控制权获取 → 写前记录落盘 → 执行 → 回读确认；
/// 无法回读确认的能力按「已请求待核对」呈现，绝不显示成功。
/// </summary>
public sealed partial class ProcmanEngine : IDisposable
{
    public const string CapabilityPriority = "priority";
    public const string CapabilityAffinity = "affinity";
    public const string CapabilityCpuSet = "cpuSet";
    public const string CapabilityMemoryPriority = "memoryPriority";
    public const string CapabilityEcoQos = "ecoQos";
    public const string CapabilityPriorityBoost = "priorityBoost";
    public const string CapabilityIoPriority = "ioPriority";
    public const string CapabilityGpuPreference = "gpuPreference";
    public const string CapabilityGpuPriority = "gpuPriority";

    private static readonly string LogPath = Path.Combine(AppDataPaths.Root, "procman-changes.json");
    private static readonly string RulesPath = Path.Combine(AppDataPaths.Root, "procman-rules.json");
    private static readonly TimeSpan RulesTickInterval = TimeSpan.FromSeconds(1);
    private readonly ProcmanCpuSampler _sampler = new();
    internal Func<ProcmanCpuSample>? SampleForTests { get; set; }
    internal Func<int>? ForegroundForTests { get; set; }
    internal Func<ProcmanProcessIdentity, bool>? ResponsivenessExcludedForTests { get; set; }
    internal Func<DateTime>? ClockForTests { get; set; }

    private readonly object _gate = new();
    private readonly ProcmanChangeLog _changeLog;
    private readonly ProcmanRuleStore _rules;
    private readonly ProcmanControlCoordinator _coordinator = new();
    private readonly ProcessPropertyGate _properties;
    private bool _frozen;
    private CancellationTokenSource? _rulesCts;
    private Task? _rulesLoop;
    private bool _rulesEngineRunning;
    private string _lastAction = "";
    private string _lastError = "";

    public ProcmanEngine() : this(LogPath, RulesPath, Path.Combine(AppDataPaths.Root, "procman-background.json")) { }

    internal ProcmanEngine(string logPath) : this(logPath, logPath + ".rules.json") { }

    internal ProcmanEngine(string logPath, string rulesPath) : this(logPath, rulesPath, logPath + ".background.json") { }

    private ProcmanEngine(string logPath, string rulesPath, string backgroundPath)
    {
        _changeLog = new ProcmanChangeLog(logPath);
        _rules = new ProcmanRuleStore(rulesPath);
        _backgroundStore = new ProcmanBackgroundStore(backgroundPath);
        try { _backgroundConfig = _backgroundStore.Load(); }
        catch (InvalidDataException ex) { _backgroundLoadError = ex.Message; }
        _properties = new ProcessPropertyGate(Path.Combine(Path.GetDirectoryName(logPath)!, "procman-owners.json"));
    }

    public void Dispose() => _ = StopRulesEngineAsync().GetAwaiter().GetResult();

    // ---------------- 状态 ----------------

    public ProcmanStatusSnapshot Snapshot() => new()
    {
        BackendPid = Environment.ProcessId,
        BackendPriorityClass = ReadOwnPriority(),
        RulesEngineRunning = _rulesEngineRunning,
        ResponsivenessEnabled = _responsiveness is { Enabled: true } && FrameSuppressionActive,
        ResponsivenessConfig = _responsiveness is { } config ? new ProcmanResponsivenessConfigArgs
        {
            Enabled = config.Enabled, CpuBusyThreshold = config.CpuBusyThreshold, CpuBusySeconds = config.CpuBusySeconds,
            CpuIdleThreshold = config.CpuIdleThreshold, CpuIdleSeconds = config.CpuIdleSeconds,
            CooldownSeconds = config.CooldownSeconds, CandidateCpuFloorPercent = config.CandidateCpuFloorPercent,
            MaxAdjustPerRound = config.MaxAdjustPerRound,
        } : null,
        HeldChangeCount = _changeLog.Records.Count,
        UnreconciledCount = _changeLog.Records.Count,
        LastAction = Volatile.Read(ref _lastAction),
        LastError = Volatile.Read(ref _lastError),
        UpdatedUtc = DateTimeOffset.UtcNow,
    };

    private static string ReadOwnPriority()
    {
        try { using var process = Process.GetCurrentProcess(); return process.PriorityClass.ToString(); }
        catch { return "未知"; }
    }

    private void SetAction(string text) => Volatile.Write(ref _lastAction, text);
    private void SetError(string text) { Volatile.Write(ref _lastError, text); Log.Error("进程管理：" + text); }

    // ---------------- 枚举与详情 ----------------

    /// <summary>分页枚举进程。只读：读取失败只计数跳过，不产生任何修改记录。</summary>
    public ProcmanListResult List(ProcmanListArgs args)
    {
        if (args.Scope is not ("all" or "general" or "delta") || args.SortOrder is not ("name" or "memory"))
            throw new ArgumentException("未知的进程范围或排序方式。");
        var offset = Math.Max(0, args.Offset);
        var limit = Math.Clamp(args.Limit <= 0 ? 200 : args.Limit, 1, 500);
        var filter = args.NameFilter?.Trim();

        var entries = new List<ProcmanProcessEntry>();
        ProcmanCpuSample cpuSample;
        lock (_gate) cpuSample = SampleForTests?.Invoke() ?? _sampler.Sample();
        var skipped = 0;
        var total = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                ProcmanProcessIdentity? identity;
                double? workingSet = null;
                try
                {
                    identity = ProcmanIdentity.TryIdentify(process.Id);
                    if (identity is null) { skipped++; continue; }
                    // CPU 占用走系统快照成本高；首版只给工作集，CPU 列由前端轮询 detail 或后续采样补充。
                    try { workingSet = process.WorkingSet64 / (1024.0 * 1024.0); } catch { }
                }
                catch { skipped++; continue; }

                var isDelta = Path.GetFileNameWithoutExtension(identity.ExecutablePath).Equals(DeltaForceLocator.GameProcessName, StringComparison.OrdinalIgnoreCase);
                if ((args.Scope == "delta" && !isDelta) || (args.Scope == "general" && isDelta)) continue;
                if (filter is { Length: > 0 } &&
                    !Path.GetFileNameWithoutExtension(identity.ExecutablePath).Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                total++;
                entries.Add(new ProcmanProcessEntry
                {
                    Pid = identity.Pid,
                    StartTimeUtcTicks = identity.StartTimeUtcTicks,
                    Name = Path.GetFileNameWithoutExtension(identity.ExecutablePath),
                    ExecutablePath = identity.ExecutablePath,
                    WorkingSetMb = workingSet,
                    CpuPercent = cpuSample.Processes.Where(p => p.Identity == identity).Select(p => (double?)p.Cpu).FirstOrDefault(),
                    OwnedPropertyCount = _changeLog.ForProcess(identity.Pid, identity.StartTimeUtcTicks, identity.ExecutablePath).Count,
                });
            }
        }

        var ordered = args.SortOrder == "name" ? entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Pid)
            : entries.OrderByDescending(e => e.WorkingSetMb ?? 0).ThenBy(e => e.Pid);
        var page = ordered.Skip(offset).Take(limit).ToList();
        return new ProcmanListResult
        {
            Processes = page,
            TotalCount = total,
            SkippedInaccessible = skipped,
            Note = skipped > 0 ? $"{skipped} 个进程信息不可读已跳过。CPU 占用列请在详情中查看。" : "",
        };
    }

    /// <summary>进程详情：身份复核 + 每项能力的支持状态/实际值/控制方。</summary>
    public ProcmanDetailResult Detail(ProcmanDetailArgs args)
    {
        var identity = args.Identity;
        var current = ProcmanIdentity.TryIdentify(identity.Pid);
        if (current is null)
            return new ProcmanDetailResult { IdentityOk = false, IdentityError = "进程已退出或无法读取（可能 PID 已复用）。" };
        if (identity.StartTimeUtcTicks != 0 && current.StartTimeUtcTicks != identity.StartTimeUtcTicks)
            return new ProcmanDetailResult { IdentityOk = false, IdentityError = "启动时间不符：PID 已被新进程复用。" };
        if (identity.ExecutablePath.Length > 0 &&
            !string.Equals(current.ExecutablePath, identity.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            return new ProcmanDetailResult { IdentityOk = false, IdentityError = "映像路径不符：进程身份已变化。" };

        var start = current.StartTimeUtcTicks;
        var path = current.ExecutablePath;
        var protectedProcess = ProcmanIdentity.IsProtectedPath(path);

        var capabilities = new List<ProcmanCapabilityInfo>
        {
            Describe(CapabilityPriority, protectedProcess, () => ProcmanCapabilities.ReadPriority(current.Pid)),
            Describe(CapabilityAffinity, protectedProcess, () => ProcmanCapabilities.ReadAffinity(current.Pid)),
            Describe(CapabilityCpuSet, protectedProcess, () => ProcmanCapabilities.ReadCpuSet(current.Pid)),
            // 内存优先级（P3 Native 兼容层）：P0 阶段标记不支持，不做未验证的修改入口。
            Describe(CapabilityMemoryPriority, protectedProcess, () => ProcmanCapabilities.ReadMemoryPriority(current.Pid)),
            Describe(CapabilityGpuPriority, protectedProcess, () => ProcmanCapabilities.ReadGpuPriority(current.Pid)),
            Describe(CapabilityIoPriority, protectedProcess, () => ProcmanCapabilities.ReadIoPriority(current.Pid)),
            new() { Capability = CapabilityGpuPreference, Support = "supported", CurrentValue = "", Owner = ProcmanControlCoordinator.OwnerSystem, Error = "" },
            Describe(CapabilityEcoQos, protectedProcess, () => ProcmanCapabilities.ReadEcoQos(current.Pid)),
            Describe(CapabilityPriorityBoost, protectedProcess, () => ProcmanCapabilities.ReadPriorityBoost(current.Pid)),
        };

        var gpuIndex = capabilities.FindIndex(c => c.Capability == CapabilityGpuPreference);
        if (gpuIndex >= 0)
        {
            var read = ProcmanCapabilities.ReadGpuPreference(path);
            capabilities[gpuIndex] = read.Ok
                ? new ProcmanCapabilityInfo { Capability = CapabilityGpuPreference, Support = "supported", CurrentValue = read.Value ?? "", Owner = ProcmanControlCoordinator.OwnerSystem, Error = "" }
                : new ProcmanCapabilityInfo { Capability = CapabilityGpuPreference, Support = "readOnly", CurrentValue = "", Owner = ProcmanControlCoordinator.OwnerSystem, Error = read.Error };
        }

        var held = _changeLog.ForProcess(current.Pid, start, path)
            .Select(r => new ProcmanHeldChange
            {
                Capability = r.Capability,
                OriginalValue = r.OriginalValue,
                AppliedValue = r.AppliedValue,
                Owner = r.Owner,
                AppliedUtc = r.AppliedUtc,
            }).ToList();
        for (int i = 0; i < capabilities.Count; i++)
        {
            var capability = capabilities[i];
            using var property = _properties.Enter(current, capability.Capability);
            capabilities[i] = new() { Capability = capability.Capability, Support = capability.Support == "supported" && !ProcmanCapabilities.CanOpenWrite(current.Pid) ? "readOnly" : capability.Support,
                CurrentValue = capability.CurrentValue, Owner = property.Owner, Error = capability.Error };
        }

        try
        {
            using var process = Process.GetProcessById(current.Pid);
            return new ProcmanDetailResult
            {
                IdentityOk = true,
                Process = new ProcmanProcessEntry
                {
                    Pid = current.Pid,
                    StartTimeUtcTicks = start,
                    Name = Path.GetFileNameWithoutExtension(path),
                    ExecutablePath = path,
                    CpuPercent = null,
                    WorkingSetMb = process.WorkingSet64 / (1024.0 * 1024.0),
                    OwnedPropertyCount = held.Count,
                },
                Capabilities = capabilities,
                HeldChanges = held,
            };
        }
        catch (Exception ex)
        {
            return new ProcmanDetailResult { IdentityOk = false, IdentityError = "读取进程详情失败：" + ex.Message };
        }
    }

    private ProcmanCapabilityInfo Describe(string capability, bool protectedProcess, Func<ProcmanCapabilityResult> read)
    {
        if (protectedProcess)
            return new() { Capability = capability, Support = "readOnly", CurrentValue = "", Owner = ProcmanControlCoordinator.OwnerSystem, Error = "受保护/关键系统进程：只读，不提供修改。" };
        ProcmanCapabilityResult result;
        try { result = read(); }
        catch (Exception ex) { result = ProcmanCapabilityResult.Failure("读取异常：" + ex.Message); }
        return new()
        {
            Capability = capability,
            Support = result.Ok ? "supported" : "readOnly",
            CurrentValue = result.Value ?? "",
            Owner = ProcmanControlCoordinator.OwnerSystem,
            Error = result.Error,
        };
    }

    // ---------------- 修改 / 还原 / 释放 ----------------

    /// <summary>
    /// 手动修改进程属性。逐属性独立处理（部分成功合法）；身份复核失败拒绝全部写入。
    /// 目标属性被游戏模式等外部控制方持有时，必须 ConfirmedTakeover=true 才接管。
    /// </summary>
    public ProcmanSetResult Set(ProcmanSetArgs args)
    {
        var identity = args.Identity;
        // ① 身份复核（严格：前端必须提供完整身份三元组）
        if (!ProcmanIdentity.Verify(identity.Pid, identity.StartTimeUtcTicks, identity.ExecutablePath))
            return new ProcmanSetResult { IdentityOk = false, IdentityError = "进程身份复核失败（已退出/PID 复用/路径不符），已拒绝全部修改。" };
        var current = new ProcmanProcessIdentity(identity.Pid, new DateTime(identity.StartTimeUtcTicks, DateTimeKind.Utc), identity.ExecutablePath);
        var start = current.StartTimeUtcTicks;
        var path = current.ExecutablePath;
        if (ProcmanIdentity.IsProtectedPath(path))
            return new ProcmanSetResult { IdentityOk = true, IdentityError = "受保护/关键系统进程，禁止修改。", Outcomes = [] };

        var outcomes = new List<ProcmanCapabilityOutcome>();
        lock (_gate)
        {
            void Apply(string capability, Func<ProcmanCapabilityOutcome> write)
            {
                try
                {
                    using var pin = ProcmanCapabilities.Pin(current);
                    using var property = _properties.Enter(current, capability);
                    outcomes.Add(write());
                }
                catch (Exception ex) { outcomes.Add(Outcome(capability, "failed", "未确认完成，证据保留：" + ex.Message)); }
            }
            if (args.CpuPriority is { } priority) Apply(CapabilityPriority, () => SetPriorityCore(current, priority, args.ConfirmedTakeover == true));
            if (args.AffinityMasks is { } affinity) Apply(CapabilityAffinity, () => SetAffinityCore(current, affinity, args.ConfirmedTakeover == true));
            if (args.CpuSetMasks is { } cpuSet) Apply(CapabilityCpuSet, () => SetCpuSetCore(current, cpuSet, args.ConfirmedTakeover == true));
            if (args.EcoQosEnabled is { } eco) Apply(CapabilityEcoQos, () => SetEcoQosCore(current, eco, args.ConfirmedTakeover == true));
            if (args.PriorityBoost is { } boost) Apply(CapabilityPriorityBoost, () => SetPriorityBoostCore(current, boost, args.ConfirmedTakeover == true));
            if (args.IoPriority is { } io) Apply(CapabilityIoPriority, () => SetIoPriorityCore(current, io, args.ConfirmedTakeover == true));
            if (args.GpuPreference is { } gpu) Apply(CapabilityGpuPreference, () => SetGpuPreferenceCore(current, gpu));
            if (args.MemoryPriority is { } memory) Apply(CapabilityMemoryPriority, () => SetNumericCore(current, CapabilityMemoryPriority, memory, args.ConfirmedTakeover == true));
            if (args.GpuPriority is { } gpuPriority) Apply(CapabilityGpuPriority, () => SetNumericCore(current, CapabilityGpuPriority, gpuPriority, args.ConfirmedTakeover == true));
        }
        SetAction($"已处理 PID {current.Pid} 的 {outcomes.Count} 项属性修改请求。");
        return new ProcmanSetResult { IdentityOk = true, Outcomes = outcomes };
    }

    private ProcmanCapabilityOutcome Outcome(string capability, string status, string message, string actual = "")
        => new() { Capability = capability, Status = status, Message = message, ActualValue = actual };

    /// <summary>通用写入骨架：控制权 → 写前记录 → 执行+回读 → 成功后更新记录 / 失败保留或回滚。</summary>
    private ProcmanCapabilityOutcome WriteCore(
        ProcmanProcessIdentity identity, string capability, string desiredValue, string originalValue,
        bool confirmedTakeover, Func<ProcmanCapabilityResult> apply, Func<ProcmanCapabilityResult>? restore)
    {
        var pid = identity.Pid; var start = identity.StartTimeUtcTicks; var path = identity.ExecutablePath;

        using var pin = ProcmanCapabilities.Pin(identity);
        using var property = _properties.Enter(identity, capability);
        var existingRecord = _changeLog.Find(pid, start, path, capability);
        var owner = property.Owner;
        if (owner != "system" && owner != "manual" && owner != "responsiveness" && !confirmedTakeover)
            return Outcome(capability, "rejected", $"该属性由 {owner} 控制，需要明确确认接管。");
        // Cross-process ownership is authoritative; the local coordinator remains a unit-tested utility.
        if (confirmedTakeover || owner is "system" or "manual") _coordinator.Takeover(pid, start, path, capability, "manual");

        // ② 写前记录（接管时保留原始值的最早版本）
        var record = new ProcmanChangeLog.ChangeRecord(pid, start, path, capability, originalValue, desiredValue,
            ProcmanControlCoordinator.OwnerManual, DateTimeOffset.UtcNow);
        if (existingRecord is not null && originalValue != existingRecord.AppliedValue && originalValue != existingRecord.OriginalValue)
            return Outcome(capability, "conflict", "当前值已被外部修改，未覆盖旧恢复记录，请先核对。");
        if (existingRecord is not null)
            record = existingRecord.Owner == "manual"
                ? record with { OriginalValue = existingRecord.OriginalValue, Parent = existingRecord.Parent }
                : record with { Parent = existingRecord };
        try { _changeLog.Replace(record); property.SetOwner("manual"); }
        catch (Exception ex) { return Outcome(capability, "failed", "修改记录落盘失败，已放弃写入：" + ex.Message); }

        // ③ 执行 + 回读
        var result = apply();
        if (result.Ok)
        {
            var confirmed = record with { AppliedValue = result.Value ?? desiredValue };
            _changeLog.Replace(confirmed);
            return Outcome(capability, "applied", "", result.Value ?? desiredValue);
        }

        // ④ 失败：尝试回滚到原值；无论回滚成败都保留记录供核对。
        if (restore is not null)
        {
            try
            {
                var rolledBack = restore();
                if (rolledBack.Ok)
                {
                    if (existingRecord is null) { _changeLog.Remove(record); property.SetOwner(null); }
                    else { _changeLog.Replace(existingRecord); property.SetOwner(existingRecord.Owner); }
                    return Outcome(capability, "failed", result.Error + " 原值已恢复。");
                }
            }
            catch { }
        }
        SetError($"PID {pid} {capability} 修改失败：{result.Error}");
        return Outcome(capability, "failed", result.Error + " 已保留修改记录供核对（如需还原请用「还原」）。");
    }

    private ProcmanCapabilityOutcome SetPriorityCore(ProcmanProcessIdentity identity, string priorityText, bool takeover = false)
    {
        if (!Enum.TryParse<ProcessPriorityClass>(priorityText, true, out var priority) || !Enum.IsDefined(priority))
            return Outcome(CapabilityPriority, "rejected", "未知优先级：" + priorityText);
        if (priority is ProcessPriorityClass.RealTime)
            return Outcome(CapabilityPriority, "rejected", "实时优先级只供明确的手动操作路径，且不开放此入口（自动策略与常规入口均禁用）。");
        var original = ProcmanCapabilities.ReadPriority(identity.Pid);
        if (!original.Ok) return Outcome(CapabilityPriority, "failed", "读取原优先级失败：" + original.Error);
        return WriteCore(identity, CapabilityPriority, priority.ToString(), original.Value!, takeover,
            () => ProcmanCapabilities.SetPriority(identity.Pid, priority),
            () => ProcmanCapabilities.SetPriority(identity.Pid, ParsePriority(original.Value!)));
    }

    private ProcmanCapabilityOutcome SetIoPriorityCore(ProcmanProcessIdentity identity, string name, bool takeover = false)
    {
        var value = ProcmanCapabilities.IoPriorityFromName(name);
        if (value < 0) return Outcome(CapabilityIoPriority, "rejected", "I/O 优先级档位不合法（支持 VeryLow/Low/Normal/High）：" + name);
        var original = ProcmanCapabilities.ReadIoPriority(identity.Pid);
        if (!original.Ok) return Outcome(CapabilityIoPriority, "failed", "读取原 I/O 优先级失败：" + original.Error);
        return WriteCore(identity, CapabilityIoPriority, name, original.Value!, takeover,
            () => ProcmanCapabilities.SetIoPriority(identity.Pid, value),
            () => ProcmanCapabilities.SetIoPriority(identity.Pid, ProcmanCapabilities.IoPriorityFromName(original.Value!)));
    }

    private ProcmanCapabilityOutcome SetGpuPreferenceCore(ProcmanProcessIdentity identity, string name)
    {
        var value = ProcmanCapabilities.GpuPreferenceFromName(name);
        if (value is < 0 or > 2) return Outcome(CapabilityGpuPreference, "rejected", "GPU 首选项不合法。");
        using var pin = ProcmanCapabilities.Pin(identity);
        using var property = _properties.Enter(identity, CapabilityGpuPreference);
        var path = identity.ExecutablePath;
        var existing = _changeLog.Records.FirstOrDefault(r => r.Capability == CapabilityGpuPreference && r.ExecutablePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        var original = ProcmanGpuPreference.Read(path);
        if (existing is not null && original != ProcmanGpuPreference.Decode(existing.AppliedValue))
            return Outcome(CapabilityGpuPreference, "conflict", "GPU 首选项已被外部修改，请先核对原记录。");
        var updated = new ProcmanGpuPreference(true, "String", ProcmanGpuPreference.Replace(original.Raw, value));
        var record = existing is null ? new ProcmanChangeLog.ChangeRecord(identity.Pid, identity.StartTimeUtcTicks, path,
            CapabilityGpuPreference, original.Encode(), updated.Encode(), "manual", DateTimeOffset.UtcNow) : existing with { AppliedValue = updated.Encode() };
        try
        {
            _changeLog.Replace(record);
            updated.Write(path);
            return Outcome(CapabilityGpuPreference, "applied", "按应用保存，重启目标程序生效。", name);
        }
        catch (Exception ex) { return Outcome(CapabilityGpuPreference, "failed", "GPU 首选项未完成，证据保留：" + ex.Message); }
    }

    private ProcmanCapabilityOutcome RestorePersistentGpu(ProcmanChangeLog.ChangeRecord record)
    {
        try
        {
            var identity = new ProcmanProcessIdentity(record.Pid, new DateTime(record.StartTimeUtcTicks, DateTimeKind.Utc), record.ExecutablePath);
            using var property = _properties.Enter(identity, CapabilityGpuPreference);
            var original = ProcmanGpuPreference.Decode(record.OriginalValue);
            var applied = ProcmanGpuPreference.Decode(record.AppliedValue);
            var current = ProcmanGpuPreference.Read(record.ExecutablePath);
            if (current != original && current != applied) return Outcome(CapabilityGpuPreference, "conflict", "图形设置已被外部修改，未覆盖，恢复记录保留。");
            if (current != original) original.Write(record.ExecutablePath);
            _changeLog.Remove(record); property.SetOwner(null);
            return Outcome(CapabilityGpuPreference, "restored", "应用 GPU 首选项已恢复（不依赖原进程存活）。");
        }
        catch (Exception ex) { return Outcome(CapabilityGpuPreference, "failed", ex.Message); }
    }

    private static ProcessPriorityClass ParsePriority(string text) =>
        Enum.TryParse<ProcessPriorityClass>(text, true, out var value) && Enum.IsDefined(value) ? value
            : throw new InvalidDataException("原优先级无法确认，未猜默认值。");

    private ProcmanCapabilityOutcome SetNumericCore(ProcmanProcessIdentity identity, string capability, int value, bool takeover)
    {
        if (capability == CapabilityMemoryPriority ? value is < 1 or > 5 : value is < 0 or > 4)
            return Outcome(capability, "rejected", "档位超出允许范围。");
        var original = capability == CapabilityMemoryPriority ? ProcmanCapabilities.ReadMemoryPriority(identity.Pid) : ProcmanCapabilities.ReadGpuPriority(identity.Pid);
        if (!original.Ok || !int.TryParse(original.Value, out var old)) return Outcome(capability, "failed", original.Error);
        if (capability == CapabilityGpuPriority && old is < 0 or > 4)
            return Outcome(capability, "rejected", "原 GPU 档位不在可可靠恢复范围，未修改。");
        ProcmanCapabilityResult Apply(int v) => capability == CapabilityMemoryPriority ? ProcmanCapabilities.SetMemoryPriority(identity.Pid, v) : ProcmanCapabilities.SetGpuPriority(identity.Pid, v);
        return WriteCore(identity, capability, value.ToString(), original.Value!, takeover, () => Apply(value), () => Apply(old));
    }

    private ProcmanCapabilityOutcome SetAffinityCore(ProcmanProcessIdentity identity, List<ProcmanGroupMask> masks, bool confirmedTakeover)
    {
        var original = ProcmanCapabilities.ReadAffinity(identity.Pid);
        if (!original.Ok) return Outcome(CapabilityAffinity, "failed", "读取原亲和性失败：" + original.Error);
        var values = masks.Select(m => new ProcmanGroupMaskValue(m.Group, m.Mask)).ToList();
        return WriteCore(identity, CapabilityAffinity, ProcmanTopology.Describe(values), original.Value!, confirmedTakeover,
            () => values.Count == 0 ? ProcmanCapabilities.ClearAffinity(identity.Pid) : ProcmanCapabilities.SetAffinity(identity.Pid, values),
            () => RestoreByOriginalAffinity(identity.Pid, original.Value!));
    }

    private ProcmanCapabilityOutcome SetCpuSetCore(ProcmanProcessIdentity identity, List<ProcmanGroupMask> masks, bool confirmedTakeover)
    {
        var original = ProcmanCapabilities.ReadCpuSet(identity.Pid);
        if (!original.Ok) return Outcome(CapabilityCpuSet, "failed", "读取原 CPU 集合失败：" + original.Error);
        var values = masks.Select(m => new ProcmanGroupMaskValue(m.Group, m.Mask)).ToList();
        return WriteCore(identity, CapabilityCpuSet, masks.Count == 0 ? "(系统默认)" : ProcmanTopology.Describe(values), original.Value!, confirmedTakeover,
            () => ProcmanCapabilities.SetCpuSet(identity.Pid, values),
            () => RestoreByOriginalCpuSet(identity.Pid, original.Value!));
    }

    private ProcmanCapabilityOutcome SetEcoQosCore(ProcmanProcessIdentity identity, bool enabled, bool takeover = false)
    {
        var read = ProcmanCapabilities.ReadEcoQos(identity.Pid);
        if (!read.Ok) return Outcome(CapabilityEcoQos, "failed", read.Error);
        var original = read.Value!;
        // EcoQoS 无法公开回读：结果按「已请求待核对」呈现（WriteCore 的 failed 分支保留记录）。
        return WriteCore(identity, CapabilityEcoQos, enabled ? "EcoQoS 启用" : "EcoQoS 高性能", original, takeover,
            () => ProcmanCapabilities.SetEcoQos(identity.Pid, enabled),
            () => ProcmanCapabilities.RestoreEcoQos(identity.Pid, original));
    }

    private ProcmanCapabilityOutcome SetPriorityBoostCore(ProcmanProcessIdentity identity, bool enableBoost, bool takeover = false)
    {
        var original = ProcmanCapabilities.ReadPriorityBoost(identity.Pid);
        if (!original.Ok) return Outcome(CapabilityPriorityBoost, "failed", "读取原动态提升状态失败：" + original.Error);
        return WriteCore(identity, CapabilityPriorityBoost, enableBoost ? "True" : "False", original.Value!, takeover,
            () => ProcmanCapabilities.SetPriorityBoost(identity.Pid, enableBoost),
            () => ProcmanCapabilities.SetPriorityBoost(identity.Pid, original.Value == "True"));
    }

    private static ProcmanCapabilityResult RestoreByOriginalAffinity(int pid, string originalValue)
    {
        var values = ParseGroupMasks(originalValue);
        return values.Count == 0 ? ProcmanCapabilityResult.Failure("原亲和性掩码无法确认，未猜默认值。") : ProcmanCapabilities.SetAffinity(pid, values);
    }

    private static ProcmanCapabilityResult RestoreByOriginalCpuSet(int pid, string originalValue)
    {
        if (originalValue == "(系统默认)") return ProcmanCapabilities.SetCpuSet(pid, []);
        var values = ParseGroupMasks(originalValue);
        return values.Count == 0 ? ProcmanCapabilityResult.Failure("原 CPU 集合无法确认，记录保留。") : ProcmanCapabilities.SetCpuSet(pid, values);
    }

    private static List<ProcmanGroupMaskValue> ParseGroupMasks(string text)
    {
        var result = new List<ProcmanGroupMaskValue>();
        foreach (var part in text.Split('（')[0].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = part.Trim();
            if (segment.StartsWith("group", StringComparison.OrdinalIgnoreCase)) segment = "g" + segment[5..];
            if (!segment.StartsWith("g", StringComparison.OrdinalIgnoreCase) || !segment.Contains(':')) return [];
            var split = segment.Split(':', 2);
            if (!ushort.TryParse(split[0][1..], out var group) || !ulong.TryParse(split[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out var mask)
                || mask == 0 || result.Any(r => r.Group == group)) return [];
            result.Add(new(group, mask));
        }
        return result;
    }

    /// <summary>还原：释放本工具在该进程上的修改（按记录恢复原值）。外部已修改 → 冲突记录；进程退出 → 记录清除。</summary>
    public ProcmanSetResult Reset(ProcmanResetArgs args)
    {
        var identity = args.Identity;
        if (!ProcmanIdentity.Verify(identity.Pid, identity.StartTimeUtcTicks, identity.ExecutablePath))
            return new ProcmanSetResult { IdentityOk = false, IdentityError = "进程身份复核失败（已退出/PID 复用），不修改新进程。" };

        var current = new ProcmanProcessIdentity(identity.Pid, new DateTime(identity.StartTimeUtcTicks, DateTimeKind.Utc), identity.ExecutablePath);
        var records = _changeLog.ForProcess(current.Pid, current.StartTimeUtcTicks, current.ExecutablePath);
        if (args.Capabilities is { Count: > 0 })
            records = records.Where(r => args.Capabilities.Contains(r.Capability, StringComparer.Ordinal)).ToList();

        var outcomes = new List<ProcmanCapabilityOutcome>();
        lock (_gate)
            foreach (var record in records) outcomes.Add(RestoreRecord(record, current));
        return new ProcmanSetResult { IdentityOk = true, Outcomes = outcomes };
    }

    /// <summary>GPU 首选项还原：原值“(未设置)”= 删除该 EXE 的设置行；否则整行写回（保留其他图形选项）。</summary>
    private static ProcmanCapabilityResult RestoreGpuPreference(ProcmanChangeLog.ChangeRecord record)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\DirectX\UserGpuPreferences", writable: true);
            if (key is null) return ProcmanCapabilityResult.Failure("打开注册表项失败。");
            if (record.OriginalValue == "(未设置)") key.DeleteValue(record.ExecutablePath, throwOnMissingValue: false);
            else key.SetValue(record.ExecutablePath, record.OriginalValue, Microsoft.Win32.RegistryValueKind.String);
            var verify = key.GetValue(record.ExecutablePath, "") as string ?? "";
            var expected = record.OriginalValue == "(未设置)" ? "" : record.OriginalValue;
            return string.Equals(verify, expected, StringComparison.Ordinal)
                ? ProcmanCapabilityResult.Success("已恢复：" + (expected.Length == 0 ? "(未设置)" : expected))
                : ProcmanCapabilityResult.Failure("回读不一致（期望 " + expected + "，实际 " + verify + "）。");
        }
        catch (Exception ex) { return ProcmanCapabilityResult.Failure("恢复 GPU 首选项失败：" + ex.Message); }
    }

    private ProcmanCapabilityOutcome RestoreRecord(ProcmanChangeLog.ChangeRecord record, ProcmanProcessIdentity identity)
    {
        if (record.Capability == CapabilityGpuPreference) return RestorePersistentGpu(record);
        using var pin = ProcmanCapabilities.Pin(identity);
        using var property = _properties.Enter(identity, record.Capability);
        // 进程身份在 Reset 入口已复核；还原前再核对当前值是否仍是我们设置的值（外部已改 → 冲突，不覆盖）。
        ProcmanCapabilityResult? current = record.Capability switch
        {
            CapabilityPriority => ProcmanCapabilities.ReadPriority(identity.Pid),
            CapabilityAffinity => ProcmanCapabilities.ReadAffinity(identity.Pid),
            CapabilityCpuSet => ProcmanCapabilities.ReadCpuSet(identity.Pid),
            CapabilityPriorityBoost => ProcmanCapabilities.ReadPriorityBoost(identity.Pid),
            CapabilityIoPriority => ProcmanCapabilities.ReadIoPriority(identity.Pid),
            CapabilityMemoryPriority => ProcmanCapabilities.ReadMemoryPriority(identity.Pid),
            CapabilityGpuPriority => ProcmanCapabilities.ReadGpuPriority(identity.Pid),
            CapabilityGpuPreference => ProcmanCapabilities.ReadGpuPreference(identity.ExecutablePath),
            CapabilityEcoQos => ProcmanCapabilities.ReadEcoQos(identity.Pid),
            _ => null,
        };

        // 当前值与记录比较：GPU 首选项的记录是注册表段（GpuPreference=N;），读取是显示名——按显示名换算比较。
        var appliedDisplay = record.Capability == CapabilityGpuPreference
            ? ProcmanCapabilities.GpuPreferenceName(ProcmanCapabilities.GpuPreferenceFromName(record.AppliedValue.Contains("=2") ? "高性能" : record.AppliedValue.Contains("=1") ? "省电" : "默认"))
            : record.AppliedValue;
        if (current is null || !current.Ok) return Outcome(record.Capability, "failed", "无法读取当前值，未恢复且记录保留。");
        if (current.Value != record.OriginalValue && current.Value != appliedDisplay)
        {
            SetError($"PID {record.Pid} {record.Capability} 当前值（{current.Value}）与记录（{appliedDisplay}）不一致，疑似外部修改，保留冲突记录。");
            return Outcome(record.Capability, "conflict", $"当前值已被外部修改为 {current.Value}（记录为 {appliedDisplay}），未覆盖，请人工核对。");
        }

        var restore = current.Value == record.OriginalValue ? current : record.Capability switch
        {
            CapabilityPriority => ProcmanCapabilities.SetPriority(identity.Pid, ParsePriority(record.OriginalValue)),
            CapabilityAffinity => RestoreByOriginalAffinity(identity.Pid, record.OriginalValue),
            CapabilityCpuSet => RestoreByOriginalCpuSet(identity.Pid, record.OriginalValue),
            CapabilityPriorityBoost => ProcmanCapabilities.SetPriorityBoost(identity.Pid, record.OriginalValue == "True"),
            CapabilityEcoQos => ProcmanCapabilities.RestoreEcoQos(identity.Pid, record.OriginalValue),
            CapabilityIoPriority => ProcmanCapabilities.SetIoPriority(identity.Pid, ProcmanCapabilities.IoPriorityFromName(record.OriginalValue)),
            CapabilityMemoryPriority => ProcmanCapabilities.SetMemoryPriority(identity.Pid, int.Parse(record.OriginalValue)),
            CapabilityGpuPriority => ProcmanCapabilities.SetGpuPriority(identity.Pid, int.Parse(record.OriginalValue)),
            CapabilityGpuPreference => RestoreGpuPreference(record),
            _ => ProcmanCapabilityResult.Failure("未知能力：" + record.Capability),
        };

        if (!restore.Ok)
        {
            SetError($"PID {record.Pid} {record.Capability} 还原失败：{restore.Error}");
            return Outcome(record.Capability, "failed", "还原失败：" + restore.Error + "，记录已保留。");
        }

        if (record.Parent is not null) _changeLog.Replace(record.Parent);
        else _changeLog.Remove(record);
        property.SetOwner(record.Parent?.Owner);
        _coordinator.Release(record.Pid, record.StartTimeUtcTicks, record.ExecutablePath, record.Capability);
        return Outcome(record.Capability, "restored", "已还原为 " + record.OriginalValue);
    }

    /// <summary>释放接管：同 Reset（恢复原值 + 移除记录 + 释放租约）；P2 在此之上交还外部控制方。</summary>
    public ProcmanSetResult Release(ProcmanReleaseArgs args) =>
        Reset(new ProcmanResetArgs { Identity = args.Identity, Capabilities = args.Capabilities });

    /// <summary>还原全部持有记录（退出流程）。</summary>
    public async Task<OperationResult> RestoreAllAsync()
    {
        await StopRulesEngineAsync();
        _automaticMemoryActive = false;
        if (_responsiveness is not null) _responsiveness.Enabled = false;
        var records = _changeLog.Records.ToArray();
        var restored = 0; var missing = 0; var failed = 0; var conflict = 0;
        foreach (var record in records)
        {
            var identity = new ProcmanProcessIdentity(record.Pid, new DateTime(record.StartTimeUtcTicks, DateTimeKind.Utc), record.ExecutablePath);
            var state = record.Capability == CapabilityGpuPreference ? "same" : ProcmanIdentity.State(identity);
            if (state == "unknown") { failed++; continue; }
            if (state == "missing")
            {
                // 进程已退出/PID 复用：不修改新进程；记录核对后清除（原进程消失，无恢复义务）。
                _changeLog.Remove(record);
                using var property = _properties.Enter(identity, record.Capability); property.SetOwner(null);
                missing++;
                continue;
            }
            var outcome = RestoreRecord(record, identity);
            while (outcome.Status == "restored" && _changeLog.Find(record.Pid, record.StartTimeUtcTicks, record.ExecutablePath, record.Capability) is { } parent)
                outcome = RestoreRecord(parent, identity);
            if (outcome.Status == "restored") restored++;
            else if (outcome.Status == "conflict") conflict++;
            else failed++;
        }
        await Task.CompletedTask;
        var message = $"通用进程控制还原完成：成功 {restored}，进程已退出 {missing}，冲突 {conflict}，失败 {failed}。";
        if (failed > 0 || conflict > 0) { SetError(message); return OperationResult.Fail(message); }
        SetAction(message);
        return OperationResult.Ok(message);
    }

    /// <summary>只读核对：逐条记录对当前值（不写任何东西）。</summary>
    public ProcmanReconcileResult Reconcile()
    {
        var entries = new List<ProcmanReconcileEntry>();
        foreach (var record in _changeLog.Records)
        {
            if (record.Capability == CapabilityGpuPreference)
            {
                try
                {
                    var raw = ProcmanGpuPreference.Read(record.ExecutablePath);
                    var status = raw == ProcmanGpuPreference.Decode(record.AppliedValue) ? "applied"
                        : raw == ProcmanGpuPreference.Decode(record.OriginalValue) ? "restored" : "conflict";
                    entries.Add(new() { Pid = record.Pid, ExecutablePath = record.ExecutablePath, Capability = record.Capability, Status = status });
                }
                catch (Exception ex) { entries.Add(new() { Pid = record.Pid, ExecutablePath = record.ExecutablePath, Capability = record.Capability, Status = "conflict", Message = ex.Message }); }
                continue;
            }
            var identity = new ProcmanProcessIdentity(record.Pid, new DateTime(record.StartTimeUtcTicks, DateTimeKind.Utc), record.ExecutablePath);
            var identityState = ProcmanIdentity.State(identity);
            if (identityState != "same")
            {
                entries.Add(new() { Pid = record.Pid, ExecutablePath = record.ExecutablePath, Capability = record.Capability,
                    Status = identityState == "missing" ? "missing" : "conflict", Message = identityState == "missing" ? "进程已退出或 PID 已复用。" : "身份未知，无法读取，恢复记录保留。" });
                continue;
            }
            var current = record.Capability switch
            {
                CapabilityPriority => ProcmanCapabilities.ReadPriority(record.Pid),
                CapabilityAffinity => ProcmanCapabilities.ReadAffinity(record.Pid),
                CapabilityCpuSet => ProcmanCapabilities.ReadCpuSet(record.Pid),
                CapabilityPriorityBoost => ProcmanCapabilities.ReadPriorityBoost(record.Pid),
                CapabilityEcoQos => ProcmanCapabilities.ReadEcoQos(record.Pid),
                CapabilityIoPriority => ProcmanCapabilities.ReadIoPriority(record.Pid),
                CapabilityMemoryPriority => ProcmanCapabilities.ReadMemoryPriority(record.Pid),
                CapabilityGpuPriority => ProcmanCapabilities.ReadGpuPriority(record.Pid),
                CapabilityGpuPreference => ProcmanCapabilities.ReadGpuPreference(record.ExecutablePath),
                _ => null,
            };
            if (current is null || !current.Ok)
                entries.Add(new() { Pid = record.Pid, ExecutablePath = record.ExecutablePath, Capability = record.Capability, Status = "conflict", Message = "当前值无法读取：" + (current?.Error ?? "该能力无公开回读接口，需人工核对。") });
            else if (current.Value == record.AppliedValue)
                entries.Add(new() { Pid = record.Pid, ExecutablePath = record.ExecutablePath, Capability = record.Capability, Status = "applied", Message = "当前值与记录一致。" });
            else
                entries.Add(new() { Pid = record.Pid, ExecutablePath = record.ExecutablePath, Capability = record.Capability, Status = "conflict", Message = $"当前值 {current.Value} 与记录 {record.AppliedValue} 不一致。" });
        }
        return new ProcmanReconcileResult
        {
            Entries = entries,
            Summary = $"核对 {entries.Count} 条记录：一致 {entries.Count(e => e.Status == "applied")}，" +
                      $"冲突 {entries.Count(e => e.Status == "conflict")}，进程消失 {entries.Count(e => e.Status == "missing")}。",
        };
    }

    /// <summary>启动时按遗留日志恢复（应用崩溃后重启的核对入口）。</summary>
    public async Task<OperationResult> RecoverAsync(string scope)
    {
        if (scope is not ("" or "all")) return OperationResult.Fail("仅允许按完整日志恢复 all，不接收未知恢复范围。");
        return await RestoreAllAsync();
    }

    // ---------------- P2：持久规则引擎 ----------------

    /// <summary>启动规则引擎：按 tick 扫描启用规则的目标进程并应用（每 tick 一个快照，不逐规则重复轮询）。</summary>
    public OperationResult StartRulesEngine()
    {
        lock (_gate)
        {
            if (BackgroundLoopDisabledForTests) { _frozen = false; return OperationResult.Ok("隔离测试手动驱动循环。"); }
            if (_rulesEngineRunning) return OperationResult.Ok("规则引擎已在运行。");
            _frozen = false;
            _rulesCts = new CancellationTokenSource();
            var cts = _rulesCts;
            _rulesEngineRunning = true;
            _rulesLoop = Task.Run(() => RunRulesLoopAsync(cts.Token));
        }
        SetAction("规则引擎已启动。");
        return OperationResult.Ok("规则引擎已启动。");
    }

    public async Task<OperationResult> StopRulesEngineAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _rulesCts;
            loop = _rulesLoop;
            _rulesCts = null;
            _rulesLoop = null;
            _rulesEngineRunning = false;
            _frozen = true;
        }
        if (cts is not null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            if (loop is not null) { try { await loop; } catch (OperationCanceledException) { } }
            cts.Dispose();
        }
        SetAction("规则引擎已停止。");
        return OperationResult.Ok("规则引擎已停止。");
    }

    private async Task RunRulesLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RulesTickAsync(token).ConfigureAwait(false);
                await ResponsivenessTickAsync().ConfigureAwait(false);
                AutomaticMemoryTick(token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                SetError("规则引擎 tick 异常：" + ex.Message);
            }
            try { await Task.Delay(RulesTickInterval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// 一次规则 tick：读取启用规则 → 一次进程快照 → 按目标路径分组应用。
    /// 只对「未被手动控制且无修改记录」的属性应用规则；规则拥有者写记录、可核对可还原。
    /// </summary>
    /// <summary>立即执行一次规则应用（测试与“立即应用”入口；与后台 tick 同一路径）。</summary>
    public Task ApplyRulesOnceAsync() => RulesTickAsync(CancellationToken.None);

    private Task RulesTickAsync(CancellationToken token)
    {
        lock (_gate) return _frozen ? Task.CompletedTask : RulesTickCoreAsync(token);
    }

    private async Task RulesTickCoreAsync(CancellationToken token)
    {
        var rules = _rules.EnabledRules();
        if (rules.Count == 0) return;

        var byPath = rules.GroupBy(r => r.ExecutablePath, StringComparer.OrdinalIgnoreCase).ToList();
        var snapshot = Process.GetProcesses();
        try
        {
            foreach (var process in snapshot)
            {
                using (process)
                {
                    // 逐进程兜底：系统/受保护进程可能拒绝访问（HasExited 也要句柄），失败只跳过该进程。
                    try
                    {
                        if (process.HasExited) continue;
                        ProcmanProcessIdentity? identity = ProcmanIdentity.TryIdentify(process.Id);
                        if (identity is null) continue;
                        if (ProcmanIdentity.IsProtectedPath(identity.ExecutablePath)) continue;

                        var matched = byPath.FirstOrDefault(g =>
                            string.Equals(g.Key, identity.ExecutablePath, StringComparison.OrdinalIgnoreCase));
                        if (matched is null) continue;

                        var held = _changeLog.ForProcess(identity.Pid, identity.StartTimeUtcTicks, identity.ExecutablePath);
                        foreach (var rule in matched)
                        {
                            token.ThrowIfCancellationRequested();
                            // 已由手动/其他规则持有的属性不重复写入（手动设置优先于规则）。
                            var existing = held.FirstOrDefault(h => h.Capability == rule.Capability);
                            if (existing is not null)
                            {
                                if (existing.Owner != "responsiveness" || RestoreRecord(existing, identity).Status != "restored") continue;
                            }
                            using var property = _properties.Enter(identity, rule.Capability);
                            if (property.Owner != "system" && !(property.Owner == "game" && rule.AutoTakeover)) continue;
                            ApplyRuleToProcess(identity, rule);
                        }
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
                    {
                        // 访问被拒/进程已退出：跳过（不影响其他进程的规则应用）。
                    }
                }
            }
        }
        finally
        {
            foreach (var process in snapshot) process.Dispose();
        }
        await Task.CompletedTask;
    }

    private void ApplyRuleToProcess(ProcmanProcessIdentity identity, ProcmanRuleEntry rule)
    {
        // 规则 owner：rule:路径（区分目标，冲突信息可读）。
        var owner = ProcmanControlCoordinator.PrefixRule + Path.GetFileName(rule.ExecutablePath);
        using var pin = ProcmanCapabilities.Pin(identity);

        var outcome = rule.Capability switch
        {
            CapabilityPriority => ApplyRulePriority(identity, rule),
            CapabilityEcoQos when rule.Value == "true" || rule.Value == "false"
                => ApplySimpleRule(identity, rule, ProcmanCapabilities.ReadEcoQos(identity.Pid),
                    () => ProcmanCapabilities.SetEcoQos(identity.Pid, rule.Value == "true"), value => ProcmanCapabilities.RestoreEcoQos(identity.Pid, value)),
            CapabilityPriorityBoost when rule.Value == "True" || rule.Value == "False"
                => ApplySimpleRule(identity, rule, ProcmanCapabilities.ReadPriorityBoost(identity.Pid),
                    () => ProcmanCapabilities.SetPriorityBoost(identity.Pid, rule.Value == "True"), value => ProcmanCapabilities.SetPriorityBoost(identity.Pid, value == "True")),
            CapabilityIoPriority => ApplySimpleRule(identity, rule, ProcmanCapabilities.ReadIoPriority(identity.Pid),
                () => ProcmanCapabilities.SetIoPriority(identity.Pid, ProcmanCapabilities.IoPriorityFromName(rule.Value)), value => ProcmanCapabilities.SetIoPriority(identity.Pid, ProcmanCapabilities.IoPriorityFromName(value))),
            CapabilityMemoryPriority => ApplySimpleRule(identity, rule, ProcmanCapabilities.ReadMemoryPriority(identity.Pid),
                () => ProcmanCapabilities.SetMemoryPriority(identity.Pid, int.Parse(rule.Value)), value => ProcmanCapabilities.SetMemoryPriority(identity.Pid, int.Parse(value))),
            CapabilityGpuPriority => ApplySimpleRule(identity, rule, ProcmanCapabilities.ReadGpuPriority(identity.Pid),
                () => ProcmanCapabilities.SetGpuPriority(identity.Pid, int.Parse(rule.Value)), value => ProcmanCapabilities.SetGpuPriority(identity.Pid, int.Parse(value))),
            CapabilityAffinity or CapabilityCpuSet => ApplyRuleMasks(identity, rule),
            _ => ProcmanCapabilityOutcome0("rejected", "规则值不合法或能力未开放：" + rule.Value),
        };
        _ = outcome;

        static ProcmanCapabilityOutcome ProcmanCapabilityOutcome0(string status, string message)
            => new() { Status = status, Message = message, Capability = "", ActualValue = "" };
    }

    private bool ApplyRuleCommon(ProcmanProcessIdentity identity, ProcmanRuleEntry rule, string desired, string original,
        Func<ProcmanCapabilityResult> apply, Func<ProcmanCapabilityResult> restore)
    {
        var record = new ProcmanChangeLog.ChangeRecord(identity.Pid, identity.StartTimeUtcTicks, identity.ExecutablePath,
            rule.Capability, original, desired, ProcmanControlCoordinator.PrefixRule + Path.GetFileName(rule.ExecutablePath),
            DateTimeOffset.UtcNow);
        using var property = _properties.Enter(identity, rule.Capability);
        if (property.Owner != "system" && !(property.Owner == "game" && rule.AutoTakeover)) return false;
        try { _changeLog.Add(record); property.SetOwner(record.Owner); }
        catch { return false; }

        var result = apply();
        if (result.Ok)
        {
            // 已确认生效：更新记录为回读值。
            _changeLog.AddOrPreserveOriginal(record with { AppliedValue = result.Value ?? desired });
            return true;
        }
        if (restore is not null) { try { _ = restore(); } catch { } }
        SetError($"规则应用失败（{identity.ExecutablePath} {rule.Capability}）：{result.Error}");
        return false;
    }

    private ProcmanCapabilityOutcome ApplySimpleRule(ProcmanProcessIdentity identity, ProcmanRuleEntry rule,
        ProcmanCapabilityResult original, Func<ProcmanCapabilityResult> apply, Func<string, ProcmanCapabilityResult> restore)
    {
        if (!original.Ok) return Outcome(rule.Capability, "failed", original.Error);
        var ok = ApplyRuleCommon(identity, rule, rule.Value, original.Value!, apply, () => restore(original.Value!));
        return Outcome(rule.Capability, ok ? "applied" : "failed", "");
    }

    private ProcmanCapabilityOutcome ApplyRulePriority(ProcmanProcessIdentity identity, ProcmanRuleEntry rule)
    {
        if (!Enum.TryParse<ProcessPriorityClass>(rule.Value, true, out var priority) || !Enum.IsDefined(priority)
            || priority is ProcessPriorityClass.RealTime)
            return new() { Status = "rejected", Message = "规则优先级不合法（实时优先级禁止）：" + rule.Value, Capability = CapabilityPriority };
        var original = ProcmanCapabilities.ReadPriority(identity.Pid);
        if (!original.Ok) return new() { Status = "failed", Message = original.Error, Capability = CapabilityPriority };
        var ok = ApplyRuleCommon(identity, rule, priority.ToString(), original.Value!,
            () => ProcmanCapabilities.SetPriority(identity.Pid, priority),
            () => ProcmanCapabilities.SetPriority(identity.Pid, ParsePriority(original.Value!)));
        return new() { Status = ok ? "applied" : "failed", Capability = CapabilityPriority, ActualValue = priority.ToString() };
    }

    private ProcmanCapabilityOutcome ApplyRuleMasks(ProcmanProcessIdentity identity, ProcmanRuleEntry rule)
    {
        // 规则值保存逻辑处理器描述（g{group}:0x{mask};…），空 = 清除。
        var masks = ParseGroupMasks(rule.Value);
        if (rule.Value.Length > 0 && masks.Count == 0)
            return new() { Status = "rejected", Message = "规则掩码格式不合法：" + rule.Value, Capability = rule.Capability };
        var read = rule.Capability == CapabilityAffinity ? ProcmanCapabilities.ReadAffinity(identity.Pid) : ProcmanCapabilities.ReadCpuSet(identity.Pid);
        if (!read.Ok) return new() { Status = "failed", Message = read.Error, Capability = rule.Capability };
        var ok = rule.Capability == CapabilityAffinity
            ? ApplyRuleCommon(identity, rule, ProcmanTopology.Describe(masks), read.Value!, () => ProcmanCapabilities.SetAffinity(identity.Pid, masks),
                () => RestoreByOriginalAffinity(identity.Pid, read.Value!))
            : ApplyRuleCommon(identity, rule, masks.Count == 0 ? "(系统默认)" : ProcmanTopology.Describe(masks), read.Value!, () => ProcmanCapabilities.SetCpuSet(identity.Pid, masks),
                () => RestoreByOriginalCpuSet(identity.Pid, read.Value!));
        return new() { Status = ok ? "applied" : "failed", Capability = rule.Capability };
    }

    private ProcmanCapabilityOutcome WrapRuleOutcome(ProcmanProcessIdentity identity, ProcmanRuleEntry rule, ProcmanCapabilityResult result)
        => new() { Status = result.Ok ? "applied" : "failed", Message = result.Error, Capability = rule.Capability, ActualValue = result.Value ?? "" };

    /// <summary>规则列表。</summary>
    public ProcmanRulesListResult ListRules() => new() { Rules = _rules.Load() };

    /// <summary>保存规则（同目标同能力合并为一条）。</summary>
    public OperationResult SaveRule(ProcmanRuleEntry rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Capability) || !KnownCapabilities.Contains(rule.Capability))
            return OperationResult.Fail("未知能力：" + rule.Capability);
        if (rule.Capability == CapabilityGpuPreference) return OperationResult.Fail("GPU 首选项是持久应用设置，请使用手动设置入口，不作为运行时规则反复写入。");
        if (rule.Capability == CapabilityIoPriority && ProcmanCapabilities.IoPriorityFromName(rule.Value) < 0) return OperationResult.Fail("I/O 规则档位无效。");
        if (rule.Capability is CapabilityMemoryPriority or CapabilityGpuPriority
            && (!int.TryParse(rule.Value, out var numeric) || (rule.Capability == CapabilityMemoryPriority ? numeric is < 1 or > 5 : numeric is < 0 or > 4)))
            return OperationResult.Fail("数值规则档位无效或实时 GPU 档位被禁止。");
        if (rule.Capability == CapabilityPriority)
        {
            // 规则值必须是合法且非实时的优先级（自动策略禁用实时优先级）。
            if (!Enum.TryParse<ProcessPriorityClass>(rule.Value, true, out var priority) || !Enum.IsDefined(priority)
                || priority is ProcessPriorityClass.RealTime)
                return OperationResult.Fail("规则优先级值不合法（实时优先级禁止）：" + rule.Value);
            rule.Value = priority.ToString();
        }
        else if (rule.Capability == CapabilityEcoQos && rule.Value is not ("true" or "false"))
            return OperationResult.Fail("效率模式规则值必须是 true/false。");
        else if (rule.Capability == CapabilityPriorityBoost && rule.Value is not ("True" or "False"))
            return OperationResult.Fail("动态提升规则值必须是 True/False。");
        else if ((rule.Capability == CapabilityAffinity || rule.Capability == CapabilityCpuSet)
            && rule.Value.Length > 0 && ParseGroupMasks(rule.Value).Count == 0)
            return OperationResult.Fail("掩码规则值格式不合法（应为 g{组}:0x{掩码};… 或空）：" + rule.Value);
        _ = StopRulesEngineAsync().GetAwaiter().GetResult();
        lock (_gate)
        {
            var failure = RestoreRuleRecords(rule.ExecutablePath, rule.Capability);
            if (failure.Length > 0) return OperationResult.Fail("规则旧值恢复未完成，未覆盖定义：" + failure);
        }
        try { _rules.Save(rule); }
        catch (Exception ex) { return OperationResult.Fail("规则保存失败：" + ex.Message); }
        // 有启用规则时保证引擎在跑（关窗驻留/最小化时规则仍在后台执行）。
        if (_rules.EnabledRules().Count > 0 || ResponsivenessEnabled || _automaticMemoryActive) _ = StartRulesEngine();
        else lock (_gate) _frozen = false;
        SetAction($"规则已保存：{rule.ExecutablePath} {rule.Capability}={rule.Value}" + (rule.Enabled ? "（启用）" : "（停用）"));
        return OperationResult.Ok("规则已保存。");
    }

    /// <summary>删除规则：先恢复该规则拥有的属性，再从规则文件移除。</summary>
    public async Task<OperationResult> DeleteRuleAsync(string executablePath, string capability)
    {
        await StopRulesEngineAsync();
        var rule = _rules.Load().FirstOrDefault(r =>
            string.Equals(r.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Capability, capability, StringComparison.Ordinal));
        if (rule is null) return OperationResult.Fail("规则不存在。");

        // 恢复该规则当前拥有的实例修改（只还原 owner 为该规则的记录）。
        string failure;
        lock (_gate) failure = RestoreRuleRecords(executablePath, capability);
        if (failure.Length > 0) return OperationResult.Fail("规则恢复未完成，定义与证据保留：" + failure);

        if (!_rules.Delete(executablePath, capability))
            return OperationResult.Fail("规则删除失败：未找到或文件被占用。");
        SetAction($"规则已删除并恢复其修改：{executablePath} {capability}");
        if (_rules.EnabledRules().Count > 0 || ResponsivenessEnabled || _automaticMemoryActive) _ = StartRulesEngine();
        else lock (_gate) _frozen = false;
        await Task.CompletedTask;
        return OperationResult.Ok("规则已删除，其拥有的修改已还原。");
    }

    private string RestoreRuleRecords(string path, string capability)
    {
        var failures = new List<string>();
        foreach (var record in _changeLog.Records.Where(r => r.Capability == capability && r.ExecutablePath.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            if (!record.Owner.StartsWith("rule:", StringComparison.Ordinal))
            {
                if (record.Parent?.Owner.StartsWith("rule:", StringComparison.Ordinal) == true)
                    _changeLog.Replace(record with { OriginalValue = record.Parent.OriginalValue, Parent = record.Parent.Parent });
                continue;
            }
            var identity = new ProcmanProcessIdentity(record.Pid, new DateTime(record.StartTimeUtcTicks, DateTimeKind.Utc), record.ExecutablePath);
            var state = ProcmanIdentity.State(identity);
            if (state == "missing") { _changeLog.Remove(record); using var property = _properties.Enter(identity, capability); property.SetOwner(null); continue; }
            if (state == "unknown") { failures.Add("身份未知"); continue; }
            var outcome = RestoreRecord(record, identity);
            if (outcome.Status != "restored") failures.Add(outcome.Message);
        }
        return string.Join("；", failures);
    }

    // ---------------- P4：原创「后台抑制」 ----------------
    // 策略与阈值为本项目自行制定（不从任何第三方产品提取）：
    //   触发：系统 CPU 使用率持续 ≥ CpuBusyThreshold%（默认 65）至少 CpuBusySeconds 秒（默认 3）；
    //   候选：排除三角洲、系统、前台应用，以及已有规则或其他属性修改的进程；
    //   剩余进程占总 CPU ≥ CandidateCpuFloorPercent%（默认按逻辑 CPU 数自动取 1–5）；
    //   动作：每轮最多 MaxAdjustPerRound（默认 1）个 Normal 进程临时降为 Below Normal；
    //   恢复：系统 CPU 连续 < CpuIdleThreshold%（默认 50）至少 CpuIdleSeconds 秒（默认 6）后恢复，之后冷却 CooldownSeconds 秒（默认 15）；
    //   采样失败时不新增调整。默认关闭。

    private sealed class ResponsivenessState
    {
        public bool Enabled;
        public int CpuBusyThreshold = 65;
        public int CpuBusySeconds = 3;
        public int CpuIdleThreshold = 50;
        public int CpuIdleSeconds = 6;
        public int CooldownSeconds = 15;
        public int CandidateCpuFloorPercent = 5;
        public int MaxAdjustPerRound = 1;

        public DateTime? BusySince;
        public DateTime? IdleSince;
        public DateTime CooldownUntil;
    }

    private ResponsivenessState? _responsiveness;
    private readonly List<ProcmanResponsivenessEvent> _responsivenessEvents = [];

    public bool ResponsivenessEnabled => _responsiveness is { Enabled: true };

    public IReadOnlyList<ProcmanResponsivenessEvent> ResponsivenessEvents()
    { lock (_gate) return _responsivenessEvents.TakeLast(200).ToList(); }

    /// <summary>配置后台抑制（启用/停用/阈值/排除项）。停用时立即恢复全部调整。</summary>
    public OperationResult ConfigureResponsiveness(ProcmanResponsivenessConfigArgs args)
    {
        if (args.CpuBusyThreshold is < 10 or > 99 || args.CpuIdleThreshold is < 5 or > 99
            || args.CpuBusyThreshold <= args.CpuIdleThreshold || args.CpuBusySeconds is < 1 or > 600
            || args.CpuIdleSeconds is < 1 or > 600 || args.CooldownSeconds is < 0 or > 3600
            || args.CandidateCpuFloorPercent is < 0 or > 100 || args.MaxAdjustPerRound is < 1 or > 4)
            return OperationResult.Fail("后台抑制参数无效；原循环和配置保持不变。");
        _ = StopRulesEngineAsync().GetAwaiter().GetResult();
        lock (_gate)
        {
            if (args.CpuBusyThreshold is < 10 or > 99 || args.CpuIdleThreshold is < 5 or > 99
                || args.CpuBusyThreshold <= args.CpuIdleThreshold
                || args.CpuBusySeconds < 1 || args.CpuIdleSeconds < 1 || args.CooldownSeconds < 0
                || args.CandidateCpuFloorPercent is < 0 or > 100 || args.MaxAdjustPerRound is < 1 or > 4)
                return OperationResult.Fail("后台抑制参数不合法（触发阈值需高于恢复阈值，各值需在合理区间）。");

            var wasEnabled = _responsiveness is { Enabled: true };
            _responsiveness = new ResponsivenessState
            {
                Enabled = args.Enabled && FrameSuppressionActive,
                CpuBusyThreshold = args.CpuBusyThreshold,
                CpuBusySeconds = args.CpuBusySeconds,
                CpuIdleThreshold = args.CpuIdleThreshold,
                CpuIdleSeconds = args.CpuIdleSeconds,
                CooldownSeconds = args.CooldownSeconds,
                CandidateCpuFloorPercent = ResolveCandidateThreshold(args.CandidateCpuFloorPercent),
                MaxAdjustPerRound = args.MaxAdjustPerRound,
            };
            if (args.Enabled && !wasEnabled) SetAction("后台抑制已启用（本项目自行制定的保守策略，默认关闭）。");
            if (!args.Enabled && wasEnabled) SetAction("后台抑制已停用，正在恢复全部调整…");
        }

        if (!args.Enabled || !FrameSuppressionActive)
        {
            // 停用：恢复所有 responsiveness 拥有的记录（同步执行，可核对）。
            foreach (var record in _changeLog.Records.Where(r => r.Owner == ProcmanControlCoordinator.OwnerResponsiveness).ToList())
            {
                var identity = new ProcmanProcessIdentity(record.Pid, new DateTime(record.StartTimeUtcTicks, DateTimeKind.Utc), record.ExecutablePath);
                var state = ProcmanIdentity.State(identity);
                if (state == "missing") { _changeLog.Remove(record); using var property = _properties.Enter(identity, record.Capability); property.SetOwner(null); continue; }
                if (state == "unknown") return OperationResult.Fail("已停用，但存在身份未知的调整，记录保留。");
                var restored = RestoreRecord(record, identity);
                if (restored.Status != "restored") return OperationResult.Fail("已停用，恢复尚未完成：" + restored.Message);
            }
        }
        if (ResponsivenessEnabled || _rules.EnabledRules().Count > 0 || _automaticMemoryActive) _ = StartRulesEngine();
        else lock (_gate) _frozen = false;
        return OperationResult.Ok(ResponsivenessEnabled ? "后台抑制已随帧格启用。" : "后台抑制已停用并恢复调整，等待一键帧格开启。");
    }

    /// <summary>后台抑制 tick（由规则引擎循环附带驱动；采样失败时不做任何调整）。</summary>
    private async Task ResponsivenessTickAsync()
    {
        var state = _responsiveness;
        if (state is not { Enabled: true }) return;
        if (!FrameSuppressionActive)
        {
            lock (_gate) RestoreResponsivenessAdjustments(reason: "帧格或后台抑制已停用，恢复原优先级。");
            state.BusySince = state.IdleSince = null;
            return;
        }

        // 转回前台不等待整机负载下降；采样失败也不妨碍恢复已确认身份的前台进程。
        var foreground = ForegroundForTests?.Invoke() ?? NativeProcman.ForegroundPid();
        if (foreground > 0)
            lock (_gate) RestoreResponsivenessAdjustments(r => r.Pid == foreground,
                "应用已切换到前台，恢复原优先级。");

        ProcmanCpuSample sample;
        lock (_gate) sample = SampleForTests?.Invoke() ?? _sampler.Sample();
        var totalCpu = sample.Total;
        if (totalCpu is null) return; // 采样失败：不新增调整

        var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
        if (totalCpu >= state.CpuBusyThreshold)
        {
            state.IdleSince = null;
            state.BusySince ??= now;
            if (state.BusySince.Value.AddSeconds(state.CpuBusySeconds) <= now && now >= state.CooldownUntil)
            {
                lock (_gate)
                    if (!_frozen && ReferenceEquals(state, _responsiveness)) DemoteSample(state, sample);
                state.BusySince = now; // 重置触发窗口，避免连续轮次叠加
                state.CooldownUntil = now.AddSeconds(Math.Max(1, state.CooldownSeconds / 2)); // 轮内冷却（完整冷却在恢复后）
            }
        }
        else if (totalCpu < state.CpuIdleThreshold)
        {
            state.BusySince = null;
            state.IdleSince ??= now;
            if (state.IdleSince.Value.AddSeconds(state.CpuIdleSeconds) <= now)
            {
                int restored;
                lock (_gate) restored = ReferenceEquals(state, _responsiveness) ? RestoreResponsivenessAdjustments() : 0;
                state.IdleSince = now;
                if (restored > 0)
                {
                    state.CooldownUntil = now.AddSeconds(state.CooldownSeconds);
                    SetAction($"后台抑制：系统负载回落，已恢复 {restored} 个进程的优先级。");
                }
            }
        }
        else
        {
            // 介于恢复阈值与触发阈值之间：维持现状
            state.BusySince = null;
            state.IdleSince = null;
        }
    }

    internal Task ResponsivenessOnceForTests() => ResponsivenessTickAsync();

    private void DemoteSample(ResponsivenessState state, ProcmanCpuSample sample)
    {
        var foreground = ForegroundForTests?.Invoke() ?? NativeProcman.ForegroundPid();
        if (foreground <= 0) return;
        var explicitRules = _rules.EnabledRules();
        var adjusted = 0;
        foreach (var (identity, cpu) in sample.Processes.OrderByDescending(p => p.Cpu))
        {
            if (adjusted >= state.MaxAdjustPerRound || !ReferenceEquals(state, _responsiveness) || !state.Enabled || !FrameSuppressionActive || _frozen) break;
            if (identity.Pid == foreground || identity.Pid == Environment.ProcessId || cpu < state.CandidateCpuFloorPercent
                || IsBackgroundExcluded(identity.ExecutablePath)
                || ProcmanResponsivenessFilter.IsToolProcess(identity.ExecutablePath)
                || Path.GetFileNameWithoutExtension(identity.ExecutablePath).Equals(DeltaForceLocator.GameProcessName, StringComparison.OrdinalIgnoreCase)
                || (ResponsivenessExcludedForTests?.Invoke(identity) ?? ProcmanResponsivenessFilter.IsExcluded(identity))
                || explicitRules.Any(r => r.ExecutablePath.Equals(identity.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                || _changeLog.ForProcess(identity.Pid, identity.StartTimeUtcTicks, identity.ExecutablePath).Count != 0) continue;
            try
            {
                using var pin = ProcmanCapabilities.Pin(identity);
                if (ResponsivenessExcludedForTests?.Invoke(identity) ?? ProcmanResponsivenessFilter.IsExcluded(identity)) continue;
                var currentForeground = ForegroundForTests?.Invoke() ?? NativeProcman.ForegroundPid();
                if (currentForeground <= 0 || currentForeground == identity.Pid) continue;
                using var property = _properties.Enter(identity, CapabilityPriority);
                if (property.Owner != "system") continue;
                var original = ProcmanCapabilities.ReadPriority(identity.Pid);
                if (!original.Ok || original.Value != "Normal") continue;
                var record = new ProcmanChangeLog.ChangeRecord(identity.Pid, identity.StartTimeUtcTicks, identity.ExecutablePath,
                    CapabilityPriority, "Normal", "BelowNormal", "responsiveness", DateTimeOffset.UtcNow);
                _changeLog.Add(record); property.SetOwner("responsiveness");
                var applied = ProcmanCapabilities.SetPriority(identity.Pid, ProcessPriorityClass.BelowNormal);
                if (!applied.Ok) { SetError(applied.Error); continue; }
                adjusted++;
                _responsivenessEvents.Add(new() { TimeUtc = DateTimeOffset.UtcNow, Pid = identity.Pid, ExecutablePath = identity.ExecutablePath,
                    Action = "demote", Reason = $"系统持续高负载；进程占全机 CPU {cpu:0.#}%，临时降低至 BelowNormal。" });
                if (_responsivenessEvents.Count > 200) _responsivenessEvents.RemoveAt(0);
            }
            catch (Exception ex) { SetError("后台抑制未调整：" + ex.Message); }
        }
    }

    private int RestoreResponsivenessAdjustments(Func<ProcmanChangeLog.ChangeRecord, bool>? filter = null,
        string reason = "系统 CPU 负载回落，恢复原优先级。")
    {
        var restored = 0;
        foreach (var record in _changeLog.Records.Where(r => r.Owner == ProcmanControlCoordinator.OwnerResponsiveness && (filter is null || filter(r))).ToList())
        {
            var identity = new ProcmanProcessIdentity(record.Pid, new DateTime(record.StartTimeUtcTicks, DateTimeKind.Utc), record.ExecutablePath);
            var state = ProcmanIdentity.State(identity);
            if (state == "missing") { _changeLog.Remove(record); using var property = _properties.Enter(identity, record.Capability); property.SetOwner(null); continue; }
            if (state == "unknown") { SetError("后台抑制恢复时身份未知，记录保留。"); continue; }
            var outcome = RestoreRecord(record, identity);
            if (outcome.Status == "restored")
            {
                restored++;
                lock (_gate) _responsivenessEvents.Add(new ProcmanResponsivenessEvent
                {
                    TimeUtc = DateTimeOffset.UtcNow,
                    Pid = record.Pid,
                    ExecutablePath = record.ExecutablePath,
                    Action = "restore",
                    Reason = reason,
                });
                while (_responsivenessEvents.Count > 200) _responsivenessEvents.RemoveAt(0);
            }
        }
        return restored;
    }

    private static readonly HashSet<string> KnownCapabilities = new(StringComparer.Ordinal)
    {
        CapabilityPriority, CapabilityAffinity, CapabilityCpuSet, CapabilityEcoQos, CapabilityPriorityBoost,
        CapabilityIoPriority, CapabilityGpuPreference,
        CapabilityMemoryPriority, CapabilityGpuPriority,
    };

    public static string DescribeOwner(string owner) => owner switch
    {
        ProcmanControlCoordinator.OwnerSystem => "系统默认",
        ProcmanControlCoordinator.OwnerManual => "本工具手动设置",
        ProcmanControlCoordinator.OwnerResponsiveness => "后台抑制",
        _ when owner.StartsWith(ProcmanControlCoordinator.PrefixRule, StringComparison.Ordinal) => "总是规则 " + owner[5..],
        _ => owner,
    };
}
