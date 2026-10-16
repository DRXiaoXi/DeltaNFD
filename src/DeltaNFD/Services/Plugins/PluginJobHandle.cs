using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeltaNFD.Services.Plugins;

/// <summary>
/// 插件进程的 Job Object 管理（规范第 5 节）：关联进程但不设 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE，
/// 宿主崩溃或关闭句柄后插件进程可自主存活（脱机移交的前提）。
/// 不能据此宣称管理员插件不能逃逸——Job 只用于已知的关联进程管理。
/// </summary>
internal sealed class PluginJobHandle : IDisposable
{
    private nint _handle;
    public string LastQueryDetail { get; private set; } = "";

    private PluginJobHandle(nint handle) => _handle = handle;

    /// <summary>创建不设 kill-on-close 的 Job 并把进程加入；失败返回 null，由调用方拒绝启动。</summary>
    public static PluginJobHandle? TryCreateFor(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        nint job = nint.Zero;
        var retained = false;
        try
        {
            // 安全属性：默认 DACL（当前用户），不允许 BREAKAWAY（成员不能自行脱离）。
            job = CreateJobObject(nint.Zero, null);
            if (job == nint.Zero) return null;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0; // 明确不设 KILL_ON_JOB_CLOSE
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var failed = !SetInformationJobObject(job, ExtendedLimitInformation,
                ref info, (uint)size);
            if (failed || !AssignProcessToJobObject(job, process.Handle))
            {
                return null;
            }
            retained = true;
            return new PluginJobHandle(job);
        }
        catch
        {
            return null;
        }
        finally { if (!retained && job != nint.Zero) CloseHandle(job); }
    }

    /// <summary>当前 Job 成员进程数（关闭/卸载前检查已知成员是否已退出）。</summary>
    public bool TryGetActiveProcessCount(out uint activeCount, int knownExitedPid = 0)
    {
        activeCount = 0;
        LastQueryDetail = "";
        var handle = _handle;
        if (handle == nint.Zero) { LastQueryDetail = "句柄已关闭"; return false; }
        for (var capacity = 16; capacity <= 4096; capacity *= 2)
        {
            var size = checked(8 + capacity * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryInformationJobObject(handle, ProcessIdListInfo, buffer, (uint)size, out _))
                {
                    var code = Marshal.GetLastWin32Error();
                    LastQueryDetail = "QueryInformationJobObject Win32=" + code;
                    if (code == 234) continue;
                    return false;
                }
                var count = Marshal.ReadInt32(buffer, 4);
                if (count < 0 || count > capacity) return false;
                for (var i = 0; i < count; i++)
                {
                    var pid = IntPtr.Size == 8 ? Marshal.ReadInt64(buffer, 8 + i * 8) : Marshal.ReadInt32(buffer, 8 + i * 4);
                    if (knownExitedPid > 0 && pid == knownExitedPid) continue;
                    try
                    {
                        using var process = Process.GetProcessById(checked((int)pid));
                        if (!process.HasExited) activeCount++;
                    }
                    catch (ArgumentException) { }
                    catch (Exception ex) { LastQueryDetail = "PID=" + pid + " " + ex.GetType().Name + " " + ex.Message; return false; }
                }
                LastQueryDetail = "仍活动成员=" + activeCount;
                return true;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return false;
    }

    public void Dispose()
    {
        // 关闭句柄不杀进程（未设 kill-on-close）；仅释放本进程的 Job 管理视图。
        var handle = System.Threading.Interlocked.Exchange(ref _handle, nint.Zero);
        if (handle != nint.Zero) CloseHandle(handle);
    }

    private const uint ExtendedLimitInformation = 9;
    private const uint ProcessIdListInfo = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint hJob, uint jobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(nint hJob, uint infoClass,
        nint lpJobObjectInformation, uint cbJobObjectInformationLength,
        out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public ulong Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
