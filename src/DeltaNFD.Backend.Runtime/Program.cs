using DeltaNFD.Backends;

namespace DeltaNFD.Backend.Runtime;

internal static class Program
{
    private static Task<int> Main(string[] args) => args.FirstOrDefault() == "--runtime-system-probe"
        ? Task.FromResult(DeltaNFD.Services.AlphaRestore.RuntimeSystemProbeProcess.Run(args))
        : BackendHost.RunAsync(RuntimeCommands.Options, args);
}
