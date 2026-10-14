using System.ComponentModel;
using System.Runtime.InteropServices;
using DeltaNFD.Services;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Execution.Procman;

internal static class ProcmanGlobalMemoryReclaimer
{
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivilege { public uint Count; public uint Low; public int High; public uint Attributes; }
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivilege next, uint size, out TokenPrivilege previous, out uint returned);
    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RestorePrivilege(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivilege previous, uint size, nint ignored, nint returned);
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int information, ref int command, uint length);
    internal static ProcmanMemoryTrimReport Trim(Func<bool> allowed)
    {
        var before = ProcmanMemoryTelemetryReader.Read(); var items = new List<ProcmanMemoryTrimItem>();
        if (!allowed()) return new([], "全局回收已取消或条件变化，未执行。", before, before);
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x28, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            {
                if (!LookupPrivilegeValueW(null, "SeProfileSingleProcessPrivilege", out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var next = new TokenPrivilege { Count = 1, Low = (uint)luid, High = (int)(luid >> 32), Attributes = 2 };
                if (!AdjustTokenPrivileges(token, false, ref next, (uint)Marshal.SizeOf<TokenPrivilege>(), out var previous, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (Marshal.GetLastWin32Error() == 1300) throw new UnauthorizedAccessException("当前令牌缺少全局回收特权，请以管理员身份运行。");
                try
                {
                    items.AddRange(ExecuteCommands(allowed, command => NtSetSystemInformation(80, ref command, sizeof(int))));
                }
                finally
                {
                    if (previous.Count > 0 && (!RestorePrivilege(token, false, ref previous, 0, 0, 0) || Marshal.GetLastWin32Error() == 1300))
                        items.Add(new(0, "回收特权还原", "failed", "未确认令牌特权还原：" + Marshal.GetLastWin32Error()));
                }
            }
        }
        catch (Exception ex) { items.Add(new(0, "全局回收", "failed", ex.Message)); }
        return new(items, "激进回收：未枚举进程，直接请求系统全局工作集与待备列表回收。范围包括三角洲、前台和系统，可能导致重新调页及短暂卡顿。", before, ProcmanMemoryTelemetryReader.Read());
    }
    internal static IReadOnlyList<ProcmanMemoryTrimItem> ExecuteCommands(Func<bool> allowed, Func<int, int> request)
    {
        var items = new List<ProcmanMemoryTrimItem>();
        foreach (var (command, name) in new[] { (2, "系统全局工作集回收"), (4, "系统待备列表回收") })
        {
            if (!allowed()) { items.Add(new(0, name, "skipped", "条件变化，停止后续全局步骤。")); break; }
            var status = request(command);
            items.Add(new(0, name, status >= 0 ? "completed" : "failed", status >= 0
                ? "系统已接受全局回收请求；不保证可用内存永久增加。" : $"系统拒绝全局回收，NTSTATUS=0x{unchecked((uint)status):X8}。"));
        }
        return items;
    }
}
