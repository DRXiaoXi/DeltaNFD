using System.Runtime.InteropServices;
using System.Security.Principal;
using DeltaNFD.Services;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Execution.Procman;

/// <summary>普通内存回收的进程保护分类；访问权限与属性所有权另行复核。</summary>
internal static class ProcmanBackgroundFilter
{
    internal static bool IsToolProcess(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.StartsWith("DeltaNFD", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("DeltaOptimizer", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("三角帧不掉洲", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(BackendEnvironment.AppRoot)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    internal static bool ExcludedByIdentity(string path, int session, string userSid, bool critical, string windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(userSid) || session < 0) return true;
        if (IsToolProcess(path)) return true;
        if (ProcmanProcessProtection.IsAceProtected(path)) return true;
        if (Path.GetFileNameWithoutExtension(path).Equals(DeltaForceLocator.GameProcessName, StringComparison.OrdinalIgnoreCase)) return true;
        var windows = Path.TrimEndingDirectorySeparator(Path.GetFullPath(windowsDirectory)) + Path.DirectorySeparatorChar;
        return session == 0 || critical || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
            || userSid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20"
            || userSid.StartsWith("S-1-5-80-", StringComparison.Ordinal); // virtual service accounts
    }

    internal static bool IsExcluded(ProcmanProcessIdentity identity)
        => IsToolProcess(identity.ExecutablePath)
            || ProcmanProcessProtection.IsAceProtected(identity.ExecutablePath)
            || Path.GetFileNameWithoutExtension(identity.ExecutablePath).Equals(DeltaForceLocator.GameProcessName, StringComparison.OrdinalIgnoreCase)
            || IsSystemProcess(identity);

    internal static bool IsSystemProcess(ProcmanProcessIdentity identity)
    {
        try
        {
            using var handle = ProcmanIdentity.TryOpenQuery(identity.Pid);
            if (handle is null || !ProcmanIdentity.Matches(handle, identity)
                || !ProcessIdToSessionId(identity.Pid, out var session) || session > int.MaxValue
                || !IsProcessCritical(handle, out var critical)
                || !OpenProcessToken(handle, 0x0008, out var token)) return true;
            using (token)
            using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
            {
                var sid = owner.User?.Value ?? "";
                var windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + Path.DirectorySeparatorChar;
                return sid.Length == 0 || session == 0 || critical || identity.Pid <= 4
                    || identity.ExecutablePath.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
                    || sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20" || sid.StartsWith("S-1-5-80-", StringComparison.Ordinal);
            }
        }
        catch { return true; } // Unknown owner/identity is not permission to adjust a system process.
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(int pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessCritical(SafeProcessHandle handle, [MarshalAs(UnmanagedType.Bool)] out bool critical);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle handle, uint access, out SafeAccessTokenHandle token);
}
