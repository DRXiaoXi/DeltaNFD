namespace DeltaNFD.Services;

/// <summary>
/// 背景图管理：解析当前生效的背景图（用户自定义 > 内置 Assets\background.*），
/// 提供选图/恢复默认/遮罩浓度的设置入口，变更通过 Changed 事件通知 MainWindow 实时应用。
/// 自定义图复制到 %APPDATA%\Delta NFD\background.*，不依赖安装目录、可随时移除。
/// </summary>
public static class BackgroundManager
{
    /// <summary>背景（图片或遮罩）发生变化。</summary>
    public static event Action? Changed;

    private static readonly string CustomDir = AppDataPaths.Root;

    private static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    /// <summary>当前生效的背景图路径；null = 无背景（Mica）。</summary>
    public static string? ResolveCurrent()
    {
        // 背景图片模式关闭：一律无背景（Mica），设置页同时锁定深色主题
        if (!AppSettingsStore.Read().BackgroundImageEnabled)
        {
            return null;
        }

        var custom = AppSettingsStore.Read().BackgroundPath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
        {
            return custom;
        }

        var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        return SupportedExtensions
            .Select(ext => Path.Combine(assetsDir, "background" + ext))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>暗色遮罩不透明度（0~1，默认 0.5）。</summary>
    public static double DimOpacity =>
        Math.Clamp(AppSettingsStore.Read().BackgroundDimOpacity, 0, 1);

    /// <summary>设置自定义背景图（复制到 AppData，持久化路径并广播变更）。</summary>
    public static void SetCustomImage(string sourcePath)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(ext))
        {
            throw new InvalidOperationException("仅支持 PNG / JPG / WebP 图片。");
        }

        Directory.CreateDirectory(CustomDir);
        foreach (var old in Directory.GetFiles(CustomDir, "background.*"))
        {
            try { File.Delete(old); } catch { }
        }

        var target = Path.Combine(CustomDir, "background" + ext);
        File.Copy(sourcePath, target, overwrite: true);
        AppSettingsStore.Update(s => s.BackgroundPath = target);
        Changed?.Invoke();
    }

    /// <summary>移除自定义背景（回到内置 Assets\background.* 或 Mica）。</summary>
    public static void ResetToDefault()
    {
        if (Directory.Exists(CustomDir))
        {
            foreach (var old in Directory.GetFiles(CustomDir, "background.*"))
            {
                try { File.Delete(old); } catch { }
            }
        }

        AppSettingsStore.Update(s => s.BackgroundPath = "");
        Changed?.Invoke();
    }

    /// <summary>开关背景图片模式（false = Mica 云母背景），并广播变更。</summary>
    public static void SetBackgroundImageEnabled(bool enabled)
    {
        AppSettingsStore.Update(s => s.BackgroundImageEnabled = enabled);
        Changed?.Invoke();
    }

    /// <summary>调整遮罩浓度（0=不遮 1=全黑）并广播变更。</summary>
    public static void SetDimOpacity(double value)
    {
        AppSettingsStore.Update(s => s.BackgroundDimOpacity = Math.Clamp(value, 0, 1));
        Changed?.Invoke();
    }
}
