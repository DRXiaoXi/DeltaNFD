using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DeltaNFD.Services;

namespace DeltaNFD.Native;

public static partial class NvApiDrs
{
    // NVIDIA nvapi_interface.h; RestoreProfileDefault removes ALL user changes in this profile.
    private const int StatusEndEnumeration = -7;
    private const int StatusProfileRemoved = -169;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RestoreProfileFn(IntPtr session, IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumProfileSettingsFn(IntPtr session, IntPtr profile, uint index, ref uint count, ref NvDrsSetting setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumProfileAppsFn(IntPtr session, IntPtr profile, uint index, ref uint count, ref NvDrsApplication app);
    private static RestoreProfileFn? _restoreProfile;
    private static EnumProfileSettingsFn? _enumProfileSettings;
    private static EnumProfileAppsFn? _enumProfileApps;

    internal sealed record ProfileResetSnapshot(bool Present, NvDrsProfile Profile,
        NvDrsApplication[] Applications, NvDrsSetting[] Settings)
    {
        public int UserSettingCount => Settings.Count(s => s.settingLocation == 0 && s.isCurrentPredefined == 0);
        public int UserApplicationCount => Applications.Count(a => a.isPredefined == 0);
    }

    internal static readonly JsonSerializerOptions ProfileBackupJson = new() { IncludeFields = true };
    internal static byte[] SerializeProfileBackup(ProfileResetSnapshot snapshot) => JsonSerializer.SerializeToUtf8Bytes(snapshot, ProfileBackupJson);

    /// <summary>Complete reset of the actual Delta Force profile; never creates a profile or changes database attributes.</summary>
    public static OperationResult ResetDeltaProfile(Action<byte[]> persistBefore, Func<bool> canWrite)
    {
        lock (Gate)
        {
            if (!EnsureSession(out var session, out var error)) return OperationResult.Fail(error);
            try
            {
                if (_restoreProfile is null || _enumProfileSettings is null || _enumProfileApps is null
                    || _getProfileInfo is null || _getBaseProfile is null || _getCurrentGlobalProfile is null)
                    return OperationResult.Fail("当前驱动未提供完整配置重置接口，请更新驱动后再试。");
                var before = ReadResetProfile(session, out var profile);
                ProfileResetSnapshot Fresh()
                {
                    if (!EnsureSession(out var fresh, out var message)) throw new IOException(message);
                    try { return ReadResetProfile(fresh, out _); }
                    finally { _destroySession!(fresh); }
                }
                return CommitProfileReset(before, persistBefore, canWrite, Fresh,
                    () => _restoreProfile(session, profile), () => _saveSettings!(session));
            }
            catch (Exception ex) { return OperationResult.Fail("未重置驱动配置：" + ex.Message); }
            finally { _destroySession!(session); }
        }
    }

    // Injection boundary exercises the real transaction with in-memory driver fixtures.
    internal static OperationResult CommitProfileReset(ProfileResetSnapshot before, Action<byte[]> persistBefore,
        Func<bool> canWrite, Func<ProfileResetSnapshot> readFresh, Func<int> reset, Func<int> save)
    {
        var saving = false;
        try
        {
            if (!canWrite()) return OperationResult.Fail("请退出三角洲，并解除驱动配置数据库只读锁定后再重置。");
            if (!before.Present) return OperationResult.Skip("未找到三角洲专用驱动配置，无需重置；当前继承全局设置。");
            // Equal-to-default user values are still overrides, so never compare DWORD values here.
            var bytes = SerializeProfileBackup(before);
            persistBefore(bytes);
            bool Unchanged() => CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(SerializeProfileBackup(readFresh())));
            if (!Unchanged()) return OperationResult.Fail("备份后驱动配置发生变化，未保存重置。请关闭其他驱动设置工具后重试。");
            if (!canWrite()) return OperationResult.Fail("游戏或数据库锁定状态已变化，未保存重置。");
            var status = reset(); // Session memory only; SaveSettings is the system write boundary.
            if (status is not (StatusOk or StatusProfileRemoved))
                return OperationResult.Fail("驱动未接受完整重置，未保存：" + DescribeStatus(status));
            if (!Unchanged() || !canWrite()) return OperationResult.Fail("保存前配置或游戏状态发生变化，未保存重置。");
            saving = true;
            status = save();
            if (status != StatusOk) return Unknown("驱动保存返回异常：" + DescribeStatus(status));
            var after = readFresh();
            if (after.Present && (after.UserSettingCount != 0 || after.UserApplicationCount != 0 || after.Profile.isPredefined == 0))
                return Unknown("重新读取后仍有用户覆盖，或被其他工具重新写入。");
            if (!after.Present && before.Profile.isPredefined == 1
                && before.Applications.Any(a => a.isPredefined == 1 && IsDeltaApplication(a.appName)))
                return Unknown("重新读取后未找到原有的驱动预设游戏关联。");
            return OperationResult.Ok($"防闪屏性重置已保存并复核：原 {before.UserSettingCount} 项用户设置覆盖已清除。"
                + "请重新启动三角洲，在原问题位置确认画面；全局设置仍会被继承。");
        }
        catch (Exception ex)
        {
            return saving ? Unknown("保存后的核对未完成：" + ex.Message)
                : OperationResult.Fail("未保存驱动配置重置：" + ex.Message);
        }
        static OperationResult Unknown(string detail) => new() { Success = false, OutcomeUnknown = true,
            Message = "尚未确认重置完成。" + detail + "原始备份已保留，请核对驱动设置，勿连续重复操作。" };
    }

    private static bool IsDeltaApplication(string name) => string.Equals(Path.GetFileName(name),
        DeltaForceLocator.GameProcessName + ".exe", StringComparison.OrdinalIgnoreCase);

    private static ProfileResetSnapshot ReadResetProfile(IntPtr session, out IntPtr profile)
    {
        var app = NewApplication(DeltaForceLocator.GameProcessName + ".exe");
        profile = IntPtr.Zero;
        var status = _findApp!(session, app.appName, ref profile, ref app);
        if (status is StatusExecutableNotFound or StatusProfileNotFound)
            return new(false, default, [], []);
        if (status != StatusOk || profile == IntPtr.Zero) throw new IOException("无法确定三角洲驱动配置：" + DescribeStatus(status));
        foreach (var reader in new[] { _getBaseProfile, _getCurrentGlobalProfile })
        {
            var global = IntPtr.Zero;
            if (reader is null || reader(session, ref global) != StatusOk || global == IntPtr.Zero || profile == global || profile == new IntPtr(-1))
                throw new IOException("无法确认独立游戏配置，已拒绝重置全局或基础配置。");
        }
        var info = new NvDrsProfile { version = (uint)(Marshal.SizeOf<NvDrsProfile>() | (1 << 16)), profileName = "" };
        status = _getProfileInfo!(session, profile, ref info);
        if (status != StatusOk || info.isPredefined > 1 || info.numOfApps is 0 or > 256 || info.numOfSettings > 4096)
            throw new IOException("游戏配置元数据不完整或超出范围，未重置。");
        var apps = new List<NvDrsApplication>();
        for (uint i = 0; i <= info.numOfApps; i++)
        {
            var entry = NewApplication(""); var count = 1u;
            status = _enumProfileApps!(session, profile, i, ref count, ref entry);
            if (i == info.numOfApps && (status == StatusEndEnumeration || status == StatusOk && count == 0)) break;
            if (status != StatusOk || count != 1 || i == info.numOfApps || string.IsNullOrWhiteSpace(entry.appName)
                || entry.isPredefined > 1 || apps.Any(a => a.appName.Equals(entry.appName, StringComparison.OrdinalIgnoreCase)
                    && a.fileInFolder == entry.fileInFolder && a.commandLine == entry.commandLine))
                throw new IOException("游戏程序关联枚举不完整，未重置。");
            apps.Add(entry);
        }
        // Trust the driver's predefined association, never the editable profile display name.
        var driverOwnedGameProfile = info.isPredefined == 1 && apps.Any(a => a.isPredefined == 1 && IsDeltaApplication(a.appName));
        if (!apps.Any(a => IsDeltaApplication(a.appName)) || apps.Any(a => !IsDeltaApplication(a.appName)
            && !(a.isPredefined == 1 && driverOwnedGameProfile)))
            throw new IOException("此配置还关联了无法确认归属的程序，请先在 NVIDIA 设置工具中拆分游戏配置。");
        var settings = new List<NvDrsSetting>();
        for (uint i = 0; i <= info.numOfSettings; i++)
        {
            var entry = NewSetting(0); var count = 1u;
            // NVDRS_BINARY_SETTING requires caller-provided capacity, otherwise the driver may return only the length.
            BitConverter.TryWriteBytes(entry.currentValue.AsSpan(), 4096u);
            BitConverter.TryWriteBytes(entry.predefinedValue.AsSpan(), 4096u);
            status = _enumProfileSettings!(session, profile, i, ref count, ref entry);
            if (i == info.numOfSettings && (status == StatusEndEnumeration || status == StatusOk && count == 0)) break;
            if (status != StatusOk || count != 1 || i == info.numOfSettings || entry.settingType is < 0 or > 4
                || entry.settingLocation is < 0 or > 3 || entry.isCurrentPredefined > 1 || entry.isPredefinedValid > 1
                || entry.currentValue is not { Length: 4100 } || entry.predefinedValue is not { Length: 4100 }
                || entry.settingType == 1 && (BitConverter.ToUInt32(entry.currentValue, 0) > 4096
                    || entry.isPredefinedValid == 1 && BitConverter.ToUInt32(entry.predefinedValue, 0) > 4096)
                || settings.Any(s => s.settingId == entry.settingId))
                throw new IOException("游戏设置枚举不完整，未重置。");
            settings.Add(entry);
        }
        return new(true, info, apps.OrderBy(a => a.appName, StringComparer.Ordinal).ThenBy(a => a.fileInFolder, StringComparer.Ordinal)
            .ThenBy(a => a.commandLine, StringComparer.Ordinal).ToArray(), settings.OrderBy(s => s.settingId).ToArray());
    }
}
