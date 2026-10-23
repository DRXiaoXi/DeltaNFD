using System.Runtime.InteropServices;

namespace DeltaNFD.Native;

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
public static class NvApiDrs
{
    // ---- NVAPI 接口 ID（nvapi_QueryInterface 解析用）----
    private const uint IdNvApiInitialize = 0x0150E828;
    private const uint IdDrsCreateSession = 0x0694D52E;
    private const uint IdDrsDestroySession = 0xDAD9CFF8;
    private const uint IdDrsLoadSettings = 0x375DBD6B;
    private const uint IdDrsSaveSettings = 0xFCBC7E14;
    private const uint IdDrsFindApplicationByName = 0xEEE566B2;
    private const uint IdDrsFindProfileByName = 0x7E4A9A0B;
    private const uint IdDrsGetProfileInfo = 0x61CD6FD6;
    private const uint IdDrsGetBaseProfile = 0xDA8466A0;
    private const uint IdDrsGetCurrentGlobalProfile = 0x617BFF9F;
    private const uint IdDrsRestoreProfileDefaultSetting = 0x53F0381E;
    private const uint IdDrsSetProfileInfo = 0x16ABD3A9;
    private const uint IdDrsCreateProfile = 0xCC176068;
    private const uint IdDrsCreateApplication = 0x4347A9DE;
    private const uint IdDrsSetSetting = 0x8A2CF5F5;
    private const uint IdDrsSetSettingOld = 0x577DD202;
    private const uint IdDrsGetSetting = 0xEA99498D;
    private const uint IdDrsGetSettingOld = 0x73BF8338;
    private const uint IdDrsDeleteProfileSetting = 0xD20D29DF;
    private const uint IdDrsDeleteProfileSettingOld = 0xE4A26362;
    private const uint IdDrsEnumAvailableSettingIds = 0xE5DE48E5;
    private const uint IdDrsEnumAvailableSettingIdsOld = 0xF020614A;
    private const uint IdDrsGetSettingNameFromId = 0x1EB13791;
    private const uint IdDrsGetSettingNameFromIdOld = 0xD61CBE6E;

    // ---- 常用 NVAPI_Status ----
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
    private delegate int SetProfileInfoFn(IntPtr session, IntPtr profile, ref NvDrsProfile profileInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateProfileFn(IntPtr session, ref NvDrsProfile profileInfo, ref IntPtr profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateAppFn(IntPtr session, IntPtr profile, ref NvDrsApplication application);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetSettingFn(IntPtr session, IntPtr profile, ref NvDrsSetting setting, uint reserved1, uint reserved2);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetSettingFn(IntPtr session, IntPtr profile, uint settingId, ref NvDrsSetting setting, ref uint reserved);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeleteSettingFn(IntPtr session, IntPtr profile, uint settingId);

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
    private static SessionFn? _saveSettings;
    private static FindAppFn? _findApp;
    private static FindProfileFn? _findProfile;
    private static GetProfileInfoFn? _getProfileInfo;
    private static GetGlobalProfileFn? _getBaseProfile;
    private static GetGlobalProfileFn? _getCurrentGlobalProfile;
    private static SetProfileInfoFn? _setProfileInfo;
    private static CreateProfileFn? _createProfile;
    private static CreateAppFn? _createApp;
    private static SetSettingFn? _setSetting;
    private static GetSettingFn? _getSetting;
    private static DeleteSettingFn? _deleteSetting;
    private static DeleteSettingFn? _restoreDefaultSetting;
    private static EnumIdsFn? _enumIds;
    private static NameFromIdFn? _nameFromId;

    /// <summary>NVAPI DRS 是否可用（加载 nvapi64.dll 并解析全部所需接口成功）。</summary>
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

            T? Resolve<T>(uint id, uint fallbackId) where T : System.Delegate
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
            _saveSettings = Resolve<SessionFn>(IdDrsSaveSettings, 0);
            _findApp = Resolve<FindAppFn>(IdDrsFindApplicationByName, 0);
            _findProfile = Resolve<FindProfileFn>(IdDrsFindProfileByName, 0);
            _getProfileInfo = Resolve<GetProfileInfoFn>(IdDrsGetProfileInfo, 0);
            _getBaseProfile = Resolve<GetGlobalProfileFn>(IdDrsGetBaseProfile, 0);
            _getCurrentGlobalProfile = Resolve<GetGlobalProfileFn>(IdDrsGetCurrentGlobalProfile, 0);
            _setProfileInfo = Resolve<SetProfileInfoFn>(IdDrsSetProfileInfo, 0);
            _createProfile = Resolve<CreateProfileFn>(IdDrsCreateProfile, 0);
            _createApp = Resolve<CreateAppFn>(IdDrsCreateApplication, 0);
            _setSetting = Resolve<SetSettingFn>(IdDrsSetSetting, IdDrsSetSettingOld);
            _getSetting = Resolve<GetSettingFn>(IdDrsGetSetting, IdDrsGetSettingOld);
            _deleteSetting = Resolve<DeleteSettingFn>(IdDrsDeleteProfileSetting, IdDrsDeleteProfileSettingOld);
            _restoreDefaultSetting = Resolve<DeleteSettingFn>(IdDrsRestoreProfileDefaultSetting, 0);
            _enumIds = Resolve<EnumIdsFn>(IdDrsEnumAvailableSettingIds, IdDrsEnumAvailableSettingIdsOld);
            _nameFromId = Resolve<NameFromIdFn>(IdDrsGetSettingNameFromId, IdDrsGetSettingNameFromIdOld);

            if (_initialize is null || _createSession is null || _destroySession is null ||
                _loadSettings is null || _saveSettings is null || _findApp is null ||
                _createProfile is null || _createApp is null || _setSetting is null ||
                _getSetting is null || _deleteSetting is null)
            {
                return;
            }

            _available = _initialize() == StatusOk;
        }
        catch
        {
            _available = false;
        }
    }

    /// <summary>把 NVAPI 状态码翻译为可读文字。</summary>
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

    // ---------------- 高层操作（每个操作一个完整会话） ----------------

    // NVIDIA NvApiDriverSettings.h: PS_SHADERDISKCACHE_MAX_SIZE_ID. Never create/save/rename profiles here.
    internal const uint ShaderDiskCacheSizeId = 0x00AC8497;
    public static DeltaNFD.Services.NvShaderCacheReport ReadShaderCacheSizes() => DeltaNFD.Inspection.NvShaderInspection.ReadShaderCacheSizes();

    internal static DeltaNFD.Services.NvShaderCacheScope ClassifyShaderCache(string scope, int status, NvDrsSetting setting)
    {
        if (status != StatusOk)
            return new(scope, "unknown", null, status == StatusSettingNotFound
                ? "当前驱动未提供该设置，无法确认。" : "读取失败：" + DescribeStatus(status));
        if (setting.settingType != NvDrsSetting.TypeDword || setting.currentValue is not { Length: >= 4 }
            || setting.settingLocation is < 0 or > 3 || setting.isCurrentPredefined > 1 || setting.isPredefinedValid > 1)
            return new(scope, "unknown", null, "驱动返回的设置类型、来源或值格式无法确认。");
        var value = BitConverter.ToUInt32(setting.currentValue, 0);
        var label = value == uint.MaxValue ? "无限制" : value == 0 ? "禁用"
            : value >= 1024 ? $"{value / 1024d:0.###} GB" : $"{value} MB";
        // Compare against this driver's predefined value, never a fixed GB baseline that changes across releases.
        var matchesDefault = setting.isPredefinedValid == 1 && setting.predefinedValue is { Length: >= 4 }
            && value == BitConverter.ToUInt32(setting.predefinedValue, 0);
        var custom = setting.isCurrentPredefined == 0 && setting.settingLocation != 3 && !matchesDefault;
        var inherited = setting.settingLocation is 1 or 2;
        return new(scope, custom ? "custom" : inherited ? "inherited" : "default", value,
            (custom ? "非默认" : "驱动默认") + (inherited ? "（继承全局/基础设置）" : "") + $"，当前值 {label}（0x{value:X8}）。");
    }

    public static (bool Success, string Message) SetShaderCacheSize(string scope, uint? value,
        Action<object> persistBefore, Func<bool> canWrite)
    {
        if (!DeltaNFD.Services.NvShaderCacheChoices.IsValid(scope, value)) return (false, "缓存范围或大小不合法。");
        lock (Gate)
        {
            if (!EnsureSession(out var session, out var error)) return (false, error);
            try
            {
                if (value is null && _restoreDefaultSetting is null) return (false, "当前驱动不支持按单项恢复默认。");
                var profiles = new List<IntPtr>();
                if (scope == "global")
                {
                    foreach (var reader in new[] { _getCurrentGlobalProfile, _getBaseProfile })
                    {
                        var profile = IntPtr.Zero;
                        if (reader is null || reader(session, ref profile) != StatusOk || profile == IntPtr.Zero)
                            return (false, "全局配置无法完整核对，未保存任何修改。");
                        if (!profiles.Contains(profile)) profiles.Add(profile);
                    }
                }
                else
                {
                    var app = NewApplication(DeltaNFD.Services.DeltaForceLocator.GameProcessName + ".exe");
                    var profile = IntPtr.Zero;
                    var found = _findApp!(session, app.appName, ref profile, ref app);
                    if (found is StatusExecutableNotFound or StatusProfileNotFound)
                    {
                        if (value is null) return (true, "三角洲没有专用覆盖，已沿用全局；请同时确认全局为默认。");
                        // Create only a dedicated profile for this EXE; do not reuse an unrelated profile by display name.
                        var name = "Delta NFD Shader Cache - " + Guid.NewGuid().ToString("N");
                        var info = new NvDrsProfile { version = (uint)(Marshal.SizeOf<NvDrsProfile>() | (1 << 16)), profileName = name };
                        if (_createProfile!(session, ref info, ref profile) != StatusOk || profile == IntPtr.Zero
                            || _createApp!(session, profile, ref app) != StatusOk) return (false, "无法建立三角洲专用配置，未保存修改。");
                    }
                    else if (found != StatusOk || profile == IntPtr.Zero) return (false, "三角洲配置无法读取，未保存修改。");
                    foreach (var reader in new[] { _getCurrentGlobalProfile, _getBaseProfile })
                    {
                        var global = IntPtr.Zero;
                        if (reader is null || reader(session, ref global) != StatusOk || global == IntPtr.Zero || global == profile)
                            return (false, "无法确认三角洲使用独立程序配置，拒绝以单游戏操作修改全局。");
                    }
                    profiles.Add(profile);
                }
                var before = new List<object>();
                foreach (var profile in profiles)
                {
                    var setting = NewSetting(ShaderDiskCacheSizeId); var reserved = 0u;
                    var status = _getSetting!(session, profile, ShaderDiskCacheSizeId, ref setting, ref reserved);
                    if (status != StatusOk && status != StatusSettingNotFound) return (false, "原值读取失败，未保存修改：" + DescribeStatus(status));
                    if (status == StatusOk && ClassifyShaderCache(scope, status, setting).State == "unknown")
                        return (false, "原值类型或来源无法确认，未保存修改。");
                    before.Add(new { scope, SettingId = ShaderDiskCacheSizeId, Status = status, setting.settingLocation,
                        setting.isCurrentPredefined, setting.isPredefinedValid,
                        Value = status == StatusOk ? BitConverter.ToUInt32(setting.currentValue, 0) : (uint?)null,
                        Predefined = status == StatusOk && setting.isPredefinedValid == 1 ? BitConverter.ToUInt32(setting.predefinedValue, 0) : (uint?)null });
                }
                return CommitShaderCacheWrites(profiles, value,
                    () => persistBefore(new { Scope = scope, RequestedValue = value, Before = before }), canWrite, (profile, size) =>
                {
                    if (size is null) return _restoreDefaultSetting!(session, profile, ShaderDiskCacheSizeId);
                    var setting = NewSetting(ShaderDiskCacheSizeId);
                    BitConverter.GetBytes(size.Value).CopyTo(setting.currentValue, 0);
                    return _setSetting!(session, profile, ref setting, 0, 0);
                }, () => _saveSettings!(session));
            }
            finally { _destroySession!(session); }
        }
    }

    internal static (bool Success, string Message) CommitShaderCacheWrites(IReadOnlyList<IntPtr> profiles, uint? value,
        Action persistBefore, Func<bool> canWrite, Func<IntPtr, uint?, int> set, Func<int> save)
    {
        persistBefore();
        foreach (var profile in profiles)
        {
            if (!canWrite()) return (false, "游戏启动或写入条件变化，未保存修改。");
            var status = set(profile, value);
            if (status != StatusOk && !(value is null && status == StatusSettingNotFound))
                return (false, "缓存大小设置未完成，未保存当前会话：" + DescribeStatus(status));
        }
        if (!canWrite()) return (false, "保存前条件变化，未保存修改。");
        int saved;
        try { saved = save(); }
        catch (Exception ex) { return (false, "驱动保存未成功，请重新检测实际状态：" + ex.Message); }
        return saved == StatusOk ? (true, "缓存大小已保存；将重新读取驱动核对结果。")
            : (false, "驱动保存未成功，请重新检测实际状态：" + DescribeStatus(saved));
    }

    /// <summary>读取指定程序配置里一组设置的当前值。
    /// 返回：settingId → (是否有本配置覆盖值, 覆盖值)。读取失败返回 null。</summary>
    public static Dictionary<uint, (bool HasOverride, uint Value)>? GetApplicationSettings(
        string appExeName, IReadOnlyList<uint> settingIds)
    {
        lock (Gate)
        {
            if (!EnsureSession(out var session, out var error))
            {
                throw new InvalidOperationException(error);
            }

            try
            {
                var profile = FindOrCreateProfile(session, appExeName, createWhenMissing: false, out _);
                if (profile == IntPtr.Zero)
                {
                    // 没有该程序的配置：全部视为「无覆盖」
                    return settingIds.ToDictionary(id => id, _ => (false, 0u));
                }

                var result = new Dictionary<uint, (bool, uint)>();
                foreach (var id in settingIds)
                {
                    var setting = NewSetting(id);
                    var reserved = 0u;
                    var st = _getSetting!(session, profile, id, ref setting, ref reserved);
                    if (st != StatusOk)
                    {
                        result[id] = (false, 0);
                        continue;
                    }

                    var hasOverride = setting.settingLocation == (int)NvDrsSetting.LocationCurrentProfile;
                    var value = setting.currentValue is { Length: >= 4 }
                        ? BitConverter.ToUInt32(setting.currentValue, 0)
                        : 0u;
                    result[id] = (hasOverride, value);
                }

                return result;
            }
            finally
            {
                _destroySession!(session);
            }
        }
    }

    /// <summary>把一组设置写入指定程序的配置（value=null 表示删除覆盖、恢复默认）。完成后保存。</summary>
    public static (bool Success, string Message) ApplyApplicationSettings(
        string appExeName, string profileDisplayName, IReadOnlyList<(uint Id, uint? Value)> changes)
    {
        lock (Gate)
        {
            if (!EnsureSession(out var session, out var error))
            {
                return (false, error);
            }

            try
            {
                var profile = FindOrCreateProfile(session, appExeName, createWhenMissing: true, out var created, profileDisplayName);
                if (profile == IntPtr.Zero)
                {
                    return (false, System.IO.Path.IsPathFullyQualified(appExeName)
                        ? "驱动无法建立与完整 EXE 路径一致的独立配置，已拒绝写入，不会改写同名应用配置。"
                        : "未找到也无法创建该程序的驱动配置。");
                }

                var ok = 0;
                var fail = new List<string>();
                foreach (var (id, value) in changes)
                {
                    int st;
                    if (value is null)
                    {
                        st = _deleteSetting!(session, profile, id);
                        if (st == StatusOk || st == StatusSettingNotFound)
                        {
                            ok++; // 没有覆盖也算成功（本来就是要恢复默认）
                        }
                        else
                        {
                            fail.Add($"0x{id:X8}: {DescribeStatus(st)}");
                        }
                    }
                    else
                    {
                        var setting = NewSetting(id);
                        setting.settingType = (int)NvDrsSetting.TypeDword;
                        setting.settingLocation = (int)NvDrsSetting.LocationCurrentProfile;
                        setting.currentValue = [.. BitConverter.GetBytes(value.Value), .. new byte[4096]];
                        st = _setSetting!(session, profile, ref setting, 0, 0);
                        if (st == StatusOk)
                        {
                            ok++;
                        }
                        else
                        {
                            fail.Add($"0x{id:X8}: {DescribeStatus(st)}");
                        }
                    }
                }

                if (fail.Count > 0)
                {
                    return (false, $"{ok} 项成功，{fail.Count} 项失败（{string.Join("；", fail.Take(3))}）");
                }

                var saveSt = _saveSettings!(session);
                return saveSt == StatusOk
                    ? (true, created ? $"已创建「{profileDisplayName}」配置并写入 {ok} 项设置。" : $"已写入 {ok} 项设置。")
                    : (false, $"设置已提交但保存失败：{DescribeStatus(saveSt)}（数据库文件可能被只读锁定）");
            }
            finally
            {
                _destroySession!(session);
            }
        }
    }

    /// <summary>枚举本机驱动支持的设置名（用于按名称发现设置 ID；名称匹配子串）。</summary>
    public static List<(uint Id, string Name)> EnumerateSettingNames()
    {
        lock (Gate)
        {
            var result = new List<(uint, string)>();
            if (!EnsureSession(out var session, out _))
            {
                return result;
            }

            try
            {
                if (_enumIds is null || _nameFromId is null)
                {
                    return result;
                }

                var count = 0u;
                if (_enumIds(IntPtr.Zero, ref count) != StatusOk || count == 0)
                {
                    return result;
                }

                var ids = Marshal.AllocHGlobal((int)(count * 4));
                var nameBuf = Marshal.AllocHGlobal(4096);
                try
                {
                    if (_enumIds(ids, ref count) != StatusOk)
                    {
                        return result;
                    }

                    for (var i = 0; i < count; i++)
                    {
                        var id = unchecked((uint)Marshal.ReadInt32(ids, i * 4));
                        if (_nameFromId(id, nameBuf) != StatusOk)
                        {
                            continue;
                        }

                        result.Add((id, Marshal.PtrToStringUni(nameBuf) ?? ""));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ids);
                    Marshal.FreeHGlobal(nameBuf);
                }

                return result;
            }
            finally
            {
                _destroySession!(session);
            }
        }
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

    /// <summary>定位（或创建）包含指定应用程序的配置。</summary>
    private static IntPtr FindOrCreateProfile(
        IntPtr session, string appExeName, bool createWhenMissing, out bool created, string profileDisplayName = "Delta NFD")
    {
        created = false;
        if (System.IO.Path.IsPathFullyQualified(appExeName))
            return FindCustomProfile(session, appExeName, createWhenMissing, out created);
        var app = NewApplication(appExeName);
        var profile = IntPtr.Zero;
        var st = _findApp!(session, appExeName, ref profile, ref app);
        if (st == StatusOk && profile != IntPtr.Zero)
        {
            TryRenameLegacyProfile(session, profile);
            return profile;
        }

        // 尝试按名字找本工具此前创建的配置
        if (_findProfile is not null)
        {
            var byName = IntPtr.Zero;
            var profileStatus = _findProfile(session, ProfileName, ref byName);
            if (profileStatus != StatusOk || byName == IntPtr.Zero)
            {
                byName = IntPtr.Zero;
                profileStatus = _findProfile(session, LegacyProfileName, ref byName);
                if (profileStatus == StatusOk && byName != IntPtr.Zero)
                    TryRenameLegacyProfile(session, byName);
            }

            if (profileStatus == StatusOk && byName != IntPtr.Zero)
            {
                profile = byName;
                if (createWhenMissing)
                {
                    var addApp = NewApplication(appExeName);
                    if (_createApp!(session, profile, ref addApp) is StatusOk or StatusError)
                    {
                        // 已存在该程序时驱动返回错误但配置仍可用
                    }
                }

                return profile;
            }
        }

        if (!createWhenMissing)
        {
            return IntPtr.Zero;
        }

        // 创建新配置 + 登记应用程序
        var info = new NvDrsProfile
        {
            version = (uint)(Marshal.SizeOf<NvDrsProfile>() | (1 << 16)),
            profileName = ProfileName,
            gpuSupport = 0,
            isPredefined = 0,
            numOfApps = 0,
            numOfSettings = 0,
        };

        var newProfile = IntPtr.Zero;
        if (_createProfile!(session, ref info, ref newProfile) != StatusOk || newProfile == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var newApp = NewApplication(appExeName);
        _createApp!(session, newProfile, ref newApp);
        created = true;
        return newProfile;
    }

    private const string ProfileName = "Delta NFD - 三角洲行动";
    internal static bool IsExactCustomApplication(string requested, string registered) =>
        System.IO.Path.IsPathFullyQualified(registered) &&
        System.IO.Path.GetFullPath(requested).Equals(System.IO.Path.GetFullPath(registered), StringComparison.OrdinalIgnoreCase);

    private static IntPtr FindCustomProfile(IntPtr session, string path, bool create, out bool created)
    {
        created = false;
        if (_findProfile is null || _getProfileInfo is null) return IntPtr.Zero;
        var name = DeltaNFD.Services.GameTargetService.CustomProfileName(path);
        var profile = IntPtr.Zero;
        var found = _findProfile(session, name, ref profile) == StatusOk && profile != IntPtr.Zero;
        if (!found)
        {
            if (!create) return IntPtr.Zero;
            var info = new NvDrsProfile { version = (uint)(Marshal.SizeOf<NvDrsProfile>() | (1 << 16)), profileName = name };
            if (_createProfile!(session, ref info, ref profile) != StatusOk || profile == IntPtr.Zero) return IntPtr.Zero;
            created = true;
        }
        var existing = NewApplication(path);
        var associated = IntPtr.Zero;
        var lookup = _findApp!(session, path, ref associated, ref existing);
        if (lookup != StatusOk || associated != profile || !IsExactCustomApplication(path, existing.appName))
        {
            if (!create) return IntPtr.Zero;
            var application = NewApplication(path);
            if (_createApp!(session, profile, ref application) != StatusOk) return IntPtr.Zero;
            existing = NewApplication(path);
            associated = IntPtr.Zero;
            if (_findApp(session, path, ref associated, ref existing) != StatusOk || associated != profile ||
                !IsExactCustomApplication(path, existing.appName)) return IntPtr.Zero;
        }
        var check = new NvDrsProfile { version = (uint)(Marshal.SizeOf<NvDrsProfile>() | (1 << 16)), profileName = "" };
        return _getProfileInfo(session, profile, ref check) == StatusOk && check.numOfApps == 1 && check.profileName == name
            ? profile : IntPtr.Zero;
    }
    private const string LegacyProfileName = "DeltaOptimizer DeltaForce";

    /// <summary>Rename our former NVIDIA DRS profile in place so existing driver settings remain attached.</summary>
    private static void TryRenameLegacyProfile(IntPtr session, IntPtr profile)
    {
        if (_getProfileInfo is null || _setProfileInfo is null)
            return;

        var info = new NvDrsProfile
        {
            version = (uint)(Marshal.SizeOf<NvDrsProfile>() | (1 << 16)),
            profileName = "",
        };
        if (_getProfileInfo(session, profile, ref info) != StatusOk ||
            !info.profileName.Equals(LegacyProfileName, StringComparison.OrdinalIgnoreCase))
            return;

        var existingNewProfile = IntPtr.Zero;
        if (_findProfile is not null &&
            _findProfile(session, ProfileName, ref existingNewProfile) == StatusOk &&
            existingNewProfile != IntPtr.Zero && existingNewProfile != profile)
            return;

        info.profileName = ProfileName;
        var status = _setProfileInfo(session, profile, ref info);
        if (status == StatusOk)
        {
            var saveStatus = _saveSettings!(session);
            if (saveStatus == StatusOk)
                DeltaNFD.Services.Log.Info("NVIDIA 驱动配置：已将旧 DeltaOptimizer 配置改名为 Delta NFD，保留原设置。");
            else
                DeltaNFD.Services.Log.Warn($"NVIDIA 驱动配置名称仅在当前会话内更新，保存失败：{DescribeStatus(saveStatus)}");
        }
        else
            DeltaNFD.Services.Log.Warn($"NVIDIA 驱动配置改名失败：{DescribeStatus(status)}；原设置仍保留在旧配置中。");
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
