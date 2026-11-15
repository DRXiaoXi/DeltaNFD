using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Execution.OptimalClock;

/// <summary>Session-scoped connection to our separately installed signed Win10 driver.</summary>
internal sealed class PendulumKernelHolder : IOptimalClockHolderApi
{
    internal const uint Magic = 0x504e4644, Version = 1;
    internal const uint SetCode = 0x22e404, QueryCode = 0x22e408, ReleaseCode = 0x22e40c;
    internal const uint Finest100ns = 5000, Coarsest100ns = 10000;
    private SafeFileHandle? _device;
    private uint _requested, _granted;
    public bool KernelRequest => true;
    public uint Resolution100ns => _requested;
    public uint Granted100ns => _granted;
    public uint Current100ns => OptimalClockNative.QueryCurrent100ns();
    public string Error { get; private set; } = "";
    public bool Holding
    {
        get
        {
            if (_device is null || _device.IsClosed || _device.IsInvalid) return false;
            try
            {
                var status = Exchange(QueryCode, 0);
                return status.Active == 1 && status.Requested100ns == _requested && status.Granted100ns == _granted;
            }
            catch (Exception ex) { Error = ex.Message; return false; }
        }
    }

    public bool Ensure(uint resolution100ns)
    {
        if (resolution100ns is < Finest100ns or > Coarsest100ns) { Error = "Win10 驱动精度范围为 0.5000～1.0000ms。"; return false; }
        try
        {
            var preparation = DeltaNFD.Inspection.PendulumWin10DriverInspection.Read();
            if (!preparation.Ready) throw new InvalidOperationException(preparation.Message);
            if (_device is null || _device.IsClosed || _device.IsInvalid)
            {
                _device?.Dispose();
                _device = CreateFile(@"\\.\DeltaNFDPendulumTimerV1", 0xc0000000, 0, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
                if (_device.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "无法打开钟摆驱动。请先安装并启动随实验包提供、已完成签名的 DeltaNFDPendulumTimerV1 驱动。");
            }
            var response = Exchange(SetCode, resolution100ns);
            if (response.Active != 1 || response.Requested100ns != resolution100ns || response.Granted100ns == 0)
                throw new InvalidDataException("驱动未确认持有本次精度请求。");
            _requested = resolution100ns; _granted = response.Granted100ns; Error = "";
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            // Closing this exclusive device handle invokes the driver's cleanup release.
            _device?.Dispose(); _device = null; _requested = _granted = 0;
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            if (_device is { IsInvalid: false, IsClosed: false })
            {
                var response = Exchange(ReleaseCode, 0);
                if (response.Active != 0) throw new IOException("驱动未确认释放请求。");
            }
        }
        finally { _device?.Dispose(); _device = null; _requested = _granted = 0; }
    }

    private Packet Exchange(uint code, uint requested)
    {
        var input = new Packet { Magic = Magic, Version = Version, Requested100ns = requested };
        if (!DeviceIoControl(_device!, code, ref input, 24, out var output, 24, out var bytes, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (bytes != 24 || output.Magic != Magic || output.Version != Version || output.Reserved != 0 || output.Active > 1)
            throw new InvalidDataException("钟摆驱动返回的协议或长度不匹配。");
        return output;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Packet { public uint Magic, Version, Requested100ns, Granted100ns, Active, Reserved; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint attributes, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref Packet input, uint inputSize,
        out Packet output, uint outputSize, out uint returned, IntPtr overlapped);
}
