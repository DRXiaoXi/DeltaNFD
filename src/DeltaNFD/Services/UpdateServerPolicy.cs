namespace DeltaNFD.Services;

internal static class UpdateServerPolicy
{
    internal static UpdateServer Normalize(UpdateServer server) => Enum.IsDefined(server) ? server : UpdateServer.SelfHosted;
    internal static UpdateServer EffectiveServer(AppSettings settings) => Normalize(settings.UpdateServer);

    internal static string Name(UpdateServer server) => Normalize(server) switch
    {
        UpdateServer.GitHub => "仅 GitHub",
        UpdateServer.SelfHosted => "仅自建服务器",
        _ => "自动（GitHub 优先）",
    };

    internal static string Description(UpdateServer server) => Normalize(server) switch
    {
        UpdateServer.GitHub => "从 GitHub 检查并下载更新，连接失败时可手动切换服务器。",
        UpdateServer.SelfHosted => "默认服务器：从国内自建服务器检查并下载，支持文件替换和全量更新。",
        _ => "优先从 GitHub 检查并下载，失败后尝试自建服务器。",
    };

    internal static IEnumerable<string> ManifestSources(UpdateConfig config, AppSettings settings) => EffectiveServer(settings) switch
    {
        UpdateServer.GitHub => [GitHubReleaseSource.ReleasesUrl],
        UpdateServer.SelfHosted => [UpdateService.SelfHostedManifest(settings.UpdateChannel)],
        _ => UpdateChannelPolicy.Normalize(settings.UpdateChannel) == UpdateChannel.Internal && config.InternalManifestUrls.Count > 0
            ? config.InternalManifestUrls : config.ManifestUrls ?? [],
    };

    internal static bool Allows(UpdateServer server, string url) => Normalize(server) switch
    {
        UpdateServer.GitHub => UpdateUrlRewriter.IsGitHubUrl(url),
        UpdateServer.SelfHosted => UpdateUrlRewriter.IsSelfHostedUrl(url),
        _ => true,
    };
}

internal static class UpdateModePolicy
{
    internal static UpdateMode Normalize(UpdateMode mode) => mode == UpdateMode.FullInstaller ? mode : UpdateMode.ChangedFiles;
    internal static string Name(UpdateMode mode) => Normalize(mode) == UpdateMode.FullInstaller ? "全量更新" : "仅替换变更文件";
    internal static string Description(UpdateMode mode) => Normalize(mode) == UpdateMode.FullInstaller
        ? "从所选服务器下载完整安装包并更新。" : "默认方式：比对文件，只下载和替换变化内容；失败时自动回退全量更新。";
    internal static bool SameSelection(AppSettings first, AppSettings second) => Normalize(first.UpdateMode) == Normalize(second.UpdateMode)
        && UpdateServerPolicy.Normalize(first.UpdateServer) == UpdateServerPolicy.Normalize(second.UpdateServer)
        && UpdateChannelPolicy.Normalize(first.UpdateChannel) == UpdateChannelPolicy.Normalize(second.UpdateChannel);
}
