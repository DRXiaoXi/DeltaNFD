using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

/// <summary>Identifies present PCI display functions by their PnP class and device identity.</summary>
public static class DisplayInterruptInspection
{
    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
    private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum\";
    private const string DriverRoot = @"SYSTEM\CurrentControlSet\Control\Class\";
    private const string DeviceParameters = "Device Parameters";
    public const string PolicyPath = @"Interrupt Management\Affinity Policy";
    public const string ValueName = "DevicePriority";

    public static bool HasPresentNvidia() => Present().Any(d =>
        d.Id.StartsWith(@"PCI\VEN_10DE&", StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<DisplayInterruptAdapter> Read()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return Present().Select(d => Read(root, d.Id, d.Started)).ToArray();
    }

    public static RegistryKey OpenDevice(RegistryKey root, string id)
    {
        if (!DisplayInterruptPriorityPolicy.ValidId(id)) throw new IOException("设备不是有效的 PCI 显卡实例。");
        var device = root.OpenSubKey(EnumRoot + id) ?? throw new IOException("显卡设备登记已消失。");
        try
        {
            if (Guid.TryParse(device.GetValue("ClassGUID") as string, out var kind) && kind == DisplayClass) return device;
            throw new IOException("设备类别已变化，未确认是显示适配器。");
        }
        catch { device.Dispose(); throw; }
    }

    public static RegistryKey OpenParameters(RegistryKey device, bool writable = false) =>
        device.OpenSubKey(DeviceParameters, writable) ?? throw new IOException("无法打开显卡设备参数。");

    internal static uint? ReadPriority(RegistryKey parameters)
    {
        using var policy = parameters.OpenSubKey(PolicyPath);
        var raw = policy?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return raw switch
        {
            null => null,
            int value when policy?.GetValueKind(ValueName) == RegistryValueKind.DWord => unchecked((uint)value),
            _ => throw new IOException("当前中断优先级的登记类型无法核对。"),
        };
    }

    public static DisplayInterruptAdapter Read(RegistryKey root, RegistryKey device, string id, bool started, RegistryKey? parameters = null)
    {
        var name = id;
        var driverKey = "";
        var version = "";
        try
        {
            var label = device.GetValue("FriendlyName") as string ?? device.GetValue("DeviceDesc") as string;
            if (!string.IsNullOrWhiteSpace(label)) name = label.Split(';')[^1];
            driverKey = device.GetValue("Driver") as string ?? "";
            var prefix = DisplayClass.ToString("B") + "\\";
            if (driverKey.Length != prefix.Length + 4 || !driverKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !driverKey.AsSpan(prefix.Length).ToString().All(char.IsAsciiDigit))
                throw new IOException("尚未确认显卡驱动身份。");
            using var driver = root.OpenSubKey(DriverRoot + driverKey) ?? throw new IOException("显卡驱动登记已消失。");
            version = driver.GetValue("DriverVersion") as string ?? "";
            var inf = driver.GetValue("InfPath") as string;
            var service = device.GetValue("Service") as string;
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(inf) || string.IsNullOrWhiteSpace(service)
                || device.GetValue("HardwareID") is not string[] { Length: > 0 } hardware)
                throw new IOException("显卡硬件或驱动身份读取不完整。");
            if (driver.GetValue("DriverDesc") is string { Length: > 0 and <= 512 } originalName) name = originalName;
            using var opened = parameters is null ? OpenParameters(device) : null;
            var priority = ReadPriority(parameters ?? opened!);
            var identity = DisplayInterruptPriorityPolicy.Hash($"{id.ToUpperInvariant()}|{driverKey.ToUpperInvariant()}|{version}|{inf}|{service}|{string.Join(";", hardware)}");
            return new(id, name, version, driverKey, identity, started, true, priority);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(id, name, version, driverKey, "", started, false, null, ex.Message);
        }
    }

    private static DisplayInterruptAdapter Read(RegistryKey root, string id, bool started)
    {
        try
        {
            using var device = OpenDevice(root, id);
            return Read(root, device, id, started);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(id, id, "", "", "", started, false, null, ex.Message);
        }
    }

    private static IEnumerable<(string Id, bool Started)> Present()
    {
        var displayClass = DisplayClass;
        const uint present = 2;
        var handle = SetupDiGetClassDevsW(ref displayClass, "PCI", IntPtr.Zero, present);
        if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new DevInfo { Size = (uint)Marshal.SizeOf<DevInfo>() };
                if (!SetupDiEnumDeviceInfo(handle, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 259) throw new Win32Exception(error);
                    yield break;
                }
                if (index >= 128) throw new IOException("显卡设备列表未完整读取。");
                var id = new StringBuilder(513);
                if (!SetupDiGetDeviceInstanceIdW(handle, ref data, id, id.Capacity, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var instance = id.ToString();
                if (data.Class != DisplayClass || !DisplayInterruptPriorityPolicy.ValidId(instance)) continue;
                var started = CM_Get_DevNode_Status(out var status, out var problem, data.Node, 0) == 0
                    && problem == 0 && (status & 8) != 0;
                yield return (instance, started);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevInfo { public uint Size; public Guid Class; public uint Node; public IntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid kind, string enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr handle, uint index, ref DevInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr handle, ref DevInfo data, StringBuilder id, int length, out uint required);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr handle);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
}
