using System.Globalization;

namespace DeltaNFD.Services;

/// <summary>游戏缓存清理的附带范围。只读策略，不枚举或删除任何文件。</summary>
public static class ShaderCacheCleanupPolicy
{
    public const string SystemCacheNotice = "执行时确认 NVIDIA 621.xx 驱动后，还会同步清理当前用户的 %LOCALAPPDATA%\\D3DSCache。该目录包含其他游戏与应用的系统着色器缓存，清理后也会重新编译。";

    public static bool IncludesSystemD3DCache(ShaderGpuDetectionState state, string? version)
    {
        if (state != ShaderGpuDetectionState.Nvidia || version is null) return false;
        var parts = version.Split('.');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major == 621
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) && minor is >= 0 and <= 99;
    }
}
