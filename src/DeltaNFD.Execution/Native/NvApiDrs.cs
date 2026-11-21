using System.Runtime.InteropServices;
using DeltaNFD.Services;

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
public static partial class NvApiDrs
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
            _createProfile = Resolve<CreateProfileFn>(IdDrsCreateProfile, 0);
            _createApp = Resolve<CreateAppFn>(IdDrsCreateApplication, 0);
            _setSetting = Resolve<SetSettingFn>(IdDrsSetSetting, IdDrsSetSettingOld);
            _getSetting = Resolve<GetSettingFn>(IdDrsGetSetting, IdDrsGetSettingOld);
            _deleteSetting = Resolve<DeleteSettingFn>(IdDrsDeleteProfileSetting, IdDrsDeleteProfileSettingOld);
            _restoreDefaultSetting = Resolve<DeleteSettingFn>(IdDrsRestoreProfileDefaultSetting, 0);
            _enumIds = Resolve<EnumIdsFn>(IdDrsEnumAvailableSettingIds, IdDrsEnumAvailableSettingIdsOld);
            _nameFromId = Resolve<NameFromIdFn>(IdDrsGetSettingNameFromId, IdDrsGetSettingNameFromIdOld);
            // Optional: old driver support for existing settings must not depend on full-profile reset.
            _restoreProfile = Resolve<RestoreProfileFn>(0xFA5F6134, 0);
            _enumProfileSettings = Resolve<EnumProfileSettingsFn>(0xAE3039DA, 0);
            _enumProfileApps = Resolve<EnumProfileAppsFn>(0x7FA2173A, 0);

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
    /// 返回：settingId → (是否有本配置覆盖值, 当前有效值)。</summary>
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
                var profile = FindBoundApplicationProfile(session, appExeName);
                if (profile != IntPtr.Zero) return ReadApplicationSettings(session, profile, settingIds);

                // A confirmed absence (not a lookup failure) can inherit a global ReBAR policy.
                var result = settingIds.Where(id => !NvDriverSettingPolicy.IsRebarSetting(id)).ToDictionary(id => id, _ => (false, 0u));
                var rebarIds = settingIds.Where(NvDriverSettingPolicy.IsRebarSetting).ToArray();
                if (rebarIds.Length != 0)
                {
                    var global = RequireProfileHandle(session, _getCurrentGlobalProfile, "全局");
                    foreach (var (id, state) in ReadApplicationSettings(session, global, rebarIds))
                        result[id] = (false, state.Value);
                }
                return result;
            }
            finally
            {
                _destroySession!(session);
            }
        }
    }

    private static (int Status, bool IsDword, bool HasOverride, uint Value) ReadApplicationSetting(IntPtr session, IntPtr profile, uint id)
    {
        var setting = NewSetting(id);
        var reserved = 0u;
        var status = _getSetting!(session, profile, id, ref setting, ref reserved);
        var isDword = setting.settingType == NvDrsSetting.TypeDword && setting.currentValue is { Length: >= 4 }
            && setting.settingLocation is >= 0 and <= 3 && setting.isCurrentPredefined is 0 or 1;
        var hasOverride = setting.settingLocation == NvDrsSetting.LocationCurrentProfile && setting.isCurrentPredefined == 0;
        return (status, isDword, hasOverride, isDword ? BitConverter.ToUInt32(setting.currentValue!, 0) : 0u);
    }

    private static Dictionary<uint, (bool HasOverride, uint Value)> ReadApplicationSettings(
        IntPtr session, IntPtr profile, IReadOnlyList<uint> ids)
    {
        var result = new Dictionary<uint, (bool, uint)>();
        foreach (var id in ids)
        {
            var current = ReadApplicationSetting(session, profile, id);
            if (current.Status == StatusSettingNotFound) result[id] = (false, NvDriverSettingPolicy.MissingValue(id));
            else if (current.Status == StatusOk && current.IsDword) result[id] = (current.HasOverride, current.Value);
            else throw new InvalidOperationException($"设置 0x{id:X8} 无法读取为 DWORD：{DescribeStatus(current.Status)}。");
        }
        return result;
    }

    /// <summary>在一个会话内暂存并保存；销毁写会话后另建会话只读核对。</summary>
    internal static OperationResult ApplyApplicationSettings(string appExeName,
        IReadOnlyList<(uint Id, uint? Value)> changes, uint? driver, bool rebarWarningAccepted, Func<bool> canSave)
    {
        if (changes.Count == 0) return OperationResult.Fail("没有要应用的设置。");
        if (NvDriverSettingPolicy.Validate(changes, driver, rebarWarningAccepted) is { } validation)
            return OperationResult.Fail(validation);
        lock (Gate)
        {
            if (!EnsureSession(out var session, out var error)) return OperationResult.Fail(error);
            var verifySession = IntPtr.Zero;
            var verifyProfile = IntPtr.Zero;
            try
            {
                var profile = FindBoundApplicationProfile(session, appExeName);
                if (profile == IntPtr.Zero)
                {
                    if (changes.All(c => c.Value is null))
                        return OperationResult.Skip("目标 EXE 未登记游戏配置，没有本游戏覆盖可删除；未写入驱动数据库。");
                    profile = CreateBoundApplicationProfile(session, appExeName);
                }

                var current = ReadApplicationSettings(session, profile, NvDriverSettingPolicy.ReadIds);
                var plan = NvDriverSettingPolicy.Expand(changes, current);
                // Expand can add a V2 Auto write; apply the driver-version guard to it too.
                var rebarPlan = plan.Where(c => NvDriverSettingPolicy.IsRebarSetting(c.Id)).ToArray();
                if (NvDriverSettingPolicy.Validate(rebarPlan, driver, rebarWarningAccepted: true) is { } problem)
                    return OperationResult.Fail(problem);
                return CommitApplicationWrites(plan, (id, value) =>
                {
                    if (value is null) return _deleteSetting!(session, profile, id);
                    var setting = NewSetting(id);
                    BitConverter.GetBytes(value.Value).CopyTo(setting.currentValue, 0);
                    return _setSetting!(session, profile, ref setting, 0, 0);
                }, () => _saveSettings!(session), () =>
                {
                    var closed = _destroySession!(session);
                    session = IntPtr.Zero;
                    if (closed != StatusOk) return closed;
                    if (!EnsureSession(out verifySession, out var verifyError)) throw new InvalidOperationException(verifyError);
                    verifyProfile = FindBoundApplicationProfile(verifySession, appExeName);
                    return verifyProfile != IntPtr.Zero ? StatusOk : StatusProfileNotFound;
                }, id => verifySession == IntPtr.Zero ? ReadApplicationSetting(session, profile, id)
                    : ReadApplicationSetting(verifySession, verifyProfile, id), canSave);
            }
            catch (Exception ex) { return OperationResult.Fail("驱动配置未保存：" + ex.Message); }
            finally
            {
                if (verifySession != IntPtr.Zero) CloseApplicationSession(verifySession);
                if (session != IntPtr.Zero) CloseApplicationSession(session);
            }
        }
    }

    private static void CloseApplicationSession(IntPtr session)
    {
        try { _destroySession!(session); }
        catch (Exception ex) { Log.Warn("无法释放 NVIDIA DRS 会话：" + ex.Message); }
    }

    internal static OperationResult CommitApplicationWrites(IReadOnlyList<(uint Id, uint? Value)> changes,
        Func<uint, uint?, int> set, Func<int> save, Func<int> openVerification,
        Func<uint, (int Status, bool IsDword, bool HasOverride, uint Value)> read, Func<bool>? canSave = null)
    {
        var saveAttempted = false;
        OperationResult Unconfirmed(string message) => new() { OutcomeUnknown = true,
            Message = message + "请核对实际状态，不要重复提交。" };
        try
        {
            var checkRebar = changes.Any(c => NvDriverSettingPolicy.IsRebarSetting(c.Id));
            var enablingV1 = changes.Any(c => c.Id == NvDriverSettingPolicy.RebarId && c.Value == 1);
            // Stage Auto/disable/deletion before enabling the requested version.
            foreach (var (id, value) in changes.OrderBy(c => NvDriverSettingPolicy.IsRebarEnable(c.Id, c.Value)))
            {
                var status = set(id, value);
                if (status != StatusOk && !(value is null && status == StatusSettingNotFound))
                    return OperationResult.Fail($"0x{id:X8} 写入失败：{DescribeStatus(status)}。本次会话未保存。");
            }
            if (checkRebar && CheckEffectiveRebarPair(read, enablingV1) is { } before)
                return OperationResult.Fail(before + "本次更改未保存。");
            if (canSave is not null && !canSave())
                return OperationResult.Fail("驱动数据库已变为只读或无法确认写入条件，本次更改未保存。");
            saveAttempted = true;
            var saved = save();
            if (saved != StatusOk) return Unconfirmed($"驱动保存未确认：{DescribeStatus(saved)}。");
            var loaded = openVerification();
            if (loaded != StatusOk) return Unconfirmed($"驱动已接受保存，但新会话回读失败：{DescribeStatus(loaded)}。");
            foreach (var (id, value) in changes)
            {
                var actual = read(id);
                var verified = value is null
                    ? actual.Status == StatusSettingNotFound || actual.Status == StatusOk && actual.IsDword && !actual.HasOverride
                    : actual.Status == StatusOk && actual.IsDword && actual.HasOverride && actual.Value == value;
                if (!verified) return Unconfirmed($"驱动已接受保存，但设置 0x{id:X8} 未通过回读核验。");
            }
            if (checkRebar && CheckEffectiveRebarPair(read, enablingV1) is { } after)
                return Unconfirmed("驱动已接受保存，但 ReBAR 回读未通过。" + after);
            return OperationResult.Ok($"已保存并回读核验 {changes.Count} 项驱动配置。请重新启动游戏后确认实际效果。");
        }
        catch (Exception ex)
        {
            return saveAttempted ? Unconfirmed("保存结果未核验：" + ex.Message + "。")
                : OperationResult.Fail("驱动配置未保存：" + ex.Message);
        }
    }

    private static string? CheckEffectiveRebarPair(Func<uint, (int Status, bool IsDword, bool HasOverride, uint Value)> read,
        bool enablingV1)
    {
        uint? ValueOf(uint id)
        {
            var state = read(id);
            return state.Status switch
            {
                StatusSettingNotFound => NvDriverSettingPolicy.MissingValue(id),
                StatusOk when state.IsDword => state.Value,
                _ => null,
            };
        }
        var v1 = ValueOf(NvDriverSettingPolicy.RebarId);
        var v2 = ValueOf(NvDriverSettingPolicy.RebarPolicyId);
        if (v1 is null || v2 is null or > 2) return "无法确认 ReBAR V1 或 V2 的有效值。";
        if (v1 == 1 && v2 == 2) return NvDriverSettingPolicy.RebarConflict;
        if (enablingV1 && (v1 != 1 || v2 != NvDriverSettingPolicy.RebarV2Auto))
            return "开启 V1 需要 V2 为自动，当前组合未达到所选策略。";
        return null;
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

        try
        {
            var created = _createSession!(ref session);
            if (created == StatusOk && session != IntPtr.Zero)
            {
                var loaded = _loadSettings!(session);
                if (loaded == StatusOk) return true;
                error = "读取驱动配置数据库失败：" + DescribeStatus(loaded);
            }
            else error = "创建 NVAPI DRS 会话失败：" + DescribeStatus(created);
        }
        catch (Exception ex) { error = "无法打开驱动配置会话：" + ex.Message; }
        if (session != IntPtr.Zero)
        {
            CloseApplicationSession(session);
            session = IntPtr.Zero;
        }
        return false;
    }

    private const string ProfileName = "Delta NFD - 三角洲行动";
    private const string LegacyProfileName = "DeltaOptimizer DeltaForce";

    internal static bool IsExactCustomApplication(string requested, string registered) =>
        System.IO.Path.IsPathFullyQualified(registered) &&
        System.IO.Path.GetFullPath(requested).Equals(System.IO.Path.GetFullPath(registered), StringComparison.OrdinalIgnoreCase);

    private static IntPtr FindBoundApplicationProfile(IntPtr session, string appExeName)
    {
        var app = NewApplication(appExeName);
        var profile = IntPtr.Zero;
        var status = _findApp!(session, appExeName, ref profile, ref app);
        if (status == StatusExecutableNotFound) return IntPtr.Zero;
        if (status != StatusOk || profile == IntPtr.Zero)
            throw new InvalidOperationException("查询目标 EXE 的驱动关联失败：" + DescribeStatus(status));

        RequireIndependentProfile(session, profile);
        if (System.IO.Path.IsPathFullyQualified(appExeName))
        {
            if (!IsExactCustomApplication(appExeName, app.appName))
                throw new InvalidOperationException("驱动未返回与完整 EXE 路径一致的关联，已拒绝使用同名应用配置。");
            RequireOwnedProfile(session, profile, GameTargetService.CustomProfileName(appExeName), appCount: 1);
        }
        else if (!string.Equals(System.IO.Path.GetFileName(app.appName), appExeName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("驱动返回的应用关联与目标 EXE 不一致。");
        return profile;
    }

    private static IntPtr CreateBoundApplicationProfile(IntPtr session, string appExeName)
    {
        var custom = System.IO.Path.IsPathFullyQualified(appExeName);
        var name = custom ? GameTargetService.CustomProfileName(appExeName) : ProfileName;
        var profile = FindNamedProfile(session, name);
        if (profile == IntPtr.Zero && !custom)
        {
            profile = FindNamedProfile(session, LegacyProfileName);
            if (profile != IntPtr.Zero) name = LegacyProfileName; // Keep existing names; never do a separate save/rename.
        }
        if (profile == IntPtr.Zero)
        {
            var info = NewProfile(name);
            var created = _createProfile!(session, ref info, ref profile);
            if (created != StatusOk || profile == IntPtr.Zero)
                throw new InvalidOperationException("创建游戏驱动配置失败：" + DescribeStatus(created));
        }
        RequireIndependentProfile(session, profile);
        // A matching friendly name is not authority to attach to a populated or predefined profile.
        RequireOwnedProfile(session, profile, name, appCount: 0);
        var app = NewApplication(appExeName);
        var added = _createApp!(session, profile, ref app);
        if (added != StatusOk)
            throw new InvalidOperationException("登记目标 EXE 失败：" + DescribeStatus(added));
        if (FindBoundApplicationProfile(session, appExeName) != profile)
            throw new InvalidOperationException("登记后无法确认目标 EXE 与该驱动配置的关联。");
        return profile;
    }

    private static IntPtr FindNamedProfile(IntPtr session, string name)
    {
        var profile = IntPtr.Zero;
        var status = _findProfile?.Invoke(session, name, ref profile) ?? StatusError;
        if (status == StatusProfileNotFound) return IntPtr.Zero;
        if (status != StatusOk || profile == IntPtr.Zero)
            throw new InvalidOperationException("查询驱动配置失败：" + DescribeStatus(status));
        return profile;
    }

    private static void RequireIndependentProfile(IntPtr session, IntPtr profile)
    {
        if (profile == new IntPtr(-1) || profile == RequireProfileHandle(session, _getBaseProfile, "基础")
            || profile == RequireProfileHandle(session, _getCurrentGlobalProfile, "全局"))
            throw new InvalidOperationException("目标不是独立游戏配置，已拒绝使用全局或基础配置。");
    }

    private static IntPtr RequireProfileHandle(IntPtr session, GetGlobalProfileFn? get, string scope)
    {
        var handle = IntPtr.Zero;
        var status = get?.Invoke(session, ref handle) ?? StatusError;
        if (status != StatusOk || handle == IntPtr.Zero)
            throw new InvalidOperationException($"无法核对{scope}驱动配置：{DescribeStatus(status)}。");
        return handle;
    }

    private static void RequireOwnedProfile(IntPtr session, IntPtr profile, string name, uint appCount)
    {
        var info = NewProfile("");
        var status = _getProfileInfo?.Invoke(session, profile, ref info) ?? StatusError;
        if (status != StatusOk || !string.Equals(info.profileName, name, StringComparison.OrdinalIgnoreCase)
            || info.isPredefined != 0 || info.numOfApps != appCount)
            throw new InvalidOperationException("无法确认独立的程序专用配置；未复用同名或已关联其他应用的配置。");
    }

    private static NvDrsProfile NewProfile(string name) => new()
    {
        version = (uint)(Marshal.SizeOf<NvDrsProfile>() | (1 << 16)),
        profileName = name,
    };

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
