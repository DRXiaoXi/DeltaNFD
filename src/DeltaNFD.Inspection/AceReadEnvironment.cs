using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;

namespace DeltaNFD.Inspection;
/// <summary>Read-only inspection shared by the UI and backend. No system mutations.</summary>
public sealed class AceReadEnvironment
{
    public string AceInstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AntiCheatExpert");

    public IReadOnlyList<string> GameAceDirectories() => DeltaForceLocator.FindGameAceDirectories().ToArray();
    public bool RegistryKeyExists(RegistryHive hive, string path)
    {
        using var key = Open(hive, path);
        return key is not null;
    }

    public IReadOnlyList<string> EnumerateSubKeyNames(RegistryHive hive, string path)
    {
        using var key = Open(hive, path);
        return key?.GetSubKeyNames() ?? [];
    }

    public bool HasAceRegistryOwnership(RegistryHive hive, string path)
    {
        if (!AceRegistryInspectionPolicy.IsCandidatePath(path))
            return false;
        using var key = Open(hive, path);
        if (key is null)
            return false;
        var roots = new[]
        {
            AceInstallDirectory
        }.Concat(GameAceDirectories()).ToArray();
        foreach (var name in new[]
        {
            "InstallLocation",
            "InstallPath",
            "InstallDir",
            "BasePath"
        }

        )
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)is string value && AceRegistryInspectionPolicy.MatchesInstallRoot(value, roots))
                return true;
        return false;
    }

    public double GetDirectorySizeMb(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return 0;
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file =>
            {
                try
                {
                    return file.Length;
                }
                catch
                {
                    return 0;
                }
            }) / 1024.0 / 1024.0;
        }
        catch
        {
            return 0;
        }
    }

    private static RegistryKey? Open(RegistryHive hive, string? subKey = null)
    {
        var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        if (subKey is null)
            return baseKey;
        using (baseKey)
            return baseKey.OpenSubKey(subKey);
    }
}
