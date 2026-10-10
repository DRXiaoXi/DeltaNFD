namespace DeltaNFD.Services;

/// <summary>
/// GitHub 加速镜像 URL 改写器：把官方 GitHub 地址前缀为 gh-proxy 加速地址。
/// 只改写白名单内的 GitHub 主机，其他主机一律不改。
/// </summary>
internal static class UpdateUrlRewriter
{
    private const string GhProxyPrefix = "https://gh-proxy.com/";

    /// <summary>gh-proxy 前缀长度（用于剥前缀还原官方地址）。</summary>
    internal static readonly int GhProxyPrefixLength = GhProxyPrefix.Length;

    /// <summary>允许被 gh-proxy 加速的主机后缀列表。</summary>
    private static readonly string[] GitHubHosts =
    [
        "github.com",
        "api.github.com",
        "githubusercontent.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "raw.githubusercontent.com",
    ];

    /// <summary>
    /// 尝试把官方 GitHub URL 改写成 gh-proxy 加速 URL。
    /// 非白名单主机或已经是 gh-proxy 地址时返回 null。
    /// </summary>
    internal static string? TryPrefixGhProxy(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (IsGhProxyUrl(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var host = uri.Host.Trim().Trim('.').ToLowerInvariant();
        foreach (var allowed in GitHubHosts)
        {
            var suffix = allowed.TrimStart('.').ToLowerInvariant();
            if (host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal))
            {
                return GhProxyPrefix + url;
            }
        }

        return null;
    }

    /// <summary>判断 URL 是否已经是 gh-proxy 加速地址。</summary>
    internal static bool IsGhProxyUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url) &&
               url.StartsWith(GhProxyPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
