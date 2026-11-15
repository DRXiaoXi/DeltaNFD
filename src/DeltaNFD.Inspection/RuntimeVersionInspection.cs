using System.Diagnostics;
using Microsoft.Win32;

namespace DeltaNFD.Inspection;

public sealed record RuntimeVersionEntry(string Architecture, string Source, string Version);
public sealed record RuntimeVersionSnapshot(IReadOnlyList<RuntimeVersionEntry> Entries, IReadOnlyList<string> Issues)
{
    public bool RequiresUpdate => Entries.Any(e => RuntimeVersionInspection.BelowMinimum(e.Version));
    public string OlderVersions => string.Join("；", Entries.Where(e => RuntimeVersionInspection.BelowMinimum(e.Version))
        .Select(e => e.Architecture + " " + e.Version).Distinct());
    public string Detail => (Entries.Count == 0 ? "未读取到可确认的 C++ 14.x 版本。" :
        string.Join("\n", Entries.Select(e => e.Architecture + " · " + e.Source + " · " + e.Version)))
        + (Issues.Count > 0 ? "\n部分版本未能读取：\n" + string.Join("\n", Issues) : "");
}

/// <summary>Version-only read. No MSI enumeration, compatibility colors, protection state or installer execution.</summary>
public static class RuntimeVersionInspection
{
    public static Task<RuntimeVersionSnapshot> ReadAsync() => InspectionScheduler.Run(Read);
    internal static bool BelowMinimum(string? value) => Parse(value) is { Major: 14 } v && v < new Version(14, 44);
    private static Version? Parse(string? value) => Version.TryParse(value?.Trim().TrimStart('v', 'V'), out var v) ? v : null;
    internal static RuntimeVersionSnapshot Read(Func<string, string?> registration, Func<string, string, string?> file)
    {
        var entries = new List<RuntimeVersionEntry>(); var issues = new List<string>();
        foreach (var arch in new[] { "x64", "x86" })
        {
            void Add(string name, Func<string?> read)
            {
                try
                {
                    var value = read();
                    if (string.IsNullOrWhiteSpace(value)) return;
                    if (Parse(value) is not { Major: 14 }) { issues.Add(arch + " " + name + " 版本无法解析"); return; }
                    entries.Add(new(arch, name, value.Trim().TrimStart('v', 'V')));
                }
                catch (Exception ex) { issues.Add(arch + " " + name + "：" + ex.Message); }
            }
            Add("安装版本", () => registration(arch));
            foreach (var name in new[] { "vcruntime140.dll", "msvcp140.dll" }) Add(name, () => file(arch, name));
        }
        return new(entries, issues);
    }
    private static RuntimeVersionSnapshot Read() => Read(arch =>
    {
        var versions = new List<Version>();
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\" + arch);
            if (key?.GetValue("Installed") is int installed && installed == 1 && Parse(key.GetValue("Version") as string) is { Major: 14 } version)
                versions.Add(version);
        }
        return versions.Count == 0 ? null : versions.Max()!.ToString();
    }, (arch, name) =>
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), arch == "x64" ? "System32" : "SysWOW64");
        var path = Path.Combine(directory, name);
        return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null;
    });
}
