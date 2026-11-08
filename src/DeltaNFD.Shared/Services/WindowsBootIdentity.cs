using System.Runtime.InteropServices;

namespace DeltaNFD.Services;

/// <summary>只读启动实例标识。不以墙上时间、运行时长或 WMI 推算重启。</summary>
internal static class WindowsBootIdentity
{
    // System Informer phnt: SYSTEM_BOOT_ENVIRONMENT_INFORMATION, class 0x5A.
    [StructLayout(LayoutKind.Sequential)]
    private struct BootEnvironment
    {
        public Guid BootIdentifier;
        public uint FirmwareType;
        public ulong BootFlags;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, out BootEnvironment value,
        uint size, out uint returned);

    internal static string? Read()
    {
        try
        {
            var size = (uint)Marshal.SizeOf<BootEnvironment>();
            return NtQuerySystemInformation(0x5A, out var value, size, out var returned) == 0
                && returned == size && value.BootIdentifier != Guid.Empty
                ? value.BootIdentifier.ToString("D") : null;
        }
        catch { return null; } // Missing/changed native API must never count as a restart.
    }
}
