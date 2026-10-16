using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeltaNFD.Native;

/// <summary>
/// 当前进程令牌的特权调整（仅 Windows）。
/// 接管 TrustedInstaller 所有的注册表键（如 Enum 下的设备键）之前，
/// 必须先启用 SeTakeOwnershipPrivilege。
/// </summary>
internal static class Privilege
{
    private const int SePrivilegeEnabled = 0x2;
    private const int ErrorNotAllAssigned = 1300;
    private const int TokenAdjustPrivileges = 0x20;
    private const int TokenQuery = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public int PrivilegeCount;
        public Luid Luid;
        public int Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenProcessToken(nint processHandle, int desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        nint tokenHandle,
        bool disableAllPrivileges,
        ref TokenPrivileges newState,
        int bufferLength,
        nint previousState,
        nint returnLength);

    /// <summary>
    /// 为当前进程令牌启用指定特权（如 "SeTakeOwnershipPrivilege"）。
    /// 进程未持有该特权（即非管理员）时抛 <see cref="UnauthorizedAccessException"/>。
    /// </summary>
    public static void Enable(string privilegeName)
    {
        if (!LookupPrivilegeValue(null, privilegeName, out var luid))
        {
            throw new Win32Exception();
        }

        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var tokenHandle))
        {
            throw new Win32Exception();
        }

        try
        {
            var newState = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };

            if (!AdjustTokenPrivileges(tokenHandle, false, ref newState, 0, nint.Zero, nint.Zero))
            {
                throw new Win32Exception();
            }

            // AdjustTokenPrivileges 在特权未分配给当前进程时也返回 true，必须检查错误码
            if (Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
            {
                throw new UnauthorizedAccessException($"当前进程未持有特权 {privilegeName}，请以管理员身份运行。");
            }
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }
}
