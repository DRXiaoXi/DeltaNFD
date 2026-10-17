using System.Diagnostics;
using DeltaNFD.Backends;
using DeltaNFD.Services;

if (args.Length != 1 && !(args.Length == 2 && args[0] == "--single"))
{
    Console.Error.WriteLine("Usage: DotNetLayoutCheck <self-contained-publish-directory> | --single <runtimeconfig.json>");
    return 2;
}
var apps = new List<DotNetApp>();
if (args.Length == 2) apps.Add(new("SDK self-contained publication", Path.GetFullPath(args[1])));
else
{
    var root = Path.GetFullPath(args[0]);
    apps.Add(new("Frontend", Path.Combine(root, "DeltaNFD.runtimeconfig.json")));
    foreach (var spec in BackendModules.All.Values)
        apps.Add(new(spec.DisplayName, Path.Combine(root, "Backends", Path.ChangeExtension(spec.ExecutableName, ".runtimeconfig.json"))));
}
// Deployment validation must pass from the staged files, without borrowing an installed system runtime
// or falling back to development directories elsewhere on the build machine.
var result = await DotNetEnvironmentProbe.CheckAsync(apps, []);
Console.WriteLine(result.Summary);
Console.WriteLine(result.Details);
if (result.Level != DotNetCheckLevel.Complete || result.Components.Any(c => !c.Bundled)) return 1;
if (args.Length == 2) return 0;

// File checks alone cannot establish that an apphost actually starts. Run only the
// metadata entry point, with no user state and no installed runtime fallback.
var checkId = Guid.NewGuid();
var isolated = Path.Combine(Path.GetTempPath(), "DeltaNFD-layout-startup", checkId.ToString("N"));
Directory.CreateDirectory(isolated);
File.WriteAllText(Path.Combine(isolated, "isolated-test.marker"), checkId.ToString("D"));
try
{
    foreach (var spec in BackendModules.All.Values)
    {
        var exe = Path.Combine(Path.GetFullPath(args[0]), "Backends", spec.ExecutableName);
        var start = new ProcessStartInfo(exe, "--describe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        start.Environment["DOTNET_ROOT"] = isolated;
        start.Environment["DOTNET_ROOT_X64"] = isolated;
        start.Environment["PATH"] = "";
        start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
        start.Environment["DELTANFD_BACKEND_ISOLATED_ROOT"] = isolated;
        start.Environment["DELTANFD_BACKEND_ISOLATED_ID"] = checkId.ToString("D");
        using var process = Process.Start(start) ?? throw new IOException("Cannot start " + exe);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new IOException(spec.Id + " --describe startup timed out.");
        }
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(await output))
            throw new IOException($"{spec.Id} --describe failed (0x{process.ExitCode:X8}): {await error}");
        Console.WriteLine(spec.Id + " apphost startup passed without system .NET (--describe only).");
    }
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
finally { Directory.Delete(isolated, true); }
