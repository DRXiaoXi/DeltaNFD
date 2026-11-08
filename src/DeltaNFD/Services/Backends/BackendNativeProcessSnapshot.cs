using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Services.Backends;

/// <summary>Read-only process metadata without a WMI dependency. No process is stopped or modified.</summary>
internal static class BackendNativeProcessSnapshot
{
    internal sealed record Row(string Name, int Pid, int ParentPid);
    private const int MaximumProcesses = 65536;

    internal static IReadOnlyList<BackendProcessTree.Entry> Read() =>
        Read(ReadRows, ReadStartTicks, () => DateTime.UtcNow.Ticks);

    internal static IReadOnlyList<BackendProcessTree.Entry> Read(Func<IReadOnlyList<Row>> enumerate,
        Func<int,long> startTicks, Func<long> utcTicks)
    {
        // A PID reused after snapshot creation must not inherit the old snapshot row's PPID.
        var began = utcTicks();
        var rows = enumerate();
        if (rows.Count == 0 || rows.Count > MaximumProcesses || began <= 0)
            throw new InvalidDataException("进程快照为空或超过读取上限，不能确认子进程状态。");
        var seen = new HashSet<int>();
        var result = new List<BackendProcessTree.Entry>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Pid == 0) continue; // Idle process, never an application descendant.
            if (row.Pid < 0 || row.ParentPid < 0 || !seen.Add(row.Pid))
                throw new InvalidDataException("进程快照身份重复或无效，不能确认子进程状态。");
            long created;
            try { created = startTicks(row.Pid); }
            catch { created = 0; }
            // Keep unreadable/disappeared identities in the tree as unknown. Dropping the row
            // could hide a live child or break a chain to a grandchild.
            if (created <= 0 || created > began) created = 0;
            result.Add(new(row.Name, row.Pid, row.ParentPid, created));
        }
        if (result.Count == 0) throw new InvalidDataException("未读取到有效进程快照。");
        return result;
    }

    private static IReadOnlyList<Row> ReadRows()
    {
        using var snapshot = CreateToolhelp32Snapshot(0x00000002, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var row = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
        if (!Process32FirstW(snapshot, ref row)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var rows = new List<Row>();
        do
        {
            if (rows.Count >= MaximumProcesses) throw new InvalidDataException("进程快照超过读取上限。");
            rows.Add(new(row.ExeFile, checked((int)row.Pid), checked((int)row.ParentPid)));
            row.Size = (uint)Marshal.SizeOf<ProcessEntry>();
        } while (Process32NextW(snapshot, ref row));
        var error = Marshal.GetLastWin32Error();
        if (error != 18) throw new Win32Exception(error); // ERROR_NO_MORE_FILES is the only complete ending.
        return rows;
    }

    private static long ReadStartTicks(int pid)
    {
        using var process = OpenProcess(0x1000, false, checked((uint)pid)); // QUERY_LIMITED_INFORMATION
        if (process.IsInvalid || !GetProcessTimes(process, out var created, out _, out _, out _)) return 0;
        var fileTime = ((ulong)created.High << 32) | created.Low;
        if (fileTime > long.MaxValue) return 0;
        try { return DateTime.FromFileTimeUtc((long)fileTime).Ticks; }
        catch (ArgumentOutOfRangeException) { return 0; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size, Usage, Pid;
        internal UIntPtr DefaultHeap;
        internal uint Module, Threads, ParentPid;
        internal int Priority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string ExeFile;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { internal uint Low, High; }
    private sealed class SnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SnapshotHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SnapshotHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SnapshotHandle snapshot, ref ProcessEntry row);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SnapshotHandle snapshot, ref ProcessEntry row);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
