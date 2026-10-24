using System.ComponentModel;
using System.Runtime.InteropServices;
using DeltaNFD.Services.TweakDb;
using Microsoft.Win32;

namespace DeltaNFD.Inspection;

public sealed record MouseParameters(int Threshold1, int Threshold2, int Acceleration)
{
    public bool Valid => Threshold1 >= 0 && Threshold2 >= 0 && Acceleration is >= 0 and <= 2;
}

public sealed record HagsAdapterStatus(string Name, bool? Supported, bool? Enabled);
public sealed record HagsRuntimeStatus(bool Complete, IReadOnlyList<HagsAdapterStatus> Adapters, string Error = "");
public sealed record HagsSnapshot(BxState State, bool CanEnable, bool PendingRestart, string Detail);

/// <summary>Read-only native switch inspection. No settings, profile or registry writes.</summary>
public static class SystemSwitchInspection
{
    private static HagsSnapshot? _lastHags;
    public static HagsSnapshot? LastHags => Volatile.Read(ref _lastHags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryMouse(uint action, uint param, [Out] int[] values, uint flags);

    public static MouseParameters ReadMouse()
    {
        var values = new int[3];
        if (!QueryMouse(3, 0, values, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = new MouseParameters(values[0], values[1], values[2]);
        if (!result.Valid) throw new InvalidDataException("Windows 鼠标运行参数无效。");
        return result;
    }

    public static HagsSnapshot ReadHags()
    {
        HagsSnapshot result;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            var value = key?.GetValue("HwSchMode");
            if (value is not null && value is not (1 or 2))
                result = new(BxState.Unknown, false, false, "HAGS 配置值异常，待核对。");
            else result = EvaluateHags(value as int?, ReadHagsRuntime());
        }
        catch (Exception ex) { result = new(BxState.Unknown, false, false, "HAGS 状态待核对：" + ex.Message); }
        Volatile.Write(ref _lastHags, result);
        return result;
    }

    internal static HagsSnapshot EvaluateHags(int? requested, HagsRuntimeStatus runtime)
    {
        if (!runtime.Complete || runtime.Adapters.Count == 0 || runtime.Adapters.Any(a => a.Supported is null || a.Supported == true && a.Enabled is null))
            return new(BxState.Unknown, false, false, "HAGS 实际状态无法确认，待核对。" + runtime.Error);
        var supported = runtime.Adapters.Where(a => a.Supported == true).ToArray();
        if (supported.Length == 0)
            return new(BxState.NotApplicable, false, false, "当前显卡或驱动不支持 HAGS。");
        var enabled = supported.Count(a => a.Enabled == true);
        var current = enabled == supported.Length ? "已开启" : enabled == 0 ? "已关闭" : "部分显卡已开启";
        var devices = string.Join("；", runtime.Adapters.Select(a => a.Name + "：" + (a.Supported == false ? "不支持" : a.Enabled == true ? "开启" : "关闭")));
        var mixed = enabled > 0 && enabled < supported.Length;
        var pending = requested is 1 or 2 && (mixed || (requested == 2) != (enabled > 0));
        var state = requested is 1 or 2 ? requested == 2 ? BxState.On : BxState.Off
            : mixed ? BxState.Mixed : enabled > 0 ? BxState.On : BxState.Off;
        var text = pending ? $"已保存：{(requested == 2 ? "开启" : "关闭")} HAGS；当前{current}，需重启后核实。"
            : $"HAGS 当前{current}{(requested is null ? "（系统默认）" : "")}。";
        return new(state, true, pending, text + " " + devices);
    }

    internal static BxState HagsItemState(HagsSnapshot snapshot, BxItem item)
    {
        if (snapshot.State is not (BxState.On or BxState.Off)) return snapshot.State;
        var enable = item.Tweaks.Single(t => t.Key?.Equals("HwSchMode", StringComparison.OrdinalIgnoreCase) == true)
            .Values.First(v => !v.ValueTypes.Contains("DEFAULT")).Value == "2";
        return enable ? snapshot.State : snapshot.State == BxState.On ? BxState.Off : BxState.On;
    }

    // These queries only inspect WDDM capability bits. Unsupported APIs/layouts remain Unknown.
    // Native structs mirror the installed Windows SDK; no driver or scheduling state is changed.
    [StructLayout(LayoutKind.Sequential)] private struct Adapter { public uint Handle, LuidLow; public int LuidHigh; public uint Sources; public int Precise; }
    [StructLayout(LayoutKind.Sequential)] private struct Enumeration { public uint Count; public nint Adapters; }
    [StructLayout(LayoutKind.Sequential)] private struct Query { public uint Handle; public int Type; public nint Data; public uint Size; }
    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref Enumeration value);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref Query value);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref uint handle);

    public static HagsRuntimeStatus ReadHagsRuntime()
    {
        const int capacity = 64;
        var size = Marshal.SizeOf<Adapter>();
        var memory = Marshal.AllocHGlobal(size * capacity);
        var result = new List<HagsAdapterStatus>();
        var initialized = false;
        try
        {
            Marshal.Copy(new byte[size * capacity], 0, memory, size * capacity);
            initialized = true;
            var enumeration = new Enumeration { Count = capacity, Adapters = memory };
            var status = D3DKMTEnumAdapters2(ref enumeration);
            if (status != 0 || enumeration.Count > capacity)
                return new(false, [], $"显卡枚举失败（0x{status:X8}）。");
            for (var i = 0; i < enumeration.Count; i++)
            {
                var adapter = Marshal.PtrToStructure<Adapter>(memory + i * size);
                if (adapter.Handle == 0) { result.Add(new("显卡", null, null)); continue; }
                var flags = QueryBits(adapter.Handle, 15);
                if (flags is null) { result.Add(new("显卡", null, null)); continue; }
                // Ignore non-render, software, indirect-display and para-virtualized adapters.
                if ((flags.Value & 1) == 0 || (flags.Value & (4 | 64 | 128)) != 0) continue;
                var caps = QueryBits(adapter.Handle, 70);
                result.Add(new(ReadAdapterName(adapter.Handle, i), caps is null ? null : (caps & 1) != 0,
                    caps is null ? null : (caps & 2) != 0));
            }
            return new(true, result);
        }
        catch (Exception ex) { return new(false, result, ex.Message); }
        finally
        {
            var closed = new HashSet<uint>();
            for (var i = 0; initialized && i < capacity; i++)
            {
                var handle = Marshal.PtrToStructure<Adapter>(memory + i * size).Handle;
                if (handle != 0 && closed.Add(handle)) try { D3DKMTCloseAdapter(ref handle); } catch { }
            }
            Marshal.FreeHGlobal(memory);
        }
    }

    private static uint? QueryBits(uint handle, int type)
    {
        var data = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(data, 0);
            var query = new Query { Handle = handle, Type = type, Data = data, Size = 4 };
            return D3DKMTQueryAdapterInfo(ref query) == 0 ? unchecked((uint)Marshal.ReadInt32(data)) : null;
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    private static string ReadAdapterName(uint handle, int index)
    {
        var data = Marshal.AllocHGlobal(2080);
        try
        {
            Marshal.Copy(new byte[2080], 0, data, 2080);
            var query = new Query { Handle = handle, Type = 8, Data = data, Size = 2080 };
            if (D3DKMTQueryAdapterInfo(ref query) == 0 && Marshal.PtrToStringUni(data, 260)?.TrimEnd('\0') is { Length: > 0 } name) return name;
            return "显卡 " + (index + 1);
        }
        finally { Marshal.FreeHGlobal(data); }
    }
}
