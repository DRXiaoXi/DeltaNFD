using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.DpcTimerLab;

internal sealed class DpcDevice : IDpcDevice
{
    private readonly SafeFileHandle handle;
    public DpcDevice()
    {
        handle = CreateFile(@"\\.\dpclat_static_device", 0xc0000000, 0, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid) { var code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code, "CreateFile(dpclat_static_device): " + new Win32Exception(code).Message); }
    }
    public uint Start(uint microseconds)
    {
        Rules.Request(microseconds);
        if (!DeviceIoControl(handle, 0x81772008, ref microseconds, 4, out var actual, 4, out var bytes, IntPtr.Zero))
            throw NativeService.Failure("DeviceIoControl(start)");
        if (bytes != 4) throw new InvalidDataException("DPC reply must contain exactly four bytes.");
        return actual;
    }
    public uint Current100ns()
    {
        var status = NtQueryTimerResolution(out var maximum, out var minimum, out var current);
        if (status < 0 || minimum == 0 || maximum < minimum || current < minimum || current > maximum)
            throw new IOException($"NtQueryTimerResolution failed/invalid: 0x{status:x8}");
        return current;
    }
    public void Dispose() => handle.Dispose();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, ref uint input, uint inputSize, out uint output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint maximum, out uint minimum, out uint current);
}

internal sealed class NativeService : IDisposable
{
    private readonly IntPtr manager, service;
    [StructLayout(LayoutKind.Sequential)]
    internal struct State { public uint Type, Current, Controls, Exit, Specific, Checkpoint, Hint; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ConfigData { public uint Type, Start, Error; public IntPtr Path, Group; public uint Tag; public IntPtr Dependencies, Account, Display; }
    public static Win32Exception Failure(string operation)
    {
        int code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{operation} failed (Win32 {code}): {new Win32Exception(code).Message}");
    }
    public NativeService(string target, bool create = false)
    {
        manager = OpenSCManager(null, null, create ? 3u : 1u);
        if (manager == IntPtr.Zero) throw Failure("OpenSCManager");
        try
        {
            service = create ? CreateService(manager, Rules.Service, "DeltaNFD DPC local VMware experiment", 0x10035, 1, 3, 1,
                Rules.ImagePath(target), null, IntPtr.Zero, null, null, null) : OpenService(manager, Rules.Service, 0x10035);
            if (service == IntPtr.Zero) throw Failure(create ? "CreateService" : "OpenService");
            Verify(target);
        }
        catch { if (service != IntPtr.Zero) CloseServiceHandle(service); CloseServiceHandle(manager); throw; }
    }
    public void Verify(string target)
    {
        var buffer = Marshal.AllocHGlobal(8192);
        try
        {
            if (!QueryServiceConfig(service, buffer, 8192, out _)) throw Failure("QueryServiceConfig");
            var config = Marshal.PtrToStructure<ConfigData>(buffer);
            Rules.Config(config.Type, config.Start, Marshal.PtrToStringUni(config.Path) ?? "", target);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public State Query()
    {
        if (!QueryServiceStatus(service, out var result)) throw Failure("QueryServiceStatus");
        if (result.Type != 1) throw new InvalidDataException("Service is not a kernel driver.");
        return result;
    }
    public void Start()
    {
        if (Query().Current != 1) throw new IOException("Driver must be STOPPED before beginning a fresh experiment.");
        if (!StartService(service, 0, IntPtr.Zero)) throw Failure("StartService");
        if (Query().Current != 4) throw new IOException("Driver did not reach RUNNING.");
    }
    public void Stop()
    {
        uint state = Query().Current;
        if (state == 1) return;
        if (state != 4) throw new IOException("Driver is in a pending/unknown state; preserve the journal and check the VM.");
        if (!ControlService(service, 1, out _)) throw Failure("ControlService(stop)");
        for (int i = 0; i < 50; i++) { if (Query().Current == 1) return; Thread.Sleep(100); }
        throw new IOException("Driver did not stop; registration and journal have been preserved.");
    }
    public void Delete()
    {
        if (Query().Current != 1) throw new IOException("Refusing to delete a running driver service.");
        if (!DeleteService(service)) throw Failure("DeleteService");
    }
    public void Dispose() { CloseServiceHandle(service); CloseServiceHandle(manager); }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateService(IntPtr manager, string name, string display, uint access, uint type, uint start, uint error, string path, string? group, IntPtr tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(IntPtr service, out State state);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(IntPtr service, IntPtr buffer, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool StartService(IntPtr service, int count, IntPtr args);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(IntPtr service, uint control, out State state);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DeleteService(IntPtr service);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
}
