using System.Diagnostics;
using DeltaNFD.Services;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Execution.Procman;

/// <summary>
/// 通用进程身份：PID + 启动时间（UTC）+ 完整映像路径。
/// 所有写入操作前必须用 <see cref="Verify"/> 重新核对；身份不符（PID 复用/已退出/路径变化）时拒绝操作。
/// 与 GameTargetService 的游戏目标身份分开：这里面向任意进程，不绑定游戏目标代次。
/// </summary>
public sealed record ProcmanProcessIdentity(int Pid, DateTime StartTimeUtc, string ExecutablePath)
{
    public long StartTimeUtcTicks => StartTimeUtc.Ticks;
}

/// <summary>对任意进程的身份核对与安全句柄获取（不接收前端传入的路径/脚本；只按 PID 打开再核对）。</summary>
public static class ProcmanIdentity
{
    public static bool Matches(SafeProcessHandle handle, ProcmanProcessIdentity identity) =>
        GetExitCodeProcess(handle, out var exit) && exit == 259
        && GetProcessTimes(handle, out var creation, out _, out _, out _)
        && DateTime.FromFileTimeUtc(creation).Ticks == identity.StartTimeUtcTicks
        && ReadPath(handle).Equals(identity.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    public static string State(ProcmanProcessIdentity identity)
    {
        using var handle = OpenProcess(0x1000, false, identity.Pid);
        if (handle.IsInvalid) return Marshal.GetLastWin32Error() == 87 ? "missing" : "unknown";
        if (!GetExitCodeProcess(handle, out var code)) return "unknown";
        if (code != 259) return "missing";
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _)) return "unknown";
        var path = ReadPath(handle);
        if (path.Length == 0) return "unknown";
        return DateTime.FromFileTimeUtc(creation).Ticks == identity.StartTimeUtcTicks
            && path.Equals(identity.ExecutablePath, StringComparison.OrdinalIgnoreCase) ? "same" : "missing";
    }
    // PROCESS_QUERY_LIMITED_INFORMATION
    private const uint ProcessQueryLimited = 0x1000;

    /// <summary>按查询权限打开进程；失败（拒绝访问/已退出）返回 null，绝不抛出。</summary>
    public static SafeProcessHandle? TryOpenQuery(int pid)
    {
        try
        {
            var handle = OpenProcess(ProcessQueryLimited, false, pid);
            return handle.IsInvalid ? null : handle;
        }
        catch { return null; }
    }

    /// <summary>读取进程映像完整路径；失败返回空（不猜）。</summary>
    public static string ReadPath(int pid)
    {
        using var handle = TryOpenQuery(pid);
        if (handle is null) return "";
        return ReadPath(handle);
    }

    public static string ReadPath(SafeProcessHandle handle)
    {
        try
        {
            var buffer = new System.Text.StringBuilder(32768);
            var size = buffer.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size)) return "";
            return buffer.ToString();
        }
        catch { return ""; }
    }

    /// <summary>读取进程启动时间 UTC；失败返回 null。</summary>
    public static DateTime? TryReadStartTimeUtc(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch { return null; }
    }

    /// <summary>读取进程基础信息（名称/启动时间/路径）。任一项失败即整体失败——不输出半截身份。</summary>
    public static ProcmanProcessIdentity? TryIdentify(int pid)
    {
        try
        {
            using var handle = TryOpenQuery(pid);
            if (handle is null || !GetExitCodeProcess(handle, out var exit) || exit != 259
                || !GetProcessTimes(handle, out var creation, out _, out _, out _)) return null;
            var path = ReadPath(handle);
            return path.Length == 0 ? null : new ProcmanProcessIdentity(pid, DateTime.FromFileTimeUtc(creation), path);
        }
        catch { return null; }
    }

    /// <summary>
    /// 身份复核：PID 对应进程的启动时间与路径必须与请求中的身份一致。
    /// startTimeUtcTicks 为 0（前端没拿到）或任一项不符 → 返回 false。
    /// </summary>
    public static bool Verify(int pid, long startTimeUtcTicks, string executablePath)
    {
        if (pid <= 0 || startTimeUtcTicks == 0 || string.IsNullOrWhiteSpace(executablePath)) return false;
        var current = TryIdentify(pid);
        return current is not null
            && current.StartTimeUtc.Ticks == startTimeUtcTicks
            && string.Equals(current.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>受保护进程不能新增主动修改或执行逐进程规则；历史记录恢复另按身份和原值校验。</summary>
    public static bool IsProtectedPath(string executablePath)
    {
        if (executablePath.Length == 0) return true;
        if (ProcmanProcessProtection.IsAceProtected(executablePath)) return true;
        var name = Path.GetFileNameWithoutExtension(executablePath);
        if (name.StartsWith("DeltaNFD", StringComparison.OrdinalIgnoreCase)) return true;
        string[] critical = ["system", "lsass", "services", "winlogon", "smss", "csrss", "svchost", "dwm", "explorer", "wininit", "Memory Compression", "sguard64", "sguard32", "ACE-Service64", "ACE-Service32", "MsMpEng"];
        if (critical.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        if (executablePath.Contains("\\AntiCheatExpert\\", StringComparison.OrdinalIgnoreCase)) return true;
        var windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + Path.DirectorySeparatorChar;
        return executablePath.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle handle, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint code);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle, uint flags, System.Text.StringBuilder path, ref int size);
}
