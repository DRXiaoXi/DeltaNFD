using DeltaNFD.Backends;

namespace DeltaNFD.Backend.SystemMaintenance;

internal static class Program
{
    private static Task<int> Main(string[] args) => BackendHost.RunAsync(SystemCommands.Options, args);
}
