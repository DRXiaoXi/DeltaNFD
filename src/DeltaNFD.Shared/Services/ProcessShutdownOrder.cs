using System.Runtime.InteropServices;

namespace DeltaNFD.Services;

/// <summary>Only changes this process's lifetime order, never persistent Windows configuration.</summary>
internal static class ProcessShutdownOrder
{
    internal const uint FrontendLevel = 0x300;
    internal const uint BackendLevel = 0x200;

    internal static void Configure(bool frontend)
    {
        if (!OperatingSystem.IsWindows()) return;
        var level = frontend ? FrontendLevel : BackendLevel;
        if (!SetProcessShutdownParameters(level, 0))
            Log.Warn($"Windows 会话收尾顺序未设置：level=0x{level:X}；Win32Error={Marshal.GetLastWin32Error()}；仍保留原核对保护。");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessShutdownParameters(uint level, uint flags);
}
