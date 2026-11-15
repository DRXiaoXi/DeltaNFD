using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.Principal;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.OptimalClock;

internal sealed class NativeDpcSessionApi(Action<string, object?>? diagnostics = null) : IDpcSessionApi
{
    public string BootId => WindowsBootIdentity.Read() ?? throw new IOException("启动身份未确认。");
    public int ControllerPid => Environment.ProcessId;
    public long ControllerStartTicks { get; } = ReadControllerStartTicks();
    private static long ReadControllerStartTicks() { using var process = Process.GetCurrentProcess(); return process.StartTime.ToUniversalTime().Ticks; }
    public void CheckStartAllowed()
    {
        if (!PendulumGlobalInspection.ExperimentalWindows10 || !Environment.Is64BitProcess) throw new IOException("仅在 Win10 1809+ x64 使用自备 DPC 路径。");
        if (!ElevationHelper.IsElevated) throw new IOException(ElevationHelper.NotElevatedMessage);
        if (OfflineModeGuard.BlocksNormalAutomation) throw new IOException("恢复模式中不能启动 DPC。");
        if (DeltaForceLocator.IsGameRunning()) throw new IOException("请先退出三角洲再启动 DPC；系统/反作弊兼容性须在玩家环境验证。");
        var source = DpcPendulumInspection.Read();
        if (!source.FileVerified) throw new IOException(source.Message);
    }
    public IDisposable LockInput(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("无法确认DPC会话用户。");
        var gate = new Semaphore(1, 1, @"Global\DeltaNFD.PendulumDpc.v1." + sid);
        if (!gate.WaitOne(0)) { gate.Dispose(); throw new IOException("另一钟摆会话正在运行DPC；未重复启动。"); }
        try { return new InputLease(DpcPendulumInspection.OpenVerified(path), gate); }
        catch { gate.Release(); gate.Dispose(); throw; }
    }
    private sealed class InputLease(IDisposable file, Semaphore gate) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            file.Dispose(); gate.Release(); gate.Dispose(); }
    }
    public DpcPendulumJournal? Read() => DpcPendulumInspection.ReadJournal();
    public void Save(DpcPendulumJournal record)
    {
        DpcPendulumInspection.ValidateJournal(record);
        DpcPendulumInspection.NoLinks(DpcPendulumInspection.JournalPath);
        Directory.CreateDirectory(DpcPendulumInspection.DataDirectory);
        DpcPendulumInspection.NoLinks(DpcPendulumInspection.DataDirectory);
        var temp = Path.Combine(DpcPendulumInspection.DataDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, record); stream.Flush(true); }
        File.Move(temp, DpcPendulumInspection.JournalPath, true);
        var actual = Read();
        if (actual is null || JsonSerializer.Serialize(actual) != JsonSerializer.Serialize(record)) throw new IOException("DPC 常驻记录写入回读失败。");
    }
    public void Archive(DpcPendulumJournal record)
    {
        var current = Read();
        if (current is null || JsonSerializer.Serialize(current) != JsonSerializer.Serialize(record)) throw new IOException("DPC 常驻记录已变化，未清除。");
        File.Move(DpcPendulumInspection.JournalPath, Path.Combine(DpcPendulumInspection.DataDirectory, "released-" + record.Id + "-" + Guid.NewGuid().ToString("N") + ".json"));
    }
    public IDpcOwnedProcess Launch(string path)
    {
        // Explicit EXE only, no command line, shell, extraction, downloader or driver API.
        var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)!,
            CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Minimized }) ?? throw new IOException("未能启动 DPC。");
        try { return new Owned(process, path, process.StartTime.ToUniversalTime().Ticks, diagnostics); }
        catch { try { process.CloseMainWindow(); } catch { } process.Dispose(); throw; }
    }
    public IDpcOwnedProcess? Find(int pid, long ticks, string path)
    {
        Process process;
        try { process = Process.GetProcessById(pid); } catch (ArgumentException) { return null; }
        var owned = new Owned(process, path, ticks, diagnostics);
        try
        {
            if (!owned.Alive) { owned.Dispose(); return null; }
            // The per-user journal is not authority to close an arbitrary elevated program.
            // Recovery also requires the pinned original; keep it locked through normal close.
            return new Recovered(owned, DpcPendulumInspection.OpenVerified(path));
        }
        catch { owned.Dispose(); throw; }
    }
    public bool ControllerAlive(int pid, long ticks)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks; }
        catch (ArgumentException) { return false; }
    }
    public DpcServiceObservation Service()
    {
        var scm = OpenSCManager(null, null, 1);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(scm, DpcPendulumSource.ServiceName, 5); // QUERY_CONFIG | QUERY_STATUS only
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 1060) return new(false, false, false, "");
                throw new Win32Exception(error);
            }
            try
            {
                if (!QueryServiceStatus(service, out var state)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var buffer = Marshal.AllocHGlobal(8192);
                try
                {
                    if (!QueryServiceConfig(service, buffer, 8192, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
                    var rawPath = Marshal.PtrToStringUni(config.Path) ?? "";
                    var path = NormalizeServicePath(rawPath);
                    bool verified = false;
                    long? bytes = null;
                    var hash = "";
                    var detail = config.Type != 1 || config.Start != 3
                        ? $"服务类型/启动类型不匹配（{config.Type}/{config.Start}，期望1/3）"
                        : path.Length == 0 ? "驱动映像路径不符合已知DPC驱动路径规则" : "";
                    if (config.Type == 1 && config.Start == 3 && path.Length > 0)
                    {
                        try
                        {
                            DpcPendulumInspection.NoLinks(path);
                            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            var identity = ReadDriverIdentity(file);
                            bytes = identity.Bytes; hash = identity.Hash; verified = identity.Verified;
                            detail = verified ? "原版驱动字节数及SHA256匹配" : bytes != 21232
                                ? $"驱动原件长度不匹配（{bytes}字节，期望21232字节；未计算SHA256）"
                                : $"驱动原件SHA256不匹配（{hash}）";
                        }
                        catch (IOException ex) { detail = $"驱动原件读取失败（0x{ex.HResult:X8}）：{ex.Message}"; }
                        catch (UnauthorizedAccessException ex) { detail = $"驱动原件读取被拒绝（0x{ex.HResult:X8}）：{ex.Message}"; }
                    }
                    return new(true, state.Current == 4, verified, path) { State = state.Current, Type = config.Type,
                        Start = config.Start, RawImagePath = rawPath, DriverBytes = bytes, DriverSha256 = hash, Detail = detail };
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
    }
    internal static (long Bytes, string Hash, bool Verified) ReadDriverIdentity(Stream file)
    {
        var bytes = file.Length;
        var hash = bytes == 21232 ? Convert.ToHexString(SHA256.HashData(file)) : "";
        return (bytes, hash, bytes == 21232 && hash.Equals(DpcPendulumSource.DriverSha256, StringComparison.OrdinalIgnoreCase));
    }
    internal static string NormalizeServicePath(string raw)
    {
        if (raw.StartsWith(@"\??\", StringComparison.Ordinal)) raw = raw[4..];
        if (raw.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) raw = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), raw[12..]);
        if (raw.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)) raw = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), raw);
        var name = Path.GetFileName(raw);
        if (raw.Length < 4 || !char.IsAsciiLetter(raw[0]) || raw[1] != ':' || raw[2] != '\\' || raw.Contains('"') || raw.IndexOf(':', 2) >= 0
            || !(name.Equals("dpclat_driver.sys", StringComparison.OrdinalIgnoreCase)
                || name.Equals("dpclat.sys", StringComparison.OrdinalIgnoreCase))) return "";
        return Path.GetFullPath(raw);
    }
    public uint Current() => OptimalClockNative.QueryCurrent100ns();
    public void Delay(int milliseconds) => Thread.Sleep(milliseconds);

    private sealed class Recovered(IDpcOwnedProcess process, IDisposable source) : IDpcOwnedProcess
    {
        public int Pid => process.Pid;
        public long StartTicks => process.StartTicks;
        public bool Alive => process.Alive;
        public bool CloseNormally(int timeoutMs) => process.CloseNormally(timeoutMs);
        public void Dispose() { process.Dispose(); source.Dispose(); }
    }

    private sealed class Owned(Process process, string expectedPath, long expectedTicks,
        Action<string, object?>? diagnostics) : IDpcOwnedProcess
    {
        private readonly object _windowGate = new();
        private Timer? _windowTimer;
        private DpcWindowPolicy? _windows;
        private bool _closing;
        private string _windowError = "";
        private DpcWindowPolicy Windows => _windows ??= new(new NativeDpcWindowApi((uint)Pid), (uint)Pid, () => Alive);
        public int Pid => process.Id;
        public long StartTicks => expectedTicks;
        public bool Alive
        {
            get
            {
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != expectedTicks) return false;
                var image = new StringBuilder(32768); uint length = (uint)image.Capacity;
                if (!QueryFullProcessImageName(process.Handle, 0, image, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!string.Equals(Path.GetFullPath(image.ToString()), expectedPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("DPC进程映像与记录原件不同，未关闭未知程序；请核对启动重定向或其他软件干预。");
                return true;
            }
        }
        public bool CloseNormally(int timeoutMs)
        {
            lock (_windowGate)
            {
                _closing = true; _windowTimer?.Dispose(); _windowTimer = null;
                if (!Alive) return true;
                if (!Windows.CloseNormally()) process.CloseMainWindow();
            }
            var exited = process.WaitForExit(timeoutMs) && process.HasExited;
            if (!exited) { lock (_windowGate) { if (Alive) Windows.Reveal(); } }
            return exited;
        }
        public void BeginWindowMaintenance()
        {
            lock (_windowGate)
            {
                if (_closing || _windowTimer is not null) return;
                MaintainWindow(null);
                _windowTimer = new Timer(MaintainWindow, null, 1000, 1000);
            }
        }
        private void MaintainWindow(object? state)
        {
            lock (_windowGate)
            {
                if (_closing) return;
                try
                {
                    if (!Alive) return;
                    Windows.Maintain(AppSettingsStore.ReadRequired().OptimalClockDpcWindowHidden);
                    _windowError = "";
                }
                catch (Exception ex)
                {
                    if (_windowError != ex.Message)
                        PendulumDiagnosticLog.Emit(diagnostics, "dpc-window-maintenance-error", new { Pid, error = ex.ToString() });
                    _windowError = ex.Message;
                }
            }
        }
        public void Dispose()
        {
            lock (_windowGate) { _closing = true; _windowTimer?.Dispose(); _windowTimer = null; process.Dispose(); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceState { public uint Type, Current, Controls, Exit, Specific, Checkpoint, Hint; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig { public uint Type, Start, Error; public IntPtr Path, Group; public uint Tag; public IntPtr Dependencies, Account, Display; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceState state);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(IntPtr service, IntPtr buffer, uint size, out uint needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
}
