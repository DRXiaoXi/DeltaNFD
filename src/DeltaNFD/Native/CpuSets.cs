using System.Runtime.InteropServices;

namespace DeltaNFD.Native;

// ---------------- CPU 集（软亲和性） ----------------
// 与硬亲和性掩码（ProcessorAffinity，线程「只能」跑指定核心）不同：
// 进程默认 CPU 集 = 调度器偏好——线程「优先」跑指定核心，指定核心忙时允许临时溢出，
// 不会出现「指定核排队、其他核看戏」的硬限制问题。
// 语义：设置默认集为全部核心 = 无偏好（还原态）。

/// <summary>进程默认 CPU 集读写（按 GROUP_AFFINITY 掩码形式，单组系统直接用组 0）。</summary>
public static class CpuSets
{
    [StructLayout(LayoutKind.Sequential)]
    private struct GROUP_AFFINITY
    {
        public nuint Mask;
        public ushort Group;
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessDefaultCpuSetMasks(IntPtr process, GROUP_AFFINITY[] masks, ushort maskCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessDefaultCpuSetMasks(IntPtr process, [Out] GROUP_AFFINITY[] masks, ushort maskCount, out ushort requiredCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadSelectedCpuSetMasks(IntPtr thread, GROUP_AFFINITY[] masks, ushort maskCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, int dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>PROCESS_SET_QUOTA | PROCESS_SET_INFORMATION——SetProcessDefaultCpuSetMasks 所需权限，
    /// 远低于全访问权，受保护程度一般的进程也能打开成功。</summary>
    private const uint ProcessAccessForCpuSets = 0x0100 | 0x0200;

    /// <summary>THREAD_SET_INFORMATION | THREAD_SET_LIMITED_INFORMATION——SetThreadSelectedCpuSetMasks 所需权限。</summary>
    private const uint ThreadAccessForCpuSets = 0x0020 | 0x0400;

    /// <summary>
    /// 按最小必要权限打开进程句柄（供 CPU Sets 写入用）。返回 IntPtr.Zero = 打开失败
    /// （拒绝访问 / 受保护进程，调用方按用户要求直接跳过该进程）。
    /// </summary>
    public static IntPtr OpenProcessForCpuSets(int pid)
    {
        try
        {
            return OpenProcess(ProcessAccessForCpuSets, false, pid);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>按最小必要权限打开线程句柄。返回 IntPtr.Zero = 打开失败（跳过该线程）。</summary>
    public static IntPtr OpenThreadForCpuSets(int tid)
    {
        try
        {
            return OpenThread(ThreadAccessForCpuSets, false, tid);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static void CloseHandleSafe(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _ = CloseHandle(handle);
        }
    }

    /// <summary>把指定线程的「线程所选 CPU 集」设为掩码（对既有线程生效；新线程走进程默认集）。</summary>
    public static bool SetThreadSelectedByMask(IntPtr threadHandle, ulong mask, ushort group = 0)
    {
        try
        {
            var masks = new[] { new GROUP_AFFINITY { Mask = (nuint)mask, Group = group } };
            return SetThreadSelectedCpuSetMasks(threadHandle, masks, 1);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把进程默认 CPU 集设为指定掩码（软亲和性绑定）。</summary>
    public static bool SetDefaultByMask(IntPtr processHandle, ulong mask, ushort group = 0)
    {
        try
        {
            var masks = new[] { new GROUP_AFFINITY { Mask = (nuint)mask, Group = group } };
            return SetProcessDefaultCpuSetMasks(processHandle, masks, 1);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取进程默认 CPU 集掩码（跨组聚合；无默认集返回 false）。</summary>
    public static bool TryGetDefaultMask(IntPtr processHandle, out ulong mask)
    {
        mask = 0;
        try
        {
            var buffer = new GROUP_AFFINITY[16];
            if (!GetProcessDefaultCpuSetMasks(processHandle, buffer, (ushort)buffer.Length, out var required))
            {
                return false;
            }

            if (required == 0)
            {
                return false; // 未设置默认集（= 无偏好）
            }

            if (required > buffer.Length)
            {
                buffer = new GROUP_AFFINITY[required];
                if (!GetProcessDefaultCpuSetMasks(processHandle, buffer, (ushort)buffer.Length, out _))
                {
                    return false;
                }
            }

            for (var i = 0; i < buffer.Length && i < required; i++)
            {
                mask |= (ulong)buffer[i].Mask;
            }

            return mask != 0;
        }
        catch
        {
            return false;
        }
    }
}
