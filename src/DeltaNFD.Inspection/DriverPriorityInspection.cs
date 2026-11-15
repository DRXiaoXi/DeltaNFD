using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

public sealed record DriverPriorityDiscovery(IReadOnlyList<DriverPriorityDevice> Devices, IReadOnlyList<string> Errors);

/// <summary>Present PCI devices only. Registry values are configuration, not proof of active interrupt mode.</summary>
public static class DriverPriorityInspection
{
    public const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum\";
    public const string MsiPath = @"Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
    public const string PriorityPath = @"Device Parameters\Interrupt Management\Affinity Policy";
    private static readonly Guid PciProperties = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");
    private static readonly Guid DriverProperties = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");
    [StructLayout(LayoutKind.Sequential)] private struct DevInfo { public uint Size; public Guid Class; public uint Node; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
    public static DriverPriorityDiscovery Discover()
    {
        var devices = new List<DriverPriorityDevice>(); var errors = new List<string>();
        var set = SetupDiGetClassDevsW(IntPtr.Zero, "PCI", IntPtr.Zero, 6);
        if (set == new IntPtr(-1)) return new([], [new Win32Exception(Marshal.GetLastWin32Error()).Message]);
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new DevInfo { Size = (uint)Marshal.SizeOf<DevInfo>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref data))
                { var code = Marshal.GetLastWin32Error(); if (code != 259) errors.Add("PCI枚举未完成：" + code); break; }
                if (i >= 1024) { errors.Add("PCI设备数量超过1024，列表未完整读取。"); break; }
                try
                {
                    var id = new StringBuilder(1024);
                    if (!SetupDiGetDeviceInstanceIdW(set, ref data, id, id.Capacity, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var instance = id.ToString(); if (!DriverPriorityPolicy.ValidId(instance)) throw new IOException("设备实例路径无效。");
                    var name = RegProperty(set, ref data, 12); if (name.Length == 0) name = RegProperty(set, ref data, 0);
                    var driver = RegProperty(set, ref data, 9); var service = RegProperty(set, ref data, 4);
                    var hardware = RegProperty(set, ref data, 1); var className = RegProperty(set, ref data, 7);
                    var version = StringProperty(set, ref data, DriverProperties, 3);
                    var modes = IntProperty(set, ref data, PciProperties, 14);
                    var maximum = IntProperty(set, ref data, PciProperties, 15);
                    if (modes > 7) modes = null; if (maximum > 2048) maximum = null;
                    var started = CM_Get_DevNode_Status(out var status, out var problem, data.Node, 0) == 0 && problem == 0 && (status & 8) != 0;
                    var identity = DriverPriorityPolicy.Hash(instance.ToUpperInvariant() + "|" + data.Class + "|" + driver + "|" + service + "|" + hardware + "|" + version + "|" + modes + "|" + maximum);
                    DriverInterruptSettings? settings = null; var detail = "";
                    try { using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                        using var key = root.OpenSubKey(EnumRoot + instance) ?? throw new IOException("设备登记已变化。"); settings = ReadSettings(key); }
                    catch (Exception ex) { detail = "中断设置读取未完成：" + ex.Message; }
                    if (!started) detail += " 设备未确认正常运行，仅供查看。";
                    if (version.Length == 0 || driver.Length == 0) detail += " 驱动身份未完整读取，仅供查看。";
                    devices.Add(new(instance, name.Length == 0 ? instance : name, className, version, driver, identity, modes, maximum,
                        started, settings, ReadIrqs(data.Node), detail.Trim()));
                }
                catch (Exception ex) { errors.Add("读取PCI设备失败：" + ex.Message); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return new(devices, errors);
    }
    public static DriverPriorityDevice? Read(string id)
    {
        if (!DriverPriorityPolicy.ValidId(id)) throw new InvalidDataException("只允许已枚举的PCI设备实例。");
        var result = Discover(); if (result.Errors.Count > 0) throw new IOException(string.Join("；", result.Errors));
        return result.Devices.SingleOrDefault(d => d.InstanceId.Equals(id, StringComparison.OrdinalIgnoreCase));
    }
    public static DriverInterruptSettings ReadSettings(RegistryKey device)
    {
        using var management = device.OpenSubKey(@"Device Parameters\Interrupt Management") ?? throw new IOException("设备没有可核对的中断管理登记，仅供查看。");
        using var msi = device.OpenSubKey(MsiPath); using var affinity = device.OpenSubKey(PriorityPath);
        var settings = new DriverInterruptSettings(ReadSlot(msi, "MSISupported"), ReadSlot(msi, "MessageNumberLimit"), ReadSlot(affinity, "DevicePriority"));
        DriverPriorityPolicy.Validate(settings); return settings;
    }
    private static InterruptDword ReadSlot(RegistryKey? key, string name)
    {
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return default;
        if (key.GetValueKind(name) != RegistryValueKind.DWord || key.GetValue(name) is not int value)
            throw new IOException(name + "不是DWORD，未将未知值当作默认。");
        return new(true, unchecked((uint)value));
    }
    private static string RegProperty(IntPtr set, ref DevInfo data, uint property)
    {
        var buffer = new byte[65536];
        return SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out var type, buffer, buffer.Length, out var size)
            && type is 1 or 7 && size <= buffer.Length && size % 2 == 0 ? Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0') : "";
    }
    private static uint? IntProperty(IntPtr set, ref DevInfo data, Guid format, uint id)
    {
        var key = new PropertyKey(format, id); var buffer = new byte[4];
        return SetupDiGetDevicePropertyW(set, ref data, ref key, out var type, buffer, 4, out var size, 0) && type == 7 && size == 4
            ? BitConverter.ToUInt32(buffer) : null;
    }
    private static string StringProperty(IntPtr set, ref DevInfo data, Guid format, uint id)
    {
        var key = new PropertyKey(format, id); var buffer = new byte[65536];
        return SetupDiGetDevicePropertyW(set, ref data, ref key, out var type, buffer, buffer.Length, out var size, 0)
            && type == 18 && size <= buffer.Length && size % 2 == 0 ? Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0') : "";
    }
    private static IReadOnlyList<int> ReadIrqs(uint node)
    {
        var values = new List<int>();
        // Only allocated resources; forced/boot configurations are not asserted to be active.
        if (CM_Get_First_Log_Conf(out var config, node, 2) != 0) return values;
        var previous = config;
        try
        {
            for (var i = 0; i < 2048; i++)
            {
                if (CM_Get_Next_Res_Des(out var next, previous, 4, out _, 0) != 0) break;
                if (previous != config) CM_Free_Res_Des_Handle(previous); previous = next;
                if (CM_Get_Res_Des_Data_Size(out var size, next, 0) != 0 || size < 16 || size > 65536) continue;
                var bytes = new byte[size];
                if (CM_Get_Res_Des_Data(next, bytes, size, 0) == 0) values.Add(BitConverter.ToInt32(bytes, 12));
            }
        }
        finally { if (previous != config) CM_Free_Res_Des_Handle(previous); CM_Free_Log_Conf_Handle(config); }
        return values;
    }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(IntPtr guid, string enumerator, IntPtr owner, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DevInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DevInfo data, StringBuilder value, int length, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DevInfo data, uint property, out uint type, byte[] buffer, int length, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref DevInfo data, ref PropertyKey key, out uint type, byte[] buffer, int length, out uint required, uint flags);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_First_Log_Conf(out IntPtr config, uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Next_Res_Des(out IntPtr next, IntPtr previous, uint resource, out uint resourceId, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Res_Des_Data_Size(out uint size, IntPtr resource, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Res_Des_Data(IntPtr resource, byte[] bytes, uint size, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Free_Res_Des_Handle(IntPtr resource);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Free_Log_Conf_Handle(IntPtr config);
}
