using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

internal static class NvTelemetryContainerPolicy
{
    private static readonly string[] KnownDirectories = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
        .Select(Environment.GetFolderPath).Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => Path.Combine(path, @"NVIDIA Corporation\NvTelemetry")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static string? NormalizeAccount(string? name)
    {
        if (NvDisplayContainerService.IsLocalSystem(name)) return "LocalSystem";
        if (string.Equals(name, @"NT AUTHORITY\NetworkService", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, @"NT AUTHORITY\NETWORK SERVICE", StringComparison.OrdinalIgnoreCase)) return "NetworkService";
        return null;
    }

    internal static string? ParseImage(string? raw)
    {
        if (raw is null || raw.Length is 0 or > 1024) return null;
        var match = NvDisplayContainerService.Executable.Match(raw);
        if (!match.Success) return null;
        var image = match.Groups["exe"].Value;
        var directory = KnownDirectories.FirstOrDefault(path =>
            string.Equals(image, Path.Combine(path, "NvTelemetryContainer.exe"), StringComparison.OrdinalIgnoreCase));
        if (directory is null) return null;
        var args = raw[match.Length..];
        if (args.Trim(' ', '\t').Length == 0) return image;
        var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"NVIDIA\NvTelemetryContainer.log");
        var plugins = Regex.Escape(Path.Combine(directory, "plugin")) + "s?";
        var pattern = $"""\A[ \t]+-s[ \t]+NvTelemetryContainer(?:[ \t]+-f[ \t]+"{Regex.Escape(log)}"[ \t]+-l[ \t]+[0-5][ \t]+-d[ \t]+"{plugins}"(?:[ \t]+-r)?)?[ \t]*\z""";
        return Regex.IsMatch(args, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            ? image : null;
    }
}
