using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

/// <summary>
/// Fixed NvCplApi v1 YieldPct calls, statically verified against NVIDIA App 11.0.9.585.
/// Uses the player's installed, trusted NVIDIA App library; never bundles or patches vendor code.
/// </summary>
internal sealed class NativeNvidiaGpuShareApi : INvidiaGpuShareApi
{
    internal const string VerifiedLibrarySha256 = "a7703de492d47d4e75d6f77e7350a860b927f35be719b5e78a94e4d50d236da1";
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct InfoV1
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4)] public byte Supported;
        [FieldOffset(8)] public int Current;
        [FieldOffset(12)] public int Default;
        [FieldOffset(16)] public int Minimum;
        [FieldOffset(20)] public int Maximum;
        [FieldOffset(24)] public int Step;
        [FieldOffset(28)] public uint PresetMask;
    }
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct SetV1
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4)] public int Percent;
        [FieldOffset(8)] public byte Written;
        [FieldOffset(12)] public int Error;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate int Setting(uint scope, ref ulong target, uint id, IntPtr data, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Init(uint flags, IntPtr reserved);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Close();
    private DriverNvidiaGpuShareApi? _driver;
    private IntPtr _library;
    private FileStream? _pin;
    private Setting? _get, _set;
    private Close? _close;
    private readonly object _gate = new();

    internal static NvidiaGpuShareInfo Query(Setting get, string hardwareKey)
    {
        var pointer = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.StructureToPtr(new InfoV1 { Version = 1 }, pointer, false);
            ulong target = 0;
            var status = get(1, ref target, 0x346, pointer, 32);
            if (status != 0) throw new IOException($"NVIDIA 比例查询失败：{status}");
            var value = Marshal.PtrToStructure<InfoV1>(pointer);
            if (value.Version != 1 || value.Supported > 1 || value.Supported == 1 && (value.Current is < 0 or > 100 || value.Default is < 0 or > 100 ||
                value.Minimum is < 0 or > 100 || value.Maximum is < 0 or > 100 || value.Minimum > value.Maximum || value.Step is < 1 or > 100)
                )
                throw new IOException("NVIDIA 比例回读的结构或范围无法确认。");
            return new() { Supported = value.Supported == 1, HardwareKey = hardwareKey, Current = value.Current, Default = value.Default,
                Minimum = value.Minimum, Maximum = value.Maximum, Step = value.Step, PresetMask = value.PresetMask };
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    internal static void Apply(Setting set, int percent)
    {
        if (percent is < 0 or > 100) throw new IOException("NVIDIA 后台比例越界。");
        var pointer = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.StructureToPtr(new SetV1 { Version = 1, Percent = percent }, pointer, false);
            ulong target = 0;
            var status = set(1, ref target, 0x347, pointer, 16);
            var result = Marshal.PtrToStructure<SetV1>(pointer);
            if (status != 0 || result.Version != 1 || result.Percent != percent || result.Written != 1 || result.Error != 0)
                throw new IOException($"NVIDIA 比例写入未被确认：API={status}，Written={result.Written}，Error={result.Error}");
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    private static string HardwareKey()
    {
        var devices = new List<string>();
        using var search = new ManagementObjectSearcher("SELECT PNPDeviceID, Status FROM Win32_VideoController");
        search.Options.Timeout = TimeSpan.FromSeconds(2);
        using var values = search.Get();
        foreach (var value in values)
            using (value)
                if (value["PNPDeviceID"] is string id && id.StartsWith(@"PCI\VEN_10DE&", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(value["Status"] as string, "OK", StringComparison.OrdinalIgnoreCase)) devices.Add(id.ToUpperInvariant());
        if (devices.Count == 0) throw new IOException("未核验到当前 NVIDIA 显卡。");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", devices.Distinct().Order()))));
    }
    private void EnsureLoaded()
    {
        if (_driver != null || _library != IntPtr.Zero) return;
        string directUnavailable;
        try { _driver = DriverNvidiaGpuShareApi.Open(); return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException or System.Security.SecurityException)
        { directUnavailable = ex.Message; }
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVIDIA app", "NvCpl", "nvcpl.dll");
        if (!File.Exists(path)) throw new IOException($"独立驱动模式不可用：{directUnavailable} 未安装可供兼容模式使用的 NVIDIA App。");
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("NVIDIA App 库路径包含重解析点，未加载。");
        _pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!TrustedNvidia(path)) throw new IOException("NVIDIA App 库签名或发布者无法确认。");
            var hash = Convert.ToHexString(SHA256.HashData(_pin)); _pin.Position = 0;
            if (!hash.Equals(VerifiedLibrarySha256, StringComparison.OrdinalIgnoreCase) ||
                FileVersionInfo.GetVersionInfo(path).ProductVersion?.Trim() != "11.0.9.585")
                throw new IOException("当前 NVIDIA App 库尚未通过本版兼容核验，未调用内部设置接口。请使用 NVIDIA App 调整，或更新工具兼容清单。");
            _library = LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x800);
            if (_library == IntPtr.Zero) throw new IOException("无法加载 NVIDIA App 库：" + Marshal.GetLastWin32Error());
            T Function<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
            _get = Function<Setting>("NvCplApiGetSetting"); _set = Function<Setting>("NvCplApiSetSetting");
            var init = Function<Init>("NvCplApiInit");
            if (init(0, IntPtr.Zero) != 0) throw new IOException("NVIDIA App API 初始化失败。");
            _close = Function<Close>("NvCplApiClose");
        }
        catch { Dispose(); throw; }
    }
    public NvidiaGpuShareInfo Read()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var key = HardwareKey();
            return (_driver != null ? _driver.Read(key) : Query(_get!, key)) with
            { HagsEnabled = DeltaNFD.Inspection.SystemSwitchInspection.ReadHags().State == DeltaNFD.Services.TweakDb.BxState.On };
        }
    }
    public void Write(NvidiaGpuShareInfo expected, int percent)
    {
        lock (_gate)
        {
            var current = Read();
            if (expected.HardwareKey != current.HardwareKey || expected.Current != current.Current || !current.Allows(percent))
                throw new IOException("NVIDIA 显卡、比例或支持范围在写入前已变化。");
            if (_driver != null) _driver.Write(current, percent); else Apply(_set!, percent);
            var actual = Read();
            if (actual.HardwareKey != current.HardwareKey || actual.Current != percent) throw new IOException("NVIDIA 比例写后回读不一致。");
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            _driver?.Dispose(); _driver = null;
            if (_library != IntPtr.Zero) { try { _close?.Invoke(); } finally { NativeLibrary.Free(_library); _library = IntPtr.Zero; _close = null; } }
            _pin?.Dispose(); _pin = null; _get = _set = null;
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);

    internal static bool TrustedNvidia(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, pointer, false);
            var trust = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Choice = 1, File = pointer, Flags = 0x1000 };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref trust) != 0) return false;
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return certificate.GetNameInfo(X509NameType.SimpleName, false) == "NVIDIA Corporation";
        }
        catch { return false; }
        finally { Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFile
    { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    { public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Choice; public IntPtr File; public uint State; public IntPtr StateData, Url; public uint Flags, Context; }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
