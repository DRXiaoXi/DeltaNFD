using System.Runtime.InteropServices;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;
// ---------------- NVAPI DRS 互操作 ----------------
// 接口 ID 与结构体布局依据 NVIDIA Profile Inspector（Orbmu2k）开源实现 + 官方 NVAPI 文档，
// 并已在本机驱动（596.36）上用只读探针实测验证（见 tools\NvProbe）。
// 说明：SetSetting/GetSetting 的 5 参数形态与 Inspector 一致（尾部两参数为保留位，传 0）。
/// <summary>NVDRS_SETTING_V1（只支持 DWORD 值的简化封装；union 区域按 4100 字节原始缓冲处理）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
public struct NvDrsSetting
{
    public uint version;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
    public string settingName;
    public uint settingId;
    /// <summary>NVDRS_SETTING_TYPE：0=DWORD 1=Binary 2=String 3=WString 4=QWord。</summary>
    public int settingType;
    /// <summary>NVDRS_SETTING_LOCATION：0=当前配置 1=全局 2=基础 3=默认。</summary>
    public int settingLocation;
    public uint isCurrentPredefined;
    public uint isPredefinedValid;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4100)]
    public byte[] predefinedValue;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4100)]
    public byte[] currentValue;
    public const uint TypeDword = 0;
    public const uint LocationCurrentProfile = 0;
}

/// <summary>NVDRS_PROFILE_V1。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
public struct NvDrsProfile
{
    public uint version;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
    public string profileName;
    public uint gpuSupport;
    public uint isPredefined;
    public uint numOfApps;
    public uint numOfSettings;
}

/// <summary>NVDRS_APPLICATION_V4（与 Inspector 同构）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
public struct NvDrsApplication
{
    public uint version;
    public uint isPredefined;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
    public string appName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
    public string userFriendlyName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
    public string launcher;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
    public string fileInFolder;
    public uint bitvector1;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
    public string commandLine;
}

/// <summary>nvapi64.dll DRS（驱动配置仓库）互操作封装。线程安全（全局锁）。</summary>
public static class NvShaderInspection
{
    private const uint IdNvApiInitialize = 0x0150E828;
    private const uint IdDrsCreateSession = 0x0694D52E;
    private const uint IdDrsDestroySession = 0xDAD9CFF8;
    private const uint IdDrsLoadSettings = 0x375DBD6B;
    private const uint IdDrsFindApplicationByName = 0xEEE566B2;
    private const uint IdDrsFindProfileByName = 0x7E4A9A0B;
    private const uint IdDrsGetProfileInfo = 0x61CD6FD6;
    private const uint IdDrsGetBaseProfile = 0xDA8466A0;
    private const uint IdDrsGetCurrentGlobalProfile = 0x617BFF9F;
    private const uint IdDrsGetSetting = 0xEA99498D;
    private const uint IdDrsGetSettingOld = 0x73BF8338;
    private const uint IdDrsEnumAvailableSettingIds = 0xE5DE48E5;
    private const uint IdDrsEnumAvailableSettingIdsOld = 0xF020614A;
    private const uint IdDrsGetSettingNameFromId = 0x1EB13791;
    private const uint IdDrsGetSettingNameFromIdOld = 0xD61CBE6E;
    public const int StatusOk = 0;
    public const int StatusError = -1;
    public const int StatusInvalidArgument = -5;
    public const int StatusIncompatibleStructVersion = -9;
    public const int StatusSettingNotFound = -160;
    public const int StatusProfileNotFound = -163;
    public const int StatusExecutableNotFound = -166;
    private static readonly object Gate = new();
    private static bool _initialized;
    private static bool _available;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr QueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VoidFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SessionFn(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateSessionFn(ref IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetGlobalProfileFn(IntPtr session, ref IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FindAppFn(IntPtr session, [MarshalAs(UnmanagedType.LPWStr)] string appName, ref IntPtr profile, ref NvDrsApplication application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FindProfileFn(IntPtr session, [MarshalAs(UnmanagedType.LPWStr)] string profileName, ref IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetProfileInfoFn(IntPtr session, IntPtr profile, ref NvDrsProfile profileInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetSettingFn(IntPtr session, IntPtr profile, uint settingId, ref NvDrsSetting setting, ref uint reserved);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumIdsFn(IntPtr ids, ref uint maxCount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NameFromIdFn(uint settingId, IntPtr nameBuffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string fileName);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);
    private static VoidFn? _initialize;
    private static CreateSessionFn? _createSession;
    private static SessionFn? _destroySession;
    private static SessionFn? _loadSettings;
    private static FindAppFn? _findApp;
    private static FindProfileFn? _findProfile;
    private static GetProfileInfoFn? _getProfileInfo;
    private static GetGlobalProfileFn? _getBaseProfile;
    private static GetGlobalProfileFn? _getCurrentGlobalProfile;
    private static GetSettingFn? _getSetting;
    private static EnumIdsFn? _enumIds;
    private static NameFromIdFn? _nameFromId;
    public static bool IsAvailable
    {
        get
        {
            lock (Gate)
            {
                EnsureInitialized();
                return _available;
            }
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            var lib = LoadLibrary(Environment.Is64BitProcess ? "nvapi64.dll" : "nvapi.dll");
            if (lib == IntPtr.Zero)
            {
                return;
            }

            var qiPtr = GetProcAddress(lib, "nvapi_QueryInterface");
            if (qiPtr == IntPtr.Zero)
            {
                return;
            }

            var qi = (QueryInterface)Marshal.GetDelegateForFunctionPointer(qiPtr, typeof(QueryInterface));
            T? Resolve<T>(uint id, uint fallbackId)
                where T : System.Delegate
            {
                var p = qi(id);
                if (p == IntPtr.Zero && fallbackId != 0)
                {
                    p = qi(fallbackId);
                }

                return p == IntPtr.Zero ? null : (T)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
            }

            _initialize = Resolve<VoidFn>(IdNvApiInitialize, 0);
            _createSession = Resolve<CreateSessionFn>(IdDrsCreateSession, 0);
            _destroySession = Resolve<SessionFn>(IdDrsDestroySession, 0);
            _loadSettings = Resolve<SessionFn>(IdDrsLoadSettings, 0);
            _findApp = Resolve<FindAppFn>(IdDrsFindApplicationByName, 0);
            _findProfile = Resolve<FindProfileFn>(IdDrsFindProfileByName, 0);
            _getProfileInfo = Resolve<GetProfileInfoFn>(IdDrsGetProfileInfo, 0);
            _getBaseProfile = Resolve<GetGlobalProfileFn>(IdDrsGetBaseProfile, 0);
            _getCurrentGlobalProfile = Resolve<GetGlobalProfileFn>(IdDrsGetCurrentGlobalProfile, 0);
            _getSetting = Resolve<GetSettingFn>(IdDrsGetSetting, IdDrsGetSettingOld);
            _enumIds = Resolve<EnumIdsFn>(IdDrsEnumAvailableSettingIds, IdDrsEnumAvailableSettingIdsOld);
            _nameFromId = Resolve<NameFromIdFn>(IdDrsGetSettingNameFromId, IdDrsGetSettingNameFromIdOld);
            if (_initialize is null || _createSession is null || _destroySession is null || _loadSettings is null || _findApp is null || _getSetting is null)
                return;
            _available = _initialize() == StatusOk;
        }
        catch
        {
            _available = false;
        }
    }

    public static string DescribeStatus(int status) => status switch
    {
        StatusOk => "成功",
        StatusError => "NVAPI 通用错误",
        StatusInvalidArgument => "参数无效",
        StatusIncompatibleStructVersion => "结构体版本不兼容（驱动过旧或过新）",
        StatusSettingNotFound => "设置不存在",
        StatusProfileNotFound => "配置不存在",
        StatusExecutableNotFound => "程序未在配置中登记",
        _ => $"NVAPI 状态 {status}",
    };
    internal const uint ShaderDiskCacheSizeId = 0x00AC8497;
    public static DeltaNFD.Services.NvShaderCacheReport ReadShaderCacheSizes()
    {
        var scopes = new List<DeltaNFD.Services.NvShaderCacheScope>();
        lock (Gate)
        {
            if (!EnsureSession(out var session, out var error))
                return new()
                {
                    Scopes = [new("全局与三角洲", "unknown", null, error)]
                };
            try
            {
                IntPtr ReadGlobal(GetGlobalProfileFn? reader, string scope)
                {
                    var handle = IntPtr.Zero;
                    int status;
                    try
                    {
                        status = reader is null ? StatusError : reader(session, ref handle);
                    }
                    catch (Exception ex)
                    {
                        scopes.Add(new(scope, "unknown", null, ex.Message));
                        return IntPtr.Zero;
                    }

                    if (status != StatusOk || handle == IntPtr.Zero)
                        scopes.Add(new(scope, "unknown", null, "读取配置失败：" + DescribeStatus(status)));
                    return status == StatusOk ? handle : IntPtr.Zero;
                }

                void ReadScope(IntPtr profile, string scope)
                {
                    if (profile == IntPtr.Zero)
                        return;
                    try
                    {
                        var setting = NewSetting(ShaderDiskCacheSizeId);
                        var reserved = 0u;
                        var status = _getSetting!(session, profile, ShaderDiskCacheSizeId, ref setting, ref reserved);
                        scopes.Add(ClassifyShaderCache(scope, status, setting));
                    }
                    catch (Exception ex)
                    {
                        scopes.Add(new(scope, "unknown", null, ex.Message));
                    }
                }

                var global = ReadGlobal(_getCurrentGlobalProfile, "全局设置");
                ReadScope(global, "全局设置");
                var baseline = ReadGlobal(_getBaseProfile, "全局基础设置");
                if (baseline != global)
                    ReadScope(baseline, "全局基础设置");
                var app = NewApplication(DeltaNFD.Services.DeltaForceLocator.GameProcessName + ".exe");
                var game = IntPtr.Zero;
                int found;
                try
                {
                    found = _findApp!(session, app.appName, ref game, ref app);
                }
                catch (Exception ex)
                {
                    scopes.Add(new("三角洲程序设置", "unknown", null, ex.Message));
                    return new()
                    {
                        Scopes = scopes
                    };
                }

                if (found is StatusExecutableNotFound or StatusProfileNotFound)
                    scopes.Add(new("三角洲程序设置", "inherited", null, "没有专用配置，沿用全局设置。"));
                else if (found != StatusOk || game == IntPtr.Zero)
                    scopes.Add(new("三角洲程序设置", "unknown", null, "读取配置失败：" + DescribeStatus(found)));
                else
                    ReadScope(game, "三角洲程序设置");
            }
            finally
            {
                _destroySession!(session);
            }
        }

        return new()
        {
            Scopes = scopes
        };
    }

    internal static DeltaNFD.Services.NvShaderCacheScope ClassifyShaderCache(string scope, int status, NvDrsSetting setting)
    {
        if (status != StatusOk)
            return new(scope, "unknown", null, status == StatusSettingNotFound ? "当前驱动未提供该设置，无法确认。" : "读取失败：" + DescribeStatus(status));
        if (setting.settingType != NvDrsSetting.TypeDword || setting.currentValue is not { Length: >= 4 } || setting.settingLocation is < 0 or > 3 || setting.isCurrentPredefined > 1 || setting.isPredefinedValid > 1)
            return new(scope, "unknown", null, "驱动返回的设置类型、来源或值格式无法确认。");
        var value = BitConverter.ToUInt32(setting.currentValue, 0);
        var label = value == uint.MaxValue ? "无限制" : value == 0 ? "禁用" : value >= 1024 ? $"{value / 1024d:0.###} GB" : $"{value} MB";
        // Compare against this driver's predefined value, never a fixed GB baseline that changes across releases.
        var matchesDefault = setting.isPredefinedValid == 1 && setting.predefinedValue is { Length: >= 4 } && value == BitConverter.ToUInt32(setting.predefinedValue, 0);
        var custom = setting.isCurrentPredefined == 0 && setting.settingLocation != 3 && !matchesDefault;
        var inherited = setting.settingLocation is 1 or 2;
        return new(scope, custom ? "custom" : inherited ? "inherited" : "default", value, (custom ? "非默认" : "驱动默认") + (inherited ? "（继承全局/基础设置）" : "") + $"，当前值 {label}（0x{value:X8}）。");
    }

    private static bool EnsureSession(out IntPtr session, out string error)
    {
        EnsureInitialized();
        session = IntPtr.Zero;
        error = "";
        if (!_available)
        {
            error = "本机无法使用 NVIDIA NVAPI（未检测到 N 卡或 nvapi64.dll 不可用）。";
            return false;
        }

        if (_createSession!(ref session) != StatusOk)
        {
            error = "创建 NVAPI DRS 会话失败。";
            return false;
        }

        if (_loadSettings!(session) != StatusOk)
        {
            _destroySession!(session);
            session = IntPtr.Zero;
            error = "读取驱动配置数据库失败。";
            return false;
        }

        return true;
    }

    private static NvDrsSetting NewSetting(uint id) => new()
    {
        version = (uint)(Marshal.SizeOf<NvDrsSetting>() | (1 << 16)),
        settingName = "",
        settingId = id,
        settingType = (int)NvDrsSetting.TypeDword,
        settingLocation = (int)NvDrsSetting.LocationCurrentProfile,
        isCurrentPredefined = 0,
        isPredefinedValid = 0,
        predefinedValue = new byte[4100],
        currentValue = new byte[4100],
    };
    private static NvDrsApplication NewApplication(string appExeName) => new()
    {
        version = (uint)(Marshal.SizeOf<NvDrsApplication>() | (4 << 16)),
        isPredefined = 0,
        appName = appExeName,
        userFriendlyName = "Delta Force",
        launcher = "",
        fileInFolder = "",
        bitvector1 = 0,
        commandLine = "",
    };
}
