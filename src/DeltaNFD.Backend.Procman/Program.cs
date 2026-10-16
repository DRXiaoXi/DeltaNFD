using DeltaNFD.Backends;

namespace DeltaNFD.Backend.Procman;

internal static class Program
{
    private static Task<int> Main(string[] args) => BackendHost.RunAsync(ProcmanCommands.Options, args);
}
