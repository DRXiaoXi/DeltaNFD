namespace DeltaNFD.Services;

/// <summary>父功能关闭时，保留子选项偏好，但不显示或执行为已选中。</summary>
internal static class FrameFeatureState
{
    public static bool IsSelected(bool enabled, bool option) => enabled && option;

    public static string Describe(bool enabled, bool hasSelectedOptions, bool frameActive)
    {
        if (!enabled) return "未启用";
        if (!hasSelectedOptions) return "未选择子功能 · 不会执行";
        return frameActive ? "已选择 · 帧格已激活" : "已配置 · 等待开启帧格";
    }
}
