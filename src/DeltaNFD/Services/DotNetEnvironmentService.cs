using DeltaNFD.Backends;
using DeltaNFD.Services.Backends;
using Microsoft.Win32;

namespace DeltaNFD.Services;

internal static class DotNetEnvironmentService
{
    internal static Task<DotNetCheckResult> CheckAsync(CancellationToken token = default) => CheckAtAsync(AppContext.BaseDirectory, token);

    internal static Task<DotNetCheckResult> CheckAtAsync(string directory, CancellationToken token = default)
    {
        var apps = new List<DotNetApp> { new("主程序", Path.Combine(directory, "DeltaNFD.runtimeconfig.json")) };
        foreach (var spec in BackendModules.All.Values)
        {
            string exe;
            var flat = Path.Combine(directory, "Backends", spec.ExecutableName);
            var nested = Path.Combine(directory, "Backends", spec.Id, spec.ExecutableName);
            try { exe = directory == AppContext.BaseDirectory ? BackendPaths.Resolve(spec.Id)
                    : File.Exists(flat) ? flat : File.Exists(nested) ? nested : flat; }
            catch { exe = Path.Combine(directory, "Backends", spec.ExecutableName); }
            apps.Add(new(spec.DisplayName, Path.ChangeExtension(exe, ".runtimeconfig.json")));
        }
        return DotNetEnvironmentProbe.CheckAsync(apps, SystemRoots(), token);
    }

    internal static IReadOnlyList<string> SystemRoots()
    {
        // x64 apphost uses architecture-specific DOTNET_ROOT ahead of machine registration.
        var env = Environment.GetEnvironmentVariable("DOTNET_ROOT_X64") ?? Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(env)) return [env];
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var setup = machine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
        var registered = setup?.GetValue("InstallLocation") as string;
        if (!string.IsNullOrWhiteSpace(registered)) return [registered];
        var programFiles = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var root = Path.Combine(programFiles, "dotnet");
        return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? [Path.Combine(root, "x64")] : [root];
    }
}
