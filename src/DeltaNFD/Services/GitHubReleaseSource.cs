using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaNFD.Services;

internal static class GitHubReleaseSource
{
    internal const string ReleasesUrl = "https://api.github.com/repos/DRXiaoXi/DeltaNFD/releases?per_page=100";

    internal static bool TryParse(string json, UpdateConfig config, out UpdateManifest? manifest, out string reason)
    {
        manifest = null;
        reason = "";
        try
        {
            var releases = JsonSerializer.Deserialize<List<Release>>(json);
            if (releases is null || releases.Count == 0)
            {
                reason = "仓库尚无已发布版本。";
                return false;
            }

            // Do not guess from a truncated API page; fetch all pages before selecting a version.
            Release? latest = null;
            Version? highest = null;
            foreach (var release in releases)
            {
                if (release is null || release.Draft ||
                    !UpdateService.TryParseVersion(release.Tag, out var version)) continue;
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
                reason = "没有具有有效版本号的已发布 Release。";
                return false;
            }

            // Alpha releases are intentional. Never fall back to an older version with usable assets.
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
                Channel = latest.Prerelease ? "alpha" : "stable",
                ReleasePageUrl = latest.PageUrl,
                Notes = latest.Body ?? "",
                PublishedAtUtc = latest.PublishedAt,
                Installers = [new UpdateInstallerInfo
                {
                    Url = asset.DownloadUrl, SizeBytes = asset.Size, Sha256 = asset.Digest[7..],
                }],
            };
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
