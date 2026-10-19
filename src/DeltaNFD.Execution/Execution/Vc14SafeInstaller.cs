using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace DeltaNFD.Services;

internal sealed record SafeInstallerEntry(bool Exists, bool HasDefault, string? Default, int Kind, string OtherHash, int Subkeys, string? Owner);
internal sealed record SafeInstallerState(int Mode, bool Elevated, bool ServiceExists, bool Disabled, uint ServiceState, SafeInstallerEntry Entry,
    int ServicePid = 0, long ServiceStartTicks = 0);
internal sealed record SafeInstallerJournal(string Id, int Mode, SafeInstallerEntry Before, bool WasRunning,
    int OwnerPid, long OwnerStartTicks, bool WriteIntent = false, bool StartIntent = false, int ServicePid = 0, long ServiceStartTicks = 0);
internal interface ISafeInstallerPlatform
{
    SafeInstallerState Capture(int? mode = null);
    bool Busy();
    void Write(int mode, SafeInstallerEntry expected, string id);
    void Start();
    void Stop(int mode, int expectedPid, long expectedStartTicks);
    void Restore(int mode, SafeInstallerEntry before, string id);
}
internal interface ISafeInstallerStore
{
    SafeInstallerJournal? Load();
    void Save(SafeInstallerJournal journal);
    void Archive(SafeInstallerJournal journal);
}

/// <summary>Fixed MSIServer leaf only, durable intent before mutations, exact ownership on recovery.</summary>
internal sealed class SafeInstallerEngine(ISafeInstallerPlatform platform, ISafeInstallerStore store)
{
    private static OperationResult Pending(string message) => new() { Success = false, Message = message, OutcomeUnknown = true };
    private static string Fingerprint(SafeInstallerState state) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state)));
    internal SafeInstallerPreparation Preview()
    {
        try
        {
            if (store.Load() is not null) return new(true, false, "", "上次临时 Installer 设置仍待恢复，请先点击“恢复临时 Installer 设置”。");
            var state = platform.Capture();
            if (state.Mode is not (1 or 2) || state.ServiceState == 4) return new(false, false, "", "当前无需临时启用 Installer。");
            var reason = !state.Elevated ? "需要管理员权限。" : !state.ServiceExists ? "Windows Installer 服务不存在或不可查询。"
                : state.Disabled ? "服务被禁用，请在正常模式核对；不会修改服务启动类型。"
                : state.ServiceState != 1 ? "Installer 服务正处于过渡状态，请稍后重试。"
                : state.Entry.Owner is not null ? "SafeBoot 项带有未知临时归属，保留并拒绝覆盖。"
                : state.Entry.HasDefault && (state.Entry.Kind != (int)RegistryValueKind.String || !string.Equals(state.Entry.Default, "Service", StringComparison.OrdinalIgnoreCase))
                    ? "现有 SafeBoot 默认值不是预期的 Service，保留并拒绝覆盖。"
                : platform.Busy() ? "安装事务或安装器状态未确认，不能临时修改服务。" : "";
            return new(true, reason.Length == 0, Fingerprint(state), reason.Length > 0 ? reason
                : "将备份当前值，仅临时设置 SafeBoot\\" + (state.Mode == 1 ? "Minimal" : "Network")
                    + "\\MSIServer 的服务准入并尝试启动 Windows Installer。卸载结束后恢复；服务仍忙或发生中断时保留恢复记录。不会更改 BCD 或服务启动类型。");
        }
        catch (Exception ex) { return new(true, false, "", "Installer 准备状态无法核对：" + ex.Message); }
    }

    internal void Enable(string fingerprint, bool confirmed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!confirmed) throw new IOException("未确认临时启用 Installer，未修改服务。");
        var preview = Preview();
        if (!preview.Required || !preview.CanEnable || preview.Fingerprint != fingerprint)
            throw new IOException("Installer 准备状态已变化，请重新预览确认。" + preview.Detail);
        var state = platform.Capture();
        if (Fingerprint(state) != fingerprint) throw new IOException("Installer 原值已变化，未修改服务。");
        using var owner = Process.GetCurrentProcess();
        var journal = new SafeInstallerJournal(Guid.NewGuid().ToString("D"), state.Mode, state.Entry, false,
            owner.Id, owner.StartTime.ToUniversalTime().Ticks, WriteIntent: true);
        store.Save(journal); // If durable backup fails, do not mutate the registry.
        token.ThrowIfCancellationRequested();
        if (platform.Capture() != state || platform.Busy()) throw new IOException("备份后 Installer 状态发生变化，未写入服务准入。");
        platform.Write(journal.Mode, journal.Before, journal.Id);
        journal = journal with { StartIntent = true }; store.Save(journal);
        token.ThrowIfCancellationRequested();
        platform.Start();
        var started = platform.Capture(journal.Mode);
        if (started.ServiceState != 4 || started.ServicePid <= 0 || started.ServiceStartTicks <= 0)
            throw new IOException("Windows Installer 未进入已确认的运行状态，未启动卸载。");
        store.Save(journal with { ServicePid = started.ServicePid, ServiceStartTicks = started.ServiceStartTicks });
    }

    internal OperationResult Restore()
    {
        try
        {
            var journal = store.Load();
            if (journal is null) return OperationResult.Ok("无待恢复的临时 Installer 设置。");
            Validate(journal);
            if (platform.Busy()) return Pending("安装事务或安装器身份仍未确认，未停止服务；临时 Installer 恢复记录保留。");
            var current = platform.Capture(journal.Mode);
            var ours = current.Entry.Owner == journal.Id && (!current.Entry.HasDefault
                || current.Entry.Kind == (int)RegistryValueKind.String && string.Equals(current.Entry.Default, "Service", StringComparison.OrdinalIgnoreCase));
            var emptyCreate = !journal.Before.Exists && current.Entry.Exists && current.Entry.Owner is null && !current.Entry.HasDefault;
            if (current.Entry != journal.Before && ((!ours && !emptyCreate)
                || current.Entry.OtherHash != journal.Before.OtherHash || current.Entry.Subkeys != journal.Before.Subkeys))
                return Pending("SafeBoot 项已发生外部变化，未停止服务或覆盖原值；恢复记录保留。");
            if (!journal.WasRunning && journal.StartIntent && current.ServiceState != 1)
            {
                if (current.ServiceState != 4) return Pending("Installer 服务处于过渡状态，恢复记录保留，请稍后重试。");
                if (journal.ServicePid <= 0 || journal.ServicePid != current.ServicePid || journal.ServiceStartTicks != current.ServiceStartTicks)
                    return Pending("Installer 运行身份不是已记录的临时实例，未停止服务。请等待服务退出后再恢复；记录保留。");
                platform.Stop(journal.Mode, journal.ServicePid, journal.ServiceStartTicks); // Graceful service control, no process termination.
            }
            if (!journal.WasRunning && platform.Capture(journal.Mode).ServiceState != 1)
                return Pending("Installer 停止尚未确认，恢复记录保留。");
            platform.Restore(journal.Mode, journal.Before, journal.Id);
            if (platform.Capture(journal.Mode).Entry != journal.Before)
                return Pending("SafeBoot 原值复检未通过，恢复记录保留。");
            store.Archive(journal);
            return OperationResult.Ok("已恢复临时 Installer 服务状态与 SafeBoot 原值；没有更改系统启动模式。");
        }
        catch (Exception ex) { return Pending("临时 Installer 恢复未完成，记录保留：" + ex.Message); }
    }

    internal static void Validate(SafeInstallerJournal journal)
    {
        if (!Guid.TryParseExact(journal.Id, "D", out _) || journal.Mode is not (1 or 2) || journal.OwnerPid <= 0 || journal.OwnerStartTicks <= 0
            || !journal.WriteIntent || journal.WasRunning || journal.Before.Owner is not null
            || journal.Before.OtherHash.Length != 64 || journal.Before.Subkeys < 0
            || (journal.ServicePid == 0) != (journal.ServiceStartTicks == 0) || journal.ServicePid < 0 || journal.ServiceStartTicks < 0
            || !journal.Before.HasDefault && (journal.Before.Kind != 0 || journal.Before.Default is not null)
            || !journal.Before.Exists && (journal.Before.HasDefault || journal.Before.Subkeys != 0)
            || journal.Before.HasDefault && (journal.Before.Kind != (int)RegistryValueKind.String
                || !string.Equals(journal.Before.Default, "Service", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("临时 Installer 恢复记录身份无效，禁止推测写入系统。");
    }
}

internal sealed class SafeInstallerFileStore(string root) : ISafeInstallerStore
{
    private string RecordPath => Path.Combine(root, "safe-installer-active.json");
    public SafeInstallerJournal? Load()
    {
        if (Directory.Exists(RecordPath)) throw new IOException("Installer 恢复记录路径被目录占用。");
        if (!File.Exists(RecordPath)) return null;
        if (new FileInfo(RecordPath).Length > 16384) throw new IOException("Installer 恢复记录大小异常。");
        var record = JsonSerializer.Deserialize<SafeInstallerJournal>(File.ReadAllText(RecordPath)) ?? throw new IOException("Installer 恢复记录为空。");
        SafeInstallerEngine.Validate(record); return record;
    }
    public void Save(SafeInstallerJournal journal)
    {
        SafeInstallerEngine.Validate(journal); Directory.CreateDirectory(root);
        var temp = RecordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, journal); file.Flush(true); }
            File.Move(temp, RecordPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Archive(SafeInstallerJournal journal)
    {
        if (Load() != journal) throw new IOException("Installer 恢复记录已变化，未归档。");
        var history = Path.Combine(root, "runtime-repair-history"); Directory.CreateDirectory(history);
        File.Move(RecordPath, Path.Combine(history, journal.Id + "-safe-installer-restored.json"), true);
    }
}

internal sealed class SafeInstallerWindowsPlatform : ISafeInstallerPlatform
{
    internal const string Marker = "DeltaNFDTemporaryInstaller";
    private static string KeyPath(int mode) => @"SYSTEM\CurrentControlSet\Control\SafeBoot\"
        + (mode == 1 ? "Minimal" : mode == 2 ? "Network" : throw new IOException("未知安全模式。")) + @"\MSIServer";
    private static SafeInstallerEntry ReadEntry(int mode)
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath(mode));
        var values = key?.GetValueNames().Where(n => n.Length > 0 && n != Marker).Order(StringComparer.Ordinal)
            .Select(n => new { Name = n, Kind = key!.GetValueKind(n), Value = key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames) }).ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values ?? [])));
        var hasDefault = key?.GetValueNames().Contains("") == true;
        var value = hasDefault ? key!.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
        return new(key is not null, hasDefault, value as string, hasDefault ? (int)key!.GetValueKind("") : 0,
            hash, key?.SubKeyCount ?? 0, key?.GetValue(Marker) as string ?? (key?.GetValueNames().Contains(Marker) == true ? "<unknown>" : null));
    }
    public SafeInstallerState Capture(int? mode = null)
    {
        var actualMode = GetSystemMetrics(67);
        var leafMode = mode ?? (actualMode is 1 or 2 ? actualMode : 1);
        var scm = OpenSCManager(null, null, 1);
        if (scm == IntPtr.Zero) throw new IOException("无法查询服务控制管理器。");
        try
        {
            var handle = OpenService(scm, "msiserver", 4);
            if (handle == IntPtr.Zero) return new(actualMode, ElevationHelper.IsElevated, false, false, 0, ReadEntry(leafMode));
            try
            {
                if (!QueryServiceStatusEx(handle, 0, out var status, Marshal.SizeOf<ProcessStatus>(), out _)) throw new IOException("无法查询 Windows Installer 状态。");
                using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\msiserver");
                var pid = status.State == 4 ? checked((int)status.Pid) : 0;
                long ticks = 0;
                if (pid > 0) { using var process = Process.GetProcessById(pid); ticks = process.StartTime.ToUniversalTime().Ticks; }
                return new(actualMode, ElevationHelper.IsElevated, true, service?.GetValue("Start") is int start && start == 4,
                    status.State, ReadEntry(leafMode), pid, ticks);
            }
            finally { CloseServiceHandle(handle); }
        }
        finally { CloseServiceHandle(scm); }
    }
    public bool Busy()
    {
        var busy = RuntimeInstallationReview.InstallerBusy();
        var pending = new RuntimeInstallationReview(AppDataPaths.Root).InspectPending(busy, out _);
        return busy || pending is not null;
    }
    public void Write(int mode, SafeInstallerEntry expected, string id)
    {
        var state = Capture(mode);
        if (state.Mode != mode || state.ServiceState != 1 || Busy() || state.Entry != expected)
            throw new IOException("安全模式、服务或原值变化，未写入服务准入。");
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath(mode), true);
        key.SetValue(Marker, id, RegistryValueKind.String);
        key.SetValue("", "Service", RegistryValueKind.String); key.Flush();
    }
    public void Restore(int mode, SafeInstallerEntry before, string id)
    {
        var now = ReadEntry(mode);
        if (now == before) return;
        var ours = now.Owner == id && (!now.HasDefault || now.Kind == (int)RegistryValueKind.String
            && string.Equals(now.Default, "Service", StringComparison.OrdinalIgnoreCase));
        var emptyCreate = !before.Exists && now.Exists && now.Owner is null && !now.HasDefault;
        if ((!ours && !emptyCreate) || now.OtherHash != before.OtherHash || now.Subkeys != before.Subkeys)
            throw new IOException("SafeBoot 项发生外部变化或归属不匹配，未覆盖或删除。");
        if (!before.Exists)
        {
            Registry.LocalMachine.DeleteSubKey(KeyPath(mode), false); return; // Leaf only, never recursive.
        }
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath(mode), true) ?? throw new IOException("SafeBoot 项已消失。");
        if (before.HasDefault) key.SetValue("", before.Default!, RegistryValueKind.String);
        else key.DeleteValue("", false);
        key.DeleteValue(Marker, false); key.Flush();
    }
    public void Start()
    {
        if (Busy()) throw new IOException("另一个安装事务已开始，未启动临时 Installer。");
        Control(start: true);
    }
    public void Stop(int mode, int expectedPid, long expectedStartTicks)
    {
        var current = Capture(mode);
        if (Busy() || current.ServicePid != expectedPid || current.ServiceStartTicks != expectedStartTicks)
            throw new IOException("Installer 事务或服务身份发生变化，未停止服务。");
        Control(start: false);
    }
    private static void Control(bool start)
    {
        var scm = OpenSCManager(null, null, 1);
        if (scm == IntPtr.Zero) throw new IOException("无法连接服务控制管理器。");
        try
        {
            var handle = OpenService(scm, "msiserver", 4u | (start ? 16u : 32u));
            if (handle == IntPtr.Zero) throw new IOException("无法取得 Installer 服务控制权限。");
            try
            {
                if (start ? !StartService(handle, 0, IntPtr.Zero) : !ControlService(handle, 1, out _))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (!start && error == 1062) { /* Already stopped by another program is acceptable. */ }
                    else if (start && error == 1084)
                        throw new IOException("Windows Installer 服务在当前安全模式下被系统禁止启动（错误 1084）。无法在此环境卸载运行库；请回到正常模式操作，本工具不会修改系统启动配置。");
                    else throw new IOException("Installer 服务控制失败或已由其他程序启动，错误 " + error);
                }
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(12))
                {
                    if (!QueryServiceStatus(handle, out var status)) throw new IOException("Installer 服务复检失败。");
                    if (status.State == (start ? 4u : 1u)) return;
                    Thread.Sleep(100);
                }
                throw new IOException("Installer 服务状态未在时限内确认，恢复记录保留。");
            }
            finally { CloseServiceHandle(handle); }
        }
        finally { CloseServiceHandle(scm); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Status { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, Hint; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessStatus { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, Hint, Pid, Flags; }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartService(IntPtr service, uint count, IntPtr args);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(IntPtr service, uint control, out Status status);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(IntPtr service, out Status status);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(IntPtr service, uint level, out ProcessStatus status, int size, out int needed);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
