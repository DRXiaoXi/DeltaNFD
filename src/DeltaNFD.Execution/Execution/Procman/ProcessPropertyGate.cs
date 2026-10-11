using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

/// <summary>Cross-process, per-identity/property ownership. The mutex is held through the actual native write.</summary>
public sealed class ProcessPropertyGate
{
    internal sealed class Claim
    {
        public Claim() { }
        public int GamePid { get; set; }
        public long GameStart { get; set; }
        public string? Override { get; set; }
    }
    private readonly string _path;
    private readonly string _mutexName;
    public ProcessPropertyGate(string path)
    {
        _path = path;
        _mutexName = @"Global\DeltaNFD.ProcessProperties." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
    }
    internal static ProcessPropertyGate Default => new(Path.Combine(AppDataPaths.Root, "procman-owners.json"));
    public sealed class Lease : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly ProcessPropertyGate _store;
        private readonly Dictionary<string, Claim> _claims;
        private readonly string _key;
        private bool _disposed;
        internal Lease(Mutex mutex, ProcessPropertyGate store, Dictionary<string, Claim> claims, string key)
        { _mutex = mutex; _store = store; _claims = claims; _key = key; }
        public string Owner => _claims.TryGetValue(_key, out var claim)
            ? claim.Override ?? (GameAlive(claim) ? "game" : "system") : "system";
        public void SetOwner(string? owner)
        {
            if (!_claims.TryGetValue(_key, out var claim)) _claims[_key] = claim = new();
            claim.Override = owner;
            if (owner is null && !GameAlive(claim)) _claims.Remove(_key);
            _store.Save(_claims);
        }
        internal void Game(bool restore)
        {
            if (!_claims.TryGetValue(_key, out var claim)) _claims[_key] = claim = new();
            if (restore)
            {
                claim.GamePid = 0; claim.GameStart = 0;
                if (claim.Override is null) _claims.Remove(_key);
            }
            else
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                if (claim.GamePid == process.Id && claim.GameStart == process.StartTime.ToUniversalTime().Ticks) return;
                claim.GamePid = process.Id; claim.GameStart = process.StartTime.ToUniversalTime().Ticks;
            }
            _store.Save(_claims);
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            try { _mutex.ReleaseMutex(); } finally { _mutex.Dispose(); }
        }
    }
    private static bool GameAlive(Claim claim)
    {
        if (claim.GamePid == 0) return false;
        try { using var process = System.Diagnostics.Process.GetProcessById(claim.GamePid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == claim.GameStart; }
        catch (ArgumentException) { return false; }
        catch { return true; } // unknown is still owned
    }
    public Lease Enter(ProcmanProcessIdentity identity, string property)
    {
        var mutex = new Mutex(false, _mutexName);
        bool entered;
        try { entered = mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { entered = true; }
        if (!entered) { mutex.Dispose(); throw new IOException("进程属性交接锁超时。"); }
        try
        {
            var claims = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, Claim>>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("所有权文件为空。") : new();
            return new Lease(mutex, this, claims, ProcmanChangeLog.ChangeKey(identity.Pid, identity.StartTimeUtcTicks, identity.ExecutablePath, property));
        }
        catch { mutex.ReleaseMutex(); mutex.Dispose(); throw; }
    }
    private void Save(Dictionary<string, Claim> claims)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, claims); stream.Flush(true); }
        File.Move(temp, _path, true);
    }
    /// <summary>Game/CPU/offline writers must acquire this before reading backups or writing. A manual override pauses this property only.</summary>
    public static IDisposable? TryEnterGame(int pid, string property, bool restoring = false)
    {
        var identity = ProcmanIdentity.TryIdentify(pid);
        if (identity is null) return null;
        Lease? lease = null;
        try
        {
            lease = Default.Enter(identity, property);
            if (lease.Owner is not ("system" or "game")) { lease.Dispose(); return null; }
            lease.Game(restoring);
            return lease;
        }
        catch { lease?.Dispose(); return null; } // store/lock failure never permits a write
    }
}
