using DeltaNFD.Backends;

namespace DeltaNFD.Backend.Ace;

internal static class Program
{
    private static Task<int> Main(string[] args) => BackendHost.RunAsync(AceCommands.Options, args);
}
