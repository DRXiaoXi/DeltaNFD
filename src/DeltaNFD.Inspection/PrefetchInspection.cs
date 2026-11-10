using Microsoft.Win32;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

internal static class PrefetchInspection
{
    internal static (bool? Disabled, string Detail) Read(bool? preLaunchOn,
        Func<string, string, string, int?>? read = null)
    {
        try
        {
            read ??= ReadDword;
            var values = PrefetchConfiguration.RegistryValues.Select(v => read(v.Hive, v.Path, v.Name)).ToArray();
            return Describe(values, preLaunchOn);
        }
        catch (Exception ex) { return (null, "预读取状态无法确认：" + ex.Message); }
    }

    internal static bool? Evaluate(IReadOnlyList<int?> values, bool? preLaunchOn) =>
        values.Count != PrefetchConfiguration.RegistryValues.Count ? null
        : values.Any(v => v != 0) || preLaunchOn == true ? false : preLaunchOn == false ? true : null;

    internal static (bool? Disabled, string Detail) Describe(IReadOnlyList<int?> values, bool? preLaunchOn)
    {
        var disabled = Evaluate(values, preLaunchOn);
        if (disabled == true)
            return (true, "预读取关闭配置已设置，ApplicationPreLaunch 当前已关闭；请手动重启后再检测。SysMain 未被此项停止或禁用");
        var parts = new List<string>();
        if (preLaunchOn != false)
            parts.Add(preLaunchOn is null ? "ApplicationPreLaunch 状态无法确认" : "ApplicationPreLaunch 开启");
        if (values.Count != PrefetchConfiguration.RegistryValues.Count) parts.Add("配置读取未完成");
        else for (var i = 0; i < values.Count; i++)
            if (values[i] != 0) parts.Add(PrefetchConfiguration.RegistryValues[i].Label
                + (values[i] is null ? "未配置关闭值" : "未关闭"));
        return (disabled, (disabled is null ? "状态无法确认（" : "未全部禁用（") + string.Join("，", parts) + "）");
    }

    private static int? ReadDword(string hive, string path, string name)
    {
        using var key = (hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(path);
        var value = key?.GetValue(name);
        if (value is null) return null;
        if (value is int number) return number;
        throw new InvalidDataException(name + " 不是 DWORD，未按默认值处理。");
    }
}
