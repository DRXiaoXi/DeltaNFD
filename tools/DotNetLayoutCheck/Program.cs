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
return result.Level == DotNetCheckLevel.Complete && result.Components.All(c => c.Bundled) ? 0 : 1;
