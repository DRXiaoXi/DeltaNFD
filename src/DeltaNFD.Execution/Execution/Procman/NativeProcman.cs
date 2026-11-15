using System.Runtime.InteropServices;

namespace DeltaNFD.Execution.Procman;

/// <summary>前台窗口进程查询（普通内存回收排除前台应用用）。失败返回 -1，绝不抛出。</summary>
internal static class NativeProcman
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    public static int ForegroundPid()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return -1;
            if (GetWindowThreadProcessId(window, out var pid) == 0) return -1;
            return (int)pid;
        }
        catch { return -1; }
    }
}
