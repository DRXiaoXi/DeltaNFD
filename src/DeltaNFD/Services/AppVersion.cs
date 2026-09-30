using System.Reflection;

namespace DeltaNFD.Services;

/// <summary>
/// 当前程序集的版本信息。唯一来源是 csproj 的 &lt;Version&gt; / &lt;InformationalVersion&gt;，
/// 供关于卡、更新检查与升级后比对共用，避免版本号散落多处同步。
/// </summary>
public static class AppVersion
{
    private static readonly Lazy<(Version Version, string Text)> Cached =
        new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>数值版本（如 0.82.0.0），用于与更新清单比较。</summary>
    public static Version Current => Cached.Value.Version;

    /// <summary>显示版本文本（如 OpenAlphaV0.82）；读取失败时为数值文本。</summary>
    public static string Text => Cached.Value.Text;

    /// <summary>三段数值文本（如 0.82.0）。</summary>
    public static string NumericText => Format(Current);

    /// <summary>格式化为三段数值文本（缺失段按 0 补）。</summary>
    public static string Format(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    private static (Version, string) Resolve()
    {
        var version = new Version(0, 0, 0, 0);
        var text = "未知版本";
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            version = assembly.GetName().Version ?? version;

            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            text = string.IsNullOrWhiteSpace(informational)
                ? Format(version)
                : StripSourceRevision(informational);
        }
        catch
        {
            // 反射失败时保持兜底值，不影响功能
        }

        return (version, text);
    }

    /// <summary>去掉 SourceLink 附加的“+提交哈希”（如 OpenAlphaV0.82+5f35a163）。</summary>
    internal static string StripSourceRevision(string value)
    {
        var index = value.IndexOf('+');
        var trimmed = (index >= 0 ? value[..index] : value).Trim();
        return trimmed.Length == 0 ? value.Trim() : trimmed;
    }
}
