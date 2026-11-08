using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeltaNFD.Native;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

/// <summary>电源节流状态中与「忽略定时器分辨率请求」有关的位（只处理位 0x4，其余位原样保留）。</summary>
internal readonly record struct FrameTimerPowerState(uint Control, uint State)
{
    internal const uint IgnoreTimer = ProcmanCapabilities.PowerThrottlingIgnoreTimerResolution;

    /// <summary>已由本功能固定为忽略（控制掩码含该位且状态位已置位）。</summary>
    internal bool Forced => (Control & IgnoreTimer) != 0 && (State & IgnoreTimer) != 0;

    internal FrameTimerPowerState Apply() => Normalize(Control | IgnoreTimer, State | IgnoreTimer);

    internal FrameTimerPowerState Restore(FrameTimerPowerState original) => Normalize(
        (Control & ~IgnoreTimer) | (original.Control & IgnoreTimer),
        (State & ~IgnoreTimer) | (original.State & IgnoreTimer));

    internal bool SameTimerBit(FrameTimerPowerState other) => ((Control ^ other.Control) & IgnoreTimer) == 0
        && ((Control & IgnoreTimer) == 0 || ((State ^ other.State) & IgnoreTimer) == 0);

    private static FrameTimerPowerState Normalize(uint control, uint state) => new(control, state & control);
}

internal interface IFrameTimerIgnoreHandle : IDisposable
{
    FrameTimerPowerState Read();
    void Write(FrameTimerPowerState state);
}

internal interface IFrameTimerIgnoreApi
{
    string IdentityState(ProcmanProcessIdentity identity);
    IFrameTimerIgnoreHandle Open(ProcmanProcessIdentity identity);
    // Admission is deliberately separate from recovery. Fake APIs can inject this policy
    // without consulting real process identities for isolated test PIDs.
    IFrameTimerIgnoreHandle OpenForApply(ProcmanProcessIdentity identity) => Open(identity);
}

/// <summary>
/// 实测无请求进程的等待粒度通过探针门禁后才允许写入忽略位，不申请任何兜底计时器。
/// 写入前落盘原值；两个忽略功能共用同一原生位的归属锁并禁止重叠接管。
/// 停止/关闭/退出按固定身份恢复；历史双份记录无法证明最早原值时保留责任，拒绝猜测恢复。
/// </summary>
internal sealed class FrameGameTimerIgnore
{
    internal sealed record Backup(ProcmanProcessIdentity Identity, uint OriginalControl, uint OriginalState, uint AppliedControl, uint AppliedState);
    internal const string Capability = "timer-ignore";

    private readonly Func<ProcmanProcessIdentity, bool> _accept;
    private readonly Func<ProcmanProcessIdentity, bool> _validate;
    private readonly string _capability;
    private readonly string _label;
    private readonly string? _peerJournalPath;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _mutexName;
    private readonly ProcessPropertyGate _ownership;
    private readonly IFrameTimerIgnoreApi _api;
    private readonly FrameTimerLegacyProbe _probe;
    private readonly Dictionary<string, string> _blocked = [];
    private List<Backup> _known = [];
    private FrameTimerIgnoreSnapshot _snapshot = new();
    internal Action? BeforeSaveForTests { get; set; }
    internal FrameTimerIgnoreSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal FrameTimerLegacyProbe Probe => _probe;

    /// <param name="accept">允许施加/维护的目标判定（默认只认经核验的三角洲主进程）。</param>
    /// <param name="validate">恢复记录的身份校验（与 accept 分开：从忽略列表移除名称后，已有原值记录仍必须能恢复）。</param>
    /// <param name="capability">属性归属锁里的能力名，多实例复用同一把锁时不能相同。</param>
    /// <param name="label">文案里的目标称呼。</param>
    internal FrameGameTimerIgnore(string path, ProcessPropertyGate ownership, IFrameTimerIgnoreApi? api = null,
        FrameTimerLegacyProbe? probe = null, Func<ProcmanProcessIdentity, bool>? accept = null,
        Func<ProcmanProcessIdentity, bool>? validate = null, string capability = Capability, string label = "三角洲",
        string? peerJournalPath = null)
    {
        _accept = accept ?? IsDelta;
        _validate = validate ?? IsDelta;
        _capability = capability;
        _label = label;
        _path = path;
        _peerJournalPath = peerJournalPath ?? (Path.GetFileName(path) switch
        {
            "frame-game-timer-ignore.json" => Path.Combine(Path.GetDirectoryName(path)!, "timer-ignore-list.json"),
            "timer-ignore-list.json" => Path.Combine(Path.GetDirectoryName(path)!, "frame-game-timer-ignore.json"),
            _ => null,
        });
        _ownership = ownership;
        _api = api ?? new NativeFrameTimerIgnoreApi(requireOrdinaryUser: capability == "timer-ignore-list");
        _probe = probe ?? new FrameTimerLegacyProbe();
        _mutexName = @"Global\DeltaNFD.FrameTimerIgnore." + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        if (File.Exists(path))
        {
            try
            {
                var count = Load().Count;
                _snapshot = new()
                {
                    RestorePending = count > 0,
                    BackupCount = count,
                    Message = count > 0 ? $"存在{_label}定时器忽略原值记录，等待核对" : $"未持有{_label}定时器忽略原值记录",
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };
            }
            catch (Exception ex)
            {
                _snapshot = new() { HasError = true, RestorePending = true, BackupCount = 1, Message = "定时器忽略原值记录无法读取：" + ex.Message, UpdatedUtc = DateTimeOffset.UtcNow };
            }
        }
    }

    private static bool IsDelta(ProcmanProcessIdentity identity) => identity.Pid > 0 && identity.StartTimeUtcTicks > 0
        && Path.IsPathFullyQualified(identity.ExecutablePath)
        && Path.GetFileName(identity.ExecutablePath).Equals("DeltaForceClient-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase);

    private bool Valid(Backup backup) => backup is not null && backup.Identity is not null && _validate(backup.Identity)
        && (backup.AppliedControl & FrameTimerPowerState.IgnoreTimer) != 0
        && (backup.AppliedState & FrameTimerPowerState.IgnoreTimer) != 0;

    private List<Backup> Load()
    {
        if (!File.Exists(_path))
        {
            if (_known.Count > 0) throw new IOException("定时器忽略原值记录已丢失，未把当前值当作原值；请保留并核对恢复记录。");
            return [];
        }
        var info = new FileInfo(_path);
        if (info.Length > 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("定时器忽略恢复记录大小或文件类型异常。");
        var records = JsonSerializer.Deserialize<List<Backup>>(File.ReadAllText(_path)) ?? throw new IOException("定时器忽略恢复记录为空。");
        if (records.Any(b => !Valid(b)) || records.Select(b => b.Identity).Distinct().Count() != records.Count)
            throw new IOException("定时器忽略恢复记录包含无效或重复项。");
        _known = records;
        return records.ToList();
    }

    private void Save(List<Backup> records)
    {
        BeforeSaveForTests?.Invoke();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, records);
                stream.Flush(true);
            }
            File.Move(temp, _path, true);
            _known = records.ToList();
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal int PendingCount
    {
        get { lock (_gate) { try { return Load().Count; } catch { return Math.Max(1, _known.Count); } } }
    }

    private string Key(ProcmanProcessIdentity id) => $"{id.Pid}:{id.StartTimeUtcTicks}:{id.ExecutablePath}:{_capability}";

    // Called only while the shared native-bit ownership mutex is held. Journals outlive owners:
    // an exited backend must not make another feature back up an already modified value.
    private void RejectPeerResponsibility(ProcmanProcessIdentity identity)
    {
        if (_peerJournalPath is null) return;
        var info = new FileInfo(_peerJournalPath);
        long length;
        try { length = info.Length; }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if (length > 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("另一项定时器忽略恢复记录无法核验，未修改进程");
        var peer = JsonSerializer.Deserialize<List<Backup>>(File.ReadAllText(_peerJournalPath))
            ?? throw new IOException("另一项定时器忽略恢复记录无法核验");
        if (peer.Any(b => b is null || b.Identity is null || b.Identity.Pid <= 0
            || b.Identity.StartTimeUtcTicks <= 0 || !Path.IsPathFullyQualified(b.Identity.ExecutablePath)
            || (b.AppliedControl & FrameTimerPowerState.IgnoreTimer) == 0
            || (b.AppliedState & FrameTimerPowerState.IgnoreTimer) == 0))
            throw new IOException("另一项定时器忽略恢复记录损坏，未修改进程");
        if (peer.Any(b => b.Identity.Pid == identity.Pid
            && b.Identity.StartTimeUtcTicks == identity.StartTimeUtcTicks
            && b.Identity.ExecutablePath.Equals(identity.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("另一项定时器忽略功能仍持有此进程的原值记录；禁止重叠接管。若已有双份记录，无法证明最早原值，已保留记录等待核对");
    }

    internal bool Sync(IReadOnlyList<ProcmanProcessIdentity> wanted, Func<ProcmanProcessIdentity, bool> allowed,
        string idle, bool supported)
    {
        lock (_gate)
        {
            using var mutex = new Mutex(false, _mutexName);
            var entered = false;
            try
            {
                try { entered = mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { entered = true; }
                if (!entered) throw new IOException("定时器忽略原值记录交接超时，未修改进程。");
                var records = Load();
                if (!supported)
                {
                    Volatile.Write(ref _snapshot, new()
                    {
                        Supported = false,
                        RestorePending = records.Count > 0,
                        BackupCount = records.Count,
                        SystemCurrentMilliseconds = ProcessPerf.QueryTimerResolutionMs(),
                        Message = records.Count > 0 ? "本系统不支持该功能（仅 Windows 11 起），存在未恢复的原值记录" : "本系统不支持该功能（仅 Windows 11 起）",
                        UpdatedUtc = DateTimeOffset.UtcNow,
                    });
                    return records.Count == 0;
                }

                if (wanted.Any(id => !_accept(id))) throw new IOException($"只允许操作经核验的{_label}进程，未操作其他进程。");
                var targets = wanted.Distinct().ToArray();
                var failures = new List<string>();

                // 1) 先决定是否施加：只有在实测「无请求进程仍能获得较细时钟」时才施加忽略位，
                //    否则目标进程会回落到默认 15.625ms（帧数暴跌），必须拒绝。
                var wantApply = targets.Length > 0;
                bool? reachable = null;
                if (wantApply)
                {
                    reachable = _probe.Probe();
                    if (reachable != true) wantApply = false;
                }

                var effective = wantApply ? targets : [];

                // 2) 先把不在生效目标内的旧记录恢复完，再开始新的施加。
                foreach (var saved in records.Where(b => !effective.Contains(b.Identity)).ToArray())
                {
                    try
                    {
                        if (_ownership.TryCompleteMissingTimerRecovery(saved.Identity, _capability,
                            () => _api.IdentityState(saved.Identity), () =>
                            {
                                var next = records.Where(b => b != saved).ToList();
                                Save(next); records = next;
                            })) continue;
                        using var lease = _ownership.Enter(saved.Identity, _capability);
                        var state = _api.IdentityState(saved.Identity);
                        if (state == "missing")
                        {
                            // It may have exited after the first locked check. Do not
                            // clear a peer's shared owner through this lease snapshot.
                            if (!_ownership.TryCompleteMissingTimerRecovery(saved.Identity, _capability,
                                () => _api.IdentityState(saved.Identity), () =>
                                {
                                    var next = records.Where(b => b != saved).ToList();
                                    Save(next); records = next;
                                })) throw new IOException("原进程退出状态无法再次确认，保留记录");
                            continue;
                        }
                        if (state != "same") throw new IOException("原进程身份无法确认，保留原值记录");
                        RejectPeerResponsibility(saved.Identity);
                        using var handle = _api.Open(saved.Identity);
                        var current = handle.Read();
                        var original = new FrameTimerPowerState(saved.OriginalControl, saved.OriginalState);
                        if (!current.SameTimerBit(original))
                        {
                            var owner = lease.Owner;
                            if (owner != "system" && owner != "game" && owner != _capability)
                                throw new IOException("进程管理手动/规则设置优先，请先解除该属性的接管");
                            var applied = new FrameTimerPowerState(saved.AppliedControl, saved.AppliedState);
                            if (!current.SameTimerBit(applied)) throw new IOException("当前值与本功能写入值不符，未覆盖外部变更");
                            handle.Write(current.Restore(original));
                            if (!handle.Read().SameTimerBit(original)) throw new IOException("原值恢复回读不符");
                        }
                        lease.Game(true);
                        var restored = records.Where(b => b != saved).ToList();
                        Save(restored); records = restored;
                    }
                    catch (Exception ex) { failures.Add($"{_label} PID {saved.Identity.Pid} 恢复失败：{ex.Message}"); }
                }

                var restorePending = records.Any(b => !effective.Contains(b.Identity));
                var appliedCount = 0;
                if (!restorePending)
                foreach (var identity in effective)
                {
                    var key = Key(identity);
                    try
                    {
                        using var lease = _ownership.Enter(identity, _capability);
                        RejectPeerResponsibility(identity);
                        var owner = lease.Owner;
                        if (owner != "system" && owner != _capability)
                            throw new IOException("该属性已由其他自动功能或进程管理手动/规则设置接管，未覆盖");
                        using var handle = _api.OpenForApply(identity);
                        var current = handle.Read();
                        if (!allowed(identity)) throw new IOException("帧格、三角洲状态或恢复门禁已变化，未写入");
                        if (_blocked.TryGetValue(key, out var blocked))
                            throw new IOException(blocked + "（本次进程不再重复写入；关闭并重新开启功能后可重试）");

                        var desired = current.Apply();
                        var saved = records.FirstOrDefault(b => b.Identity == identity);
                        if (saved is null || saved.AppliedControl != desired.Control || saved.AppliedState != desired.State)
                        {
                            var next = new Backup(identity, saved?.OriginalControl ?? current.Control, saved?.OriginalState ?? current.State, desired.Control, desired.State);
                            var updated = records.Where(b => b != saved).Append(next).ToList();
                            Save(updated); records = updated; // 落盘先于任何原生写入
                        }

                        if (!current.Forced)
                        {
                            if (!allowed(identity)) throw new IOException("提交前执行条件已失效，未写入");
                            try
                            {
                                handle.Write(desired);
                                if (!handle.Read().Forced) throw new IOException("忽略位回读未确认");
                            }
                            catch (Exception ex) { _blocked[key] = ex.Message; throw; }
                        }
                        lease.Game(false, _capability);
                        appliedCount++;
                    }
                    catch (Exception ex) { failures.Add($"{_label} PID {identity.Pid} 未生效：{ex.Message}"); }
                }

                if (targets.Length == 0) _blocked.Clear();
                var unresolvedResponsibility = restorePending || (failures.Count > 0 && records.Count > 0);
                var message = BuildMessage(targets.Length, appliedCount, restorePending, records.Count, idle,
                    reachable, _probe, failures);
                if (failures.Count > 0) message = string.Join("；", failures) + "；" + message;

                Volatile.Write(ref _snapshot, new()
                {
                    Supported = true,
                    Maintaining = wantApply && !unresolvedResponsibility && appliedCount > 0,
                    HasError = failures.Count > 0,
                    RestorePending = unresolvedResponsibility,
                    BackupCount = records.Count,
                    InstanceCount = targets.Length,
                    AppliedCount = appliedCount,
                    SystemCurrentMilliseconds = ProcessPerf.QueryTimerResolutionMs(),
                    LegacyGlobalReachable = reachable,
                    RequestlessGranularityMilliseconds = _probe.GranularityMs,
                    IgnoreApplied = appliedCount > 0,
                    Message = message,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                });
                return failures.Count == 0;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _snapshot, new()
                {
                    Supported = true,
                    HasError = true,
                    RestorePending = true,
                    BackupCount = Math.Max(1, _known.Count),
                    SystemCurrentMilliseconds = ProcessPerf.QueryTimerResolutionMs(),
                    LegacyGlobalReachable = _probe.Reachable,
                    RequestlessGranularityMilliseconds = _probe.GranularityMs,
                    Message = $"{_label}定时器请求忽略未完成：" + ex.Message,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                });
                return false;
            }
            finally { if (entered) mutex.ReleaseMutex(); }
        }
    }

    private string BuildMessage(int instanceCount, int appliedCount, bool restorePending, int backupCount,
        string idle, bool? reachable, FrameTimerLegacyProbe probe, List<string> failures)
    {
        if (instanceCount == 0)
            return backupCount == 0 ? idle : $"{_label}定时器忽略原值恢复尚未完成，已保留记录。";
        if (restorePending)
            return "旧定时器忽略记录恢复未完成，未开始新的施加。";
        if (reachable != true)
            return "未施加忽略位：" + probe.Message + "；已保留三角洲自身精度";
        if (appliedCount == 0)
            return "忽略位尚未确认生效";
        return $"已忽略 {appliedCount}/{instanceCount} 个{_label}进程的精度请求"
            + (probe.GranularityMs is { } granularity ? $"（无请求进程实测 {granularity:0.##} ms）" : "");
    }
}

internal sealed class NativeFrameTimerIgnoreApi(bool requireOrdinaryUser = false) : IFrameTimerIgnoreApi
{
    public string IdentityState(ProcmanProcessIdentity identity) => ProcmanIdentity.State(identity);
    public IFrameTimerIgnoreHandle Open(ProcmanProcessIdentity identity) => new Handle(identity.Pid,
        ProcmanCapabilities.Pin(identity));
    public IFrameTimerIgnoreHandle OpenForApply(ProcmanProcessIdentity identity) => new Handle(identity.Pid,
        ProcmanCapabilities.Pin(identity, requireOrdinaryUser: requireOrdinaryUser));

    private sealed class Handle(int pid, IDisposable pin) : IFrameTimerIgnoreHandle
    {
        public FrameTimerPowerState Read()
        {
            var result = ProcmanCapabilities.ReadPowerThrottling(pid);
            if (!result.Ok) throw new IOException(result.Error);
            return new(result.Control, result.State);
        }

        public void Write(FrameTimerPowerState state)
        {
            var result = ProcmanCapabilities.WritePowerThrottling(pid, state.Control, state.State);
            if (!result.Ok) throw new IOException(result.Error);
            return;
        }

        public void Dispose() => pin.Dispose();
    }
}
