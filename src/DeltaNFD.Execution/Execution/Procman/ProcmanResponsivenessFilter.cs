using System.Runtime.InteropServices;
using System.Security.Principal;
using DeltaNFD.Services;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Execution.Procman;

/// <summary>后台抑制只按三角洲主进程/系统进程分类；写入权限与属性所有权另行复核。</summary>
internal static class ProcmanResponsivenessFilter
{
    internal static bool ExcludedByIdentity(string path, int session, string userSid, bool critical, string windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(userSid) || session < 0) return true;
        if (Path.GetFileNameWithoutExtension(path).Equals(DeltaForceLocator.GameProcessName, StringComparison.OrdinalIgnoreCase)) return true;
        var windows = Path.TrimEndingDirectorySeparator(Path.GetFullPath(windowsDirectory)) + Path.DirectorySeparatorChar;
        return session == 0 || critical || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
            || userSid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20"
            || userSid.StartsWith("S-1-5-80-", StringComparison.Ordinal); // virtual service accounts
    }

    internal static bool IsExcluded(ProcmanProcessIdentity identity)
    {
        try
        {
            // Exclusion of the game does not depend on whether it is currently foreground.
            if (Path.GetFileNameWithoutExtension(identity.ExecutablePath).Equals(DeltaForceLocator.GameProcessName, StringComparison.OrdinalIgnoreCase)) return true;
            using var handle = ProcmanIdentity.TryOpenQuery(identity.Pid);
            if (handle is null || !ProcmanIdentity.Matches(handle, identity)
                || !ProcessIdToSessionId(identity.Pid, out var session) || session > int.MaxValue
                || !IsProcessCritical(handle, out var critical)
                || !OpenProcessToken(handle, 0x0008, out var token)) return true;
            using (token)
            using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
                return ExcludedByIdentity(identity.ExecutablePath, (int)session, owner.User?.Value ?? "", critical,
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows));
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
