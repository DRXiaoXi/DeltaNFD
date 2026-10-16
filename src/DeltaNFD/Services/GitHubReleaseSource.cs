using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaNFD.Services;

internal static class GitHubReleaseSource
{
    internal const string ReleasesUrl = "https://api.github.com/repos/DRXiaoXi/DeltaNFD/releases?per_page=100";
    internal const string NoMatchingReleaseReason = "当前更新通道没有已发布版本。";

    /// <summary>
    /// 判断 URL 是否指向本仓库的 Releases API（兼容识别历史代理配置，实际请求始终直连）。
    /// 用于 UpdateService 决定是否需要分页拉取完整发布列表。
    /// </summary>
    internal static bool IsRepositoryReleasesApi(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var effective = UpdateUrlRewriter.RestoreOfficialUrl(url);

        if (!Uri.TryCreate(effective, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.AbsolutePath, "/repos/DRXiaoXi/DeltaNFD/releases", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryParse(string json, UpdateConfig config, out UpdateManifest? manifest, out string reason, UpdateChannel? channel = null)
    {
        manifest = null;
        reason = "";
        try
        {
            var releases = JsonSerializer.Deserialize<List<Release>>(json);
            if (releases is null || releases.Count == 0)
            {
                reason = channel.HasValue ? NoMatchingReleaseReason : "仓库尚无已发布版本。";
                return false;
            }

            // Do not guess from a truncated API page; fetch all pages before selecting a version.
            Release? latest = null;
            Version? highest = null;
            foreach (var release in releases)
            {
                if (release is null || release.Draft ||
                    !UpdateService.TryParseVersion(release.Tag, out var version)) continue;
                if (channel.HasValue && !UpdateChannelPolicy.Allows(channel.Value,
                    UpdateChannelPolicy.Classify(release.Tag, release.Name, prerelease: release.Prerelease))) continue;
                if (highest is null || UpdateService.CompareVersions(version, highest) > 0 ||
                    (UpdateService.CompareVersions(version, highest) == 0 &&
                     string.CompareOrdinal(release.PublishedAt, latest?.PublishedAt) > 0))
                {
                    latest = release;
                    highest = version;
                }
            }

            if (latest is null || highest is null)
            {
                reason = channel.HasValue ? NoMatchingReleaseReason : "没有具有有效版本号的已发布 Release。";
                return false;
            }

            // Select the highest version within the chosen channel before validating its assets.
            var assets = (latest.Assets ?? []).Where(a => a is not null &&
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("DeltaNFD", StringComparison.OrdinalIgnoreCase) &&
                (a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("安装包", StringComparison.Ordinal)) &&
                (a.Name.Contains("_x64", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("-x64", StringComparison.OrdinalIgnoreCase)) &&
                (a.State == "uploaded") && a.Size > 0).ToList();
            var conventional = $"DeltaNFD_Setup_{AppVersion.Format(highest)}_x64.exe";
            var asset = assets.FirstOrDefault(a => a.Name.Equals(conventional, StringComparison.OrdinalIgnoreCase));
            if (asset is null && assets.Count == 1) asset = assets[0];
            if (asset is null)
            {
                reason = $"最新发布 {latest.Tag} 未提供唯一可识别的 x64 安装包。";
                return false;
            }

            if (!asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                !UpdateService.IsValidSha256(asset.Digest[7..]))
            {
                reason = $"最新发布 {latest.Tag} 的安装包缺少 GitHub SHA256，不能安全安装。";
                return false;
            }

            var generated = new UpdateManifest
            {
                Version = AppVersion.Format(highest),
                DisplayVersion = string.IsNullOrWhiteSpace(latest.Name) ? latest.Tag : latest.Name,
                Channel = UpdateChannelPolicy.Classify(latest.Tag, latest.Name, prerelease: latest.Prerelease).ToString().ToLowerInvariant(),
                ReleasePageUrl = latest.PageUrl,
                Notes = latest.Body ?? "",
                PublishedAtUtc = latest.PublishedAt,
                Installers = [new UpdateInstallerInfo
                {
                    Url = asset.DownloadUrl, SizeBytes = asset.Size, Sha256 = asset.Digest[7..],
                }],
            };
            var fileAssets = (latest.Assets ?? []).Where(a => a is not null && a.State == "uploaded"
                && a.Name.Equals($"DeltaNFD_Files_{AppVersion.Format(highest)}_x64.json", StringComparison.OrdinalIgnoreCase)).ToList();
            if (fileAssets.Count == 1 && fileAssets[0].Size is > 0 and <= DeltaNFD.Updates.FileUpdateRules.MaximumManifestBytes
                && fileAssets[0].Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                && UpdateService.IsValidSha256(fileAssets[0].Digest[7..]))
                generated.FileUpdate = new() { Url = fileAssets[0].DownloadUrl, SizeBytes = fileAssets[0].Size, Sha256 = fileAssets[0].Digest[7..] };
            return UpdateService.TryParseManifest(JsonSerializer.Serialize(generated), config, out manifest, out reason);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException)
        {
            reason = "Release 数据无效：" + ex.Message;
            return false;
        }
    }

    private sealed class Release
    {
        [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("published_at")] public string PublishedAt { get; set; } = "";
        [JsonPropertyName("html_url")] public string PageUrl { get; set; } = "";
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("assets")] public List<Asset>? Assets { get; set; }
    }

    private sealed class Asset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("state")] public string State { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string Digest { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    }
}
