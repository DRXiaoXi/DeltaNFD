using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Plugin.DpcTimerHold;

/// <summary>真实 Windows 实现。只做只读查询、启动/关闭本插件自己启动的进程、清理归属可核验的遗留服务。</summary>
internal sealed class NativeEnvironment : IEnvironmentInfo
{
    private readonly Lazy<(int Major, int Build)> _version = new(QueryVersion, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Lazy<bool> _admin = new(QueryAdmin, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Lazy<string> _secureBoot = new(QuerySecureBoot, LazyThreadSafetyMode.ExecutionAndPublication);

    public int OsMajor => _version.Value.Major;
    public int OsBuild => _version.Value.Build;
    public bool Is64BitProcess => Environment.Is64BitProcess;
    public bool IsAdmin => _admin.Value;
    public string SecureBootState => _secureBoot.Value;

    private static (int Major, int Build) QueryVersion()
    {
        var info = new OsVersionInfoEx { OSVersionInfoSize = (uint)Marshal.SizeOf<OsVersionInfoEx>() };
        var status = RtlGetVersion(ref info);
        if (status != 0)
            return (Environment.OSVersion.Version.Major, Environment.OSVersion.Version.Build);
        return ((int)info.MajorVersion, (int)info.BuildNumber);
    }

    private static bool QueryAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { return false; }
    }

    private static string QuerySecureBoot()
    {
        try
        {
            if (!GetFirmwareType(out var type)) return "未知";
            if (type != 2) return "不适用（Legacy BIOS）";
            var buffer = new byte[1];
            var size = GetFirmwareEnvironmentVariable("SecureBoot",
                "{8be4df61-93ca-11d2-aa0d-00e098032b8c}", buffer, 1);
            if (size == 1) return buffer[0] != 0 ? "开启" : "关闭";
            return "未知";
        }
        catch (Exception) { return "未知"; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfoEx
    {
        public uint OSVersionInfoSize;
        public uint MajorVersion;
        public uint MinorVersion;
        public uint BuildNumber;
        public uint PlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string CSDVersion;
        public ushort ServicePackMajor;
        public ushort ServicePackMinor;
        public ushort SuiteMask;
        public byte ProductType;
        public byte Reserved;
    }

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
    private static extern int RtlGetVersion(ref OsVersionInfoEx versionInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out uint firmwareType);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFirmwareEnvironmentVariable(string name, string guid, byte[] buffer, uint size);
}

internal sealed class NativeProcessApi : IProcessApi
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public OwnedProcess Launch(string exePath)
    {
        var directory = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(directory)) throw new InvalidDataException("原版 DPC 路径没有父目录。");
        var start = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Minimized,
            ErrorDialog = false,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start 未返回进程。");
        var startTicks = process.StartTime.ToUniversalTime().Ticks;
        var actual = QueryImagePath(process.Id) ?? "";
        if (!PathsEqual(actual, exePath))
        {
            try { process.CloseMainWindow(); } catch (InvalidOperationException) { }
            process.WaitForExit(5000);
            throw new InvalidDataException(
                $"启动后的进程映像路径与已核验文件不一致（实际“{actual}”）；已尝试关闭，拒绝继续。");
        }
        return new OwnedProcess(process.Id, startTicks, Path.GetFullPath(exePath));
    }

    public bool IsAlive(OwnedProcess owned)
    {
        try
        {
            using var process = Process.GetProcessById(owned.Pid);
            if (process.HasExited) return false;
            if (process.StartTime.ToUniversalTime().Ticks != owned.StartTicksUtc) return false;
            var actual = QueryImagePath(owned.Pid) ?? "";
            return PathsEqual(actual, owned.ExePath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    public bool CloseGracefully(OwnedProcess owned, int timeoutMs)
    {
        Process process;
        try { process = Process.GetProcessById(owned.Pid); }
        catch (ArgumentException) { return true; }
        using (process)
        {
            try
            {
                if (process.HasExited) return true;
                if (process.StartTime.ToUniversalTime().Ticks != owned.StartTicksUtc) return false;
                try { process.CloseMainWindow(); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                return process.WaitForExit(timeoutMs) && process.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return false;
            }
        }
    }

    internal static string? QueryImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    internal static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(DpcRules.NormalizeImagePath(left), DpcRules.NormalizeImagePath(right),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal sealed class NativeServiceApi : IServiceApi
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceDelete = 0x10000;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceWin32 = 0x00000001;
    private const uint ServiceKernelDriver = 0x00000001;
    private const uint ServiceDemandStart = 0x00000003;
    private const int ErrorServiceDoesNotExist = 1060;

    public ServiceInfo Query(string name)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
            return new ServiceInfo(false, 0, 0, 0, "", Win32("OpenSCManager"));
        var service = IntPtr.Zero;
        try
        {
            service = OpenService(manager, name, ServiceQueryStatus | ServiceQueryConfig);
            if (service == IntPtr.Zero)
            {
                var code = Marshal.GetLastWin32Error();
                if (code == ErrorServiceDoesNotExist) return new ServiceInfo(false, 0, 0, 0, "", "");
                return new ServiceInfo(false, 0, 0, 0, "", Win32("OpenService", code));
            }
            if (!QueryServiceStatus(service, out var status))
                return new ServiceInfo(false, 0, 0, 0, "", Win32("QueryServiceStatus"));
            if (!QueryServiceConfig(service, IntPtr.Zero, 0, out var needed) || needed == 0)
            {
                // 首次调用只为取得所需缓冲区大小；ERROR_INSUFFICIENT_BUFFER 属于预期。
                var firstError = Marshal.GetLastWin32Error();
                if (needed == 0) return new ServiceInfo(true, status.CurrentState, status.ServiceType, 0, "",
                    Win32("QueryServiceConfig(size)", firstError));
            }
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!QueryServiceConfig(service, buffer, needed, out _))
                    return new ServiceInfo(true, status.CurrentState, status.ServiceType, 0, "",
                        Win32("QueryServiceConfig"));
                var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
                var image = Marshal.PtrToStringUni(config.BinaryPathName) ?? "";
                return new ServiceInfo(true, status.CurrentState, config.ServiceType, config.StartType, image, "");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(manager);
        }
    }

    public bool StopAndDelete(ServiceInfo expected, out string detail)
    {
        detail = "";
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) { detail = Win32("OpenSCManager"); return false; }
        var service = IntPtr.Zero;
        try
        {
            service = OpenService(manager, DpcRules.ServiceName,
                ServiceQueryStatus | ServiceQueryConfig | ServiceStop | ServiceDelete);
            if (service == IntPtr.Zero) { detail = Win32("OpenService(delete)"); return false; }
            if (!QueryServiceStatus(service, out var status)) { detail = Win32("QueryServiceStatus"); return false; }
            if (status.ServiceType != ServiceKernelDriver || status.CurrentState == 0)
            {
                detail = "服务不是内核驱动，拒绝删除。";
                return false;
            }
            if (!QueryServiceConfig(service, IntPtr.Zero, 0, out var needed) && needed == 0)
            {
                detail = Win32("QueryServiceConfig(size)");
                return false;
            }
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!QueryServiceConfig(service, buffer, needed, out _)) { detail = Win32("QueryServiceConfig"); return false; }
                var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
                var image = Marshal.PtrToStringUni(config.BinaryPathName) ?? "";
                if (config.ServiceType != expected.Type || config.StartType != expected.StartType ||
                    !string.Equals(DpcRules.NormalizeImagePath(image), DpcRules.NormalizeImagePath(expected.ImagePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    detail = "服务身份与记录不一致，未做任何改动。";
                    return false;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }

            if (status.CurrentState != (uint)DpcServiceState.Stopped)
            {
                if (!ControlService(service, ServiceControlStop, out _)) { detail = Win32("ControlService(stop)"); return false; }
                var deadline = DateTime.UtcNow.AddMilliseconds(DpcRules.ServiceCleanupTimeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    if (!QueryServiceStatus(service, out status)) { detail = Win32("QueryServiceStatus"); return false; }
                    if (status.CurrentState == (uint)DpcServiceState.Stopped) break;
                    Thread.Sleep(DpcRules.PollIntervalMs);
                }
                if (status.CurrentState != (uint)DpcServiceState.Stopped)
                {
                    detail = "服务未在期限内停止，未删除。";
                    return false;
                }
            }
            if (!DeleteService(service)) { detail = Win32("DeleteService"); return false; }
            detail = "已停止并删除归属可核验的遗留服务。";
            return true;
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(manager);
        }
    }

    private static string Win32(string operation) => Win32(operation, Marshal.GetLastWin32Error());

    private static string Win32(string operation, int code) =>
        $"{operation} 失败（Win32 {code}）：{new Win32Exception(code).Message}";

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceConfig
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(IntPtr service, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

internal sealed class NativeTimerApi : ITimerApi
{
    public uint? TryCurrent100ns(out string error)
    {
        var status = NtQueryTimerResolution(out var maximum, out var minimum, out var current);
        if (status < 0) { error = $"NtQueryTimerResolution 失败（0x{status:x8}）。"; return null; }
        if (minimum == 0 || maximum < minimum || current < minimum || current > maximum || current > 10_000_000)
        {
            error = $"NtQueryTimerResolution 读数越界（max {maximum}、min {minimum}、current {current}）。";
            return null;
        }
        error = "";
        return current;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint maximum, out uint minimum, out uint current);
}

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public void Delay(int milliseconds) => Thread.Sleep(milliseconds);
}

internal static class DpcNative
{
    public static DpcHoldController CreateController() =>
        new(new NativeEnvironment(), new NativeProcessApi(), new NativeServiceApi(), new NativeTimerApi(),
            new SystemClock(), new RealInputApi());
}
