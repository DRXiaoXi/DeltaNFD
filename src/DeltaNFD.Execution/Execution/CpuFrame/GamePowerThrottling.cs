using System.Runtime.InteropServices;
using System.Text.Json;
using DeltaNFD.Execution.Procman;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Execution.CpuFrame;

internal readonly record struct GamePowerState(uint Control, uint State)
{
    internal GamePowerState Disable() => new(Control | 1u, State & ~1u);
    internal GamePowerState Restore(GamePowerState original) =>
        new((Control & ~1u) | (original.Control & 1u), (State & ~1u) | (original.State & 1u));
    internal bool SameExecution(GamePowerState other) => ((Control ^ other.Control) & 1u) == 0 && ((State ^ other.State) & 1u) == 0;
}

internal interface IGamePowerHandle : IDisposable
{
    GamePowerState Read();
    void Write(GamePowerState state);
}

internal interface IGamePowerApi
{
    string IdentityState(ProcmanProcessIdentity identity);
    IGamePowerHandle Open(ProcmanProcessIdentity identity);
}

/// <summary>只控制 EXECUTION_SPEED 位；先落盘原值，再写入，复用进程管理的属性归属锁。</summary>
internal sealed class GamePowerThrottling
{
    public sealed record Backup(ProcmanProcessIdentity Identity, GamePowerState Original);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _mutexName;
    private readonly ProcessPropertyGate _ownership;
    private readonly IGamePowerApi _api;
    private List<Backup>? _backups;
    internal int AppliedCount { get; private set; }
    internal bool HasError { get; private set; }
    internal string Status { get; private set; } = "未启用";
    internal int PendingCount { get { lock (_gate) { try { _backups = null; return Load().Count; } catch { return 1; } } } }

    internal GamePowerThrottling(string path, ProcessPropertyGate ownership, IGamePowerApi? api = null)
    {
        _path = path; _ownership = ownership; _api = api ?? new NativeApi();
        _mutexName = @"Global\DeltaNFD.FrameGamePower." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
    }

    private List<Backup> Load() => _backups ??= File.Exists(_path)
        ? JsonSerializer.Deserialize<List<Backup>>(File.ReadAllText(_path)) ?? throw new IOException("游戏节能恢复记录为空。")
        : [];

    private void Save(List<Backup> next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, next); stream.Flush(true); }
            File.Move(temp, _path, true);
            _backups = next;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal bool Sync(IReadOnlyList<ProcmanProcessIdentity> wanted)
    {
        lock (_gate)
        {
            using var mutex = new Mutex(false, _mutexName);
            var entered = false;
            try
            {
                try { entered = mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { entered = true; }
                if (!entered) throw new IOException("游戏节能原值记录交接超时，未修改进程。");
                _backups = null; // 另一后端可能刚完成交接，不能用旧缓存覆盖其原值。
                return SyncCore(wanted);
            }
            catch (Exception ex) { HasError = true; Status = "游戏节能交接失败：" + ex.Message; return false; }
            finally { if (entered) mutex.ReleaseMutex(); }
        }
    }

    private bool SyncCore(IReadOnlyList<ProcmanProcessIdentity> wanted)
    {
            AppliedCount = 0;
            var failures = new List<string>();
            List<Backup> backups;
            try { backups = Load().ToList(); }
            catch (Exception ex) { HasError = true; Status = "恢复记录不可读，未修改：" + ex.Message; return false; }
            foreach (var backup in backups.Where(b => !wanted.Contains(b.Identity)))
            {
                try
                {
                    using var lease = _ownership.Enter(backup.Identity, "ecoQos");
                    var identityState = _api.IdentityState(backup.Identity);
                    if (identityState == "missing") { Save(Load().Where(b => b != backup).ToList()); lease.Game(true); continue; }
                    if (identityState != "same") throw new IOException("原进程身份无法确认，保留恢复记录");
                    if (lease.Owner is not ("system" or "game")) throw new IOException("进程管理已接管，请先恢复其手动/规则设置");
                    using var handle = _api.Open(backup.Identity);
                    var current = handle.Read();
                    // 外部程序已改变此位时交还控制，不能覆盖其后来的选择。
                    if (current.SameExecution(backup.Original.Disable()))
                    {
                        var restored = current.Restore(backup.Original);
                        handle.Write(restored);
                        if (!handle.Read().SameExecution(restored)) throw new IOException("恢复回读不符");
                    }
                    lease.Game(true);
                    Save(Load().Where(b => b != backup).ToList());
                }
                catch (Exception ex) { failures.Add($"PID {backup.Identity.Pid}：{ex.Message}"); }
            }
            foreach (var identity in wanted.Distinct())
            {
                try
                {
                    using var lease = _ownership.Enter(identity, "ecoQos");
                    if (lease.Owner is not ("system" or "game")) throw new IOException("由进程管理手动/规则设置接管，未覆盖");
                    using var handle = _api.Open(identity); // 同一个句柄贯穿身份核对、读取、写入及回读。
                    var current = handle.Read();
                    var backup = Load().FirstOrDefault(b => b.Identity == identity);
                    if (backup is null)
                    {
                        backup = new Backup(identity, current);
                        Save([.. Load(), backup]);
                    }
                    lease.Game(false);
                    var desired = current.Disable();
                    if (current != desired) handle.Write(desired);
                    if (!handle.Read().SameExecution(desired)) throw new IOException("生效回读不符");
                    AppliedCount++;
                }
                catch (Exception ex) { failures.Add($"PID {identity.Pid}：{ex.Message}"); }
            }
            HasError = failures.Count > 0;
            Status = failures.Count > 0 ? "未全部完成：" + string.Join("；", failures)
                : wanted.Count > 0 ? $"已生效 · {AppliedCount} 个游戏进程" : "已交还游戏节能控制";
            return failures.Count == 0;
    }

    private sealed class NativeApi : IGamePowerApi
    {
        public string IdentityState(ProcmanProcessIdentity identity) => ProcmanIdentity.State(identity);
        public IGamePowerHandle Open(ProcmanProcessIdentity identity)
        {
            var handle = OpenProcess(0x1200, false, identity.Pid);
            if (handle.IsInvalid || !ProcmanIdentity.Matches(handle, identity))
            { handle.Dispose(); throw new IOException("进程已退出、身份变化或权限不足，未写入"); }
            return new NativeHandle(handle);
        }
    }
    private sealed class NativeHandle(SafeProcessHandle handle) : IGamePowerHandle
    {
        public GamePowerState Read()
        {
            var value = new PowerState { Version = 1 };
            if (!GetProcessInformation(handle, 4, ref value, 12)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return new(value.Control, value.State);
        }
        public void Write(GamePowerState state)
        {
            var value = new PowerState { Version = 1, Control = state.Control, State = state.State };
            if (!SetProcessInformation(handle, 4, ref value, 12)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() => handle.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct PowerState { public uint Version, Control, State; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessInformation(SafeProcessHandle handle, int kind, ref PowerState state, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(SafeProcessHandle handle, int kind, ref PowerState state, int size);
}
