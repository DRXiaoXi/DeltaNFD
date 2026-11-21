using DeltaNFD.Services;
using Microsoft.Win32;

namespace DeltaNFD.Inspection;

internal static class NvDlssIndicatorInspection
{
    internal const string KeyPath = @"SOFTWARE\NVIDIA Corporation\Global\NGXCore";
    internal const string ValueName = "ShowDlssIndicator";
    // NVIDIA DLSS Programming Guide §3.18: release builds use 1024, not the debug .reg file's 1.
    internal const int ReleaseEnabled = 1024;

    internal static NvDlssIndicatorStatus Read()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(KeyPath, writable: false);
            return Read(key);
        }
        catch (Exception ex) { return Unavailable("无法读取 DLSS 指示器：" + ex.Message); }
    }

    internal static NvDlssIndicatorStatus Read(RegistryKey? key)
    {
        if (key is null) return Unavailable("未找到 NVIDIA NGXCore 注册表项，不能确认指示器支持情况。");
        var value = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null && !key.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase))
            return Classify(null, null);
        return Classify(key.GetValueKind(ValueName), value);
    }

    internal static NvDlssIndicatorStatus Classify(RegistryValueKind? kind, object? value)
    {
        if (kind is null && value is null) return new(false, true, "当前：关闭（未设置，使用驱动默认）。");
        if (kind != RegistryValueKind.DWord || value is not int raw)
            return Unavailable("指示器的注册表值不是可识别的 DWORD；未按已关闭处理，也不会覆盖该值。");
        return raw switch
        {
            ReleaseEnabled => new(true, true, "当前：已开启（正式版 DLSS 指示器）。", ReleaseEnabled),
            0 => new(false, true, "当前：已关闭。", 0),
            _ => new(null, true, $"当前：非标准值（0x{unchecked((uint)raw):X8}），无法确认正式版是否显示。可重新设置。", unchecked((uint)raw)),
        };
    }

    private static NvDlssIndicatorStatus Unavailable(string message) => new(null, false, message);
}
