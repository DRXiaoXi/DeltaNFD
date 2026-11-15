using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

/// <summary>Query only: no device opens, service control, certificate or boot writes.</summary>
public static class PendulumWin10DriverInspection
{
    public const string ServiceName = "DeltaNFDPendulumTimerV1";
    public const string ExpectedDriverSha256 = "9a7e35234617b1d4a86a0dddeb0a819821189b18783028ceecb6db4591f65522";
    public static string DriverDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DeltaNFD Pendulum Driver");
    public static string DriverPath => Path.Combine(DriverDirectory, ServiceName + ".sys");

    public static bool EvaluateTestSigning(int status, uint returned, uint length, uint options)
    {
        if (status != 0 || returned != 8 || length != 8) throw new IOException($"系统测试签名状态未确认（NTSTATUS 0x{status:x8}）。");
        return (options & 2) != 0;
    }
    public static bool ReadTestSigning()
    {
        var info = new CodeIntegrity { Length = 8 };
        var status = NtQuerySystemInformation(103, ref info, 8, out var returned);
        return EvaluateTestSigning(status, returned, info.Length, info.Options);
    }
    public static bool MatchesService(uint type, uint start, string image, string target)
        => type == 1 && start == 3 && Path.IsPathFullyQualified(target)
            && image.Equals(@"\??\" + target, StringComparison.OrdinalIgnoreCase);

    public static PendulumWin10DriverStatus Evaluate(bool readable, bool testMode, bool registered, bool running, bool verified, string error = "")
    {
        var state = new PendulumWin10DriverStatus { Readable = readable, TestSigningEnabled = testMode,
            DriverRegistered = registered, DriverRunning = running, DriverIdentityVerified = verified };
        var message = !readable ? "准备状态未确认：" + error
            : !testMode ? "测试签名模式尚未生效。可点击一键准备，再按提示重启；重开软件会自动继续。仅执行开启命令不算完成。"
            : !registered ? "测试签名模式已生效；尚未安装自研钟摆驱动，可点击一键准备自动安装。"
            : !verified ? "测试签名模式已生效；驱动文件、准备记录或服务路径核对失败。请保留记录，按说明核对或使用 RepairPath。" + error
            : !running ? "测试签名模式已生效，驱动已安装但未启动；可点击一键准备启动，已有授权时重开软件会自动续办。"
            : "测试签名模式已生效，自研驱动身份和运行状态已核对；可以检测并保存驱动分辨率。";
        return state with { Message = message };
    }
    public static PendulumWin10DriverStatus Read()
    {
        bool test = false, registered = false, running = false, verified = false;
        try
        {
            if (!PendulumGlobalInspection.ExperimentalWindows10 || RuntimeInformation.OSArchitecture != Architecture.X64)
                return Evaluate(false, false, false, false, false, "自研驱动仅支持 Windows 10 1809+ x64。");
            test = ReadTestSigning();
            var manager = OpenSCManager(null, null, 1);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法只读访问服务管理器。");
            try
            {
                var service = OpenService(manager, ServiceName, 5); // QUERY_CONFIG | QUERY_STATUS only
                if (service == IntPtr.Zero)
                {
                    var code = Marshal.GetLastWin32Error();
                    if (code == 1060) return Evaluate(true, test, false, false, false);
                    throw new Win32Exception(code);
                }
                registered = true;
                try
                {
                    if (!QueryServiceStatus(service, out var state)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    running = state.Current == 4;
                    var buffer = Marshal.AllocHGlobal(8192);
                    try
                    {
                        if (!QueryServiceConfig(service, buffer, 8192, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
                        if (!MatchesService(config.Type, config.Start, Marshal.PtrToStringUni(config.Path) ?? "", DriverPath))
                            return Evaluate(true, test, registered, running, false);
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                    VerifyFiles();
                    verified = true;
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
            return Evaluate(true, test, registered, running, verified);
        }
        catch (Exception ex) { return Evaluate(false, test, registered, running, false, ex.Message); }
    }
    private static void VerifyFiles()
    {
        for (var directory = new DirectoryInfo(DriverDirectory); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("驱动目录不能经过重解析点。");
        var receiptPath = Path.Combine(DriverDirectory, "active-driver.json");
        foreach (var path in new[] { DriverPath, receiptPath })
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("驱动文件或记录不能是重解析点。");
        using var receiptLock = new FileStream(receiptPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (receiptLock.Length > 65536) throw new IOException("驱动准备记录大小异常。");
        using var reader = new StreamReader(receiptLock);
        using var record = JsonDocument.Parse(reader.ReadToEnd());
        var root = record.RootElement;
        if (root.GetProperty("Service").GetString() != ServiceName
            || !string.Equals(root.GetProperty("Path").GetString(), DriverPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(root.GetProperty("SHA256").GetString(), ExpectedDriverSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("驱动准备记录与 Alpha0.935 驱动不匹配。");
        using var file = new FileStream(DriverPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 1048576 || !Convert.ToHexString(SHA256.HashData(file)).Equals(ExpectedDriverSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("已安装驱动与随包驱动不匹配。");
    }
    [StructLayout(LayoutKind.Sequential)] private struct CodeIntegrity { public uint Length, Options; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceState { public uint Type, Current, Controls, Exit, Specific, Checkpoint, Hint; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig { public uint Type, Start, Error; public IntPtr Path, Group; public uint Tag; public IntPtr Dependencies, Account, Display; }
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int kind, ref CodeIntegrity info, uint length, out uint returned);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceState state);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(IntPtr service, IntPtr buffer, uint size, out uint needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
}
