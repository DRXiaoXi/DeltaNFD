namespace DeltaNFD.Services;

/// <summary>只迁移历史加速 URL 到 GitHub 直连，不再生成任何加速地址。</summary>
internal static class UpdateUrlRewriter
{
    private const string GhProxyPrefix = "https://gh-proxy.com/";
    private static readonly string[] LegacyProxyPrefixes = [GhProxyPrefix, "https://gh-proxy.org/"];
    internal static readonly int GhProxyPrefixLength = GhProxyPrefix.Length;

    internal static bool IsGhProxyUrl(string? url) =>
        LegacyProxyPrefixes.Any(prefix => url?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true);

    internal static bool IsLegacyProxyHost(string host)
    {
        host = host.Trim().TrimEnd('.');
        return new[] { "gh-proxy.com", "gh-proxy.org" }.Any(proxy =>
            host.Equals(proxy, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + proxy, StringComparison.OrdinalIgnoreCase));
    }

    internal static string RestoreOfficialUrl(string? url)
    {
        var original = url ?? "";
        var candidate = original;
        for (var i = 0; i < 8; i++)
        {
            var prefix = LegacyProxyPrefixes.FirstOrDefault(p => candidate.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (prefix is null) break;
            candidate = candidate[prefix.Length..];
        }
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
