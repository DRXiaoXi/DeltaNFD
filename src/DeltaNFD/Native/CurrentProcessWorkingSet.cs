using System.Runtime.InteropServices;

namespace DeltaNFD.Native;

internal static class CurrentProcessWorkingSet
{
    internal static bool TryTrim(out int error)
    {
        var success = K32EmptyWorkingSet(GetCurrentProcess());
        error = success ? 0 : Marshal.GetLastWin32Error();
        return success;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32EmptyWorkingSet(nint process);
}
