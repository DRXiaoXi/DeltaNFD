namespace DeltaNFD.Services;

/// <summary>
/// GitHub 连通性探测：HEAD 请求仓库 API，判断官方源是否可达。
/// 结果由 UpdateService 缓存到 AppSettings，避免频繁探测。
/// </summary>
internal static class GitHubConnectivity
{
    /// <summary>
    /// 探测 GitHub API 是否可达。
    /// 返回 true = 直连可用；false = 不可达（超时、网络错误、非 2xx）。
    /// </summary>
    internal static async Task<bool> IsGitHubReachableAsync(
        TimeSpan timeout,
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            using var request = new HttpRequestMessage(
                HttpMethod.Head,
                new Uri("https://api.github.com/repos/DRXiaoXi/DeltaNFD", UriKind.Absolute));
            using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            return ((int)response.StatusCode) >= 200 && ((int)response.StatusCode) < 400;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
