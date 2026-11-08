using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

internal interface IFramePriorityHandle : IDisposable
{
    int Read(string property);
    void Write(string property, int value);
}
internal interface IFramePriorityApi
{
    string IdentityState(ProcmanProcessIdentity identity);
    IFramePriorityHandle Open(ProcmanProcessIdentity identity);
}

/// <summary>Durable per-property originals, fixed Delta identity, and shared procman ownership.</summary>
internal sealed class FrameGamePriority
{
    internal sealed record Backup(ProcmanProcessIdentity Identity, string Property, int Original, int Applied);
    private static readonly string[] Properties = ["priority", "ioPriority", "memoryPriority"];
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _mutexName;
    private readonly ProcessPropertyGate _ownership;
    private readonly IFramePriorityApi _api;
    private readonly Dictionary<string, string> _blocked = [];
    private List<Backup> _known = [];
    private FrameGamePrioritySnapshot _snapshot = new();
    internal Action? BeforeSaveForTests { get; set; }
    internal FrameGamePrioritySnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal FrameGamePriority(string path, ProcessPropertyGate ownership, IFramePriorityApi? api = null)
    {
        _path = path; _ownership = ownership; _api = api ?? new NativeFramePriorityApi();
        _mutexName = @"Global\DeltaNFD.FramePriority." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        if (File.Exists(path))
        {
            try
            {
                var count = Load().Count;
                _snapshot = new() { RestorePending = count > 0, BackupCount = count,
                    Message = count > 0 ? "存在三角洲优先级恢复记录，等待核对原值" : "未持有三角洲优先级恢复记录", UpdatedUtc = DateTimeOffset.UtcNow };
            }
            catch (Exception ex)
            { _snapshot = new() { HasError = true, RestorePending = true, BackupCount = 1, Message = "优先级恢复记录无法读取：" + ex.Message, UpdatedUtc = DateTimeOffset.UtcNow }; }
        }
    }
    private static bool IsDelta(ProcmanProcessIdentity identity) => identity.Pid > 0 && identity.StartTimeUtcTicks > 0
        && Path.IsPathFullyQualified(identity.ExecutablePath)
        && Path.GetFileName(identity.ExecutablePath).Equals("DeltaForceClient-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase);
    private static bool Valid(string property, int value) => property switch
    {
        "priority" => Enum.IsDefined((FrameCpuPriority)value), "ioPriority" => value is >= 0 and <= 3,
        "memoryPriority" => value is >= 1 and <= 5, _ => false,
    };
    private List<Backup> Load()
    {
        if (!File.Exists(_path))
        {
            if (_known.Count > 0) throw new IOException("优先级原值记录已丢失，未把当前值当作原值；请保留并核对恢复记录。");
            return [];
        }
        var info = new FileInfo(_path);
        if (info.Length > 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("优先级恢复记录大小或文件类型异常。");
        var records = JsonSerializer.Deserialize<List<Backup>>(File.ReadAllText(_path)) ?? throw new IOException("优先级恢复记录为空。");
        if (records.Any(b => b is null || b.Identity is null || !IsDelta(b.Identity) || !Valid(b.Property, b.Original) || !Valid(b.Property, b.Applied))
            || records.Select(b => (b.Identity, b.Property)).Distinct().Count() != records.Count)
            throw new IOException("优先级恢复记录包含无效或重复项。");
        _known = records; return records.ToList();
    }
    private void Save(List<Backup> records)
    {
        BeforeSaveForTests?.Invoke();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, records); stream.Flush(true); }
            File.Move(temp, _path, true); _known = records.ToList();
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal int PendingCount
    {
        get { lock (_gate) { try { return Load().Count; } catch { return Math.Max(1, _known.Count); } } }
    }
    private static string Key(ProcmanProcessIdentity id, string property, int value) => $"{id.Pid}:{id.StartTimeUtcTicks}:{id.ExecutablePath}:{property}:{value}";
    internal bool Sync(IReadOnlyList<ProcmanProcessIdentity> wanted, FrameGamePriorityConfig config, Func<ProcmanProcessIdentity, bool> allowed,
        string idle = "未启用，已恢复本功能保存的原值", Func<ProcmanProcessIdentity, string, bool>? propertyAllowed = null)
    {
        lock (_gate)
        {
            using var mutex = new Mutex(false, _mutexName);
            var entered = false;
            try
            {
                try { entered = mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { entered = true; }
                if (!entered) throw new IOException("优先级原值记录交接超时。");
                var records = Load();
                if (wanted.Any(id => !IsDelta(id))) throw new IOException("只允许操作经核验的三角洲主进程，未操作其他进程。");
                if (!config.IsValid && wanted.Count > 0) throw new IOException("优先级档位无效，未修改进程。");
                var targets = wanted.Distinct().ToArray();
                var failures = new List<string>();
                var counts = new Dictionary<string, int> { ["priority"] = 0, ["ioPriority"] = 0, ["memoryPriority"] = 0 };
                // Finish restoration before admitting a different process/configuration.
                foreach (var saved in records.Where(b => !targets.Contains(b.Identity)).ToArray())
                {
                    try
                    {
                        using var lease = _ownership.Enter(saved.Identity, saved.Property);
                        var state = _api.IdentityState(saved.Identity);
                        if (state == "missing") { lease.Game(true); var next = records.Where(b => b != saved).ToList(); Save(next); records = next; continue; }
                        if (state != "same") throw new IOException("原进程身份无法确认，保留原值记录");
                        using var handle = _api.Open(saved.Identity);
                        var current = handle.Read(saved.Property);
                        if (current != saved.Original)
                        {
                            if (lease.Owner is not ("system" or "game" or "frame-priority")) throw new IOException("进程管理手动/规则设置优先，请先解除该属性的接管");
                            if (current != saved.Applied) throw new IOException("当前值与本功能写入值不符，未覆盖外部变更");
                            handle.Write(saved.Property, saved.Original);
                            if (handle.Read(saved.Property) != saved.Original) throw new IOException("原值恢复回读不符");
                        }
                        lease.Game(true); var restored = records.Where(b => b != saved).ToList(); Save(restored); records = restored;
                    }
                    catch (Exception ex) { failures.Add($"PID {saved.Identity.Pid} {saved.Property} 恢复失败：{ex.Message}"); }
                }
                var restorePending = records.Any(b => !targets.Contains(b.Identity));
                if (!restorePending)
                foreach (var identity in targets)
                foreach (var property in Properties)
                {
                    var desired = property == "priority" ? (int)config.Cpu : property == "ioPriority" ? (int)config.Io : (int)config.Memory;
                    var key = Key(identity, property, desired);
                    try
                    {
                        using var lease = _ownership.Enter(identity, property);
                        if (lease.Owner is not ("system" or "frame-priority")) throw new IOException("该属性已由其他自动功能或进程管理手动/规则设置接管，未覆盖");
                        if (propertyAllowed?.Invoke(identity, property) == false) throw new IOException("该属性已有启用的自动设置或进程规则，帧格未覆盖");
                        using var handle = _api.Open(identity);
                        var current = handle.Read(property);
                        if (!Valid(property, current)) throw new IOException("当前优先级不在可恢复范围，未修改");
                        if (!allowed(identity)) throw new IOException("帧格、三角洲状态或恢复门禁已变化，未写入");
                        var saved = records.FirstOrDefault(b => b.Identity == identity && b.Property == property);
                        if (_blocked.TryGetValue(key, out var blocked)) throw new IOException(blocked + "（本次进程不再重复写入；关闭并重新开启功能后可重试）");
                        if (saved is null || saved.Applied != desired)
                        {
                            var next = new Backup(identity, property, saved?.Original ?? current, desired);
                            var updated = records.Where(b => b != saved).Append(next).ToList();
                            Save(updated); records = updated; // durable before any native write
                        }
                        if (current != desired)
                        {
                            if (!allowed(identity)) throw new IOException("提交前执行条件已失效，未写入");
                            try
                            {
                                handle.Write(property, desired);
                                if (handle.Read(property) != desired) throw new IOException("请求档位与实际回读不符");
                            }
                            catch (Exception ex) { _blocked[key] = ex.Message; throw; }
                        }
                        lease.Game(false, "frame-priority"); counts[property]++;
                    }
                    catch (Exception ex) { failures.Add($"PID {identity.Pid} {property} 未生效：{ex.Message}"); }
                }
                if (targets.Length == 0) _blocked.Clear();
                var message = targets.Length == 0 ? (records.Count == 0 ? idle : "优先级恢复尚未完成，已保留原值记录。") : restorePending ? "旧优先级恢复未完成，未开始新的锁定。"
                    : $"CPU {FrameGamePriorityConfig.CpuLabel(config.Cpu)} {counts["priority"]}/{targets.Length}；IO {FrameGamePriorityConfig.IoLabel(config.Io)} {counts["ioPriority"]}/{targets.Length}；内存 {FrameGamePriorityConfig.MemoryLabel(config.Memory)} {counts["memoryPriority"]}/{targets.Length}（回读确认）";
                if (failures.Count > 0) message = string.Join("；", failures) + "；" + message;
                Volatile.Write(ref _snapshot, new() { Maintaining = targets.Length > 0 && !restorePending,
                    HasError = failures.Count > 0, RestorePending = restorePending, BackupCount = records.Count, InstanceCount = targets.Length,
                    CpuAppliedCount = counts["priority"], IoAppliedCount = counts["ioPriority"], MemoryAppliedCount = counts["memoryPriority"],
                    Message = message, UpdatedUtc = DateTimeOffset.UtcNow });
                return failures.Count == 0;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _snapshot, new() { HasError = true, RestorePending = true, BackupCount = Math.Max(1, _known.Count),
                    Message = "三角洲优先级未完成：" + ex.Message, UpdatedUtc = DateTimeOffset.UtcNow });
                return false;
            }
            finally { if (entered) mutex.ReleaseMutex(); }
        }
    }
}

internal sealed class NativeFramePriorityApi : IFramePriorityApi
{
    public string IdentityState(ProcmanProcessIdentity identity) => ProcmanIdentity.State(identity);
    public IFramePriorityHandle Open(ProcmanProcessIdentity identity) => new Handle(identity, ProcmanCapabilities.Pin(identity));
    private sealed class Handle(ProcmanProcessIdentity identity, IDisposable pin) : IFramePriorityHandle
    {
        public int Read(string property)
        {
            var result = property switch { "priority" => ProcmanCapabilities.ReadPriority(identity.Pid),
                "ioPriority" => ProcmanCapabilities.ReadIoPriority(identity.Pid), "memoryPriority" => ProcmanCapabilities.ReadMemoryPriority(identity.Pid),
                _ => throw new IOException("未知优先级属性") };
            if (!result.Ok) throw new IOException(result.Error);
            if (property == "priority" && Enum.TryParse<ProcessPriorityClass>(result.Value, out var cpu)) return (int)cpu;
            if (property == "ioPriority") return ProcmanCapabilities.IoPriorityFromName(result.Value ?? "");
            if (int.TryParse(result.Value, out var value)) return value;
            throw new IOException("优先级查询值无法解析");
        }
        public void Write(string property, int value)
        {
            using var privilege = property == "priority" && value == (int)FrameCpuPriority.RealTime || property == "ioPriority" && value == 3
                ? FramePriorityPrivilege.Acquire() : null;
            var result = property switch { "priority" => ProcmanCapabilities.SetPriority(identity.Pid, (ProcessPriorityClass)value),
                "ioPriority" => ProcmanCapabilities.SetIoPriority(identity.Pid, value), "memoryPriority" => ProcmanCapabilities.SetMemoryPriority(identity.Pid, value),
                _ => throw new IOException("未知优先级属性") };
            if (!result.Ok) throw new IOException(result.Error);
        }
        public void Dispose() => pin.Dispose();
    }
}

/// <summary>Enable only the already-assigned backend token privilege, then restore its previous state.</summary>
internal sealed class FramePriorityPrivilege : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct State { public uint Count, Low; public int High; public uint Attributes; }
    private readonly SafeAccessTokenHandle _token;
    private readonly State _previous;
    private FramePriorityPrivilege(SafeAccessTokenHandle token, State previous) { _token = token; _previous = previous; }
    internal static FramePriorityPrivilege Acquire()
    {
        if (!OpenProcessToken(new IntPtr(-1), 0x28, out var token)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法读取后端调度特权");
        try
        {
            if (!LookupPrivilegeValue(null, "SeIncreaseBasePriorityPrivilege", out var luid)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var next = new State { Count = 1, Low = luid.Low, High = luid.High, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref next, (uint)Marshal.SizeOf<State>(), out var previous, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (Marshal.GetLastWin32Error() == 1300) throw new UnauthorizedAccessException("后端未持有提高调度优先级特权，请以管理员运行；未绕过系统保护。");
            return new(token, previous);
        }
        catch { token.Dispose(); throw; }
    }
    public void Dispose()
    {
        try
        {
            if (_previous.Count == 0) return;
            var original = _previous;
            if (!AdjustTokenPrivileges(_token, false, ref original, (uint)Marshal.SizeOf<State>(), out _, out _) || Marshal.GetLastWin32Error() == 1300)
                throw new IOException("后端临时调度特权恢复未确认。");
        }
        finally { _token.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid value);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref State value,
        uint length, out State previous, out uint returned);
}
