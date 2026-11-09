using System.Runtime.InteropServices;
using Microsoft.Win32;
using DeltaNFD.Services.TweakDb;

namespace DeltaNFD.Inspection;

public sealed record SecurityServiceEvidence(bool? Exists, int? Start, uint? RuntimeState, string Error = "");
public sealed record SecurityFeatureSnapshot(BxState State, bool ConfiguredDisabled, string Detail);

/// <summary>Fixed-name, query-only SCM evidence. Missing optional components and UI startup settings do not prove protection is running.</summary>
public static class SecurityFeatureInspection
{
    public static bool Handles(string name) => name is "DefenderName" or "SecurityHealthService";
    public static SecurityFeatureSnapshot Read(string name) => Evaluate(name, ReadService(name == "DefenderName" ? "WinDefend" : "SecurityHealthService"),
        name == "DefenderName" ? ReadService("WdNisSvc") : null);
    public static SecurityFeatureSnapshot Evaluate(string name, SecurityServiceEvidence primary, SecurityServiceEvidence? network = null)
    {
        if (!Handles(name)) return new(BxState.Unknown, false, "条目不在固定安全状态检测范围。");
        if (primary.Exists == false) return new(BxState.NotApplicable, false, "核心服务不存在；不冒充已禁用或已恢复。");
        var configured = primary.Start == 4;
        if (primary.Exists != true || primary.Start is null || primary.RuntimeState is null)
            return new(BxState.Unknown, configured, "核心服务配置或运行状态不可读，不能确认已禁用。" + primary.Error);
        if (configured && primary.RuntimeState == 1)
        {
            if (network is { Exists: true, RuntimeState: null })
                return new(BxState.Unknown, true, "Defender 核心服务已禁用并停止，但网络检查服务状态尚未确认。");
            if (network is { Exists: null }) return new(BxState.Unknown, true, "无法确认 Defender 网络检查服务是否存在或运行。");
            if (network is { Exists: true, RuntimeState: not 1 })
                return new(BxState.Mixed, true, "核心服务已禁用并停止，网络检查服务尚未停止，需重启后核对。");
            return new(BxState.On, true, "核心服务已配置禁用且确认停止；策略兼容项和托盘启动项不作为核心是否运行的证据。");
        }
        if (configured) return new(BxState.Mixed, true, "已保存禁用启动配置，核心服务尚未停止；需手动重启后核对。");
        if (primary.RuntimeState == 1) return new(BxState.Mixed, false, "核心服务当前停止，但未配置禁用；不能视为持续禁用。");
        return primary.RuntimeState == 4 ? new(BxState.Off, false, "核心服务未禁用且正在运行。")
            : new(BxState.Mixed, false, "核心服务处于启动或停止过渡状态，需刷新后核对。");
    }
    public static SecurityServiceEvidence ReadService(string name)
    {
        if (name is not ("WinDefend" or "WdNisSvc" or "Sense" or "SecurityHealthService" or "SysMain"))
            return new(null, null, null, "服务名不在固定只读范围。");
        int? start = null;
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
            start = key?.GetValue("Start") as int?;
        }
        catch { }
        var manager = OpenSCManagerW(null, null, 1);
        if (manager == IntPtr.Zero) return new(null, start, null, "无法打开服务只读查询句柄。");
        try
        {
            var service = OpenServiceW(manager, name, 4);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                return new(error == 1060 ? false : null, start, null, "服务查询错误 " + error);
            }
            try
            {
                return QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _)
                    ? new(true, start, status.CurrentState) : new(true, start, null, "无法读取服务运行状态。");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint Type, CurrentState, Controls, Win32ExitCode, ServiceExitCode, Checkpoint, WaitHint, ProcessId, Flags;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string service, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr service);
}
