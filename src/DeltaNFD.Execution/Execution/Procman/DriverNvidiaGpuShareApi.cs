using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

// Direct path for one verified Windows x64 NVAPI build. The YieldPct call is private:
// never assume a later driver implements the same ABI. No NVIDIA App files are loaded here.
internal sealed class DriverNvidiaGpuShareApi : IDisposable
{
    private const string KnownHash = "4C7B6A7481ACA948B4ECB4AF4B71B8A8E77ADB00D484CC050BEF21D82942AA89";
    [StructLayout(LayoutKind.Sequential)] internal struct YieldV1
    { public uint Version, Command, Percent, Reserved1, Reserved2, Reserved3, Reserved4; }
    [StructLayout(LayoutKind.Sequential)] private struct ArchV2 { public uint Version, Architecture, Implementation, Revision; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Resolve(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Lifecycle();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Enumerate([Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] IntPtr[] handles, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Architecture(IntPtr gpu, ref ArchV2 arch);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DriverVersion(out uint version, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] byte[] branch);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Control(IntPtr gpu, ref YieldV1 data);
    private IntPtr _module;
    private FileStream? _pin;
    private Lifecycle? _unload;
    private Enumerate _enumerate = null!;
    private Architecture _arch = null!;
    private DriverVersion _version = null!;
    private Control _control = null!;

    private static void Require(bool ok, string message) { if (!ok) throw new IOException(message); }
    internal static bool Eligible(uint driver, uint count, uint architecture) => driver == 62114 && count == 1 && architecture == 0x1B0;
    internal static NvidiaGpuShareInfo Describe(int pct, string key)
    {
        // The audited NvApp wrapper itself supplies these bounds; the GET command returns the current value only.
        var info = new NvidiaGpuShareInfo { Supported = true, Current = pct, Default = 20, Minimum = 10, Maximum = 50, Step = 5, PresetMask = 15, HardwareKey = key };
        Require(info.Allows(pct), "驱动返回的原比例无法按已核验规则恢复，未接管。");
        return info;
    }
    internal static int ReadPercent(Control control, IntPtr gpu)
    {
        var data = new YieldV1 { Version = 0x1001c };
        var status = control(gpu, ref data);
        Require(status == 0 && data.Version == 0x1001c && data.Command == 0 && data.Percent <= 100, $"驱动未确认 NVIDIA 比例读取（状态 {status}）。");
        return (int)data.Percent;
    }
    internal static void WritePercent(Control control, IntPtr gpu, NvidiaGpuShareInfo expected, int pct)
    {
        Require(expected.Allows(pct) && ReadPercent(control, gpu) == expected.Current, "驱动比例或可恢复范围已变化，未写入。");
        var data = new YieldV1 { Version = 0x1001c, Command = 1, Percent = (uint)pct };
        var status = control(gpu, ref data);
        Require(status == 0 && data.Version == 0x1001c && data.Command == 1, $"驱动未确认 NVIDIA 比例写入（状态 {status}）。");
        Require(ReadPercent(control, gpu) == pct, "NVIDIA 驱动比例写后回读不一致。");
    }
    private IntPtr GetGpu()
    {
        var devices = new IntPtr[64]; var arch = new ArchV2 { Version = 0x20010 };
        Require(_version(out var version, new byte[64]) == 0 && version == 62114 &&
            _enumerate(devices, out var count) == 0 && count == 1 && devices[0] != IntPtr.Zero &&
            _arch(devices[0], ref arch) == 0 && Eligible(version, count, arch.Architecture),
            "当前独立模式仅支持已核验的 621.14 驱动和单张 Blackwell NVIDIA 显卡。");
        return devices[0];
    }
    internal static DriverNvidiaGpuShareApi Open()
    {
        var d = new DriverNvidiaGpuShareApi();
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
            for (var n = path; n != null; n = Path.GetDirectoryName(n))
                Require((File.GetAttributes(n) & FileAttributes.ReparsePoint) == 0, "NVIDIA 系统库路径包含重解析点。");
            d._pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Require(Convert.ToHexString(SHA256.HashData(d._pin)) == KnownHash && NativeNvidiaGpuShareApi.TrustedNvidia(path), "当前 NVIDIA 驱动库未列入独立模式兼容清单。");
            d._module = LoadLibraryEx(path, IntPtr.Zero, 0x800);
            var loaded = new StringBuilder(1024);
            Require(d._module != IntPtr.Zero && GetModuleFileName(d._module, loaded, loaded.Capacity) is > 0 and < 1024 &&
                string.Equals(loaded.ToString(), path, StringComparison.OrdinalIgnoreCase), "未能从系统目录载入已核验的 NVIDIA 驱动库。");
            var resolve = Marshal.GetDelegateForFunctionPointer<Resolve>(NativeLibrary.GetExport(d._module, "nvapi_QueryInterface"));
            T Get<T>(uint id) where T : Delegate { var ptr = resolve(id); Require(ptr != IntPtr.Zero, "当前驱动未公开所需比例接口。"); return Marshal.GetDelegateForFunctionPointer<T>(ptr); }
            var init = Get<Lifecycle>(0x0150e828); var close = Get<Lifecycle>(0xd22bdd7e);
            Require(init() == 0, "NVIDIA 驱动初始化失败。"); d._unload = close;
            d._enumerate = Get<Enumerate>(0xe5ac921f); d._arch = Get<Architecture>(0xd8265d24); d._version = Get<DriverVersion>(0x2926aaad);
            d.GetGpu(); d._control = Get<Control>(0xc742bb22); return d;
        }
        catch { d.Dispose(); throw; }
    }
    internal NvidiaGpuShareInfo Read(string key) => Describe(ReadPercent(_control, GetGpu()), key);
    internal void Write(NvidiaGpuShareInfo expected, int pct) => WritePercent(_control, GetGpu(), expected, pct);
    public void Dispose()
    {
        try { _unload?.Invoke(); } finally { _unload = null; if (_module != IntPtr.Zero) { NativeLibrary.Free(_module); _module = IntPtr.Zero; } _pin?.Dispose(); _pin = null; }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetModuleFileName(IntPtr module, StringBuilder path, int length);
}
