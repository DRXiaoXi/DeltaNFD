using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeltaNFD.Services.AlphaRestore;

internal static class RuntimeRepairPreflight
{
    internal static RuntimePreflightSnapshot Read()
    {
        try
        {
            var services = new[] { ReadService("msiserver"), ReadService("TrustedInstaller") };
            var busy = InstallerBusy();
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cbs = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            using var update = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using var session = registry.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            var pendingFiles = session?.GetValue("PendingFileRenameOperations") as string[];
            var winsxsWarning = CheckWinSxSPermissions();
            return new(services, busy, cbs is not null || update is not null || pendingFiles?.Any(s => s.Length != 0) == true,
                "", winsxsWarning, ServicingRebootPending: cbs is not null || update is not null);
        }
        catch (Exception ex) { return new([], false, false, ex.Message); }
    }

    /// <summary>尝试自动修复 Windows Installer 服务状态问题。</summary>
    internal static OperationResult TryRepairInstallerService()
    {
        if (!ElevationHelper.IsElevated) return OperationResult.Fail("需要管理员权限才能修复安装服务状态。");
        var issues = new List<string>();
        foreach (var name in new[] { "msiserver", "TrustedInstaller" })
        {
            var state = ReadService(name);
            if (state.Error.Length > 0)
            {
                issues.Add($"{name}：{state.Error}");
                continue;
            }
            if (state.StartType == 4)
            {
                if (!TrySetServiceStartType(name, 3, out var error))
                {
                    issues.Add($"{name} 启动类型修复失败：{error}");
                    continue;
                }
                Log.Info($"已将服务 {name} 启动类型从禁用恢复为手动。");
            }
            if (state.State is < 1 or > 4 && name == "msiserver")
            {
                if (!TryStartService(name, out var error))
                {
                    issues.Add($"{name} 启动失败：{error}");
                    continue;
                }
                Log.Info($"已启动服务 {name}。");
            }
        }
        return issues.Count == 0
            ? new OperationResult { Success = true, Message = "Windows Installer 服务状态已修复。" }
            : OperationResult.Fail("部分服务修复失败：\n" + string.Join("\n", issues));
    }

    private static bool TrySetServiceStartType(string name, uint startType, out string error)
    {
        var manager = OpenSCManagerW(null, null, 0xF003F);
        if (manager == IntPtr.Zero) { error = new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
        try
        {
            var service = OpenServiceW(manager, name, 0x0002);
            if (service == IntPtr.Zero) { error = new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
            try
            {
                if (!ChangeServiceConfigW(service, 0xFFFFFFFF, startType, 0xFFFFFFFF, null, null, IntPtr.Zero, null, null, null, null))
                {
                    error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return false;
                }
                error = "";
                return true;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static bool TryStartService(string name, out string error)
    {
        var manager = OpenSCManagerW(null, null, 0xF003F);
        if (manager == IntPtr.Zero) { error = new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
        try
        {
            var service = OpenServiceW(manager, name, 0x0010);
            if (service == IntPtr.Zero) { error = new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
            try
            {
                if (!StartServiceW(service, 0, null))
                {
                    var code = Marshal.GetLastWin32Error();
                    if (code == 1056) { error = ""; return true; }
                    error = new Win32Exception(code).Message;
                    return false;
                }
                error = "";
                return true;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    internal static bool InstallerBusy()
    {
        foreach (var name in new[] { @"Global\_MSIExecute", "_MSIExecute" })
        {
            if (!Mutex.TryOpenExisting(name, out var mutex)) continue;
            using (mutex)
            {
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return true;
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }
        return false;
    }

    /// <summary>尝试在指定超时内等待 Windows Installer 互斥锁释放。</summary>
    internal static bool WaitForInstallerIdle(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!InstallerBusy()) return true;
            Thread.Sleep(TimeSpan.FromSeconds(2));
        }
        return !InstallerBusy();
    }

    /// <summary>检测 WinSxS 程序集缓存的关键权限问题。</summary>
    internal static string CheckWinSxSPermissions()
    {
        try
        {
            var winsxs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS");
            if (!Directory.Exists(winsxs)) return "WinSxS 目录不存在";
            
            // 尝试枚举目录以检测基本访问权限
            try
            {
                _ = Directory.EnumerateDirectories(winsxs).Take(1).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                return "WinSxS 目录访问被拒绝，可能存在权限异常";
            }

            // 检查 Pending.xml 文件（安装器事务文件）权限
            var pending = Path.Combine(winsxs, "Pending.xml");
            if (File.Exists(pending))
            {
                try { using var _ = File.OpenRead(pending); }
                catch (UnauthorizedAccessException)
                {
                    return "WinSxS\\Pending.xml 访问被拒绝，可能导致 1935 错误";
                }
            }

            return "";
        }
        catch (Exception ex)
        {
            return "WinSxS 权限检查失败：" + ex.Message;
        }
    }

    /// <summary>检测 Windows 11 系统上安装 VC++ 2005/2008 的兼容性风险。</summary>
    internal static string CheckLegacyRuntimeCompatibility(string[] targets)
    {
        if (!targets.Any(t => t.StartsWith("2005") || t.StartsWith("2008"))) return "";
        
        var osVersion = Environment.OSVersion.Version;
        var build = osVersion.Build;
        
        if (osVersion.Major >= 10 && build >= 22000)
        {
            var legacyTargets = targets.Where(t => t.StartsWith("2005") || t.StartsWith("2008")).ToArray();
            
            // Windows 11 24H2+ (Build 26040+) 改善了旧版运行库兼容性
            if (build >= 26040)
            {
                return $"Windows 11 24H2/25H2 对 VC++ 2005/2008 兼容性较好，但仍建议游戏开发者迁移到 2015-2022 版本。受影响分支：{string.Join(", ", legacyTargets)}。";
            }
            // Windows 11 22H2-23H2 (Build 22621-25999) 兼容性受限
            else if (build >= 22621)
            {
                return $"Windows 11 22H2/23H2 对 VC++ 2005/2008 支持受限，安装可能失败（退出码 1603/1633）。受影响分支：{string.Join(", ", legacyTargets)}。如失败，建议联系游戏开发者更新运行库依赖。";
            }
            // Windows 11 21H2 (Build 22000-22620) 基础支持
            else
            {
                return $"Windows 11 21H2 对 VC++ 2005/2008 有基础支持，但可能出现兼容性问题。受影响分支：{string.Join(", ", legacyTargets)}。";
            }
        }
        
        return "";
    }

    private static RuntimeServiceState ReadService(string name)
    {
        var manager = OpenSCManagerW(null, null, 1);
        if (manager == IntPtr.Zero) return new(name, -1, -1, new Win32Exception(Marshal.GetLastWin32Error()).Message);
        try
        {
            var service = OpenServiceW(manager, name, 1 | 4);
            if (service == IntPtr.Zero) return new(name, -1, -1, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            try
            {
                QueryServiceConfigW(service, IntPtr.Zero, 0, out var required);
                if (required == 0 || required > 65536) return new(name, -1, -1, "服务配置大小无效");
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    if (!QueryServiceConfigW(service, buffer, required, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
                    if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatus>(), out _))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    return new(name, (int)config.StartType, (int)status.State);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch (Exception ex) { return new(name, -1, -1, ex.Message); }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig
    { public uint Type, StartType, ErrorControl; public IntPtr BinaryPath, Group; public uint Tag; public IntPtr Dependencies, Account, DisplayName; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceConfigW(IntPtr service, IntPtr config, uint size, out uint required);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, uint size, out uint required);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ChangeServiceConfigW(IntPtr service, uint type, uint startType, uint errorControl, string? binaryPath, string? group, IntPtr tag, string? dependencies, string? account, string? password, string? displayName);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceW(IntPtr service, uint argc, string?[]? argv);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
}
