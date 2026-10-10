using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

internal enum UpdateReleaseKind { Unknown, Alpha, Beta, Release }

internal static class UpdateChannelPolicy
{
    internal static UpdateChannel Normalize(UpdateChannel channel) => channel == UpdateChannel.Internal ? channel : UpdateChannel.Public;
    internal static string Name(UpdateChannel channel) => Normalize(channel) == UpdateChannel.Internal ? "内测通道" : "公测通道";

    internal static UpdateReleaseKind Prefix(string? text)
    {
        var match = Regex.Match(text ?? "", @"^\s*v?(OpenAlpha|Alpha|Beta|Release)[\s_-]*v?\s*\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return UpdateReleaseKind.Unknown;
        var prefix = match.Groups[1].Value;
        return prefix.EndsWith("Alpha", StringComparison.OrdinalIgnoreCase) ? UpdateReleaseKind.Alpha
            : prefix.Equals("Beta", StringComparison.OrdinalIgnoreCase) ? UpdateReleaseKind.Beta : UpdateReleaseKind.Release;
    }
    internal static UpdateReleaseKind Classify(string? version, string? display, string? channel = null, bool prerelease = false)
    {
        var kinds = new[] { Prefix(version), Prefix(display) };
        // An explicit Alpha prefix cannot be promoted by a misleading stable/prerelease flag.
        if (kinds.Contains(UpdateReleaseKind.Alpha)) return UpdateReleaseKind.Alpha;
        if (kinds.Contains(UpdateReleaseKind.Beta)) return UpdateReleaseKind.Beta;
        if (kinds.Contains(UpdateReleaseKind.Release)) return UpdateReleaseKind.Release;
        // Compatibility for older numeric-only release tags/manifests, never for unknown named prefixes.
        bool Numeric(string? value) => string.IsNullOrWhiteSpace(value)
            || Regex.IsMatch(value.Trim(), @"^v?\d+(\.\d+){1,3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!Numeric(version) || !Numeric(display)) return UpdateReleaseKind.Unknown;
        return channel?.Trim().ToLowerInvariant() switch
        {
            "alpha" or "internal" => UpdateReleaseKind.Alpha,
            "beta" => UpdateReleaseKind.Beta,
            "release" or "stable" => UpdateReleaseKind.Release,
            _ => prerelease ? UpdateReleaseKind.Alpha : UpdateReleaseKind.Release,
        };
    }
    internal static bool Allows(UpdateChannel channel, UpdateReleaseKind kind) => kind is UpdateReleaseKind.Beta or UpdateReleaseKind.Release
        || Normalize(channel) == UpdateChannel.Internal && kind == UpdateReleaseKind.Alpha;
    internal static bool Allows(UpdateChannel channel, UpdateManifest manifest)
        => Allows(channel, Classify(manifest.Version, manifest.DisplayVersion, manifest.Channel));
}
