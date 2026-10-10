using DeltaNFD.Backends;

namespace DeltaNFD.Backend.Runtime;

internal static class Program
{
    private static Task<int> Main(string[] args) => args.Length > 0 && args[0] == "--runtime-system-probe"
        ? Task.FromResult(DeltaNFD.Services.RuntimeSystemProbeProcess.Run(args))
        : BackendHost.RunAsync(RuntimeCommands.Options, args);
}
