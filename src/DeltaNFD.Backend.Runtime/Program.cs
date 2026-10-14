using DeltaNFD.Backends;

namespace DeltaNFD.Backend.Runtime;

internal static class Program
{
    private static Task<int> Main(string[] args) => BackendHost.RunAsync(RuntimeCommands.Options, args);
}
