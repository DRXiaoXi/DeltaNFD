using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

internal static class NvLocalSystemContainerPolicy
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NvContainer");
    private static readonly string LogPath = Regex.Escape(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        @"NVIDIA Corporation\NVIDIA App\NvContainer\NvContainerLocalSystem.log"));
    private static readonly string Plugins = Regex.Escape(Path.Combine(DirectoryPath, @"plugins\LocalSystem"));
    private static readonly Regex Arguments = new(
        $$"""\A[ \t]+-s[ \t]+NvContainerLocalSystem[ \t]+-a[ \t]+-f[ \t]+"{{LogPath}}"[ \t]+-l[ \t]+[0-5][ \t]+-d[ \t]+"{{Plugins}}"[ \t]+-r[ \t]+-p[ \t]+30000(?:[ \t]+-ert)?[ \t]*\z""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static string? ParseImage(string? raw)
    {
        if (raw is null || raw.Length is 0 or > 1024 || !Path.IsPathFullyQualified(DirectoryPath)) return null;
        var match = NvDisplayContainerService.Executable.Match(raw);
        if (!match.Success) return null;
        var image = match.Groups["exe"].Value;
        return string.Equals(image, Path.Combine(DirectoryPath, "nvcontainer.exe"), StringComparison.OrdinalIgnoreCase)
            && Arguments.IsMatch(raw[match.Length..]) ? image : null;
    }
}
