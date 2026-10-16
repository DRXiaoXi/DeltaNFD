namespace DeltaNFD.Services;

/// <summary>只迁移历史加速 URL 到 GitHub 直连，不再生成任何加速地址。</summary>
internal static class UpdateUrlRewriter
{
    private const string GhProxyPrefix = "https://gh-proxy.com/";
    internal static readonly int GhProxyPrefixLength = GhProxyPrefix.Length;

    internal static bool IsGhProxyUrl(string? url) =>
        url?.StartsWith(GhProxyPrefix, StringComparison.OrdinalIgnoreCase) == true;

    internal static string RestoreOfficialUrl(string? url)
    {
        var original = url ?? "";
        var candidate = original;
        for (var i = 0; i < 8 && IsGhProxyUrl(candidate); i++)
            candidate = candidate[GhProxyPrefixLength..];
        return IsGitHubUrl(candidate) ? candidate : original;
    }

    internal static bool IsGitHubUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && (uri.Host == "github.com" || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host == "githubusercontent.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    internal static bool IsSelfHostedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.Host == "deltaxiaoxi.xin";
}
