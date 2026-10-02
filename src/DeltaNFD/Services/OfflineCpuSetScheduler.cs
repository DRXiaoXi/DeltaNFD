using System.Diagnostics;
using System.Numerics;
using DeltaNFD.Native;

namespace DeltaNFD.Services;

/// <summary>
/// 单次脱机软调度。只修改进程默认 CPU Sets，不触碰线程所选集合或硬亲和性；
/// 每个写入先原子保存原值与进程身份，并在写后读回核对。
/// </summary>
public sealed class OfflineCpuSetScheduler
{
    private static readonly HashSet<string> DualCcdExemptNames = new(StringComparer.OrdinalIgnoreCase)
    { "System", "Registry", "Memory Compression", "Idle" };

    private readonly ICpuTopologyService _topology;
    private readonly GameTargetService _target;
    private readonly OfflineModeStateStore _stateStore;
    private readonly Func<Process[]> _otherProcesses;
    private HashSet<string>? _knownDefaultGamePaths;

    public OfflineCpuSetScheduler()
        : this(new CpuTopologyService(), GameTargetService.Default, OfflineModeStateStore.Default) { }

    internal OfflineCpuSetScheduler(ICpuTopologyService topology, GameTargetService target, OfflineModeStateStore stateStore,
        Func<Process[]>? otherProcesses = null)
    {
        _topology = topology;
        _target = target;
        _stateStore = stateStore;
        _otherProcesses = otherProcesses ?? Process.GetProcesses;
    }

    public OfflineTargetDiscovery DiscoverCurrentTargets()
        => DiscoverTargets(_target.Current);

    public async Task<OperationResult> ApplyOnceAsync(OfflineModePreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var started = Stopwatch.StartNew();
        using var targetOperation = _target.BeginOperation();

        if (!_stateStore.TryRead(out var state, out var stateError))
            return OperationResult.Fail(stateError);
        if (!state.OfflineModeEnabled || state.SavedPreferences is null)
            return OperationResult.Fail("脱机模式未就绪或原配置快照缺失，拒绝应用 CPU Sets。");

        CpuTopology topology;
        try { topology = await _topology.GetTopologyAsync().WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return OperationResult.Fail("脱机 CPU 拓扑检测已取消。"); }
        catch (Exception ex) { return OperationResult.Fail("读取 CPU 拓扑失败：" + ex.Message); }

        if (!OfflineCpuSetPlanner.TryCreate(topology, preferences, out var plan, out var planError))
            return OperationResult.Fail(planError);
        if (plan is null)
            return OperationResult.Ok("未配置脱机 CPU 调度规则；未写入 CPU Sets。");
        if (started.Elapsed >= TimeSpan.FromSeconds(30))
            return OperationResult.Fail("脱机 CPU 调度准备超过 30 秒，已停止后续处理。");

        if (!OfflineCpuSetCatalog.TryRead(out var catalog, out var catalogError))
            return OperationResult.Fail("读取 CPU Sets 清单失败：" + catalogError);

        var api = Environment.OSVersion.Version.Build >= 22000
            ? OfflineCpuSetApi.Windows11Masks
            : OfflineCpuSetApi.Windows10Ids;
        if (!TryCreateAssignment(api, catalog, topology.GroupCount, plan.TargetMask, out var targetAssignment, out var targetMapError))
            return OperationResult.Fail("目标游戏 CPU Sets 映射失败：" + targetMapError);

        Assignment? otherAssignment = null;
        if (plan.IsDualCcd)
        {
            if (!TryCreateAssignment(api, catalog, topology.GroupCount, plan.OtherProcessMask, out var other, out var otherMapError))
                return OperationResult.Fail("其他进程 CPU Sets 映射失败：" + otherMapError);
            otherAssignment = other;
        }

        var targetSnapshot = _target.Current;
        var targetIdentities = CaptureTargets(targetSnapshot);
        if (targetIdentities.Count == 0)
            return OperationResult.Fail("未找到可验证路径与身份的目标游戏进程，未写入 CPU Sets。");

        var changedTargets = 0;
        var alreadyTargets = 0;
        var failedTargets = new List<string>();
        foreach (var identity in targetIdentities)
        {
            if (cancellationToken.IsCancellationRequested || started.Elapsed >= TimeSpan.FromSeconds(30))
                break;

            var result = ApplyDefaultSet(identity, targetAssignment, topology.AllMask, "target-game", targetSnapshot);
            if (result == ApplyResult.Applied) changedTargets++;
            else if (result == ApplyResult.AlreadyApplied) alreadyTargets++;
            else failedTargets.Add($"PID {identity.ProcessId}：{_lastApplyError}");
        }

        if (changedTargets + alreadyTargets == 0)
            return OperationResult.Fail("没有目标游戏进程成功应用 CPU Sets：" + string.Join("；", failedTargets));

        var changedOthers = 0;
        var skippedOthers = 0;
        var failedOthers = 0;
        if (otherAssignment is { } otherSet)
        {
            foreach (var process in _otherProcesses())
            {
                using (process)
                {
                    if (cancellationToken.IsCancellationRequested || started.Elapsed >= TimeSpan.FromSeconds(30))
                    {
                        failedOthers++;
                        break;
                    }

                    try
                    {
                        if (process.Id <= 4 || process.Id == Environment.ProcessId ||
                            DualCcdExemptNames.Contains(process.ProcessName) || _target.ProtectFromCleanup(process))
                        {
                            skippedOthers++;
                            continue;
                        }

                        var identity = CaptureOtherProcess(process);
                        if (identity is null || !HasFullHardAffinity(process, topology.AllMask))
                        {
                            skippedOthers++;
                            continue;
                        }

                        var result = ApplyDefaultSet(identity, otherSet, topology.AllMask, "other-process", null);
                        if (result is ApplyResult.Applied or ApplyResult.AlreadyApplied) changedOthers++;
                        else failedOthers++;
                    }
                    catch
                    {
                        // 当前进程不可访问或恰好退出：按任务约定记为跳过，继续其他进程。
                        skippedOthers++;
                    }
                }
            }
        }

        if (cancellationToken.IsCancellationRequested || started.Elapsed >= TimeSpan.FromSeconds(30))
            return OperationResult.Fail($"CPU Sets 部分完成后达到 30 秒或被取消；目标 {changedTargets + alreadyTargets}，其他进程 {changedOthers}，失败 {failedOthers}。已写入项保留身份和原值记录。 ");

        var message = plan.Description + $" 目标进程：新应用 {changedTargets}、原已匹配 {alreadyTargets}、失败 {failedTargets.Count}。";
        if (plan.IsDualCcd)
            message += $" 当前可访问的其他进程：新应用/已匹配 {changedOthers}、跳过 {skippedOthers}、失败 {failedOthers}。未使用硬亲和性回退。";
        if (failedTargets.Count != 0 || failedOthers != 0)
            message += " 部分进程未能应用；可查看日志和恢复清单。";

        Log.Info("脱机 CPU Sets：" + message);
        return OperationResult.Ok(message);
    }

    /// <summary>人工退出脱机模式时还原仍存活且身份完全匹配的进程；PID 已结束/复用时只清掉旧记录。</summary>
    public OperationResult RestoreRecordedAssignments()
    {
        if (!_stateStore.TryRead(out var state, out var error)) return OperationResult.Fail(error);
        var restored = 0;
        var stale = 0;
        var failed = new List<string>();

        foreach (var entry in state.CpuSetChanges.ToArray())
        {
            var match = ProbeRestoreProcess(entry, out var matchedProcess);
            using var process = matchedProcess;
            if (process is null)
            {
                if (match == OfflineRestoreProcessState.GoneOrReused)
                {
                    if (RemoveRecord(entry)) stale++;
                    else failed.Add($"PID {entry.ProcessId} 的过期记录无法清理");
                }
                else
                {
                    MarkRestoreFailed(entry, "进程身份无法可靠读取，保留原值记录，未当作已退出。");
                    failed.Add($"PID {entry.ProcessId} 身份无法确认");
                }
                continue;
            }

            using (process)
            {
                var handle = CpuSets.OpenProcessForDefaultCpuSets(entry.ProcessId);
                if (handle == IntPtr.Zero)
                {
                    MarkRestoreFailed(entry, "无法打开原进程读取 CPU Sets。");
                    failed.Add($"PID {entry.ProcessId} 无法打开");
                    continue;
                }

                try
                {
                    if (!TryReadAssignment(handle, entry.Api, out var current))
                    {
                        MarkRestoreFailed(entry, "无法读取当前 CPU Sets。");
                        failed.Add($"PID {entry.ProcessId} 当前 CPU Sets 读取失败");
                        continue;
                    }

                    var original = FromOriginal(entry);
                    var applied = FromApplied(entry);
                    if (AssignmentEquals(current, original))
                    {
                        if (RemoveRecord(entry)) stale++; else failed.Add($"PID {entry.ProcessId} 无改动记录无法清理");
                        continue;
                    }
                    if (!AssignmentEquals(current, applied))
                    {
                        MarkRestoreFailed(entry, "CPU Sets 已被外部修改；为避免覆盖第三方状态，未还原。");
                        failed.Add($"PID {entry.ProcessId} CPU Sets 与本工具最后写入值不符");
                        continue;
                    }

                    if (!TrySetAssignment(handle, original) || !TryReadAssignment(handle, entry.Api, out var after) ||
                        !AssignmentEquals(after, original))
                    {
                        MarkRestoreFailed(entry, "写回或复核原 CPU Sets 失败。");
                        failed.Add($"PID {entry.ProcessId} 原 CPU Sets 还原/复核失败");
                        continue;
                    }

                    if (RemoveRecord(entry)) restored++;
                    else failed.Add($"PID {entry.ProcessId} 已写回，但恢复记录删除失败");
                }
                finally { CpuSets.CloseHandleSafe(handle); }
            }
        }

        if (failed.Count != 0)
            return OperationResult.Fail($"CPU Sets 已还原 {restored} 个，过期记录清理 {stale} 个；仍有待恢复项：" + string.Join("；", failed));
        return OperationResult.Ok($"CPU Sets 已还原 {restored} 个，已清理已退出/身份不匹配的记录 {stale} 个。");
    }

    private string _lastApplyError = "";

    private ApplyResult ApplyDefaultSet(OfflineProcessIdentity identity, Assignment desired, ulong allMask,
        string purpose, GameTarget? target)
    {
        _lastApplyError = "";
        using var process = TryGetMatchingProcess(identity, target);
        if (process is null) { _lastApplyError = "进程已退出或身份不匹配"; return ApplyResult.Failed; }
        {
            if (!HasFullHardAffinity(process, allMask)) { _lastApplyError = "已有硬亲和性限制，未触碰"; return ApplyResult.Failed; }
            var handle = CpuSets.OpenProcessForDefaultCpuSets(identity.ProcessId);
            if (handle == IntPtr.Zero) { _lastApplyError = "无法打开进程默认 CPU Sets"; return ApplyResult.Failed; }

            try
            {
                if (!TryReadAssignment(handle, desired.Api, out var current))
                { _lastApplyError = "无法读取进程默认 CPU Sets"; return ApplyResult.Failed; }

                if (AssignmentEquals(current, desired))
                {
                    var previous = FindRecord(identity);
                    if (previous is null) return ApplyResult.AlreadyApplied; // 可能是用户/第三方原值，不认领
                    if (!AssignmentEquals(current, FromApplied(previous)))
                    { _lastApplyError = "当前集合虽匹配目标，但与已有本工具记录不符"; return ApplyResult.Failed; }
                    if (previous.Status != OfflineCpuSetRecordStatus.Applied)
                    {
                        previous = previous.Copy();
                        previous.Status = OfflineCpuSetRecordStatus.Applied;
                        previous.Error = "";
                        if (!SaveRecord(previous)) { _lastApplyError = "已读回目标集合，但记录状态无法确认保存"; return ApplyResult.Failed; }
                    }
                    return ApplyResult.AlreadyApplied;
                }

                var record = FindRecord(identity);
                if (record is not null)
                {
                    if (!AssignmentEquals(desired, FromApplied(record)))
                    { _lastApplyError = "已有其他 CPU 集合的恢复记录，请先恢复，未覆盖原应用值"; return ApplyResult.Failed; }
                    if (record.Status != OfflineCpuSetRecordStatus.Applied || !AssignmentEquals(current, FromApplied(record)))
                    { _lastApplyError = "已有 CPU Sets 记录待恢复，或当前值被外部修改"; return ApplyResult.Failed; }
                    record = record.Copy();
                    record.Purpose = purpose;
                    SetApplied(record, desired);
                    record.Status = OfflineCpuSetRecordStatus.Pending;
                    record.Error = "";
                }
                else
                {
                    record = CreateRecord(identity, purpose, current, desired);
                }

                if (!SaveRecord(record)) { _lastApplyError = "原值/身份记录保存失败"; return ApplyResult.Failed; }
                if (!IsIdentityCurrent(identity, target)) { _lastApplyError = "写入前进程身份复核失败"; return ApplyResult.Failed; }

                if (!TrySetAssignment(handle, desired))
                {
                    TryReconcileAfterFailedSet(handle, record, identity);
                    _lastApplyError = "设置进程默认 CPU Sets 失败";
                    return ApplyResult.Failed;
                }

                if (!TryReadAssignment(handle, desired.Api, out var after) || !AssignmentEquals(after, desired))
                {
                    TryReconcileAfterFailedSet(handle, record, identity);
                    _lastApplyError = "写入后 CPU Sets 复核不符";
                    return ApplyResult.Failed;
                }
                if (!IsIdentityCurrent(identity, target))
                {
                    // 句柄仍只指向原进程；记录保留，后续按身份复核决定清理或还原。
                    _lastApplyError = "写入后进程身份变化，保留恢复记录";
                    return ApplyResult.Failed;
                }

                SetApplied(record, desired);
                record.Status = OfflineCpuSetRecordStatus.Applied;
                record.Error = "";
                if (!SaveRecord(record))
                {
                    _lastApplyError = "写入成功但状态落盘失败；保留原值记录等待恢复";
                    return ApplyResult.Failed;
                }

                return ApplyResult.Applied;
            }
            finally { CpuSets.CloseHandleSafe(handle); }
        }
    }

    private List<OfflineProcessIdentity> CaptureTargets(GameTarget target)
        => DiscoverTargets(target).VerifiedProcesses
            .Select(p => new OfflineProcessIdentity(p.ProcessId, p.StartTimeUtcTicks, p.ExecutablePath, p.SessionId))
            .ToList();

    private OfflineTargetDiscovery DiscoverTargets(GameTarget target)
    {
        var result = new List<OfflineProcessIdentity>();
        var roots = target.IsCustom ? [] : GetKnownGameExecutablePaths();
        var candidates = _target.GetProcesses(forceRefresh: true);
        var observedCount = candidates.Length;
        foreach (var process in candidates)
        {
            using (process)
            {
                var identity = CaptureTarget(process, target, roots);
                if (identity is not null) result.Add(identity);
            }
        }
        return new OfflineTargetDiscovery(observedCount,
            result.Select(i => new OfflineTargetProcess(i.ProcessId, i.StartTimeUtcTicks, i.ExecutablePath, i.SessionId ?? -1)).ToArray());
    }

    private HashSet<string> GetKnownGameExecutablePaths()
    {
        if (_knownDefaultGamePaths is not null) return _knownDefaultGamePaths;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in DeltaForceLocator.FindRoots())
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(root, "Binaries", "Win64", DeltaForceLocator.GameProcessName + ".exe"),
                         Path.Combine(root, "DeltaForce", "Binaries", "Win64", DeltaForceLocator.GameProcessName + ".exe"),
                     })
            {
                try { if (File.Exists(candidate)) paths.Add(GameTargetService.CanonicalPath(candidate)); }
                catch { }
            }
        }
        _knownDefaultGamePaths = paths;
        return paths;
    }

    private OfflineProcessIdentity? CaptureTarget(Process process, GameTarget target, HashSet<string> knownPaths)
    {
        try
        {
            if (!_target.Matches(process, target)) return null;
            var started = process.StartTime.ToUniversalTime();
            var actualPath = GameTargetService.CanonicalPath(GameTargetService.ReadProcessPath(process.Id));
            var expectedPath = target.IsCustom
                ? GameTargetService.CanonicalPath(target.ExecutablePath)
                : knownPaths.Contains(actualPath) ? actualPath : "";
            if (expectedPath.Length == 0 || !actualPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase)) return null;
            var session = process.SessionId;
            if (process.HasExited || process.StartTime.ToUniversalTime() != started || !_target.Matches(process, target))
                return null;
            return new OfflineProcessIdentity(process.Id, started.Ticks, actualPath, session);
        }
        catch { return null; }
    }

    private static OfflineProcessIdentity? CaptureOtherProcess(Process process)
    {
        try
        {
            if (process.HasExited) return null;
            var started = process.StartTime.ToUniversalTime();
            var path = GameTargetService.CanonicalPath(GameTargetService.ReadProcessPath(process.Id));
            if (Path.GetFileNameWithoutExtension(path).Equals("", StringComparison.Ordinal)) return null;
            var sessionId = process.SessionId;
            if (process.HasExited || process.StartTime.ToUniversalTime() != started || process.SessionId != sessionId) return null;
            return new OfflineProcessIdentity(process.Id, started.Ticks, path, sessionId);
        }
        catch { return null; }
    }

    private static OfflineRestoreProcessState ProbeRestoreProcess(OfflineCpuSetChange entry, out Process? matched)
    {
        matched = null;
        Process? process = null;
        try
        {
            try { process = Process.GetProcessById(entry.ProcessId); }
            catch (ArgumentException) { return OfflineRestoreProcessState.GoneOrReused; }
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != entry.StartTimeUtcTicks)
                return OfflineRestoreProcessState.GoneOrReused;
            var path = GameTargetService.CanonicalPath(GameTargetService.ReadProcessPath(process.Id));
            if (!path.Equals(entry.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                return OfflineRestoreProcessState.Unverifiable;
            matched = process;
            process = null;
            return OfflineRestoreProcessState.Matched;
        }
        catch { return OfflineRestoreProcessState.Unverifiable; }
        finally { process?.Dispose(); }
    }

    private Process? TryGetMatchingProcess(OfflineProcessIdentity identity, GameTarget? target = null)
    {
        try
        {
            var process = Process.GetProcessById(identity.ProcessId);
            if (IsIdentityCurrent(process, identity, target)) return process;
            process.Dispose();
            return null;
        }
        catch { return null; }
    }

    private bool IsIdentityCurrent(OfflineProcessIdentity identity, GameTarget? target = null)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return IsIdentityCurrent(process, identity, target);
        }
        catch { return false; }
    }

    private bool IsIdentityCurrent(Process process, OfflineProcessIdentity identity, GameTarget? target = null)
    {
        try
        {
            var started = process.StartTime.ToUniversalTime();
            if (process.HasExited || started.Ticks != identity.StartTimeUtcTicks) return false;
            var currentPath = GameTargetService.CanonicalPath(GameTargetService.ReadProcessPath(process.Id));
            if (!currentPath.Equals(identity.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return false;
            if (identity.SessionId.HasValue && process.SessionId != identity.SessionId.Value) return false;
            if (target is not null && !_target.Matches(process, target)) return false;
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartTimeUtcTicks;
        }
        catch { return false; }
    }

    private static bool HasFullHardAffinity(Process process, ulong allMask)
    {
        try { return unchecked((ulong)process.ProcessorAffinity.ToInt64()) == allMask; }
        catch { return false; }
    }

    private static bool TryCreateAssignment(OfflineCpuSetApi api, IReadOnlyList<SystemCpuSetDescriptor> catalog,
        int groupCount, ulong mask, out Assignment assignment, out string error)
    {
        error = "";
        if (mask == 0) { assignment = default; error = "CPU Sets 掩码为空。"; return false; }
        if (api == OfflineCpuSetApi.Windows10Ids)
        {
            if (!OfflineCpuSetCatalog.TryMapMaskToIds(catalog, groupCount, 0, mask, out var ids, out error))
            { assignment = default; return false; }
            assignment = new Assignment(api, true, ids, []);
            return true;
        }

        if (!OfflineCpuSetCatalog.IsMaskUsable(catalog, groupCount, 0, mask))
        { assignment = default; error = "掩码中存在缺失、重复或已分配给其他目标的 CPU Set。"; return false; }
        assignment = new Assignment(api, true, [], [new CpuSetGroupMask(0, mask)]);
        return true;
    }

    private static bool TryReadAssignment(IntPtr processHandle, OfflineCpuSetApi api, out Assignment assignment)
    {
        if (api == OfflineCpuSetApi.Windows10Ids)
        {
            if (!CpuSets.TryGetDefaultCpuSetIds(processHandle, out var ids)) { assignment = default; return false; }
            assignment = new Assignment(api, ids.Length != 0, ids, []);
            return true;
        }
        if (!CpuSets.TryGetDefaultCpuSetMasks(processHandle, out var masks)) { assignment = default; return false; }
        assignment = new Assignment(api, masks.Length != 0, [], masks);
        return true;
    }

    private static bool TrySetAssignment(IntPtr processHandle, Assignment assignment)
        => assignment.Api == OfflineCpuSetApi.Windows10Ids
            ? CpuSets.TrySetDefaultCpuSetIds(processHandle, assignment.IsSet ? assignment.Ids : [])
            : CpuSets.TrySetDefaultCpuSetMasks(processHandle, assignment.IsSet ? assignment.Masks : []);

    private static Assignment FromOriginal(OfflineCpuSetChange record)
        => new(record.Api, record.OriginalAssignmentWasSet, record.OriginalCpuSetIds.ToArray(), record.OriginalMasks.ToArray());

    private static Assignment FromApplied(OfflineCpuSetChange record)
        => new(record.Api, record.AppliedAssignmentWasSet, record.AppliedCpuSetIds.ToArray(), record.AppliedMasks.ToArray());

    private static bool AssignmentEquals(Assignment a, Assignment b)
    {
        if (a.Api != b.Api || a.IsSet != b.IsSet) return false;
        if (!a.IsSet) return true;
        return a.Api == OfflineCpuSetApi.Windows10Ids
            ? a.Ids.Order().SequenceEqual(b.Ids.Order())
            : a.Masks.OrderBy(x => x.Group).SequenceEqual(b.Masks.OrderBy(x => x.Group));
    }

    private OfflineCpuSetChange? FindRecord(OfflineProcessIdentity identity)
    {
        if (!_stateStore.TryRead(out var state, out _)) return null;
        return state.CpuSetChanges.FirstOrDefault(r => SameIdentity(r, identity));
    }

    private bool SaveRecord(OfflineCpuSetChange record)
        => _stateStore.TryUpdate(state =>
        {
            var index = state.CpuSetChanges.FindIndex(r => SameIdentity(r, record));
            if (index < 0) state.CpuSetChanges.Add(record.Copy());
            else state.CpuSetChanges[index] = record.Copy();
        }, out _);

    private bool RemoveRecord(OfflineCpuSetChange record)
        => _stateStore.TryUpdate(state => state.CpuSetChanges.RemoveAll(r => SameIdentity(r, record)), out _);

    private static bool SameIdentity(OfflineCpuSetChange record, OfflineProcessIdentity identity)
        => record.ProcessId == identity.ProcessId && record.StartTimeUtcTicks == identity.StartTimeUtcTicks &&
           record.ExecutablePath.Equals(identity.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    private static bool SameIdentity(OfflineCpuSetChange left, OfflineCpuSetChange right)
        => left.ProcessId == right.ProcessId && left.StartTimeUtcTicks == right.StartTimeUtcTicks &&
           left.ExecutablePath.Equals(right.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    private void MarkRestoreFailed(OfflineCpuSetChange record, string error)
    {
        var updated = record.Copy();
        updated.Status = OfflineCpuSetRecordStatus.RestoreFailed;
        updated.Error = error;
        _ = SaveRecord(updated);
        Log.Warn($"脱机 CPU Sets：PID {record.ProcessId} 待恢复——{error}");
    }

    private void TryReconcileAfterFailedSet(IntPtr handle, OfflineCpuSetChange record, OfflineProcessIdentity identity)
    {
        if (!TryReadAssignment(handle, record.Api, out var current))
        {
            MarkRestoreFailed(record, "写入结果不明，读取当前值失败。");
            return;
        }
        if (AssignmentEquals(current, FromOriginal(record)))
        {
            _ = RemoveRecord(record);
            return;
        }
        if (!AssignmentEquals(current, FromApplied(record)) || !IsIdentityCurrent(identity))
        {
            MarkRestoreFailed(record, "写入结果与原值/目标值均不符，或进程身份已改变。");
            return;
        }
        if (!TrySetAssignment(handle, FromOriginal(record)) || !TryReadAssignment(handle, record.Api, out var after) ||
            !AssignmentEquals(after, FromOriginal(record)))
        {
            MarkRestoreFailed(record, "设置失败后的原值回滚未能复核。");
            return;
        }
        _ = RemoveRecord(record);
    }

    private static OfflineCpuSetChange CreateRecord(OfflineProcessIdentity identity, string purpose, Assignment original,
        Assignment applied)
    {
        var record = new OfflineCpuSetChange
        {
            ProcessId = identity.ProcessId,
            StartTimeUtcTicks = identity.StartTimeUtcTicks,
            ExecutablePath = identity.ExecutablePath,
            Purpose = purpose,
            Api = original.Api,
            OriginalAssignmentWasSet = original.IsSet,
            OriginalCpuSetIds = [.. original.Ids],
            OriginalMasks = [.. original.Masks],
            Status = OfflineCpuSetRecordStatus.Pending,
        };
        SetApplied(record, applied);
        return record;
    }

    private static void SetApplied(OfflineCpuSetChange record, Assignment applied)
    {
        record.AppliedAssignmentWasSet = applied.IsSet;
        record.AppliedCpuSetIds = [.. applied.Ids];
        record.AppliedMasks = [.. applied.Masks];
    }

    private sealed record OfflineProcessIdentity(int ProcessId, long StartTimeUtcTicks, string ExecutablePath, int? SessionId);
    private readonly record struct Assignment(OfflineCpuSetApi Api, bool IsSet, uint[] Ids, CpuSetGroupMask[] Masks);
    private enum ApplyResult { Failed, AlreadyApplied, Applied }
    private enum OfflineRestoreProcessState { Matched, GoneOrReused, Unverifiable }
}

public sealed record OfflineTargetProcess(int ProcessId, long StartTimeUtcTicks, string ExecutablePath, int SessionId);
public sealed record OfflineTargetDiscovery(int ObservedProcessCount, IReadOnlyList<OfflineTargetProcess> VerifiedProcesses);
