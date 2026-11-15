using Microsoft.Win32;

namespace DeltaNFD.Inspection;
internal static class AceRegistryInspectionPolicy
{
    internal static readonly (RegistryHive Hive, string Label, string Path)[] Roots = [(RegistryHive.LocalMachine, "HKLM", @"SOFTWARE\AppDataLow"), (RegistryHive.Users, "HKU", @".DEFAULT\SOFTWARE\AppDataLow"), ];
    private static readonly string[] Parents = ["", "Software", @"Software\Tencent"];
    internal static bool IsName(string name) => name.Equals("AntiCheatExpert", StringComparison.OrdinalIgnoreCase) || name.Equals("ACE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ACE-", StringComparison.OrdinalIgnoreCase);
    internal static bool IsSharedPath(string path) => Roots.Any(r => path.Equals(r.Path, StringComparison.OrdinalIgnoreCase) || path.StartsWith(r.Path + "\\", StringComparison.OrdinalIgnoreCase));
    internal static bool IsCandidatePath(string path)
    {
        if (path.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
            return false;
        foreach (var root in Roots)
            foreach (var parent in Parents)
            {
                var prefix = root.Path + (parent.Length == 0 ? "" : "\\" + parent) + "\\";
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var leaf = path[prefix.Length..];
                if (!leaf.Contains('\\') && IsName(leaf))
                    return true;
            }

        return false;
    }

    internal static IReadOnlyList<string> Candidates(Func<string, IReadOnlyList<string>> enumerate, string root)
    {
        if (!Roots.Any(r => r.Path.Equals(root, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("未知共享根。");
        return Parents.SelectMany(parent =>
        {
            var path = root + (parent.Length == 0 ? "" : "\\" + parent);
            return enumerate(path).Where(IsName).Select(name => path + "\\" + name);
        }).Where(IsCandidatePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool MatchesInstallRoot(string? value, IEnumerable<string> allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
            return false;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
            if (!Path.IsPathFullyQualified(expanded))
                return false;
            var actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
            return allowedRoots.Any(root => Path.IsPathFullyQualified(root) && Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Equals("AntiCheatExpert", StringComparison.OrdinalIgnoreCase) && actual.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
